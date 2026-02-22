using Npgsql;
using Lib;
using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CustomerAddressDeltasV2Processor : IExternalStep
{
    private static readonly string[] CompareFields =
    {
        "customer_id", "address_line1", "city", "state_province",
        "postal_code", "country", "start_date", "end_date"
    };

    private static readonly List<string> OutputColumns = new()
    {
        "change_type", "address_id", "customer_id", "customer_name",
        "address_line1", "city", "state_province", "postal_code",
        "country", "start_date", "end_date", "as_of", "record_count"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var currentDate = (DateOnly)sharedState[DataSourcing.MinDateKey];
        var previousDate = currentDate.AddDays(-1);

        using var connection = new NpgsqlConnection(ConnectionHelper.GetConnectionString());
        connection.Open();

        var currentAddresses = FetchAddresses(connection, currentDate);
        var previousAddresses = FetchAddresses(connection, previousDate);
        var customerNames = FetchCustomerNames(connection, currentDate);

        // Baseline day: no previous snapshot means no meaningful delta
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
                ["as_of"] = currentDate.ToString("yyyy-MM-dd"),
                ["record_count"] = 0
            });
            var sentinelDf = new DataFrame(new List<Row> { nullRow }, OutputColumns);
            DscWriterUtil.Write("customer_address_deltas", false, sentinelDf);
            sharedState["output"] = sentinelDf;
            return sharedState;
        }

        // Build lookup dictionaries keyed by address_id
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

        // Detect deltas
        var deltaRows = new List<Row>();

        foreach (var (addressId, current) in currentByAddressId.OrderBy(kv => kv.Key))
        {
            string? changeType = null;

            if (!previousByAddressId.TryGetValue(addressId, out var previous))
            {
                changeType = "NEW";
            }
            else if (HasFieldChanged(current, previous))
            {
                changeType = "UPDATED";
            }

            if (changeType == null) continue;

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
                ["country"] = current["country"]?.ToString()?.Trim(),
                ["start_date"] = FormatDate(current["start_date"]),
                ["end_date"] = FormatDate(current["end_date"]),
                ["as_of"] = currentDate.ToString("yyyy-MM-dd"),
                ["record_count"] = deltaRows.Count // placeholder, updated below
            }));
        }

        int recordCount = deltaRows.Count;

        if (recordCount == 0)
        {
            // No deltas: single row with nulls except as_of and record_count
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
                ["as_of"] = currentDate.ToString("yyyy-MM-dd"),
                ["record_count"] = 0
            }));
        }
        else
        {
            // Set correct record_count on every row
            for (int i = 0; i < deltaRows.Count; i++)
            {
                deltaRows[i]["record_count"] = recordCount;
            }
        }

        var df = new DataFrame(deltaRows, OutputColumns);
        DscWriterUtil.Write("customer_address_deltas", false, df);
        sharedState["output"] = df;
        return sharedState;
    }

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
            names[id] = $"{firstName} {lastName}";
        }

        return names;
    }

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

    private static string Normalize(object? value)
    {
        if (value is null || value is DBNull) return "";
        if (value is DateTime dt) return DateOnly.FromDateTime(dt).ToString("yyyy-MM-dd");
        if (value is DateOnly d) return d.ToString("yyyy-MM-dd");
        return value.ToString()?.Trim() ?? "";
    }

    private static string? FormatDate(object? value)
    {
        if (value is null || value is DBNull) return null;
        if (value is DateTime dt) return DateOnly.FromDateTime(dt).ToString("yyyy-MM-dd");
        if (value is DateOnly d) return d.ToString("yyyy-MM-dd");
        return value.ToString();
    }
}
