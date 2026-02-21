using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class ExecutiveDashboardBuilder : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "metric_name", "metric_value", "as_of"
        };

        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;
        var accounts = sharedState.ContainsKey("accounts") ? sharedState["accounts"] as DataFrame : null;
        var transactions = sharedState.ContainsKey("transactions") ? sharedState["transactions"] as DataFrame : null;
        var loanAccounts = sharedState.ContainsKey("loan_accounts") ? sharedState["loan_accounts"] as DataFrame : null;
        var branchVisits = sharedState.ContainsKey("branch_visits") ? sharedState["branch_visits"] as DataFrame : null;

        // Weekend guard on customers, accounts, or loan_accounts empty
        if (customers == null || customers.Count == 0 ||
            accounts == null || accounts.Count == 0 ||
            loanAccounts == null || loanAccounts.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Get as_of from first customer row (or first transaction row as fallback)
        object? asOf = customers.Rows[0]["as_of"];
        if (asOf == null && transactions != null && transactions.Count > 0)
        {
            asOf = transactions.Rows[0]["as_of"];
        }

        // 1. total_customers
        var totalCustomers = (decimal)customers.Count;

        // 2. total_accounts
        var totalAccounts = (decimal)accounts.Count;

        // 3. total_balance = sum of all account current_balance
        var totalBalance = 0m;
        foreach (var row in accounts.Rows)
        {
            totalBalance += Convert.ToDecimal(row["current_balance"]);
        }

        // 4. total_transactions
        var totalTransactions = 0m;
        var totalTxnAmount = 0m;
        if (transactions != null)
        {
            totalTransactions = transactions.Count;
            foreach (var row in transactions.Rows)
            {
                totalTxnAmount += Convert.ToDecimal(row["amount"]);
            }
        }

        // 6. avg_txn_amount
        var avgTxnAmount = totalTransactions > 0 ? totalTxnAmount / totalTransactions : 0m;

        // 7. total_loans
        var totalLoans = (decimal)loanAccounts.Count;

        // 8. total_loan_balance
        var totalLoanBalance = 0m;
        foreach (var row in loanAccounts.Rows)
        {
            totalLoanBalance += Convert.ToDecimal(row["current_balance"]);
        }

        // 9. total_branch_visits
        var totalBranchVisits = 0m;
        if (branchVisits != null)
        {
            totalBranchVisits = branchVisits.Count;
        }

        // Build metric rows
        var metrics = new List<(string name, decimal value)>
        {
            ("total_customers", Math.Round(totalCustomers, 2)),
            ("total_accounts", Math.Round(totalAccounts, 2)),
            ("total_balance", Math.Round(totalBalance, 2)),
            ("total_transactions", Math.Round(totalTransactions, 2)),
            ("total_txn_amount", Math.Round(totalTxnAmount, 2)),
            ("avg_txn_amount", Math.Round(avgTxnAmount, 2)),
            ("total_loans", Math.Round(totalLoans, 2)),
            ("total_loan_balance", Math.Round(totalLoanBalance, 2)),
            ("total_branch_visits", Math.Round(totalBranchVisits, 2))
        };

        var outputRows = new List<Row>();
        foreach (var (name, value) in metrics)
        {
            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["metric_name"] = name,
                ["metric_value"] = value,
                ["as_of"] = asOf
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
