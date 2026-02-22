using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 of CustomerTransactionActivity: aggregates transaction counts/amounts per customer.
/// Uses External module for empty-DataFrame guard (framework Transformation does
/// not register empty DataFrames as SQLite tables, causing SQL to fail on weekends
/// when datalake.accounts has no data).
/// Note: transactions has data on all days, but the INNER JOIN with accounts means
/// that on weekends (when accounts is empty/unregistered) the SQL would fail.
/// </summary>
public class CustomerTransactionActivityV2 : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "customer_id", "as_of", "transaction_count", "total_amount", "debit_count", "credit_count"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var accounts = sharedState.ContainsKey("accounts")
            ? sharedState["accounts"] as DataFrame
            : null;

        if (accounts == null || accounts.Count == 0)
        {
            // On weekends, accounts is empty so the JOIN would produce no rows anyway
            sharedState["activity_output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        return new Transformation("activity_output", @"
            SELECT a.customer_id, t.as_of,
                   COUNT(*) AS transaction_count,
                   SUM(t.amount) AS total_amount,
                   SUM(CASE WHEN t.txn_type = 'Debit' THEN 1 ELSE 0 END) AS debit_count,
                   SUM(CASE WHEN t.txn_type = 'Credit' THEN 1 ELSE 0 END) AS credit_count
            FROM transactions t
            JOIN accounts a ON t.account_id = a.account_id AND t.as_of = a.as_of
            GROUP BY a.customer_id, t.as_of
        ").Execute(sharedState);
    }
}
