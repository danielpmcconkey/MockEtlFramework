using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class PreferenceSummaryCounter : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "preference_type", "opted_in_count", "opted_out_count", "total_customers", "as_of"
        };

        var prefs = sharedState.ContainsKey("customer_preferences")
            ? sharedState["customer_preferences"] as DataFrame
            : null;

        if (prefs == null || prefs.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        var asOf = prefs.Rows[0]["as_of"];

        // AP6: Row-by-row iteration where SQL GROUP BY would suffice
        var counts = new Dictionary<string, (int optedIn, int optedOut)>();
        foreach (var row in prefs.Rows)
        {
            var prefType = row["preference_type"]?.ToString() ?? "";
            var optedIn = Convert.ToBoolean(row["opted_in"]);

            if (!counts.ContainsKey(prefType))
                counts[prefType] = (0, 0);

            var current = counts[prefType];
            if (optedIn)
                counts[prefType] = (current.optedIn + 1, current.optedOut);
            else
                counts[prefType] = (current.optedIn, current.optedOut + 1);
        }

        var outputRows = new List<Row>();
        foreach (var kvp in counts)
        {
            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["preference_type"] = kvp.Key,
                ["opted_in_count"] = kvp.Value.optedIn,
                ["opted_out_count"] = kvp.Value.optedOut,
                ["total_customers"] = kvp.Value.optedIn + kvp.Value.optedOut,
                ["as_of"] = asOf
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
