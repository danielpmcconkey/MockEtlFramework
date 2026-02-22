using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CustomerFullProfileV2Processor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "first_name", "last_name", "age", "age_bracket",
            "primary_phone", "primary_email", "segments", "as_of"
        };

        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;

        if (customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        var phoneNumbers = sharedState.ContainsKey("phone_numbers") ? sharedState["phone_numbers"] as DataFrame : null;
        var emailAddresses = sharedState.ContainsKey("email_addresses") ? sharedState["email_addresses"] as DataFrame : null;
        var customersSegments = sharedState.ContainsKey("customers_segments") ? sharedState["customers_segments"] as DataFrame : null;
        var segments = sharedState.ContainsKey("segments") ? sharedState["segments"] as DataFrame : null;

        // Build customer_id -> first phone lookup
        var phoneByCustomer = new Dictionary<int, string>();
        if (phoneNumbers != null)
        {
            foreach (var phoneRow in phoneNumbers.Rows)
            {
                var custId = Convert.ToInt32(phoneRow["customer_id"]);
                if (!phoneByCustomer.ContainsKey(custId))
                {
                    phoneByCustomer[custId] = phoneRow["phone_number"]?.ToString() ?? "";
                }
            }
        }

        // Build customer_id -> first email lookup
        var emailByCustomer = new Dictionary<int, string>();
        if (emailAddresses != null)
        {
            foreach (var emailRow in emailAddresses.Rows)
            {
                var custId = Convert.ToInt32(emailRow["customer_id"]);
                if (!emailByCustomer.ContainsKey(custId))
                {
                    emailByCustomer[custId] = emailRow["email_address"]?.ToString() ?? "";
                }
            }
        }

        // Build segment_id -> segment_name lookup
        var segmentNames = new Dictionary<int, string>();
        if (segments != null)
        {
            foreach (var segRow in segments.Rows)
            {
                var segId = Convert.ToInt32(segRow["segment_id"]);
                segmentNames[segId] = segRow["segment_name"]?.ToString() ?? "";
            }
        }

        // Build customer_id -> list of segment_ids
        var customerSegmentIds = new Dictionary<int, List<int>>();
        if (customersSegments != null)
        {
            foreach (var csRow in customersSegments.Rows)
            {
                var custId = Convert.ToInt32(csRow["customer_id"]);
                var segId = Convert.ToInt32(csRow["segment_id"]);
                if (!customerSegmentIds.ContainsKey(custId))
                {
                    customerSegmentIds[custId] = new List<int>();
                }
                customerSegmentIds[custId].Add(segId);
            }
        }

        var outputRows = new List<Row>();
        foreach (var custRow in customers.Rows)
        {
            var customerId = Convert.ToInt32(custRow["id"]);
            var firstName = custRow["first_name"]?.ToString() ?? "";
            var lastName = custRow["last_name"]?.ToString() ?? "";

            var birthdate = ToDateOnly(custRow["birthdate"]);
            var asOfDate = ToDateOnly(custRow["as_of"]);

            var age = asOfDate.Year - birthdate.Year;
            if (birthdate > asOfDate.AddYears(-age)) age--;

            var ageBracket = age switch
            {
                < 26 => "18-25",
                <= 35 => "26-35",
                <= 45 => "36-45",
                <= 55 => "46-55",
                <= 65 => "56-65",
                _ => "65+"
            };

            var primaryPhone = phoneByCustomer.GetValueOrDefault(customerId, "");
            var primaryEmail = emailByCustomer.GetValueOrDefault(customerId, "");

            // Build comma-separated segment names
            var segList = customerSegmentIds.GetValueOrDefault(customerId, new List<int>());
            var segNamesList = segList
                .Where(segId => segmentNames.ContainsKey(segId))
                .Select(segId => segmentNames[segId])
                .ToList();
            var segmentsStr = string.Join(",", segNamesList);

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = customerId,
                ["first_name"] = firstName,
                ["last_name"] = lastName,
                ["age"] = age,
                ["age_bracket"] = ageBracket,
                ["primary_phone"] = primaryPhone,
                ["primary_email"] = primaryEmail,
                ["segments"] = segmentsStr,
                ["as_of"] = custRow["as_of"]
            }));
        }

        var df = new DataFrame(outputRows, outputColumns);
        DscWriterUtil.Write("customer_full_profile", true, df);
        sharedState["output"] = df;
        return sharedState;
    }

    private static DateOnly ToDateOnly(object? val) => val switch
    {
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        string s => DateOnly.Parse(s),
        _ => DateOnly.Parse(val?.ToString() ?? "")
    };
}
