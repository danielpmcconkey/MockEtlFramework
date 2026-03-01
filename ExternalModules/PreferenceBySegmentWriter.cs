using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class PreferenceBySegmentWriter : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string> { "segment_name", "preference_type", "opt_in_rate", "as_of" };

        var prefs = sharedState.ContainsKey("customer_preferences")
            ? sharedState["customer_preferences"] as DataFrame
            : null;
        var custSegments = sharedState.ContainsKey("customers_segments")
            ? sharedState["customers_segments"] as DataFrame
            : null;
        var segments = sharedState.ContainsKey("segments")
            ? sharedState["segments"] as DataFrame
            : null;

        if (prefs == null || prefs.Count == 0 || custSegments == null || segments == null)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // W7: Count INPUT rows before any grouping (inflated count for trailer)
        var inputCount = prefs.Count;

        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];
        var dateStr = maxDate.ToString("yyyy-MM-dd");

        // Build segment_id -> segment_name lookup
        var segmentLookup = new Dictionary<int, string>();
        foreach (var row in segments.Rows)
        {
            var segId = Convert.ToInt32(row["segment_id"]);
            segmentLookup[segId] = row["segment_name"]?.ToString() ?? "";
        }

        // Build customer_id -> segment_name lookup
        var custSegLookup = new Dictionary<int, string>();
        foreach (var row in custSegments.Rows)
        {
            var custId = Convert.ToInt32(row["customer_id"]);
            var segId = Convert.ToInt32(row["segment_id"]);
            custSegLookup[custId] = segmentLookup.GetValueOrDefault(segId, "Unknown");
        }

        // Group by (segment_name, preference_type) -> (opted_in_count, total_count)
        var groups = new Dictionary<(string segment, string prefType), (int optedIn, int total)>();
        foreach (var row in prefs.Rows)
        {
            var custId = Convert.ToInt32(row["customer_id"]);
            var prefType = row["preference_type"]?.ToString() ?? "";
            var optedIn = Convert.ToBoolean(row["opted_in"]);
            var segment = custSegLookup.GetValueOrDefault(custId, "Unknown");

            var key = (segment, prefType);
            if (!groups.ContainsKey(key))
                groups[key] = (0, 0);

            var current = groups[key];
            if (optedIn)
                groups[key] = (current.optedIn + 1, current.total + 1);
            else
                groups[key] = (current.optedIn, current.total + 1);
        }

        // Write CSV directly (bypassing CsvFileWriter)
        var solutionRoot = GetSolutionRoot();
        var outputPath = Path.Combine(solutionRoot, "Output", "curated", "preference_by_segment.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using (var writer = new StreamWriter(outputPath, append: false))
        {
            writer.Write(string.Join(",", outputColumns) + "\n");

            foreach (var kvp in groups.OrderBy(k => k.Key.segment).ThenBy(k => k.Key.prefType))
            {
                var (segment, prefType) = kvp.Key;
                var (optedIn, total) = kvp.Value;
                // W5: Banker's rounding
                decimal rate = total > 0
                    ? Math.Round((decimal)optedIn / total, 2, MidpointRounding.ToEven)
                    : 0m;

                writer.Write($"{segment},{prefType},{rate},{dateStr}\n");
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
