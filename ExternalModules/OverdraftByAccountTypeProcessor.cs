using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class OverdraftByAccountTypeProcessor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "account_type", "account_count", "overdraft_count", "overdraft_rate", "as_of"
        };

        var overdraftEvents = sharedState.ContainsKey("overdraft_events")
            ? sharedState["overdraft_events"] as DataFrame
            : null;
        var accounts = sharedState.ContainsKey("accounts")
            ? sharedState["accounts"] as DataFrame
            : null;

        if (overdraftEvents == null || overdraftEvents.Count == 0 || accounts == null || accounts.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        var asOf = overdraftEvents.Rows[0]["as_of"];

        // Build account_id -> account_type lookup (AP6: row-by-row iteration)
        var accountTypeLookup = new Dictionary<int, string>();
        foreach (var acct in accounts.Rows)
        {
            var accountId = Convert.ToInt32(acct["account_id"]);
            var accountType = acct["account_type"]?.ToString() ?? "";
            accountTypeLookup[accountId] = accountType;
        }

        // Count accounts per type
        var accountCounts = new Dictionary<string, int>();
        foreach (var acct in accounts.Rows)
        {
            var accountType = acct["account_type"]?.ToString() ?? "";
            if (!accountCounts.ContainsKey(accountType))
                accountCounts[accountType] = 0;
            accountCounts[accountType]++;
        }

        // AP6: Row-by-row iteration to count overdrafts per account_type
        var overdraftCounts = new Dictionary<string, int>();
        foreach (var evt in overdraftEvents.Rows)
        {
            var accountId = Convert.ToInt32(evt["account_id"]);
            var accountType = accountTypeLookup.ContainsKey(accountId)
                ? accountTypeLookup[accountId]
                : "Unknown";

            if (!overdraftCounts.ContainsKey(accountType))
                overdraftCounts[accountType] = 0;
            overdraftCounts[accountType]++;
        }

        var outputRows = new List<Row>();
        foreach (var kvp in accountCounts)
        {
            var accountType = kvp.Key;
            int accountCount = kvp.Value;
            int odCount = overdraftCounts.ContainsKey(accountType) ? overdraftCounts[accountType] : 0;

            // W4: Integer division — overdraft_count / account_count both int → truncates to 0
            decimal overdraftRate = (decimal)(odCount / accountCount);

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["account_type"] = accountType,
                ["account_count"] = accountCount,
                ["overdraft_count"] = odCount,
                ["overdraft_rate"] = overdraftRate,
                ["as_of"] = asOf
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
