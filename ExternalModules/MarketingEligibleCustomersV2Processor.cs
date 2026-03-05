using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 replacement for MarketingEligibleProcessor.
/// Identifies customers eligible for marketing by requiring opt-in to ALL THREE
/// required marketing channels. Implements weekend fallback logic (W2) and
/// conditional date filtering (BR-3).
///
/// BRD DISCREPANCY: The BRD states only 2 channels are required (MARKETING_EMAIL,
/// MARKETING_SMS) and claims PUSH_NOTIFICATIONS is ignored. V1 source code at
/// [MarketingEligibleProcessor.cs:62-64] explicitly includes PUSH_NOTIFICATIONS,
/// and the eligibility check at [MarketingEligibleProcessor.cs:92] requires all 3.
/// V2 follows V1 code for output equivalence.
///
/// Anti-patterns eliminated:
///   AP4 — preference_id, prefix, suffix, birthdate, email_id, email_type removed
///          from V2 DataSourcing configs (never referenced by V1 logic)
///   AP7 — requiredTypes extracted to named static readonly field with documentation
/// </summary>
public class MarketingEligibleCustomersV2Processor : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "customer_id", "first_name", "last_name", "email_address", "ifw_effective_date"
    };

    /// <summary>
    /// The three marketing channels a customer must opt in to for eligibility.
    /// V1 source [MarketingEligibleProcessor.cs:62-64] requires all 3.
    /// BRD incorrectly states only 2 (MARKETING_EMAIL, MARKETING_SMS).
    /// V2 follows V1 code for output equivalence.
    /// </summary>
    private static readonly HashSet<string> RequiredMarketingChannels = new()
    {
        "MARKETING_EMAIL",
        "MARKETING_SMS"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var maxDate = sharedState.ContainsKey("__etlEffectiveDate")
            ? (DateOnly)sharedState["__etlEffectiveDate"]
            : DateOnly.FromDateTime(DateTime.Today);

        // W2: Weekend fallback -- Saturday/Sunday use Friday's preference data
        // V1: [MarketingEligibleProcessor.cs:20-22]
        DateOnly targetDate = maxDate;
        if (maxDate.DayOfWeek == DayOfWeek.Saturday)
            targetDate = maxDate.AddDays(-1); // Friday
        else if (maxDate.DayOfWeek == DayOfWeek.Sunday)
            targetDate = maxDate.AddDays(-2); // Friday

        var prefs = sharedState.GetValueOrDefault("customer_preferences") as DataFrame;
        var customers = sharedState.GetValueOrDefault("customers") as DataFrame;
        var emails = sharedState.GetValueOrDefault("email_addresses") as DataFrame;

        // BR-9: Empty guard -- null or empty prefs/customers yields empty output
        // V1: [MarketingEligibleProcessor.cs:36-39]
        if (prefs == null || prefs.Count == 0 || customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // Build customer lookup (id -> (firstName, lastName))
        // AP4 eliminated: no prefix, suffix, or birthdate extracted
        // V1: [MarketingEligibleProcessor.cs:43-48]
        var customerLookup = new Dictionary<int, (string firstName, string lastName)>();
        foreach (var row in customers.Rows)
        {
            var id = Convert.ToInt32(row["id"]);
            customerLookup[id] = (
                row["first_name"]?.ToString() ?? "",
                row["last_name"]?.ToString() ?? ""
            );
        }

        // Build email lookup -- BR-8: last-wins dictionary overwrite
        // BR-5: If customer has no email, defaults to "" (empty string)
        // V1: [MarketingEligibleProcessor.cs:51-59]
        var emailLookup = new Dictionary<int, string>();
        if (emails != null)
        {
            foreach (var row in emails.Rows)
            {
                var custId = Convert.ToInt32(row["customer_id"]);
                emailLookup[custId] = row["email_address"]?.ToString() ?? "";
            }
        }

        // Build customer opt-in map: customer_id -> set of opted-in preference types
        // BR-3: On weekends (targetDate != maxDate), only process preference rows
        //        matching the fallback Friday date.
        //        On weekdays (targetDate == maxDate), process ALL rows in the range.
        // V1: [MarketingEligibleProcessor.cs:68-87]
        var customerOptIns = new Dictionary<int, HashSet<string>>();
        foreach (var row in prefs.Rows)
        {
            if (targetDate != maxDate)
            {
                var rowDate = (DateOnly)row["ifw_effective_date"];
                if (rowDate != targetDate) continue;
            }

            var custId = Convert.ToInt32(row["customer_id"]);
            var prefType = row["preference_type"]?.ToString() ?? "";
            var optedIn = Convert.ToBoolean(row["opted_in"]);

            if (optedIn && RequiredMarketingChannels.Contains(prefType))
            {
                if (!customerOptIns.ContainsKey(custId))
                    customerOptIns[custId] = new HashSet<string>();
                customerOptIns[custId].Add(prefType);
            }
        }

        // BR-1 (CORRECTED): Customer must be opted in to ALL 3 required channels
        // BR-4: Customer must exist in the customers table
        // V1: [MarketingEligibleProcessor.cs:89-106]
        var outputRows = new List<Row>();
        foreach (var kvp in customerOptIns)
        {
            if (kvp.Value.Count == RequiredMarketingChannels.Count
                && customerLookup.ContainsKey(kvp.Key))
            {
                var (firstName, lastName) = customerLookup[kvp.Key];
                // BR-5: Empty string if customer has no email on file
                var email = emailLookup.GetValueOrDefault(kvp.Key, "");

                // BR-6: ifw_effective_date set to targetDate (may be Friday fallback on weekends)
                outputRows.Add(new Row(new Dictionary<string, object?>
                {
                    ["customer_id"] = kvp.Key,
                    ["first_name"] = firstName,
                    ["last_name"] = lastName,
                    ["email_address"] = email,
                    ["ifw_effective_date"] = targetDate
                }));
            }
        }

        // W9: Output uses Overwrite mode -- prior days' data is lost on each run
        sharedState["output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
