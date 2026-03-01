using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class MarketingEligibleProcessor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "first_name", "last_name", "email_address", "as_of"
        };

        var maxDate = sharedState.ContainsKey("__maxEffectiveDate")
            ? (DateOnly)sharedState["__maxEffectiveDate"]
            : DateOnly.FromDateTime(DateTime.Today);

        // W2: Weekend fallback — use Friday's data on Sat/Sun
        DateOnly targetDate = maxDate;
        if (maxDate.DayOfWeek == DayOfWeek.Saturday) targetDate = maxDate.AddDays(-1);
        else if (maxDate.DayOfWeek == DayOfWeek.Sunday) targetDate = maxDate.AddDays(-2);

        var prefs = sharedState.ContainsKey("customer_preferences")
            ? sharedState["customer_preferences"] as DataFrame
            : null;
        var customers = sharedState.ContainsKey("customers")
            ? sharedState["customers"] as DataFrame
            : null;
        var emails = sharedState.ContainsKey("email_addresses")
            ? sharedState["email_addresses"] as DataFrame
            : null;

        // AP4: unused columns prefix, suffix, birthdate sourced from customers; email_type from email_addresses

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

        // Build email lookup
        var emailLookup = new Dictionary<int, string>();
        if (emails != null)
        {
            foreach (var row in emails.Rows)
            {
                var custId = Convert.ToInt32(row["customer_id"]);
                emailLookup[custId] = row["email_address"]?.ToString() ?? "";
            }
        }

        // AP6: Row-by-row — find customers opted in to ALL 3 marketing channels
        var requiredTypes = new HashSet<string>
        {
            "MARKETING_EMAIL", "MARKETING_SMS", "PUSH_NOTIFICATIONS"
        };

        // Build customer_id -> set of opted-in preference types
        var customerOptIns = new Dictionary<int, HashSet<string>>();
        foreach (var row in prefs.Rows)
        {
            if (targetDate != maxDate)
            {
                var rowDate = (DateOnly)row["as_of"];
                if (rowDate != targetDate) continue;
            }

            var custId = Convert.ToInt32(row["customer_id"]);
            var prefType = row["preference_type"]?.ToString() ?? "";
            var optedIn = Convert.ToBoolean(row["opted_in"]);

            if (optedIn && requiredTypes.Contains(prefType))
            {
                if (!customerOptIns.ContainsKey(custId))
                    customerOptIns[custId] = new HashSet<string>();
                customerOptIns[custId].Add(prefType);
            }
        }

        var outputRows = new List<Row>();
        foreach (var kvp in customerOptIns)
        {
            if (kvp.Value.Count == requiredTypes.Count && customerLookup.ContainsKey(kvp.Key))
            {
                var (firstName, lastName) = customerLookup[kvp.Key];
                var email = emailLookup.GetValueOrDefault(kvp.Key, "");

                outputRows.Add(new Row(new Dictionary<string, object?>
                {
                    ["customer_id"] = kvp.Key,
                    ["first_name"] = firstName,
                    ["last_name"] = lastName,
                    ["email_address"] = email,
                    ["as_of"] = targetDate
                }));
            }
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
