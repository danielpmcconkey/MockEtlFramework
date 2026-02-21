using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class LoanRiskCalculator : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "loan_id", "customer_id", "loan_type", "current_balance",
            "interest_rate", "loan_status", "avg_credit_score", "risk_tier", "as_of"
        };

        var loanAccounts = sharedState.ContainsKey("loan_accounts") ? sharedState["loan_accounts"] as DataFrame : null;
        var creditScores = sharedState.ContainsKey("credit_scores") ? sharedState["credit_scores"] as DataFrame : null;

        if (loanAccounts == null || loanAccounts.Count == 0 || creditScores == null || creditScores.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Group credit scores by customer_id to compute avg score per customer
        var avgScoreByCustomer = new Dictionary<int, decimal>();
        var scoreListByCustomer = new Dictionary<int, List<decimal>>();
        foreach (var row in creditScores.Rows)
        {
            var custId = Convert.ToInt32(row["customer_id"]);
            var score = Convert.ToDecimal(row["score"]);

            if (!scoreListByCustomer.ContainsKey(custId))
                scoreListByCustomer[custId] = new List<decimal>();

            scoreListByCustomer[custId].Add(score);
        }

        foreach (var kvp in scoreListByCustomer)
        {
            avgScoreByCustomer[kvp.Key] = kvp.Value.Average();
        }

        // For each loan, look up customer's avg credit score and compute risk tier
        var outputRows = new List<Row>();
        foreach (var loanRow in loanAccounts.Rows)
        {
            var customerId = Convert.ToInt32(loanRow["customer_id"]);

            object? avgCreditScore;
            string riskTier;

            if (avgScoreByCustomer.ContainsKey(customerId))
            {
                var avgScore = avgScoreByCustomer[customerId];
                avgCreditScore = avgScore;

                riskTier = avgScore switch
                {
                    >= 750 => "Low Risk",
                    >= 650 => "Medium Risk",
                    >= 550 => "High Risk",
                    _ => "Very High Risk"
                };
            }
            else
            {
                avgCreditScore = DBNull.Value;
                riskTier = "Unknown";
            }

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["loan_id"] = loanRow["loan_id"],
                ["customer_id"] = loanRow["customer_id"],
                ["loan_type"] = loanRow["loan_type"],
                ["current_balance"] = loanRow["current_balance"],
                ["interest_rate"] = loanRow["interest_rate"],
                ["loan_status"] = loanRow["loan_status"],
                ["avg_credit_score"] = avgCreditScore,
                ["risk_tier"] = riskTier,
                ["as_of"] = loanRow["as_of"]
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
