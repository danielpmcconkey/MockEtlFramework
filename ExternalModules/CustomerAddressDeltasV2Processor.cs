using Npgsql;
using Lib;
using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 implementation of customer address delta detection.
/// Compares current and previous day address snapshots to identify NEW and UPDATED records.
/// Tier 3 (Full External) -- required because:
///   1. Cross-date data access: needs addresses from currentDate AND currentDate-1,
///      which DataSourcing cannot express (min/max effective dates are set to the same day).
///   2. PostgreSQL DISTINCT ON for customer name lookup has no SQLite equivalent.
/// </summary>
public class CustomerAddressDeltasV2Processor : IExternalStep
{
    /// <summary>
    /// Fields compared between current and previous day address snapshots
    /// to determine if an address has been UPDATED. (AP7: named constant with documenting comment)
    /// </summary>
    private static readonly string[] CompareFields =
    {
        "customer_id", "address_line1", "city", "state_province",
        "postal_code", "country", "start_date", "end_date"
    };

    /// <summary>
    /// Output column order -- must match V1 exactly for Parquet schema compatibility.
    /// V1 reference: [CustomerAddressDeltaProcessor.cs:16-21]
    /// </summary>
    private static readonly List<string> OutputColumns = new()
    {
        "change_type", "address_id", "customer_id", "customer_name",
        "address_line1", "city", "state_province", "postal_code",
        "country", "start_date", "end_date", "as_of", "record_count"
    };

    /// <summary>
    /// Date format for as_of, start_date, and end_date output fields. (AP7: named constant)
    /// </summary>
    private const string DateFormat = "yyyy-MM-dd";

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        // BR-1: Read effective date from shared state
        var currentDate = (DateOnly)sharedState[DataSourcing.MinDateKey];
        // BR-1: Previous date is always currentDate - 1 (no weekend fallback)
        var previousDate = currentDate.AddDays(-1);

        using var connection = new NpgsqlConnection(ConnectionHelper.GetConnectionString());
        connection.Open();

        // BR-2: Fetch address snapshots for both dates via direct PostgreSQL query
        var currentAddresses = FetchAddresses(connection, currentDate);
        var previousAddresses = FetchAddresses(connection, previousDate);
        // BR-8: Fetch most recent customer names as of current date
        var customerNames = FetchCustomerNames(connection, currentDate);

        // BR-3: Baseline day -- no previous snapshot means no meaningful delta.
        // Produce a single null-filled sentinel row with as_of and record_count=0.
        if (previousAddresses.Count == 0)
        {
            var nullRow = new Row(new Dictionary<string, object?>
            {
                ["change_type"] = null,
                ["address_id"] = null,
                ["customer_id"] = null,
                ["customer_name"] = null,
                ["address_line1"] = null,
                ["city"] = null,
                ["state_province"] = null,
                ["postal_code"] = null,
                ["country"] = null,
                ["start_date"] = null,
                ["end_date"] = null,
                ["as_of"] = currentDate.ToString(DateFormat),
                ["record_count"] = 0
            });
            sharedState["output"] = new DataFrame(new List<Row> { nullRow }, OutputColumns);
            return sharedState;
        }

        // Build lookup dictionaries keyed by address_id for set-based comparison
        var currentByAddressId = new Dictionary<int, Dictionary<string, object?>>();
        foreach (var row in currentAddresses)
        {
            var addressId = Convert.ToInt32(row["address_id"]);
            currentByAddressId[addressId] = row;
        }

        var previousByAddressId = new Dictionary<int, Dictionary<string, object?>>();
        foreach (var row in previousAddresses)
        {
            var addressId = Convert.ToInt32(row["address_id"]);
            previousByAddressId[addressId] = row;
        }

        // Detect deltas: iterate current addresses ordered by address_id ascending (BR-15)
        var deltaRows = new List<Row>();

        foreach (var (addressId, current) in currentByAddressId.OrderBy(kv => kv.Key))
        {
            string? changeType = null;

            // BR-4: NEW -- address_id exists in current but not in previous
            if (!previousByAddressId.TryGetValue(addressId, out var previous))
            {
                changeType = "NEW";
            }
            // BR-5: UPDATED -- address_id exists in both but at least one CompareField changed
            else if (HasFieldChanged(current, previous))
            {
                changeType = "UPDATED";
            }

            // BR-7: No DELETED detection -- only iterates current addresses
            if (changeType == null) continue;

            // BR-9: Customer name = "first_name last_name"; empty string if customer not found
            var customerId = Convert.ToInt32(current["customer_id"]);
            var customerName = customerNames.GetValueOrDefault(customerId, "");

            deltaRows.Add(new Row(new Dictionary<string, object?>
            {
                ["change_type"] = changeType,
                ["address_id"] = current["address_id"],
                ["customer_id"] = current["customer_id"],
                ["customer_name"] = customerName,
                ["address_line1"] = current["address_line1"],
                ["city"] = current["city"],
                ["state_province"] = current["state_province"],
                ["postal_code"] = current["postal_code"],
                ["country"] = current["country"]?.ToString()?.Trim(), // BR-10: trim country
                ["start_date"] = FormatDate(current["start_date"]),   // BR-11: format as yyyy-MM-dd
                ["end_date"] = FormatDate(current["end_date"]),       // BR-11: format as yyyy-MM-dd
                ["as_of"] = currentDate.ToString(DateFormat),         // BR-12: as_of as string
                ["record_count"] = 0 // placeholder, set to final count below
            }));
        }

        int recordCount = deltaRows.Count;

        // BR-14: No deltas detected -- produce a single null-filled sentinel row
        if (recordCount == 0)
        {
            deltaRows.Add(new Row(new Dictionary<string, object?>
            {
                ["change_type"] = null,
                ["address_id"] = null,
                ["customer_id"] = null,
                ["customer_name"] = null,
                ["address_line1"] = null,
                ["city"] = null,
                ["state_province"] = null,
                ["postal_code"] = null,
                ["country"] = null,
                ["start_date"] = null,
                ["end_date"] = null,
                ["as_of"] = currentDate.ToString(DateFormat),
                ["record_count"] = 0
            }));
        }
        else
        {
            // BR-13: Stamp final record_count on every delta row
            for (int i = 0; i < deltaRows.Count; i++)
            {
                deltaRows[i]["record_count"] = recordCount;
            }
        }

        sharedState["output"] = new DataFrame(deltaRows, OutputColumns);
        return sharedState;
    }

    /// <summary>
    /// Fetch all address records for a given date, ordered by address_id.
    /// Returns a list of dictionaries, one per address row.
    /// </summary>
    private static List<Dictionary<string, object?>> FetchAddresses(NpgsqlConnection connection, DateOnly asOfDate)
    {
        const string query = @"
            SELECT address_id, customer_id, address_line1, city, state_province,
                   postal_code, country, start_date, end_date
            FROM datalake.addresses
            WHERE as_of = @date
            ORDER BY address_id";

        using var cmd = new NpgsqlCommand(query, connection);
        cmd.Parameters.AddWithValue("@date", asOfDate.ToDateTime(TimeOnly.MinValue));

        using var reader = cmd.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();

        while (reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// Fetch the most recent customer name for each customer as of the given date.
    /// Uses PostgreSQL DISTINCT ON to get the latest as_of row per customer id (BR-8).
    /// Returns a dictionary mapping customer_id to "first_name last_name".
    /// </summary>
    private static Dictionary<int, string> FetchCustomerNames(NpgsqlConnection connection, DateOnly asOfDate)
    {
        const string query = @"
            SELECT DISTINCT ON (id) id, first_name, last_name
            FROM datalake.customers
            WHERE as_of <= @date
            ORDER BY id, as_of DESC";

        using var cmd = new NpgsqlCommand(query, connection);
        cmd.Parameters.AddWithValue("@date", asOfDate.ToDateTime(TimeOnly.MinValue));

        using var reader = cmd.ExecuteReader();
        var names = new Dictionary<int, string>();

        while (reader.Read())
        {
            var id = reader.GetInt32(reader.GetOrdinal("id"));
            var firstName = reader.GetString(reader.GetOrdinal("first_name"));
            var lastName = reader.GetString(reader.GetOrdinal("last_name"));
            // BR-9: Customer name is "first_name last_name"
            names[id] = $"{firstName} {lastName}";
        }

        return names;
    }

    /// <summary>
    /// Compare two address records across all CompareFields (BR-6).
    /// Returns true if any field has changed between current and previous snapshots.
    /// </summary>
    private static bool HasFieldChanged(Dictionary<string, object?> current, Dictionary<string, object?> previous)
    {
        foreach (var field in CompareFields)
        {
            var currentVal = Normalize(current.GetValueOrDefault(field));
            var previousVal = Normalize(previous.GetValueOrDefault(field));

            if (!string.Equals(currentVal, previousVal, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Normalize a field value for comparison (BR-16).
    /// - null/DBNull -> empty string
    /// - DateTime -> DateOnly formatted as yyyy-MM-dd
    /// - DateOnly -> formatted as yyyy-MM-dd
    /// - Other -> ToString().Trim() (handles whitespace in country, etc.)
    /// </summary>
    private static string Normalize(object? value)
    {
        if (value is null || value is DBNull) return "";
        if (value is DateTime dt) return DateOnly.FromDateTime(dt).ToString(DateFormat);
        if (value is DateOnly d) return d.ToString(DateFormat);
        return value.ToString()?.Trim() ?? "";
    }

    /// <summary>
    /// Format a date field for output (BR-11).
    /// - null/DBNull -> null (preserves null in output)
    /// - DateTime -> DateOnly formatted as yyyy-MM-dd
    /// - DateOnly -> formatted as yyyy-MM-dd
    /// - Other -> ToString()
    /// </summary>
    private static string? FormatDate(object? value)
    {
        if (value is null || value is DBNull) return null;
        if (value is DateTime dt) return DateOnly.FromDateTime(dt).ToString(DateFormat);
        if (value is DateOnly d) return d.ToString(DateFormat);
        return value.ToString();
    }
}
