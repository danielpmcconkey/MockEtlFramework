using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class DoNotContactProcessor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "first_name", "last_name", "as_of"
        };

        var maxDate = sharedState.ContainsKey("__maxEffectiveDate")
            ? (DateOnly)sharedState["__maxEffectiveDate"]
            : DateOnly.FromDateTime(DateTime.Today);

        // W1: Sunday skip — return empty DataFrame on Sundays
        if (maxDate.DayOfWeek == DayOfWeek.Sunday)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        var prefs = sharedState.ContainsKey("customer_preferences")
            ? sharedState["customer_preferences"] as DataFrame
            : null;
        var customers = sharedState.ContainsKey("customers")
            ? sharedState["customers"] as DataFrame
            : null;

        if (prefs == null || prefs.Count == 0 || customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Build customer lookup
        var customerLookup = new Dictionary<int, (string firstName, string lastName)>();
        foreach (var row in customers.Rows)
        {
            var id = Convert.ToInt32(row["id"]);
            customerLookup[id] = (row["first_name"]?.ToString() ?? "", row["last_name"]?.ToString() ?? "");
        }

        // AP6: Row-by-row — find customers opted out of ALL preferences
        var customerPrefs = new Dictionary<int, (int total, int optedOut)>();
        foreach (var row in prefs.Rows)
        {
            var custId = Convert.ToInt32(row["customer_id"]);
            var optedIn = Convert.ToBoolean(row["opted_in"]);

            if (!customerPrefs.ContainsKey(custId))
                customerPrefs[custId] = (0, 0);

            var current = customerPrefs[custId];
            if (!optedIn)
                customerPrefs[custId] = (current.total + 1, current.optedOut + 1);
            else
                customerPrefs[custId] = (current.total + 1, current.optedOut);
        }

        var asOf = prefs.Rows[0]["as_of"];

        var outputRows = new List<Row>();
        foreach (var kvp in customerPrefs)
        {
            // Customer opted out of ALL preferences
            if (kvp.Value.total > 0 && kvp.Value.total == kvp.Value.optedOut && customerLookup.ContainsKey(kvp.Key))
            {
                var (firstName, lastName) = customerLookup[kvp.Key];
                outputRows.Add(new Row(new Dictionary<string, object?>
                {
                    ["customer_id"] = kvp.Key,
                    ["first_name"] = firstName,
                    ["last_name"] = lastName,
                    ["as_of"] = asOf
                }));
            }
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
