using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class PeakTransactionTimesWriter : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "hour_of_day", "txn_count", "total_amount", "as_of"
        };

        var transactions = sharedState.ContainsKey("transactions") ? sharedState["transactions"] as DataFrame : null;

        if (transactions == null || transactions.Count == 0)
        {
            WriteDirectCsv(new List<Row>(), outputColumns, 0, sharedState);
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // W7: Count INPUT rows for trailer (before hourly bucketing)
        var inputCount = transactions.Count;

        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];
        var dateStr = maxDate.ToString("yyyy-MM-dd");

        // Group by hour of day from txn_timestamp
        var hourlyGroups = new Dictionary<int, (int count, decimal total)>();
        foreach (var row in transactions.Rows)
        {
            var timestamp = row["txn_timestamp"];
            int hour = 0;
            if (timestamp is DateTime dt)
                hour = dt.Hour;
            else if (timestamp != null && DateTime.TryParse(timestamp.ToString(), out var parsed))
                hour = parsed.Hour;

            if (!hourlyGroups.ContainsKey(hour))
                hourlyGroups[hour] = (0, 0m);

            var current = hourlyGroups[hour];
            hourlyGroups[hour] = (current.count + 1, current.total + Convert.ToDecimal(row["amount"]));
        }

        var outputRows = new List<Row>();
        foreach (var kvp in hourlyGroups.OrderBy(k => k.Key))
        {
            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["hour_of_day"] = kvp.Key,
                ["txn_count"] = kvp.Value.count,
                ["total_amount"] = Math.Round(kvp.Value.total, 2),
                ["as_of"] = dateStr
            }));
        }

        // W7: External writes CSV directly, trailer uses inputCount (inflated)
        WriteDirectCsv(outputRows, outputColumns, inputCount, sharedState);

        sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
        return sharedState;
    }

    private static void WriteDirectCsv(List<Row> rows, List<string> columns, int inputCount, Dictionary<string, object> sharedState)
    {
        var solutionRoot = GetSolutionRoot();
        var outputPath = Path.Combine(solutionRoot, "Output", "curated", "peak_transaction_times.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var maxDate = sharedState.ContainsKey("__maxEffectiveDate") ? (DateOnly)sharedState["__maxEffectiveDate"] : DateOnly.FromDateTime(DateTime.Today);
        var dateStr = maxDate.ToString("yyyy-MM-dd");

        using var writer = new StreamWriter(outputPath, append: false);
        writer.NewLine = "\n";

        // Header
        writer.WriteLine(string.Join(",", columns));

        // Data rows
        foreach (var row in rows)
        {
            var values = columns.Select(c => row[c]?.ToString() ?? "").ToArray();
            writer.WriteLine(string.Join(",", values));
        }

        // W7: Trailer uses inputCount (inflated) instead of output row count
        writer.WriteLine($"TRAILER|{inputCount}|{dateStr}");
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
