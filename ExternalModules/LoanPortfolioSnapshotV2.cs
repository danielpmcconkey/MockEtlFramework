using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 of LoanPortfolioSnapshot: pass-through of loan account records.
/// Uses External module for empty-DataFrame guard (framework Transformation does
/// not register empty DataFrames as SQLite tables, causing SQL to fail on weekends
/// when datalake.loan_accounts has no data).
/// </summary>
public class LoanPortfolioSnapshotV2 : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "loan_id", "customer_id", "loan_type", "original_amount", "current_balance",
        "interest_rate", "loan_status", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var loanAccounts = sharedState.ContainsKey("loan_accounts")
            ? sharedState["loan_accounts"] as DataFrame
            : null;

        if (loanAccounts == null || loanAccounts.Count == 0)
        {
            sharedState["loan_snapshot_result"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        return new Transformation("loan_snapshot_result", @"
            SELECT loan_id, customer_id, loan_type, original_amount, current_balance,
                   interest_rate, loan_status, as_of
            FROM loan_accounts
        ").Execute(sharedState);
    }
}
