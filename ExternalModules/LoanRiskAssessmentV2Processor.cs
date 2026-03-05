using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 replacement for LoanRiskCalculator.
/// Minimal Tier 2 External module that performs type-casting operations required
/// for Parquet schema fidelity and handles the empty-input edge case.
/// This module contains NO business logic -- all joining, averaging, and risk
/// tier classification is handled by SQL in the Transformation step.
///
/// Responsibilities:
///   1. Empty-input guard (BR-6): empty output when loan_accounts or credit_scores is null/empty
///   2. Decimal type cast for avg_credit_score (BR-9): double (SQLite) -> decimal (V1 Parquet schema)
///   3. DateOnly reconstruction for ifw_effective_date (BR-8): string (SQLite) -> DateOnly (V1 Parquet schema)
///   4. Decimal restoration for current_balance and interest_rate: double (SQLite) -> decimal
///   5. Int32 restoration for loan_id and customer_id: int64 (SQLite) -> int32
///
/// Anti-patterns eliminated:
///   AP1 — customers and segments DataSourcing removed from V2 config (dead-end sourcing)
///   AP3 — Join, aggregation, and risk tier logic moved to SQL Transformation
///   AP4 — credit_score_id and bureau removed from credit_scores sourcing (never used)
///   AP6 — V1's nested foreach loops replaced with SQL LEFT JOIN + AVG + CASE
///   AP7 — No magic values in External (thresholds are in SQL; this module only casts types)
/// </summary>
public class LoanRiskAssessmentV2Processor : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "loan_id", "customer_id", "loan_type", "current_balance",
        "interest_rate", "loan_status", "avg_credit_score", "risk_tier", "ifw_effective_date"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        // BR-6: Empty-input guard -- replicates V1 compound null/empty check
        // V1: LoanRiskCalculator.cs:19-23
        var loanAccounts = sharedState.GetValueOrDefault("loan_accounts") as DataFrame;
        var creditScores = sharedState.GetValueOrDefault("credit_scores") as DataFrame;

        if (loanAccounts == null || loanAccounts.Count == 0 ||
            creditScores == null || creditScores.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        var sqlOutput = sharedState["sql_output"] as DataFrame;
        if (sqlOutput == null || sqlOutput.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        var outputRows = new List<Row>();
        foreach (var row in sqlOutput.Rows)
        {
            var rawAvg = row["avg_credit_score"];

            // BR-9: Cast avg_credit_score from double (SQLite) to decimal (V1 type)
            // BR-3: Null avg -> DBNull.Value [LoanRiskCalculator.cs:68]
            object? avgCreditScore = (rawAvg == null || rawAvg is DBNull)
                ? DBNull.Value
                : Convert.ToDecimal(rawAvg);

            // BR-8: Reconstruct DateOnly from SQLite text [LoanRiskCalculator.cs:82]
            var asOf = DateOnly.Parse(row["ifw_effective_date"]?.ToString() ?? "");

            // Restore decimal types for monetary fields (SQLite REAL -> decimal)
            var currentBalance = Convert.ToDecimal(row["current_balance"]);
            var interestRate = Convert.ToDecimal(row["interest_rate"]);

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["loan_id"] = Convert.ToInt32(row["loan_id"]),
                ["customer_id"] = Convert.ToInt32(row["customer_id"]),
                ["loan_type"] = row["loan_type"]?.ToString(),
                ["current_balance"] = currentBalance,
                ["interest_rate"] = interestRate,
                ["loan_status"] = row["loan_status"]?.ToString(),
                ["avg_credit_score"] = avgCreditScore,
                ["risk_tier"] = row["risk_tier"]?.ToString(),
                ["ifw_effective_date"] = asOf
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
