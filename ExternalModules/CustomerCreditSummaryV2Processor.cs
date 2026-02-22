using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CustomerCreditSummaryV2Processor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "first_name", "last_name", "avg_credit_score",
            "total_loan_balance", "total_account_balance", "loan_count",
            "account_count", "as_of"
        };

        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;
        var accounts = sharedState.ContainsKey("accounts") ? sharedState["accounts"] as DataFrame : null;
        var creditScores = sharedState.ContainsKey("credit_scores") ? sharedState["credit_scores"] as DataFrame : null;
        var loanAccounts = sharedState.ContainsKey("loan_accounts") ? sharedState["loan_accounts"] as DataFrame : null;

        if (customers == null || customers.Count == 0 ||
            accounts == null || accounts.Count == 0 ||
            creditScores == null || creditScores.Count == 0 ||
            loanAccounts == null || loanAccounts.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Group credit scores by customer_id
        var scoresByCustomer = new Dictionary<int, List<decimal>>();
        foreach (var row in creditScores.Rows)
        {
            var custId = Convert.ToInt32(row["customer_id"]);
            var score = Convert.ToDecimal(row["score"]);

            if (!scoresByCustomer.ContainsKey(custId))
                scoresByCustomer[custId] = new List<decimal>();

            scoresByCustomer[custId].Add(score);
        }

        // Group loan balances by customer_id
        var loansByCustomer = new Dictionary<int, (decimal totalBalance, int count)>();
        foreach (var row in loanAccounts.Rows)
        {
            var custId = Convert.ToInt32(row["customer_id"]);
            var balance = Convert.ToDecimal(row["current_balance"]);

            if (!loansByCustomer.ContainsKey(custId))
                loansByCustomer[custId] = (0m, 0);

            var current = loansByCustomer[custId];
            loansByCustomer[custId] = (current.totalBalance + balance, current.count + 1);
        }

        // Group account balances by customer_id
        var accountsByCustomer = new Dictionary<int, (decimal totalBalance, int count)>();
        foreach (var row in accounts.Rows)
        {
            var custId = Convert.ToInt32(row["customer_id"]);
            var balance = Convert.ToDecimal(row["current_balance"]);

            if (!accountsByCustomer.ContainsKey(custId))
                accountsByCustomer[custId] = (0m, 0);

            var current = accountsByCustomer[custId];
            accountsByCustomer[custId] = (current.totalBalance + balance, current.count + 1);
        }

        // Build output row per customer
        var outputRows = new List<Row>();
        foreach (var custRow in customers.Rows)
        {
            var customerId = Convert.ToInt32(custRow["id"]);
            var firstName = custRow["first_name"]?.ToString() ?? "";
            var lastName = custRow["last_name"]?.ToString() ?? "";

            // Avg credit score
            object? avgCreditScore;
            if (scoresByCustomer.ContainsKey(customerId))
            {
                avgCreditScore = Math.Round(scoresByCustomer[customerId].Average(), 2);
            }
            else
            {
                avgCreditScore = DBNull.Value;
            }

            // Loan totals
            decimal totalLoanBalance = 0m;
            int loanCount = 0;
            if (loansByCustomer.ContainsKey(customerId))
            {
                var loanData = loansByCustomer[customerId];
                totalLoanBalance = loanData.totalBalance;
                loanCount = loanData.count;
            }

            // Account totals
            decimal totalAccountBalance = 0m;
            int accountCount = 0;
            if (accountsByCustomer.ContainsKey(customerId))
            {
                var acctData = accountsByCustomer[customerId];
                totalAccountBalance = acctData.totalBalance;
                accountCount = acctData.count;
            }

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

        var df = new DataFrame(outputRows, outputColumns);
        DscWriterUtil.Write("customer_credit_summary", true, df);
        sharedState["output"] = df;
        return sharedState;
    }
}
