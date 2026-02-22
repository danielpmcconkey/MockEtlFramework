using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 of ExecutiveDashboard: computes aggregate metrics across customers, accounts,
/// transactions, loans, and branch visits.
/// Uses External module for empty-DataFrame guard (framework Transformation does
/// not register empty DataFrames as SQLite tables, causing SQL to fail on weekends
/// when datalake.customers, datalake.accounts, and datalake.loan_accounts have no data).
/// The original SQL has WHERE EXISTS checks for customers, accounts, and loan_accounts,
/// producing no output when any is empty -- so returning empty DataFrame is correct.
/// </summary>
public class ExecutiveDashboardV2 : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "metric_name", "metric_value", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var customers = sharedState.ContainsKey("customers")
            ? sharedState["customers"] as DataFrame
            : null;
        var accounts = sharedState.ContainsKey("accounts")
            ? sharedState["accounts"] as DataFrame
            : null;
        var loanAccounts = sharedState.ContainsKey("loan_accounts")
            ? sharedState["loan_accounts"] as DataFrame
            : null;

        // The original SQL requires all three tables to have data via EXISTS checks
        if (customers == null || customers.Count == 0
            || accounts == null || accounts.Count == 0
            || loanAccounts == null || loanAccounts.Count == 0)
        {
            sharedState["dashboard_output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        return new Transformation("dashboard_output", @"
            SELECT metric_name, metric_value, as_of FROM (
                SELECT 'total_customers' AS metric_name,
                       ROUND(CAST(COUNT(*) AS REAL), 2) AS metric_value,
                       (SELECT as_of FROM customers LIMIT 1) AS as_of
                FROM customers
                UNION ALL
                SELECT 'total_accounts',
                       ROUND(CAST(COUNT(*) AS REAL), 2),
                       (SELECT as_of FROM customers LIMIT 1)
                FROM accounts
                UNION ALL
                SELECT 'total_balance',
                       ROUND(SUM(current_balance), 2),
                       (SELECT as_of FROM customers LIMIT 1)
                FROM accounts
                UNION ALL
                SELECT 'total_transactions',
                       ROUND(CAST(COUNT(*) AS REAL), 2),
                       (SELECT as_of FROM customers LIMIT 1)
                FROM transactions
                UNION ALL
                SELECT 'total_txn_amount',
                       ROUND(SUM(amount), 2),
                       (SELECT as_of FROM customers LIMIT 1)
                FROM transactions
                UNION ALL
                SELECT 'avg_txn_amount',
                       CASE WHEN COUNT(*) > 0 THEN ROUND(SUM(amount) / COUNT(*), 2) ELSE 0 END,
                       (SELECT as_of FROM customers LIMIT 1)
                FROM transactions
                UNION ALL
                SELECT 'total_loans',
                       ROUND(CAST(COUNT(*) AS REAL), 2),
                       (SELECT as_of FROM customers LIMIT 1)
                FROM loan_accounts
                UNION ALL
                SELECT 'total_loan_balance',
                       ROUND(SUM(current_balance), 2),
                       (SELECT as_of FROM customers LIMIT 1)
                FROM loan_accounts
                UNION ALL
                SELECT 'total_branch_visits',
                       ROUND(CAST(COUNT(*) AS REAL), 2),
                       (SELECT as_of FROM customers LIMIT 1)
                FROM branch_visits
            )
            WHERE EXISTS (SELECT 1 FROM customers)
            AND EXISTS (SELECT 1 FROM accounts)
            AND EXISTS (SELECT 1 FROM loan_accounts)
        ").Execute(sharedState);
    }
}
