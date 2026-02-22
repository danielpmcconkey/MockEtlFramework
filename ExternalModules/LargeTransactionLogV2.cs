using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 of LargeTransactionLog: filters transactions > $500 and joins with accounts/customers.
/// Uses External module for empty-DataFrame guard (framework Transformation does
/// not register empty DataFrames as SQLite tables, causing SQL to fail on weekends
/// when datalake.accounts and datalake.customers have no data).
/// Note: transactions table has data on all days, but accounts/customers are weekday-only.
/// The LEFT JOINs mean the SQL still references accounts/customers tables which won't be registered.
/// </summary>
public class LargeTransactionLogV2 : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "transaction_id", "account_id", "customer_id", "first_name", "last_name",
        "txn_type", "amount", "description", "txn_timestamp", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var accounts = sharedState.ContainsKey("accounts")
            ? sharedState["accounts"] as DataFrame
            : null;

        if (accounts == null || accounts.Count == 0)
        {
            // On weekends, accounts table is empty so it won't be registered in SQLite.
            // Transactions exist but the SQL references accounts/customers tables.
            // We need to handle this by running a simpler query with just transactions,
            // providing default values for the joined columns.
            var transactions = sharedState.ContainsKey("transactions")
                ? sharedState["transactions"] as DataFrame
                : null;

            if (transactions == null || transactions.Count == 0)
            {
                sharedState["large_txn_result"] = new DataFrame(new List<Row>(), OutputColumns);
                return sharedState;
            }

            // Register only transactions and run query without joins
            return new Transformation("large_txn_result", @"
                SELECT t.transaction_id, t.account_id,
                       0 AS customer_id,
                       '' AS first_name,
                       '' AS last_name,
                       t.txn_type, t.amount, t.description,
                       REPLACE(t.txn_timestamp, 'T', ' ') AS txn_timestamp, t.as_of
                FROM transactions t
                WHERE t.amount > 500
            ").Execute(sharedState);
        }

        return new Transformation("large_txn_result", @"
            SELECT t.transaction_id, t.account_id,
                   COALESCE(a.customer_id, 0) AS customer_id,
                   COALESCE(c.first_name, '') AS first_name,
                   COALESCE(c.last_name, '') AS last_name,
                   t.txn_type, t.amount, t.description,
                   REPLACE(t.txn_timestamp, 'T', ' ') AS txn_timestamp, t.as_of
            FROM transactions t
            LEFT JOIN accounts a ON t.account_id = a.account_id AND t.as_of = a.as_of
            LEFT JOIN customers c ON a.customer_id = c.id AND a.as_of = c.as_of
            WHERE t.amount > 500
        ").Execute(sharedState);
    }
}
