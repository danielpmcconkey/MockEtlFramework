using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class FundAllocationWriter : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "security_type", "holding_count", "total_value", "avg_value", "as_of"
        };

        var holdings = sharedState.ContainsKey("holdings") ? sharedState["holdings"] as DataFrame : null;
        var securities = sharedState.ContainsKey("securities") ? sharedState["securities"] as DataFrame : null;

        if (holdings == null || holdings.Count == 0 || securities == null || securities.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];
        var dateStr = maxDate.ToString("yyyy-MM-dd");

        // Build security_id -> security_type lookup
        var typeLookup = new Dictionary<int, string>();
        foreach (var secRow in securities.Rows)
        {
            var secId = Convert.ToInt32(secRow["security_id"]);
            typeLookup[secId] = secRow["security_type"]?.ToString() ?? "Unknown";
        }

        // Group by security_type
        var typeGroups = new Dictionary<string, (int count, decimal totalValue)>();
        foreach (var row in holdings.Rows)
        {
            var secId = Convert.ToInt32(row["security_id"]);
            var secType = typeLookup.GetValueOrDefault(secId, "Unknown");
            var value = Convert.ToDecimal(row["current_value"]);

            if (!typeGroups.ContainsKey(secType))
                typeGroups[secType] = (0, 0m);

            var current = typeGroups[secType];
            typeGroups[secType] = (current.count + 1, current.totalValue + value);
        }

        // Write CSV directly (bypassing CsvFileWriter)
        var solutionRoot = GetSolutionRoot();
        var outputPath = Path.Combine(solutionRoot, "Output", "curated", "fund_allocation_breakdown.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        int rowCount = 0;
        using (var writer = new StreamWriter(outputPath, append: false))
        {
            writer.Write(string.Join(",", outputColumns) + "\n");

            foreach (var kvp in typeGroups.OrderBy(k => k.Key))
            {
                var secType = kvp.Key;
                var (count, totalValue) = kvp.Value;
                var avgValue = count > 0 ? Math.Round(totalValue / count, 2) : 0m;

                writer.Write($"{secType},{count},{Math.Round(totalValue, 2)},{avgValue},{dateStr}\n");
                rowCount++;
            }

            // W8: Trailer stale date — hardcoded to "2024-10-01" instead of maxDate
            writer.Write($"TRAILER|{rowCount}|2024-10-01\n");
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
