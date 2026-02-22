using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 of AccountStatusSummary: aggregates accounts by type and status.
/// Uses External module for empty-DataFrame guard (framework Transformation does
/// not register empty DataFrames as SQLite tables, causing SQL to fail on weekends
/// when datalake.accounts has no data).
/// </summary>
public class AccountStatusSummaryV2 : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "account_type", "account_status", "account_count", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var accounts = sharedState.ContainsKey("accounts")
            ? sharedState["accounts"] as DataFrame
            : null;

        if (accounts == null || accounts.Count == 0)
        {
            sharedState["summary_result"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        return new Transformation("summary_result", @"
            SELECT account_type, account_status, COUNT(*) AS account_count, as_of
            FROM accounts
            GROUP BY account_type, account_status, as_of
        ").Execute(sharedState);
    }
}
