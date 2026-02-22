using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 of LoanRiskAssessment: joins loan accounts with credit scores to compute risk tiers.
/// Uses External module for empty-DataFrame guard (framework Transformation does
/// not register empty DataFrames as SQLite tables, causing SQL to fail on weekends
/// when datalake.loan_accounts and datalake.credit_scores have no data).
/// </summary>
public class LoanRiskAssessmentV2 : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "loan_id", "customer_id", "loan_type", "current_balance", "interest_rate",
        "loan_status", "avg_credit_score", "risk_tier", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var loanAccounts = sharedState.ContainsKey("loan_accounts")
            ? sharedState["loan_accounts"] as DataFrame
            : null;

        if (loanAccounts == null || loanAccounts.Count == 0)
        {
            sharedState["loan_risk_result"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        return new Transformation("loan_risk_result", @"
            SELECT la.loan_id, la.customer_id, la.loan_type, la.current_balance,
                   la.interest_rate, la.loan_status,
                   ROUND(avg_scores.avg_score, 2) AS avg_credit_score,
                   CASE
                     WHEN avg_scores.avg_score IS NULL THEN 'Unknown'
                     WHEN avg_scores.avg_score >= 750 THEN 'Low Risk'
                     WHEN avg_scores.avg_score >= 650 THEN 'Medium Risk'
                     WHEN avg_scores.avg_score >= 550 THEN 'High Risk'
                     ELSE 'Very High Risk'
                   END AS risk_tier,
                   la.as_of
            FROM loan_accounts la
            LEFT JOIN (
                SELECT customer_id, as_of, AVG(score) AS avg_score
                FROM credit_scores
                GROUP BY customer_id, as_of
            ) avg_scores ON la.customer_id = avg_scores.customer_id AND la.as_of = avg_scores.as_of
        ").Execute(sharedState);
    }
}
