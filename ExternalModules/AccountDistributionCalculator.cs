using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class AccountDistributionCalculator : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "account_type", "account_count", "total_accounts", "percentage", "as_of"
        };

        var accounts = sharedState.ContainsKey("accounts") ? sharedState["accounts"] as DataFrame : null;

        if (accounts == null || accounts.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Get as_of from first account row
        var asOf = accounts.Rows[0]["as_of"];
        var totalAccounts = accounts.Count;

        // Count accounts by type
        var typeCounts = new Dictionary<string, int>();
        foreach (var acctRow in accounts.Rows)
        {
            var accountType = acctRow["account_type"]?.ToString() ?? "";
            if (!typeCounts.ContainsKey(accountType))
                typeCounts[accountType] = 0;
            typeCounts[accountType]++;
        }

        var outputRows = new List<Row>();
        foreach (var kvp in typeCounts)
        {
            var typeCount = kvp.Value;
            var percentage = (double)typeCount / totalAccounts * 100.0;

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["account_type"] = kvp.Key,
                ["account_count"] = typeCount,
                ["total_accounts"] = totalAccounts,
                ["percentage"] = percentage,
                ["as_of"] = asOf
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
