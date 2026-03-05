using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 replacement for ExecutiveDashboardBuilder.
/// Produces a vertical table of 9 key business metrics (metric_name, metric_value, ifw_effective_date).
/// Guard clause returns empty DataFrame when customers, accounts, or loan_accounts are empty.
///
/// Anti-patterns eliminated:
///   AP1 — branches and segments DataSourcing removed from V2 config (never referenced by V1 logic)
///   AP4 — Unused columns removed from all DataSourcing configs (txn_type, customer_id, etc.)
///   AP6 — V1's foreach loops replaced with LINQ .Sum() (set-based)
///
/// Wrinkles preserved:
///   W5 — Banker's rounding: Math.Round with MidpointRounding.ToEven (explicit, matches V1 default)
///   W9 — Overwrite mode: V1 uses Overwrite, prior days' data is lost on each run
/// </summary>
public class ExecutiveDashboardV2Processor : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "metric_name", "metric_value", "ifw_effective_date"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var customers = sharedState.TryGetValue("customers", out var custVal)
            ? custVal as DataFrame
            : null;
        var accounts = sharedState.TryGetValue("accounts", out var acctVal)
            ? acctVal as DataFrame
            : null;
        var transactions = sharedState.TryGetValue("transactions", out var txnVal)
            ? txnVal as DataFrame
            : null;
        var loanAccounts = sharedState.TryGetValue("loan_accounts", out var loanVal)
            ? loanVal as DataFrame
            : null;
        var branchVisits = sharedState.TryGetValue("branch_visits", out var bvVal)
            ? bvVal as DataFrame
            : null;

        // BR-1: Guard clause — if customers, accounts, or loan_accounts is null/empty,
        // return empty DataFrame with correct schema. This fires on weekends when
        // the datalake has no data for these tables.
        if (customers == null || customers.Count == 0 ||
            accounts == null || accounts.Count == 0 ||
            loanAccounts == null || loanAccounts.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // BR-3: ifw_effective_date from first customer row, fallback to first transaction row.
        object? asOf = customers.Rows[0]["ifw_effective_date"];
        if (asOf == null && transactions != null && transactions.Count > 0)
        {
            asOf = transactions.Rows[0]["ifw_effective_date"];
        }

        // BR-4: Compute 9 metrics in fixed order.
        // AP6 fix: LINQ .Sum() replaces V1's foreach loops for set-based accumulation.

        // 1. total_customers — row count, not distinct
        var totalCustomers = (decimal)customers.Count;

        // 2. total_accounts — row count, not distinct
        var totalAccounts = (decimal)accounts.Count;

        // 3. total_balance — sum ALL account balances, no filter
        var totalBalance = accounts.Rows.Sum(r => Convert.ToDecimal(r["current_balance"]));

        // 4. total_transactions — count of transaction rows
        var totalTransactions = (decimal)(transactions?.Count ?? 0);

        // 5. total_txn_amount — sum of transaction amounts
        var totalTxnAmount = transactions?.Rows.Sum(r => Convert.ToDecimal(r["amount"])) ?? 0m;

        // 6. avg_txn_amount — total_txn_amount / total_transactions, 0 if no transactions
        var avgTxnAmount = totalTransactions > 0 ? totalTxnAmount / totalTransactions : 0m;

        // 7. total_loans — count of loan_accounts rows
        var totalLoans = (decimal)loanAccounts.Count;

        // 8. total_loan_balance — sum of loan balances
        var totalLoanBalance = loanAccounts.Rows.Sum(r => Convert.ToDecimal(r["current_balance"]));

        // 9. total_branch_visits — count of branch_visits rows, 0 if null
        var totalBranchVisits = (decimal)(branchVisits?.Count ?? 0);

        // W5: Banker's rounding — V1 uses default MidpointRounding.ToEven.
        // Replicated explicitly for clarity. All 9 values rounded to 2 decimal places.
        var metrics = new List<(string name, decimal value)>
        {
            ("total_customers", Math.Round(totalCustomers, 2, MidpointRounding.ToEven)),
            ("total_accounts", Math.Round(totalAccounts, 2, MidpointRounding.ToEven)),
            ("total_balance", Math.Round(totalBalance, 2, MidpointRounding.ToEven)),
            ("total_transactions", Math.Round(totalTransactions, 2, MidpointRounding.ToEven)),
            ("total_txn_amount", Math.Round(totalTxnAmount, 2, MidpointRounding.ToEven)),
            ("avg_txn_amount", Math.Round(avgTxnAmount, 2, MidpointRounding.ToEven)),
            ("total_loans", Math.Round(totalLoans, 2, MidpointRounding.ToEven)),
            ("total_loan_balance", Math.Round(totalLoanBalance, 2, MidpointRounding.ToEven)),
            ("total_branch_visits", Math.Round(totalBranchVisits, 2, MidpointRounding.ToEven))
        };

        // Build 9-row vertical output DataFrame
        var outputRows = metrics
            .Select(m => new Row(new Dictionary<string, object?>
            {
                ["metric_name"] = m.name,
                ["metric_value"] = m.value,
                ["ifw_effective_date"] = asOf
            }))
            .ToList();

        sharedState["output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
