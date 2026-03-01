using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class ComplianceTransactionRatioWriter : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "event_type", "event_count", "txn_count", "events_per_1000_txns", "as_of"
        };

        var complianceEvents = sharedState.ContainsKey("compliance_events") ? sharedState["compliance_events"] as DataFrame : null;
        var transactions = sharedState.ContainsKey("transactions") ? sharedState["transactions"] as DataFrame : null;

        if (complianceEvents == null || complianceEvents.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];
        var dateStr = maxDate.ToString("yyyy-MM-dd");

        // W7: Count INPUT rows from both DataFrames for trailer (inflated)
        var inputCount = complianceEvents.Count + (transactions?.Count ?? 0);

        var txnCount = transactions?.Count ?? 0;

        // Group compliance events by event_type
        var eventGroups = new Dictionary<string, int>();
        foreach (var row in complianceEvents.Rows)
        {
            var eventType = row["event_type"]?.ToString() ?? "Unknown";
            eventGroups[eventType] = eventGroups.GetValueOrDefault(eventType, 0) + 1;
        }

        // Write CSV directly (bypassing CsvFileWriter)
        var solutionRoot = GetSolutionRoot();
        var outputPath = Path.Combine(solutionRoot, "Output", "curated", "compliance_transaction_ratio.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using (var writer = new StreamWriter(outputPath, append: false))
        {
            writer.Write(string.Join(",", outputColumns) + "\n");

            foreach (var kvp in eventGroups.OrderBy(k => k.Key))
            {
                var eventType = kvp.Key;
                var eventCount = kvp.Value;
                // W4: Integer division — (eventCount * 1000) / txnCount where both are int
                int eventsPer1000 = txnCount > 0 ? (eventCount * 1000) / txnCount : 0;
                writer.Write($"{eventType},{eventCount},{txnCount},{eventsPer1000},{dateStr}\n");
            }

            // W7: Trailer uses input count (inflated) instead of output row count
            writer.Write($"TRAILER|{inputCount}|{dateStr}\n");
        }

        // Set empty output so the framework doesn't complain
        sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
        return sharedState;
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
