using System.Text;
using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 minimal External module for PeakTransactionTimes.
/// Handles ONLY file I/O — all business logic (hourly grouping, aggregation,
/// rounding, ordering) lives in the upstream SQL Transformation.
///
/// Why this External exists (Tier 2 SCALPEL):
///   W7 — Trailer uses input transaction count (before hourly grouping), not output row count.
///         CsvFileWriter's {row_count} token substitutes df.Count (output rows), which is wrong.
///   BR-10 — V1 uses UTF-8 with BOM encoding. CsvFileWriter uses UTF8Encoding(false) (no BOM).
///
/// Anti-patterns eliminated:
///   AP1 — accounts DataSourcing removed (never referenced by V1 logic)
///   AP3 — Business logic (grouping, aggregation, rounding) moved to SQL Transformation
///   AP4 — Unused columns removed: accounts table entirely, transactions reduced to txn_timestamp + amount
///   AP6 — V1's foreach + Dictionary replaced with SQL GROUP BY
/// </summary>
public class PeakTransactionTimesV2Processor : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "hour_of_day", "txn_count", "total_amount", "ifw_effective_date"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        // Read the aggregated output from the Transformation module
        var peakData = sharedState.TryGetValue("peak_transaction_times", out var peakVal)
            ? peakVal as DataFrame
            : null;

        // Read the raw transactions DataFrame for W7 trailer count
        var transactions = sharedState.TryGetValue("transactions", out var txnVal)
            ? txnVal as DataFrame
            : null;

        // W7: Trailer uses input transaction count (before hourly grouping)
        int inputCount = transactions?.Count ?? 0;

        var maxDate = sharedState.ContainsKey("__etlEffectiveDate")
            ? (DateOnly)sharedState["__etlEffectiveDate"]
            : DateOnly.FromDateTime(DateTime.Today);
        var dateStr = maxDate.ToString("yyyy-MM-dd");

        // Write CSV directly with W7 trailer and BR-10 UTF-8 BOM encoding
        WriteCsv(peakData, inputCount, dateStr);

        // Store empty DataFrame as output — the file has already been written directly
        sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
        return sharedState;
    }

    private static void WriteCsv(DataFrame? peakData, int inputCount, string dateStr)
    {
        var solutionRoot = GetSolutionRoot();
        var outputPath = Path.Combine(solutionRoot, "Output", "double_secret_curated", "peak_transaction_times.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // BR-10: V1 uses new StreamWriter(path, false) which defaults to UTF-8 with BOM.
        // CsvFileWriter uses UTF8Encoding(false) (no BOM), so we must write directly.
        using var writer = new StreamWriter(outputPath, append: false);
        writer.NewLine = "\n";

        // Header
        writer.WriteLine(string.Join(",", OutputColumns));

        // Data rows
        if (peakData != null)
        {
            foreach (var row in peakData.Rows)
            {
                var values = OutputColumns.Select(c => row[c]?.ToString() ?? "").ToArray();
                writer.WriteLine(string.Join(",", values));
            }
        }

        // W7: Trailer uses input transaction count (before hourly grouping), not output row count.
        // V1 behavior: transactions.Count is written to trailer regardless of how many hourly buckets exist.
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
