using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class AccountSnapshotBuilder : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "account_id", "customer_id", "account_type", "account_status",
            "current_balance", "as_of"
        };

        var accounts = sharedState.ContainsKey("accounts") ? sharedState["accounts"] as DataFrame : null;

        if (accounts == null || accounts.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        var outputRows = new List<Row>();
        foreach (var acctRow in accounts.Rows)
        {
            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["account_id"] = acctRow["account_id"],
                ["customer_id"] = acctRow["customer_id"],
                ["account_type"] = acctRow["account_type"],
                ["account_status"] = acctRow["account_status"],
                ["current_balance"] = acctRow["current_balance"],
                ["as_of"] = acctRow["as_of"]
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
