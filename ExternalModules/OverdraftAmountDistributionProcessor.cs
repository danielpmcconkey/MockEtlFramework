using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class OverdraftAmountDistributionProcessor : IExternalStep
{
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

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "amount_bucket", "event_count", "total_amount", "as_of"
        };

        var overdraftEvents = sharedState.ContainsKey("overdraft_events")
            ? sharedState["overdraft_events"] as DataFrame
            : null;

        var maxDate = sharedState.ContainsKey("__maxEffectiveDate")
            ? (DateOnly)sharedState["__maxEffectiveDate"]
            : DateOnly.FromDateTime(DateTime.Today);

        // W7: Count INPUT rows before bucketing for inflated trailer count
        int inputRowCount = overdraftEvents?.Count ?? 0;

        if (overdraftEvents == null || overdraftEvents.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        var asOf = overdraftEvents.Rows[0]["as_of"]?.ToString() ?? maxDate.ToString("yyyy-MM-dd");

        // Bucket overdraft amounts into ranges
        var buckets = new Dictionary<string, (int count, decimal total)>
        {
            ["0-50"] = (0, 0m),
            ["50-100"] = (0, 0m),
            ["100-250"] = (0, 0m),
            ["250-500"] = (0, 0m),
            ["500+"] = (0, 0m)
        };

        foreach (var row in overdraftEvents.Rows)
        {
            var amount = Convert.ToDecimal(row["overdraft_amount"]);
            string bucket;

            if (amount <= 50m) bucket = "0-50";
            else if (amount <= 100m) bucket = "50-100";
            else if (amount <= 250m) bucket = "100-250";
            else if (amount <= 500m) bucket = "250-500";
            else bucket = "500+";

            var current = buckets[bucket];
            buckets[bucket] = (current.count + 1, current.total + amount);
        }

        // W7: External writes CSV directly (bypassing CsvFileWriter)
        var solutionRoot = GetSolutionRoot();
        var outputPath = Path.Combine(solutionRoot, "Output", "curated", "overdraft_amount_distribution.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using (var writer = new StreamWriter(outputPath, false))
        {
            writer.WriteLine(string.Join(",", outputColumns));

            foreach (var kvp in buckets)
            {
                if (kvp.Value.count == 0)
                    continue;

                writer.WriteLine($"{kvp.Key},{kvp.Value.count},{kvp.Value.total},{asOf}");
            }

            // W7: Trailer uses INPUT row count (inflated), not output bucket count
            writer.WriteLine($"TRAILER|{inputRowCount}|{maxDate:yyyy-MM-dd}");
        }

        // Still set output for framework compatibility
        var outputRows = new List<Row>();
        foreach (var kvp in buckets)
        {
            if (kvp.Value.count == 0)
                continue;

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["amount_bucket"] = kvp.Key,
                ["event_count"] = kvp.Value.count,
                ["total_amount"] = kvp.Value.total,
                ["as_of"] = asOf
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
