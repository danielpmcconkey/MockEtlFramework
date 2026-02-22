using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 of AccountCustomerJoin: joins accounts with customers.
/// Uses External module for empty-DataFrame guard (framework Transformation does
/// not register empty DataFrames as SQLite tables, causing SQL to fail on weekends
/// when datalake.accounts and datalake.customers have no data).
/// </summary>
public class AccountCustomerJoinV2 : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "account_id", "customer_id", "first_name", "last_name", "account_type", "account_status", "current_balance", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var accounts = sharedState.ContainsKey("accounts")
            ? sharedState["accounts"] as DataFrame
            : null;

        if (accounts == null || accounts.Count == 0)
        {
            sharedState["join_result"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        return new Transformation("join_result", @"
            SELECT a.account_id, a.customer_id,
                   COALESCE(c.first_name, '') AS first_name,
                   COALESCE(c.last_name, '') AS last_name,
                   a.account_type, a.account_status, a.current_balance, a.as_of
            FROM accounts a
            LEFT JOIN customers c ON a.customer_id = c.id AND a.as_of = c.as_of
        ").Execute(sharedState);
    }
}
