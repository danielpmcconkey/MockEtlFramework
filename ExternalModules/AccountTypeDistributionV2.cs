using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 of AccountTypeDistribution: computes account type counts and percentages.
/// Uses External module for empty-DataFrame guard (framework Transformation does
/// not register empty DataFrames as SQLite tables, causing SQL to fail on weekends
/// when datalake.accounts has no data).
/// </summary>
public class AccountTypeDistributionV2 : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "account_type", "account_count", "total_accounts", "percentage", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var accounts = sharedState.ContainsKey("accounts")
            ? sharedState["accounts"] as DataFrame
            : null;

        if (accounts == null || accounts.Count == 0)
        {
            sharedState["distribution_result"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        return new Transformation("distribution_result", @"
            SELECT account_type, COUNT(*) AS account_count,
                   (SELECT COUNT(*) FROM accounts) AS total_accounts,
                   CAST(COUNT(*) AS REAL) / (SELECT COUNT(*) FROM accounts) * 100.0 AS percentage,
                   as_of
            FROM accounts
            GROUP BY account_type, as_of
        ").Execute(sharedState);
    }
}
