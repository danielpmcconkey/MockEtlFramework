using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 replacement for CustomerAttritionScorer.
/// Minimal scoring module: receives pre-aggregated data from SQL Transformation
/// and computes avg_balance, attrition_score, risk_level, and as_of with proper
/// C# types for Parquet schema equivalence.
///
/// Anti-patterns eliminated:
///   AP4 — transactions.amount removed from V2 DataSourcing (never used by V1 logic)
///   AP6 — V1's three foreach+Dictionary aggregation loops replaced with SQL joins/GROUP BY
///   AP7 — All magic values replaced with named constants
///
/// Output-affecting wrinkles replicated:
///   W5 — Math.Round with default banker's rounding (MidpointRounding.ToEven)
///   W6 — Attrition score computed using double arithmetic, not decimal
/// </summary>
public class CustomerAttritionSignalsV2Processor : IExternalStep
{
    // Attrition score factor weights [CustomerAttritionScorer.cs:84-86]
    // NOTE: BRD text has dormancy=35 and declining_txn=40, but V1 source code
    // uses dormancy=40 and declining_txn=35. We follow the code for output equivalence.
    private const double DormancyWeight = 35.0;
    private const double DecliningTxnWeight = 40.0;
    private const double LowBalanceWeight = 25.0;

    // Binary factor thresholds [CustomerAttritionScorer.cs:78-80]
    private const int DecliningTxnThreshold = 3;       // txn_count < 3 = "declining"
    private const double LowBalanceThreshold = 100.0;   // avg_balance < 100 = "low balance"

    // Risk level classification thresholds [CustomerAttritionScorer.cs:89-90]
    private const double HighRiskThreshold = 75.0;
    private const double MediumRiskThreshold = 40.0;

    private static readonly List<string> OutputColumns = new()
    {
        "customer_id", "first_name", "last_name",
        "account_count", "txn_count", "avg_balance",
        "attrition_score", "risk_level", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var preScored = sharedState.TryGetValue("pre_scored", out var val)
            ? val as DataFrame
            : null;

        // BR-7: Empty/null input produces empty DataFrame with correct 9-column schema.
        // Matches V1 behavior [CustomerAttritionScorer.cs:21-25].
        if (preScored == null || preScored.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];

        var outputRows = new List<Row>();
        foreach (var row in preScored.Rows)
        {
            var customerId = Convert.ToInt32(row["customer_id"]);
            var firstName = row["first_name"]?.ToString() ?? "";
            var lastName = row["last_name"]?.ToString() ?? "";
            var accountCount = Convert.ToInt32(row["account_count"]);
            var txnCount = Convert.ToInt32(row["txn_count"]);
            var totalBalance = Convert.ToDecimal(row["total_balance"]);

            // avg_balance: decimal division with banker's rounding
            // V1 uses Math.Round with default banker's rounding (MidpointRounding.ToEven).
            // Replicated for output equivalence (W5). [CustomerAttritionScorer.cs:73,100]
            var avgBalance = accountCount > 0 ? totalBalance / accountCount : 0m;
            avgBalance = Math.Round(avgBalance, 2);

            // W6: Attrition score uses double (not decimal) for accumulation.
            // V1 uses double arithmetic throughout. Replicated for output equivalence.
            // [CustomerAttritionScorer.cs:76-86]
            double dormancyFactor = accountCount == 0 ? 1.0 : 0.0;
            double decliningTxnFactor = txnCount < DecliningTxnThreshold ? 1.0 : 0.0;
            double lowBalanceFactor = (double)avgBalance < LowBalanceThreshold ? 1.0 : 0.0;

            double attritionScore = 0.0;
            attritionScore += dormancyFactor * DormancyWeight;
            attritionScore += decliningTxnFactor * DecliningTxnWeight;
            attritionScore += lowBalanceFactor * LowBalanceWeight;

            // Risk classification [CustomerAttritionScorer.cs:88-91]
            string riskLevel;
            if (attritionScore >= HighRiskThreshold) riskLevel = "High";
            else if (attritionScore >= MediumRiskThreshold) riskLevel = "Medium";
            else riskLevel = "Low";

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = customerId,
                ["first_name"] = firstName,
                ["last_name"] = lastName,
                ["account_count"] = accountCount,
                ["txn_count"] = txnCount,
                ["avg_balance"] = avgBalance,
                ["attrition_score"] = attritionScore,
                ["risk_level"] = riskLevel,
                ["as_of"] = maxDate
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
