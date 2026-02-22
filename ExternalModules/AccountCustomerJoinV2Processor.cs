using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class AccountCustomerJoinV2Processor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "account_id", "customer_id", "first_name", "last_name",
            "account_type", "account_status", "current_balance", "as_of"
        };

        var accounts = sharedState.ContainsKey("accounts") ? sharedState["accounts"] as DataFrame : null;
        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;

        if (accounts == null || accounts.Count == 0 || customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Build customer_id -> (first_name, last_name) lookup
        var customerNames = new Dictionary<int, (string firstName, string lastName)>();
        foreach (var custRow in customers.Rows)
        {
            var custId = Convert.ToInt32(custRow["id"]);
            var firstName = custRow["first_name"]?.ToString() ?? "";
            var lastName = custRow["last_name"]?.ToString() ?? "";
            customerNames[custId] = (firstName, lastName);
        }

        // Iterate accounts, join with customer name
        var outputRows = new List<Row>();
        foreach (var acctRow in accounts.Rows)
        {
            var customerId = Convert.ToInt32(acctRow["customer_id"]);
            var (firstName, lastName) = customerNames.GetValueOrDefault(customerId, ("", ""));

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["account_id"] = acctRow["account_id"],
                ["customer_id"] = acctRow["customer_id"],
                ["first_name"] = firstName,
                ["last_name"] = lastName,
                ["account_type"] = acctRow["account_type"],
                ["account_status"] = acctRow["account_status"],
                ["current_balance"] = acctRow["current_balance"],
                ["as_of"] = acctRow["as_of"]
            }));
        }

        var df = new DataFrame(outputRows, outputColumns);
        DscWriterUtil.Write("account_customer_join", true, df);
        sharedState["output"] = df;
        return sharedState;
    }
}
