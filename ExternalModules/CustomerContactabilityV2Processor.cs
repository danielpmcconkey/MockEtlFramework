using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 External module for CustomerContactability.
/// Implements weekend fallback date logic (W2) and join/filter across four DataFrames.
///
/// Tier 2 justification: Weekend fallback requires access to __maxEffectiveDate from
/// shared state to compute targetDate BEFORE filtering. This date-conditional logic
/// cannot be expressed in a Transformation SQL module because __maxEffectiveDate is
/// not available as a scalar inside SQLite queries.
///
/// Anti-patterns eliminated:
///   AP1 — segments table no longer sourced (dead-end in V1)
///   AP4 — prefix/suffix columns no longer sourced from customers (unused in V1)
///
/// Wrinkles preserved:
///   W2 — Weekend fallback: Saturday/Sunday use Friday's preference data
///   W9 — Overwrite write mode: prior days' data lost on each run (handled by writer config)
/// </summary>
public class CustomerContactabilityV2Processor : IExternalStep
{
    /// <summary>
    /// Output column names matching V1's output schema order.
    /// </summary>
    private static readonly List<string> OutputColumns = new()
    {
        "customer_id", "first_name", "last_name",
        "email_address", "phone_number", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var maxDate = sharedState.ContainsKey("__maxEffectiveDate")
            ? (DateOnly)sharedState["__maxEffectiveDate"]
            : DateOnly.FromDateTime(DateTime.Today);

        // W2: Weekend fallback — Saturday/Sunday use Friday's preference data
        DateOnly targetDate = maxDate;
        if (maxDate.DayOfWeek == DayOfWeek.Saturday)
            targetDate = maxDate.AddDays(-1); // Friday
        else if (maxDate.DayOfWeek == DayOfWeek.Sunday)
            targetDate = maxDate.AddDays(-2); // Friday

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

        // BR-9: Empty guard — null or empty prefs/customers yields empty output
        if (prefs == null || prefs.Count == 0 || customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // Build customer lookup (id -> (firstName, lastName))
        // AP4 eliminated: no prefix/suffix extracted
        var customerLookup = new Dictionary<int, (string firstName, string lastName)>();
        foreach (var row in customers.Rows)
        {
            var id = Convert.ToInt32(row["id"]);
            // BR-9: Null coalescing on name fields — matches V1 behavior
            customerLookup[id] = (
                row["first_name"]?.ToString() ?? "",
                row["last_name"]?.ToString() ?? ""
            );
        }

        // Build email lookup — BR-8: last-wins dictionary overwrite
        // When a customer has multiple email rows, the last row encountered
        // (ordered by as_of from DataSourcing) determines the value.
        var emailLookup = new Dictionary<int, string>();
        if (emails != null)
        {
            foreach (var row in emails.Rows)
            {
                var custId = Convert.ToInt32(row["customer_id"]);
                emailLookup[custId] = row["email_address"]?.ToString() ?? "";
            }
        }

        // Build phone lookup — BR-8: last-wins dictionary overwrite
        var phoneLookup = new Dictionary<int, string>();
        if (phones != null)
        {
            foreach (var row in phones.Rows)
            {
                var custId = Convert.ToInt32(row["customer_id"]);
                phoneLookup[custId] = row["phone_number"]?.ToString() ?? "";
            }
        }

        // BR-1: Find customers with MARKETING_EMAIL opt-in
        // BR-4: On weekends (targetDate != maxDate), only process preference rows
        //        matching the fallback Friday date. On weekdays, process ALL rows
        //        in the effective date range.
        var marketingOptIn = new HashSet<int>();
        foreach (var row in prefs.Rows)
        {
            if (targetDate != maxDate)
            {
                // W2: Weekend — filter to only Friday's preference data
                var rowDate = (DateOnly)row["as_of"];
                if (rowDate != targetDate) continue;
            }

            var custId = Convert.ToInt32(row["customer_id"]);
            var prefType = row["preference_type"]?.ToString() ?? "";
            var optedIn = Convert.ToBoolean(row["opted_in"]);

            if (optedIn && prefType == "MARKETING_EMAIL")
                marketingOptIn.Add(custId);
        }

        // BR-2: Customer must exist in all three lookups (customer, email, phone)
        var outputRows = new List<Row>();
        foreach (var custId in marketingOptIn)
        {
            if (!customerLookup.ContainsKey(custId)) continue;
            if (!emailLookup.ContainsKey(custId)) continue;
            if (!phoneLookup.ContainsKey(custId)) continue;

            var (firstName, lastName) = customerLookup[custId];

            // BR-5: as_of set to targetDate (may be Friday fallback on weekends)
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

        // W9: Output uses Overwrite mode — prior days' data is lost on each run
        sharedState["output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
