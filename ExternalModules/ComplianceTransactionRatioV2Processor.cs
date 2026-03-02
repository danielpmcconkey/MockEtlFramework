using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 replacement for ComplianceTransactionRatioWriter.
/// Tier 2 justification: Framework CsvFileWriter cannot produce the inflated trailer count (W7),
/// and Transformation.RegisterTable skips empty DataFrames (Transformation.cs:46), so a SQL
/// subquery referencing an empty 'transactions' table would fail.
///
/// This External module is minimal: it reads pre-grouped data from the SQL Transformation,
/// augments with txn_count/events_per_1000_txns/as_of, and writes the CSV with inflated trailer.
///
/// Anti-patterns addressed:
///   AP3 — Grouping/NULL coalescing/ordering moved to SQL Transformation (partial elimination)
///   AP6 — V1's foreach + Dictionary grouping replaced by SQL GROUP BY
///
/// Output-affecting wrinkles reproduced:
///   W4 — Integer division for events_per_1000_txns (intentional V1 replication)
///   W7 — Trailer uses inflated input row count, not output row count (intentional V1 replication)
/// </summary>
public class ComplianceTransactionRatioV2Processor : IExternalStep
{
    // BR-3: Rate denominator for "per 1,000 transactions" metric
    private const int RatePerThousand = 1000;

    // Output columns match V1 exactly (ComplianceTransactionRatioWriter.cs:10-12)
    private static readonly List<string> OutputColumns = new()
    {
        "event_type", "event_count", "txn_count", "events_per_1000_txns", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        // Step 1: Read compliance_events from shared state for empty-data guard and row count
        var complianceEvents = sharedState.TryGetValue("compliance_events", out var ceVal)
            ? ceVal as DataFrame
            : null;

        // BR-1/Edge Case 1: If compliance_events is null or empty, return empty output.
        // Matches V1 behavior (ComplianceTransactionRatioWriter.cs:18-21) where no CSV is written.
        if (complianceEvents == null || complianceEvents.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // Step 2: Read grouped_events from Transformation output (already ordered alphabetically by SQL)
        var groupedEvents = sharedState.TryGetValue("grouped_events", out var geVal)
            ? geVal as DataFrame
            : null;

        if (groupedEvents == null || groupedEvents.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // Step 3: Read transactions from shared state for txn_count and trailer count
        var transactions = sharedState.TryGetValue("transactions", out var txVal)
            ? txVal as DataFrame
            : null;

        // BR-2: Total transaction count from ALL transactions rows
        int txnCount = transactions?.Count ?? 0;

        // W7: Inflated trailer count — sum of ALL input rows from both source DataFrames,
        // not the output row count. V1 behavior: ComplianceTransactionRatioWriter.cs:28
        int inputCount = complianceEvents.Count + (transactions?.Count ?? 0);

        // Step 4: Read effective date from shared state (BR-9)
        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];
        var dateStr = maxDate.ToString("yyyy-MM-dd");

        // Step 5: Build output rows from pre-grouped data (already ordered by event_type from SQL)
        var solutionRoot = GetSolutionRoot();
        var outputPath = Path.Combine(solutionRoot, "Output", "double_secret_curated", "compliance_transaction_ratio.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // Step 6: Write CSV file directly (framework CsvFileWriter cannot produce inflated trailer — W7)
        using (var writer = new StreamWriter(outputPath, append: false))
        {
            // Header row
            writer.Write(string.Join(",", OutputColumns) + "\n");

            // Data rows — iterate pre-grouped rows from SQL Transformation
            foreach (var row in groupedEvents.Rows)
            {
                var eventType = row["event_type"]?.ToString() ?? "Unknown";

                // SQLite COUNT(*) returns int64 (long); cast to int for V1-compatible integer arithmetic
                int eventCount = Convert.ToInt32(row["event_count"]);

                // W4: Integer division — (eventCount * 1000) / txnCount where both operands are int.
                // V1 bug: integer division truncates the result. Replicated for output equivalence.
                // (ComplianceTransactionRatioWriter.cs:54)
                int eventsPer1000Txns = txnCount > 0
                    ? (eventCount * RatePerThousand) / txnCount
                    : 0;

                writer.Write($"{eventType},{eventCount},{txnCount},{eventsPer1000Txns},{dateStr}\n");
            }

            // W7: Trailer uses input count (inflated) instead of output row count.
            // V1 behavior: ComplianceTransactionRatioWriter.cs:59
            writer.Write($"TRAILER|{inputCount}|{dateStr}\n");
        }

        // Step 7: Set empty output DataFrame matching V1 behavior
        // (ComplianceTransactionRatioWriter.cs:62)
        sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
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
