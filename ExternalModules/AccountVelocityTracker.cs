using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class AccountVelocityTracker : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "account_id", "customer_id", "txn_date", "txn_count", "total_amount", "as_of"
        };

        var transactions = sharedState.ContainsKey("transactions") ? sharedState["transactions"] as DataFrame : null;
        var accounts = sharedState.ContainsKey("accounts") ? sharedState["accounts"] as DataFrame : null;

        if (transactions == null || transactions.Count == 0 || accounts == null || accounts.Count == 0)
        {
            WriteDirectCsv(new List<Row>(), outputColumns, sharedState);
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];
        var dateStr = maxDate.ToString("yyyy-MM-dd");

        // Build account_id -> customer_id lookup
        var accountToCustomer = new Dictionary<int, int>();
        foreach (var acctRow in accounts.Rows)
        {
            var accountId = Convert.ToInt32(acctRow["account_id"]);
            var customerId = Convert.ToInt32(acctRow["customer_id"]);
            accountToCustomer[accountId] = customerId;
        }

        // Group by account_id and txn_date (as_of)
        var groups = new Dictionary<(int accountId, string txnDate), (int count, decimal total)>();
        foreach (var row in transactions.Rows)
        {
            var accountId = Convert.ToInt32(row["account_id"]);
            var txnDate = row["as_of"]?.ToString() ?? dateStr;

            var key = (accountId, txnDate);
            if (!groups.ContainsKey(key))
                groups[key] = (0, 0m);

            var current = groups[key];
            groups[key] = (current.count + 1, current.total + Convert.ToDecimal(row["amount"]));
        }

        var outputRows = new List<Row>();
        foreach (var kvp in groups.OrderBy(k => k.Key.txnDate).ThenBy(k => k.Key.accountId))
        {
            var (accountId, txnDate) = kvp.Key;
            var (count, total) = kvp.Value;
            var customerId = accountToCustomer.GetValueOrDefault(accountId, 0);

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["account_id"] = accountId,
                ["customer_id"] = customerId,
                ["txn_date"] = txnDate,
                ["txn_count"] = count,
                ["total_amount"] = Math.Round(total, 2),
                ["as_of"] = dateStr
            }));
        }

        // W12: External writes CSV directly with header re-emitted on every append
        WriteDirectCsv(outputRows, outputColumns, sharedState);

        sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
        return sharedState;
    }

    private static void WriteDirectCsv(List<Row> rows, List<string> columns, Dictionary<string, object> sharedState)
    {
        var solutionRoot = GetSolutionRoot();
        var outputPath = Path.Combine(solutionRoot, "Output", "curated", "account_velocity_tracking.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // W12: Append mode with header re-emitted on every run
        using var writer = new StreamWriter(outputPath, append: true);
        writer.NewLine = "\n";

        // Header re-emitted each time
        writer.WriteLine(string.Join(",", columns));

        foreach (var row in rows)
        {
            var values = columns.Select(c => row[c]?.ToString() ?? "").ToArray();
            writer.WriteLine(string.Join(",", values));
        }
    }

    private static string GetSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Solution root not found");
    }
}
