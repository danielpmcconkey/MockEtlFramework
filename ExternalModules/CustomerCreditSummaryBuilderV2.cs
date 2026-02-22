using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 of CustomerCreditSummary: per-customer financial summary combining avg credit score,
/// total loan/account balances, and counts.
/// Eliminates AP-1 (unused segments), AP-4 (unused columns in accounts/credit_scores/loans),
/// AP-6 (manual foreach loops replaced with LINQ).
/// Retains External module for empty-DataFrame guard (all four inputs empty on weekends).
/// </summary>
public class CustomerCreditSummaryBuilderV2 : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "customer_id", "first_name", "last_name", "avg_credit_score",
        "total_loan_balance", "total_account_balance", "loan_count",
        "account_count", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var customers = sharedState.ContainsKey("customers")
            ? sharedState["customers"] as DataFrame : null;
        var accounts = sharedState.ContainsKey("accounts")
            ? sharedState["accounts"] as DataFrame : null;
        var creditScores = sharedState.ContainsKey("credit_scores")
            ? sharedState["credit_scores"] as DataFrame : null;
        var loanAccounts = sharedState.ContainsKey("loan_accounts")
            ? sharedState["loan_accounts"] as DataFrame : null;

        // Empty guard: original returns empty when ANY input is empty
        if (customers == null || customers.Count == 0 ||
            accounts == null || accounts.Count == 0 ||
            creditScores == null || creditScores.Count == 0 ||
            loanAccounts == null || loanAccounts.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // Group credit scores by customer -> average (LINQ)
        var scoresByCustomer = creditScores.Rows
            .GroupBy(r => Convert.ToInt32(r["customer_id"]))
            .ToDictionary(
                g => g.Key,
                g => Math.Round(g.Average(r => Convert.ToDecimal(r["score"])), 2)
            );

        // Group loans by customer -> (total balance, count) (LINQ)
        var loansByCustomer = loanAccounts.Rows
            .GroupBy(r => Convert.ToInt32(r["customer_id"]))
            .ToDictionary(
                g => g.Key,
                g => (totalBalance: g.Sum(r => Convert.ToDecimal(r["current_balance"])),
                      count: g.Count())
            );

        // Group accounts by customer -> (total balance, count) (LINQ)
        var accountsByCustomer = accounts.Rows
            .GroupBy(r => Convert.ToInt32(r["customer_id"]))
            .ToDictionary(
                g => g.Key,
                g => (totalBalance: g.Sum(r => Convert.ToDecimal(r["current_balance"])),
                      count: g.Count())
            );

        // Build one output row per customer
        var outputRows = new List<Row>();
        foreach (var custRow in customers.Rows)
        {
            var customerId = Convert.ToInt32(custRow["id"]);
            var firstName = custRow["first_name"]?.ToString() ?? "";
            var lastName = custRow["last_name"]?.ToString() ?? "";

            // avg_credit_score: NULL if customer has no scores (asymmetric handling - AP-5 documented)
            object? avgCreditScore = scoresByCustomer.TryGetValue(customerId, out var avg)
                ? (object)avg
                : DBNull.Value;

            // Loan totals: default to 0 when no loans
            decimal loanBalance = 0m;
            int loanCount = 0;
            if (loansByCustomer.TryGetValue(customerId, out var loans))
            {
                loanBalance = loans.totalBalance;
                loanCount = loans.count;
            }

            // Account totals: default to 0 when no accounts
            decimal acctBalance = 0m;
            int acctCount = 0;
            if (accountsByCustomer.TryGetValue(customerId, out var accts))
            {
                acctBalance = accts.totalBalance;
                acctCount = accts.count;
            }

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = customerId,
                ["first_name"] = firstName,
                ["last_name"] = lastName,
                ["avg_credit_score"] = avgCreditScore,
                ["total_loan_balance"] = loanBalance,
                ["total_account_balance"] = acctBalance,
                ["loan_count"] = loanCount,
                ["account_count"] = acctCount,
                ["as_of"] = custRow["as_of"]
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
