using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 replacement for CustomerCreditSummaryBuilder.
/// Produces a per-customer credit summary: average credit score (decimal precision),
/// total loan balance, total account balance, and counts of loans and accounts.
///
/// Anti-patterns eliminated:
///   AP1 -- segments DataSourcing removed from V2 config (never referenced by V1 logic)
///   AP4 -- unused columns removed: account_id, account_type, account_status from accounts;
///          credit_score_id, bureau from credit_scores; loan_id, loan_type from loan_accounts
///   AP6 -- V1's foreach + Dictionary replaced with LINQ ToLookup/GroupBy (set-based)
///
/// Output-affecting wrinkles reproduced:
///   W9 -- V1 uses Overwrite writeMode; prior days' data is lost on each run. Reproduced via config.
/// </summary>
public class CustomerCreditSummaryV2Processor : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "customer_id", "first_name", "last_name", "avg_credit_score",
        "total_loan_balance", "total_account_balance", "loan_count",
        "account_count", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;
        var accounts = sharedState.ContainsKey("accounts") ? sharedState["accounts"] as DataFrame : null;
        var creditScores = sharedState.ContainsKey("credit_scores") ? sharedState["credit_scores"] as DataFrame : null;
        var loanAccounts = sharedState.ContainsKey("loan_accounts") ? sharedState["loan_accounts"] as DataFrame : null;

        // BR-1: Compound empty guard -- all four sources must be non-null and non-empty
        if (customers == null || customers.Count == 0 ||
            accounts == null || accounts.Count == 0 ||
            creditScores == null || creditScores.Count == 0 ||
            loanAccounts == null || loanAccounts.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // BR-2: Credit score lookup using decimal precision (AP6 fix: set-based via LINQ)
        var scoresByCustomer = creditScores.Rows
            .ToLookup(row => Convert.ToInt32(row["customer_id"]));

        // BR-3: Loan aggregation -- total balance and count per customer (AP6 fix: set-based via LINQ)
        var loansByCustomer = loanAccounts.Rows
            .GroupBy(row => Convert.ToInt32(row["customer_id"]))
            .ToDictionary(
                g => g.Key,
                g => (totalBalance: g.Sum(r => Convert.ToDecimal(r["current_balance"])),
                      count: g.Count()));

        // BR-4: Account aggregation -- total balance and count per customer (AP6 fix: set-based via LINQ)
        var accountsByCustomer = accounts.Rows
            .GroupBy(row => Convert.ToInt32(row["customer_id"]))
            .ToDictionary(
                g => g.Key,
                g => (totalBalance: g.Sum(r => Convert.ToDecimal(r["current_balance"])),
                      count: g.Count()));

        // BR-8: Customer-driven iteration -- one output row per customer
        var outputRows = new List<Row>();
        foreach (var custRow in customers.Rows)
        {
            var customerId = Convert.ToInt32(custRow["id"]);
            var firstName = custRow["first_name"]?.ToString() ?? "";
            var lastName = custRow["last_name"]?.ToString() ?? "";

            // BR-2: Average credit score with decimal precision. DBNull.Value if no scores.
            // Uses Enumerable.Average() on decimal values -- matches V1's List<decimal>.Average()
            // which yields up to 28-29 significant digits (decimal type).
            object avgCreditScore;
            if (scoresByCustomer.Contains(customerId))
            {
                avgCreditScore = scoresByCustomer[customerId]
                    .Select(r => Convert.ToDecimal(r["score"]))
                    .Average();
            }
            else
            {
                avgCreditScore = DBNull.Value;
            }

            // BR-3, BR-5: Loan totals with defaults (0 balance, 0 count if no loans)
            decimal totalLoanBalance = 0m;
            int loanCount = 0;
            if (loansByCustomer.TryGetValue(customerId, out var loanData))
            {
                totalLoanBalance = loanData.totalBalance;
                loanCount = loanData.count;
            }

            // BR-4, BR-6: Account totals with defaults (0 balance, 0 count if no accounts)
            decimal totalAccountBalance = 0m;
            int accountCount = 0;
            if (accountsByCustomer.TryGetValue(customerId, out var acctData))
            {
                totalAccountBalance = acctData.totalBalance;
                accountCount = acctData.count;
            }

            // BR-7: as_of from customer row (injected by DataSourcing)
            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = customerId,
                ["first_name"] = firstName,
                ["last_name"] = lastName,
                ["avg_credit_score"] = avgCreditScore,
                ["total_loan_balance"] = totalLoanBalance,
                ["total_account_balance"] = totalAccountBalance,
                ["loan_count"] = loanCount,
                ["account_count"] = accountCount,
                ["as_of"] = custRow["as_of"]
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
