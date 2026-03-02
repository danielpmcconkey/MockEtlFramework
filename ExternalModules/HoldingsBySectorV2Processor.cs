using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 replacement for HoldingsBySectorWriter.
/// Writes the CSV file with the W7 inflated trailer count.
///
/// All business logic (join, group, aggregate) is handled upstream by the
/// SQL Transformation module. This External module exists solely because
/// CsvFileWriter's {row_count} token substitutes df.Count (output rows),
/// but V1's trailer uses the raw holdings input count (before grouping).
///
/// Anti-patterns eliminated:
///   AP4 — holdings reduced from 7 to 2 columns; securities from 6 to 2
///   AP6 — V1's foreach + Dictionary grouping replaced with SQL GROUP BY
///
/// Wrinkles reproduced:
///   W7 — Trailer uses input holdings count, not grouped output row count
///   W9 — Overwrite mode: prior days' data lost on each auto-advance run
/// </summary>
public class HoldingsBySectorV2Processor : IExternalStep
{
    private const string OutputRelativePath = "Output/double_secret_curated/holdings_by_sector.csv";
    private const string TrailerPrefix = "TRAILER";
    private const string HeaderLine = "sector,holding_count,total_value,as_of";

    private static readonly List<string> OutputColumns = new()
    {
        "sector", "holding_count", "total_value", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var output = sharedState.TryGetValue("output", out var outVal)
            ? outVal as DataFrame
            : null;

        var holdings = sharedState.TryGetValue("holdings", out var holdVal)
            ? holdVal as DataFrame
            : null;

        // BR-3: Empty input — no file written, empty DataFrame stored as "output"
        if (holdings == null || holdings.Count == 0 || output == null || output.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // W7: Count INPUT rows before grouping (inflated count for trailer)
        var inputCount = holdings.Count;

        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];
        var dateStr = maxDate.ToString("yyyy-MM-dd");

        // Resolve output path
        var solutionRoot = GetSolutionRoot();
        var outputPath = Path.Combine(solutionRoot, OutputRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // W9: V1 uses Overwrite — prior days' data is lost on each run.
        using (var writer = new StreamWriter(outputPath, append: false))
        {
            // Header
            writer.Write(HeaderLine + "\n");

            // Data rows — already ordered by sector from SQL ORDER BY
            foreach (var row in output.Rows)
            {
                var sector = row["sector"]?.ToString() ?? "Unknown";
                var holdingCount = row["holding_count"];
                var totalValue = row["total_value"];
                var asOf = row["as_of"];
                writer.Write($"{sector},{holdingCount},{totalValue},{asOf}\n");
            }

            // W7: V1 bug — trailer uses input holdings count (before grouping),
            // not output row count. Replicated for output equivalence.
            writer.Write($"{TrailerPrefix}|{inputCount}|{dateStr}\n");
        }

        // Set empty output so the framework doesn't complain
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
