using Npgsql;
using Lib;
using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CoveredTransactionProcessor : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "transaction_id", "txn_timestamp", "txn_type", "amount", "description",
        "customer_id", "name_prefix", "first_name", "last_name", "sort_name",
        "name_suffix", "customer_segment", "address_id", "address_line1",
        "city", "state_province", "postal_code", "country",
        "account_id", "account_type", "account_status", "account_opened",
        "as_of", "record_count"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var effectiveDate = (DateOnly)sharedState[DataSourcing.MinDateKey];
        var dateParam = effectiveDate.ToDateTime(TimeOnly.MinValue);

        using var connection = new NpgsqlConnection(ConnectionHelper.GetConnectionString());
        connection.Open();

        // 1. Fetch transactions for the effective date
        var transactions = FetchRows(connection,
            @"SELECT transaction_id, account_id, txn_timestamp, txn_type, amount, description
              FROM datalake.transactions WHERE as_of = @date",
            dateParam);

        // 2. Fetch accounts with snapshot fallback (most recent <= effective date), Checking only
        var accountRows = FetchRows(connection,
            @"SELECT DISTINCT ON (account_id) account_id, customer_id, account_type, account_status, open_date
              FROM datalake.accounts WHERE as_of <= @date
              ORDER BY account_id, as_of DESC",
            dateParam);

        var checkingAccounts = new Dictionary<int, Dictionary<string, object?>>();
        foreach (var row in accountRows)
        {
            if (row["account_type"]?.ToString() == "Checking")
            {
                var accountId = Convert.ToInt32(row["account_id"]);
                checkingAccounts[accountId] = row;
            }
        }

        // 3. Fetch customers with snapshot fallback
        var customerRows = FetchRows(connection,
            @"SELECT DISTINCT ON (id) id, prefix, first_name, last_name, sort_name, suffix
              FROM datalake.customers WHERE as_of <= @date
              ORDER BY id, as_of DESC",
            dateParam);

        var customers = new Dictionary<int, Dictionary<string, object?>>();
        foreach (var row in customerRows)
        {
            var id = Convert.ToInt32(row["id"]);
            customers[id] = row;
        }

        // 4. Fetch active US addresses (ordered by start_date so first = earliest)
        var addressRows = FetchRows(connection,
            @"SELECT address_id, customer_id, address_line1, city, state_province, postal_code, country, start_date
              FROM datalake.addresses
              WHERE as_of = @date AND country = 'US' AND (end_date IS NULL OR end_date >= @date)
              ORDER BY customer_id, start_date ASC",
            dateParam);

        var activeUsAddresses = new Dictionary<int, Dictionary<string, object?>>();
        foreach (var row in addressRows)
        {
            var customerId = Convert.ToInt32(row["customer_id"]);
            // First row per customer_id wins (earliest start_date due to ORDER BY)
            if (!activeUsAddresses.ContainsKey(customerId))
                activeUsAddresses[customerId] = row;
        }

        // 5. Fetch segment mappings (deduplicated, first alphabetically)
        var segmentRows = FetchRows(connection,
            @"SELECT DISTINCT ON (cs.customer_id) cs.customer_id, s.segment_code
              FROM datalake.customers_segments cs
              JOIN datalake.segments s ON cs.segment_id = s.segment_id AND s.as_of = cs.as_of
              WHERE cs.as_of = @date
              ORDER BY cs.customer_id, s.segment_code ASC",
            dateParam);

        var segments = new Dictionary<int, string>();
        foreach (var row in segmentRows)
        {
            var customerId = Convert.ToInt32(row["customer_id"]);
            segments[customerId] = row["segment_code"]?.ToString() ?? "";
        }

        // 6. Join and filter: transaction -> checking account -> customer with active US address
        var outputRows = new List<(int customerId, int transactionId, Row row)>();

        foreach (var txn in transactions)
        {
            var accountId = Convert.ToInt32(txn["account_id"]);

            // Must be a Checking account
            if (!checkingAccounts.TryGetValue(accountId, out var account))
                continue;

            var customerId = Convert.ToInt32(account["customer_id"]);

            // Must have an active US address
            if (!activeUsAddresses.TryGetValue(customerId, out var address))
                continue;

            // Look up customer demographics
            customers.TryGetValue(customerId, out var customer);

            // Look up segment
            segments.TryGetValue(customerId, out var segmentCode);

            var transactionId = Convert.ToInt32(txn["transaction_id"]);

            var outputRow = new Row(new Dictionary<string, object?>
            {
                ["transaction_id"] = txn["transaction_id"],
                ["txn_timestamp"] = FormatTimestamp(txn["txn_timestamp"]),
                ["txn_type"] = txn["txn_type"]?.ToString()?.Trim(),
                ["amount"] = txn["amount"],
                ["description"] = txn["description"]?.ToString()?.Trim(),
                ["customer_id"] = account["customer_id"],
                ["name_prefix"] = customer?["prefix"]?.ToString()?.Trim(),
                ["first_name"] = customer?["first_name"]?.ToString()?.Trim(),
                ["last_name"] = customer?["last_name"]?.ToString()?.Trim(),
                ["sort_name"] = customer?["sort_name"]?.ToString()?.Trim(),
                ["name_suffix"] = customer?["suffix"]?.ToString()?.Trim(),
                ["customer_segment"] = segmentCode,
                ["address_id"] = address["address_id"],
                ["address_line1"] = address["address_line1"]?.ToString()?.Trim(),
                ["city"] = address["city"]?.ToString()?.Trim(),
                ["state_province"] = address["state_province"]?.ToString()?.Trim(),
                ["postal_code"] = address["postal_code"]?.ToString()?.Trim(),
                ["country"] = address["country"]?.ToString()?.Trim(),
                ["account_id"] = account["account_id"],
                ["account_type"] = account["account_type"]?.ToString()?.Trim(),
                ["account_status"] = account["account_status"]?.ToString()?.Trim(),
                ["account_opened"] = FormatDate(account["open_date"]),
                ["as_of"] = effectiveDate.ToString("yyyy-MM-dd"),
                ["record_count"] = 0 // placeholder
            });

            outputRows.Add((customerId, transactionId, outputRow));
        }

        // Sort: customer_id ASC, transaction_id DESC
        outputRows.Sort((a, b) =>
        {
            int cmp = a.customerId.CompareTo(b.customerId);
            return cmp != 0 ? cmp : b.transactionId.CompareTo(a.transactionId);
        });

        var finalRows = outputRows.Select(x => x.row).ToList();
        int recordCount = finalRows.Count;

        if (recordCount == 0)
        {
            // Zero-row case: single null row with as_of and record_count = 0
            finalRows.Add(new Row(new Dictionary<string, object?>
            {
                ["transaction_id"] = null,
                ["txn_timestamp"] = null,
                ["txn_type"] = null,
                ["amount"] = null,
                ["description"] = null,
                ["customer_id"] = null,
                ["name_prefix"] = null,
                ["first_name"] = null,
                ["last_name"] = null,
                ["sort_name"] = null,
                ["name_suffix"] = null,
                ["customer_segment"] = null,
                ["address_id"] = null,
                ["address_line1"] = null,
                ["city"] = null,
                ["state_province"] = null,
                ["postal_code"] = null,
                ["country"] = null,
                ["account_id"] = null,
                ["account_type"] = null,
                ["account_status"] = null,
                ["account_opened"] = null,
                ["as_of"] = effectiveDate.ToString("yyyy-MM-dd"),
                ["record_count"] = 0
            }));
        }
        else
        {
            for (int i = 0; i < finalRows.Count; i++)
                finalRows[i]["record_count"] = recordCount;
        }

        sharedState["output"] = new DataFrame(finalRows, OutputColumns);
        return sharedState;
    }

    private static List<Dictionary<string, object?>> FetchRows(
        NpgsqlConnection connection, string query, DateTime dateParam)
    {
        using var cmd = new NpgsqlCommand(query, connection);
        cmd.Parameters.AddWithValue("@date", dateParam);

        using var reader = cmd.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();

        while (reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return rows;
    }

    private static string? FormatTimestamp(object? value)
    {
        if (value is null || value is DBNull) return null;
        if (value is DateTime dt) return dt.ToString("yyyy-MM-dd HH:mm:ss");
        return value.ToString();
    }

    private static string? FormatDate(object? value)
    {
        if (value is null || value is DBNull) return null;
        if (value is DateTime dt) return DateOnly.FromDateTime(dt).ToString("yyyy-MM-dd");
        if (value is DateOnly d) return d.ToString("yyyy-MM-dd");
        return value.ToString();
    }
}
