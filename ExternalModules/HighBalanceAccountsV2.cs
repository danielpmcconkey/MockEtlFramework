using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 of HighBalanceAccounts: filters accounts with balance > 10000 and joins with customers.
/// Uses External module for empty-DataFrame guard (framework Transformation does
/// not register empty DataFrames as SQLite tables, causing SQL to fail on weekends
/// when datalake.accounts and datalake.customers have no data).
/// </summary>
public class HighBalanceAccountsV2 : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "account_id", "customer_id", "account_type", "current_balance", "first_name", "last_name", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var accounts = sharedState.ContainsKey("accounts")
            ? sharedState["accounts"] as DataFrame
            : null;

        if (accounts == null || accounts.Count == 0)
        {
            sharedState["high_balance_result"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        return new Transformation("high_balance_result", @"
            SELECT a.account_id, a.customer_id, a.account_type, a.current_balance,
                   COALESCE(c.first_name, '') AS first_name,
                   COALESCE(c.last_name, '') AS last_name, a.as_of
            FROM accounts a
            LEFT JOIN customers c ON a.customer_id = c.id AND a.as_of = c.as_of
            WHERE a.current_balance > 10000
        ").Execute(sharedState);
    }
}
