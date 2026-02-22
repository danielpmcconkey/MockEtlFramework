using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CustomerAccountSummaryV2V2Processor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "first_name", "last_name",
            "account_count", "total_balance", "active_balance", "as_of"
        };

        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;
        var accounts = sharedState.ContainsKey("accounts") ? sharedState["accounts"] as DataFrame : null;

        // Weekend guard on customers or accounts empty
        if (customers == null || customers.Count == 0 || accounts == null || accounts.Count == 0)
        {
            var emptyDf = new DataFrame(new List<Row>(), outputColumns);
            DscWriterUtil.Write("customer_account_summary_v2", true, emptyDf);
            sharedState["output"] = emptyDf;
            return sharedState;
        }

        // Group accounts by customer_id
        var accountsByCustomer = new Dictionary<int, (int count, decimal totalBalance, decimal activeBalance)>();
        foreach (var acctRow in accounts.Rows)
        {
            var custId = Convert.ToInt32(acctRow["customer_id"]);
            var balance = Convert.ToDecimal(acctRow["current_balance"]);
            var status = acctRow["account_status"]?.ToString() ?? "";

            if (!accountsByCustomer.ContainsKey(custId))
                accountsByCustomer[custId] = (0, 0m, 0m);

            var current = accountsByCustomer[custId];
            var activeAdd = status == "Active" ? balance : 0m;
            accountsByCustomer[custId] = (current.count + 1, current.totalBalance + balance, current.activeBalance + activeAdd);
        }

        // Iterate customers, look up aggregated account data
        var outputRows = new List<Row>();
        foreach (var custRow in customers.Rows)
        {
            var customerId = Convert.ToInt32(custRow["id"]);
            var firstName = custRow["first_name"]?.ToString() ?? "";
            var lastName = custRow["last_name"]?.ToString() ?? "";

            var (accountCount, totalBalance, activeBalance) = accountsByCustomer.GetValueOrDefault(customerId, (0, 0m, 0m));

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = customerId,
                ["first_name"] = firstName,
                ["last_name"] = lastName,
                ["account_count"] = accountCount,
                ["total_balance"] = totalBalance,
                ["active_balance"] = activeBalance,
                ["as_of"] = custRow["as_of"]
            }));
        }

        var df = new DataFrame(outputRows, outputColumns);
        DscWriterUtil.Write("customer_account_summary_v2", true, df);
        sharedState["output"] = df;
        return sharedState;
    }
}
