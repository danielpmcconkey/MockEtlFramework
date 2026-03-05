using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 minimal External module for WireDirectionSummary.
/// Handles ONLY file I/O — all business logic (grouping by direction,
/// COUNT, SUM, AVG, ROUND) lives in the upstream SQL Transformation.
///
/// Why this External exists (Tier 2):
///   W7 — Trailer uses input wire transfer count (before grouping), not output row count.
///         CsvFileWriter's {row_count} token substitutes df.Count (output rows = 2),
///         but V1's trailer uses the raw input count (typically 35-62 rows).
///         [WireDirectionSummaryWriter.cs:26, 104]
///
/// Anti-patterns eliminated:
///   AP3 — Business logic (GROUP BY, COUNT, SUM, AVG) moved to SQL Transformation
///   AP4 — Unused columns removed: wire_id, customer_id, status (3 of 5 dropped)
///   AP6 — V1's foreach + Dictionary grouping replaced with SQL GROUP BY
///
/// What this module does NOT do:
///   - No grouping, aggregation, counting, summing, or averaging
///   - No data transformation of any kind
///   - No database queries
///   - No business logic decisions
/// </summary>
public class WireDirectionSummaryV2Processor : IExternalStep
{
    private const string OutputRelativePath = "Output/double_secret_curated/wire_direction_summary.csv";

    private static readonly List<string> OutputColumns = new()
    {
        "direction", "wire_count", "total_amount", "avg_amount", "ifw_effective_date"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        // Read the aggregated output from the Transformation module
        var summary = sharedState.TryGetValue("wire_direction_summary", out var sumVal)
            ? sumVal as DataFrame
            : null;

        // Read the raw wire_transfers DataFrame for W7 trailer count
        var wireTransfers = sharedState.TryGetValue("wire_transfers", out var wtVal)
            ? wtVal as DataFrame
            : null;

        // W7: Trailer uses input row count (before grouping), not output row count.
        // V1 behavior: wireTransfers.Count is written to trailer regardless of how many
        // direction groups exist. [WireDirectionSummaryWriter.cs:26, 104]
        int inputCount = wireTransfers?.Count ?? 0;

        // BR-9: Trailer date from __etlEffectiveDate with fallback to DateTime.Today
        // [WireDirectionSummaryWriter.cs:88-89]
        var maxDate = sharedState.ContainsKey("__etlEffectiveDate")
            ? (DateOnly)sharedState["__etlEffectiveDate"]
            : DateOnly.FromDateTime(DateTime.Today);
        var dateStr = maxDate.ToString("yyyy-MM-dd");

        // Write CSV directly with W7 inflated trailer count
        WriteCsv(summary, inputCount, dateStr);

        // BR-6: Store output DataFrame in shared state (matches V1 behavior, though unused
        // by subsequent modules) [WireDirectionSummaryWriter.cs:64]
        sharedState["output"] = summary ?? new DataFrame(new List<Row>(), OutputColumns);
        return sharedState;
    }

    private static void WriteCsv(DataFrame? summary, int inputCount, string dateStr)
    {
        // BR-8: Create output directory if needed [WireDirectionSummaryWriter.cs:84-86]
        var solutionRoot = GetSolutionRoot();
        var outputPath = Path.Combine(solutionRoot, OutputRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // W9: V1 uses Overwrite — prior days' data is lost on each run.
        // [WireDirectionSummaryWriter.cs:91]
        using var writer = new StreamWriter(outputPath, append: false);
        writer.NewLine = "\n"; // LF line endings [WireDirectionSummaryWriter.cs:92]

        // Header: column names joined by comma [WireDirectionSummaryWriter.cs:95]
        writer.WriteLine(string.Join(",", OutputColumns));

        // Data rows: values joined by comma via .ToString() (no RFC 4180 quoting)
        // BR-5: [WireDirectionSummaryWriter.cs:100]
        if (summary != null)
        {
            foreach (var row in summary.Rows)
            {
                var values = OutputColumns.Select(c => row[c]?.ToString() ?? "").ToArray();
                writer.WriteLine(string.Join(",", values));
            }
        }

        // W7: Trailer uses input row count (before grouping), not output row count.
        // V1 behavior replicated for output equivalence.
        // [WireDirectionSummaryWriter.cs:104-105]
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
