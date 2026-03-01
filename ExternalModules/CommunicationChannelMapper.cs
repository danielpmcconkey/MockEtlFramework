using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CommunicationChannelMapper : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "first_name", "last_name", "preferred_channel",
            "email", "phone", "as_of"
        };

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

        if (customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
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

        // Build preference lookup: customer_id -> set of opted-in types
        var prefLookup = new Dictionary<int, HashSet<string>>();
        if (prefs != null)
        {
            foreach (var row in prefs.Rows)
            {
                var custId = Convert.ToInt32(row["customer_id"]);
                var prefType = row["preference_type"]?.ToString() ?? "";
                var optedIn = Convert.ToBoolean(row["opted_in"]);

                if (optedIn)
                {
                    if (!prefLookup.ContainsKey(custId))
                        prefLookup[custId] = new HashSet<string>();
                    prefLookup[custId].Add(prefType);
                }
            }
        }

        var asOf = customers.Rows[0]["as_of"];

        // AP6: Row-by-row iteration through customers
        var outputRows = new List<Row>();
        foreach (var custRow in customers.Rows)
        {
            var custId = Convert.ToInt32(custRow["id"]);
            var firstName = custRow["first_name"]?.ToString() ?? "";
            var lastName = custRow["last_name"]?.ToString() ?? "";

            var custPrefs = prefLookup.GetValueOrDefault(custId, new HashSet<string>());

            // Determine preferred channel
            string preferredChannel;
            if (custPrefs.Contains("MARKETING_EMAIL"))
                preferredChannel = "Email";
            else if (custPrefs.Contains("MARKETING_SMS"))
                preferredChannel = "SMS";
            else if (custPrefs.Contains("PUSH_NOTIFICATIONS"))
                preferredChannel = "Push";
            else
                preferredChannel = "None";

            // AP5: Asymmetric NULL handling — null email → "N/A" but null phone → "" (empty string)
            var email = emailLookup.ContainsKey(custId) ? emailLookup[custId] : "N/A";
            var phone = phoneLookup.ContainsKey(custId) ? phoneLookup[custId] : "";

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = custId,
                ["first_name"] = firstName,
                ["last_name"] = lastName,
                ["preferred_channel"] = preferredChannel,
                ["email"] = email,
                ["phone"] = phone,
                ["as_of"] = asOf
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
