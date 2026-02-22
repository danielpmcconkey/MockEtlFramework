using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 of AccountBalanceSnapshot: pass-through of account balance records.
/// Uses External module for empty-DataFrame guard (framework Transformation does
/// not register empty DataFrames as SQLite tables, causing SQL to fail on weekends
/// when datalake.accounts has no data).
/// </summary>
public class AccountBalanceSnapshotV2 : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "account_id", "customer_id", "account_type", "account_status", "current_balance", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var accounts = sharedState.ContainsKey("accounts")
            ? sharedState["accounts"] as DataFrame
            : null;

        if (accounts == null || accounts.Count == 0)
        {
            sharedState["snapshot_result"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        return new Transformation("snapshot_result", @"
            SELECT account_id, customer_id, account_type, account_status, current_balance, as_of
            FROM accounts
        ").Execute(sharedState);
    }
}
