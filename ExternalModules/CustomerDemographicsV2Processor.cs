using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CustomerDemographicsV2Processor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "first_name", "last_name", "birthdate", "age",
            "age_bracket", "primary_phone", "primary_email", "as_of"
        };

        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;

        if (customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        var phoneNumbers = sharedState.ContainsKey("phone_numbers") ? sharedState["phone_numbers"] as DataFrame : null;
        var emailAddresses = sharedState.ContainsKey("email_addresses") ? sharedState["email_addresses"] as DataFrame : null;

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

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = customerId,
                ["first_name"] = firstName,
                ["last_name"] = lastName,
                ["birthdate"] = custRow["birthdate"],
                ["age"] = age,
                ["age_bracket"] = ageBracket,
                ["primary_phone"] = primaryPhone,
                ["primary_email"] = primaryEmail,
                ["as_of"] = custRow["as_of"]
            }));
        }

        var df = new DataFrame(outputRows, outputColumns);
        DscWriterUtil.Write("customer_demographics", true, df);
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
