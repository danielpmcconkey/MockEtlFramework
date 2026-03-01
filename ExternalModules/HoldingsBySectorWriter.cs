using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class HoldingsBySectorWriter : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string> { "sector", "holding_count", "total_value", "as_of" };

        var holdings = sharedState.ContainsKey("holdings") ? sharedState["holdings"] as DataFrame : null;
        var securities = sharedState.ContainsKey("securities") ? sharedState["securities"] as DataFrame : null;

        if (holdings == null || holdings.Count == 0 || securities == null || securities.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // W7: Count INPUT rows before any grouping (inflated count for trailer)
        var inputCount = holdings.Count;

        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];
        var dateStr = maxDate.ToString("yyyy-MM-dd");

        // Build security_id -> sector lookup
        var sectorLookup = new Dictionary<int, string>();
        foreach (var secRow in securities.Rows)
        {
            var secId = Convert.ToInt32(secRow["security_id"]);
            sectorLookup[secId] = secRow["sector"]?.ToString() ?? "Unknown";
        }

        // Group holdings by sector
        var sectorGroups = new Dictionary<string, (int count, decimal totalValue)>();
        foreach (var row in holdings.Rows)
        {
            var secId = Convert.ToInt32(row["security_id"]);
            var sector = sectorLookup.GetValueOrDefault(secId, "Unknown");
            var value = Convert.ToDecimal(row["current_value"]);

            if (!sectorGroups.ContainsKey(sector))
                sectorGroups[sector] = (0, 0m);

            var current = sectorGroups[sector];
            sectorGroups[sector] = (current.count + 1, current.totalValue + value);
        }

        // Write CSV directly (bypassing CsvFileWriter)
        var solutionRoot = GetSolutionRoot();
        var outputPath = Path.Combine(solutionRoot, "Output", "curated", "holdings_by_sector.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using (var writer = new StreamWriter(outputPath, append: false))
        {
            writer.Write(string.Join(",", outputColumns) + "\n");

            foreach (var kvp in sectorGroups.OrderBy(k => k.Key))
            {
                var sector = kvp.Key;
                var (count, totalValue) = kvp.Value;
                writer.Write($"{sector},{count},{Math.Round(totalValue, 2)},{dateStr}\n");
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
