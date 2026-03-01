using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CustomerContactabilityProcessor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "first_name", "last_name", "email_address", "phone_number", "as_of"
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
        var phones = sharedState.ContainsKey("phone_numbers")
            ? sharedState["phone_numbers"] as DataFrame
            : null;

        // AP1: segments sourced but never used (dead-end)
        // AP4: unused columns prefix, suffix from customers

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

        // Build phone lookup
        var phoneLookup = new Dictionary<int, string>();
        if (phones != null)
        {
            foreach (var row in phones.Rows)
            {
                var custId = Convert.ToInt32(row["customer_id"]);
                phoneLookup[custId] = row["phone_number"]?.ToString() ?? "";
            }
        }

        // Find customers with marketing opt-in
        var marketingOptIn = new HashSet<int>();
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

            if (optedIn && prefType == "MARKETING_EMAIL")
                marketingOptIn.Add(custId);
        }

        var outputRows = new List<Row>();
        foreach (var custId in marketingOptIn)
        {
            // Must have valid email AND phone AND be in customer lookup
            if (!customerLookup.ContainsKey(custId)) continue;
            if (!emailLookup.ContainsKey(custId)) continue;
            if (!phoneLookup.ContainsKey(custId)) continue;

            var (firstName, lastName) = customerLookup[custId];

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = custId,
                ["first_name"] = firstName,
                ["last_name"] = lastName,
                ["email_address"] = emailLookup[custId],
                ["phone_number"] = phoneLookup[custId],
                ["as_of"] = targetDate
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
