using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 replacement for PreferenceBySegmentWriter.
/// Computes opt-in rates per (segment_name, preference_type) group and writes
/// CSV directly with an inflated trailer count.
///
/// Anti-patterns eliminated:
///   AP4 — preference_id removed from customer_preferences DataSourcing (never used)
///   AP7 — Magic value "Unknown" replaced with named constant
///
/// Anti-patterns partially eliminated:
///   AP3 — DataSourcing handles all data access; External handles only business logic + file I/O
///   AP6 — Dictionary-based lookups retained for BR-9 last-write-wins semantics
///
/// Wrinkles preserved:
///   W5 — Banker's rounding: Math.Round with MidpointRounding.ToEven (explicit, matches V1)
///   W7 — Trailer uses INPUT row count (customer_preferences.Count before grouping), not output row count
///   W9 — Overwrite mode: append: false, prior days' data is lost on each run
/// </summary>
public class PreferenceBySegmentV2Processor : IExternalStep
{
    // Default segment name when customer has no segment mapping or segment_id doesn't exist in segments table
    private const string DefaultSegmentName = "Unknown";

    private static readonly List<string> OutputColumns = new()
    {
        "segment_name", "preference_type", "opt_in_rate", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var customerPreferences = sharedState.TryGetValue("customer_preferences", out var cpVal)
            ? cpVal as DataFrame
            : null;
        var customersSegments = sharedState.TryGetValue("customers_segments", out var csVal)
            ? csVal as DataFrame
            : null;
        var segments = sharedState.TryGetValue("segments", out var sVal)
            ? sVal as DataFrame
            : null;

        // Guard clause: if any required source is null/empty, write empty output and return
        if (customerPreferences == null || customerPreferences.Count == 0 ||
            customersSegments == null || customersSegments.Count == 0 ||
            segments == null || segments.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // W7: Capture INPUT row count before any grouping (inflated count for trailer)
        var inputCount = customerPreferences.Count;

        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];
        var dateStr = maxDate.ToString("yyyy-MM-dd");

        // Build segment lookup: segment_id -> segment_name
        var segmentLookup = new Dictionary<int, string>();
        foreach (var row in segments.Rows)
        {
            var segId = Convert.ToInt32(row["segment_id"]);
            segmentLookup[segId] = row["segment_name"]?.ToString() ?? "";
        }

        // Build customer -> segment lookup with dictionary-overwrite semantics (BR-9)
        // When a customer has multiple entries in customers_segments, the last-encountered
        // row wins. This replicates V1's non-deterministic but consistent behavior.
        var custSegLookup = new Dictionary<int, string>();
        foreach (var row in customersSegments.Rows)
        {
            var custId = Convert.ToInt32(row["customer_id"]);
            var segId = Convert.ToInt32(row["segment_id"]);
            custSegLookup[custId] = segmentLookup.GetValueOrDefault(segId, DefaultSegmentName);
        }

        // Group by (segment_name, preference_type) -> (opted_in_count, total_count)
        // No date filtering within the module — V1 aggregates ALL rows across the effective
        // date range (BRD edge case #5). With Overwrite mode, only the last day's output persists.
        var groups = new Dictionary<(string segment, string prefType), (int optedIn, int total)>();
        foreach (var row in customerPreferences.Rows)
        {
            var custId = Convert.ToInt32(row["customer_id"]);
            var prefType = row["preference_type"]?.ToString() ?? "";
            var optedIn = Convert.ToBoolean(row["opted_in"]);
            var segment = custSegLookup.GetValueOrDefault(custId, DefaultSegmentName);

            var key = (segment, prefType);
            if (!groups.ContainsKey(key))
                groups[key] = (0, 0);

            var current = groups[key];
            if (optedIn)
                groups[key] = (current.optedIn + 1, current.total + 1);
            else
                groups[key] = (current.optedIn, current.total + 1);
        }

        // Write CSV directly — bypassing CsvFileWriter because W7 requires inflated trailer
        WriteCsv(groups, inputCount, dateStr);

        // Set empty DataFrame as output so the framework doesn't complain
        sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
        return sharedState;
    }

    private void WriteCsv(
        Dictionary<(string segment, string prefType), (int optedIn, int total)> groups,
        int inputCount,
        string dateStr)
    {
        var solutionRoot = GetSolutionRoot();
        var outputPath = Path.Combine(solutionRoot, "Output", "double_secret_curated", "preference_by_segment.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // W9: Overwrite mode — prior days' data is lost on each run.
        // V1 uses append: false; V2 replicates this exactly.
        using var writer = new StreamWriter(outputPath, append: false);

        // Header
        writer.Write(string.Join(",", OutputColumns) + "\n");

        // Data rows — ordered by segment_name ASC, preference_type ASC (BR-4)
        foreach (var kvp in groups.OrderBy(k => k.Key.segment).ThenBy(k => k.Key.prefType))
        {
            var (segment, prefType) = kvp.Key;
            var (optedIn, total) = kvp.Value;

            // W5: Banker's rounding (MidpointRounding.ToEven) — matches V1 behavior.
            // V1: Math.Round((decimal)optedIn / total, 2, MidpointRounding.ToEven)
            decimal rate = total > 0
                ? Math.Round((decimal)optedIn / total, 2, MidpointRounding.ToEven)
                : 0m;

            writer.Write($"{segment},{prefType},{rate},{dateStr}\n");
        }

        // W7: Trailer uses input count (inflated) instead of output row count.
        // V1 counts preference rows before grouping — e.g. 11150 input vs ~40 output rows.
        writer.Write($"TRAILER|{inputCount}|{dateStr}\n");
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
