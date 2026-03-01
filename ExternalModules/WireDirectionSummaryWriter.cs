using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class WireDirectionSummaryWriter : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "direction", "wire_count", "total_amount", "avg_amount", "as_of"
        };

        var wireTransfers = sharedState.ContainsKey("wire_transfers") ? sharedState["wire_transfers"] as DataFrame : null;

        if (wireTransfers == null || wireTransfers.Count == 0)
        {
            // Still need to write empty file
            WriteDirectCsv(new List<Row>(), outputColumns, 0, sharedState);
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // W7: count INPUT rows for trailer (before grouping)
        var inputCount = wireTransfers.Count;

        // AP8: complex SQL with unused CTE — but done in C# here instead
        // Group by direction
        var groups = new Dictionary<string, (int count, decimal total)>();
        foreach (var row in wireTransfers.Rows)
        {
            var direction = row["direction"]?.ToString() ?? "";
            var amount = Convert.ToDecimal(row["amount"]);

            if (!groups.ContainsKey(direction))
                groups[direction] = (0, 0m);

            var current = groups[direction];
            groups[direction] = (current.count + 1, current.total + amount);
        }

        var asOf = wireTransfers.Rows[0]["as_of"];
        var outputRows = new List<Row>();
        foreach (var kvp in groups)
        {
            var wireCount = kvp.Value.count;
            var totalAmount = kvp.Value.total;
            var avgAmount = wireCount > 0 ? Math.Round(totalAmount / wireCount, 2) : 0m;

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["direction"] = kvp.Key,
                ["wire_count"] = wireCount,
                ["total_amount"] = Math.Round(totalAmount, 2),
                ["avg_amount"] = avgAmount,
                ["as_of"] = asOf
            }));
        }

        // W7: External writes CSV directly (bypassing CsvFileWriter)
        WriteDirectCsv(outputRows, outputColumns, inputCount, sharedState);

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
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

    private void WriteDirectCsv(List<Row> rows, List<string> columns, int inputCount, Dictionary<string, object> sharedState)
    {
        var solutionRoot = GetSolutionRoot();
        var outputPath = Path.Combine(solutionRoot, "Output", "curated", "wire_direction_summary.csv");

        var outputDir = Path.GetDirectoryName(outputPath)!;
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

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

        // W7: trailer uses inputCount (inflated) instead of output row count
        writer.WriteLine($"TRAILER|{inputCount}|{dateStr}");
    }
}
