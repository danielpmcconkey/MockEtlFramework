using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 replacement for OverdraftAmountDistributionProcessor.
/// Minimal I/O adapter (Tier 2): all bucketing and aggregation logic lives in
/// the Transformation SQL module. This External module only writes the CSV
/// with the inflated trailer that CsvFileWriter cannot produce.
///
/// Anti-patterns eliminated:
///   AP3 — External reduced from full-pipeline to minimal I/O adapter
///   AP4 — Unused columns (overdraft_id, account_id, customer_id, fee_amount,
///          fee_waived, event_timestamp) removed from DataSourcing config
///   AP6 — Row-by-row foreach bucketing replaced with SQL CASE/GROUP BY
///   AP7 — Bucket boundaries documented with inline comments in SQL
///
/// Wrinkles replicated:
///   W7 — Trailer uses INPUT row count (inflated), not output bucket count
///   W9 — Overwrite mode (append: false); prior days' data lost on each run
/// </summary>
public class OverdraftAmountDistributionV2Processor : IExternalStep
{
    private const string OutputDataFrameName = "output";
    private const string SourceDataFrameName = "overdraft_events";
    private const string TrailerPrefix = "TRAILER";

    private static readonly List<string> OutputColumns = new()
    {
        "amount_bucket", "event_count", "total_amount", "ifw_effective_date"
    };

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
        // Read the Transformation output (already bucketed and aggregated by SQL)
        var output = sharedState.TryGetValue(OutputDataFrameName, out var outVal)
            ? outVal as DataFrame
            : null;

        // Read the source DataFrame for W7 inflated trailer count and OQ-1 ifw_effective_date format
        var overdraftEvents = sharedState.TryGetValue(SourceDataFrameName, out var srcVal)
            ? srcVal as DataFrame
            : null;

        var maxDate = sharedState.ContainsKey("__etlEffectiveDate")
            ? (DateOnly)sharedState["__etlEffectiveDate"]
            : DateOnly.FromDateTime(DateTime.Today);

        // W7: Count INPUT rows before bucketing for inflated trailer count.
        // V1 behavior: trailer reports total overdraft events (e.g., 139),
        // not the number of output buckets (e.g., 5).
        int inputRowCount = overdraftEvents?.Count ?? 0;

        // OQ-1: Read ifw_effective_date from the source DataFrame as raw DateOnly, then call
        // .ToString() to match V1's format exactly. The Transformation SQL produces
        // ifw_effective_date as a "yyyy-MM-dd" string (via SQLite), but V1 calls DateOnly.ToString()
        // which produces a culture-dependent format. Reading from the source DataFrame
        // preserves the original DateOnly object.
        var asOf = overdraftEvents != null && overdraftEvents.Count > 0
            ? overdraftEvents.Rows[0]["ifw_effective_date"]?.ToString() ?? maxDate.ToString("yyyy-MM-dd")
            : maxDate.ToString("yyyy-MM-dd");

        // Handle empty data edge case
        if (output == null || output.Count == 0)
        {
            sharedState[OutputDataFrameName] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // Write CSV directly — CsvFileWriter cannot produce the inflated trailer (W7)
        var solutionRoot = GetSolutionRoot();
        var outputPath = Path.Combine(solutionRoot, "Output", "double_secret_curated", "overdraft_amount_distribution.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // W9: V1 uses Overwrite — prior days' data is lost on each run.
        // Replicated for output equivalence.
        using (var writer = new StreamWriter(outputPath, false))
        {
            // Header row
            writer.WriteLine(string.Join(",", OutputColumns));

            // Data rows from the Transformation output (already ordered by SQL)
            foreach (var row in output.Rows)
            {
                var bucket = row["amount_bucket"];
                var eventCount = row["event_count"];
                var totalAmount = row["total_amount"];
                writer.WriteLine($"{bucket},{eventCount},{totalAmount},{asOf}");
            }

            // W7: Trailer uses INPUT row count (inflated), not output bucket count.
            // V1 behavior replicated for output equivalence.
            writer.WriteLine($"{TrailerPrefix}|{inputRowCount}|{maxDate:yyyy-MM-dd}");
        }

        // Return output DataFrame for framework compatibility
        return sharedState;
    }
}
