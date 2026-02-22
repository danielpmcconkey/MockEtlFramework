using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CustomerTransactionActivityV2Processor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "as_of", "transaction_count", "total_amount", "debit_count", "credit_count"
        };

        var transactions = sharedState.ContainsKey("transactions") ? sharedState["transactions"] as DataFrame : null;
        var accounts = sharedState.ContainsKey("accounts") ? sharedState["accounts"] as DataFrame : null;

        // Weekend guard on accounts empty
        if (accounts == null || accounts.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        if (transactions == null || transactions.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Build account_id -> customer_id lookup
        var accountToCustomer = new Dictionary<int, int>();
        foreach (var acctRow in accounts.Rows)
        {
            var accountId = Convert.ToInt32(acctRow["account_id"]);
            var customerId = Convert.ToInt32(acctRow["customer_id"]);
            accountToCustomer[accountId] = customerId;
        }

        // Group transactions by customer_id
        var customerTxns = new Dictionary<int, (int count, decimal totalAmount, int debits, int credits)>();
        foreach (var txnRow in transactions.Rows)
        {
            var accountId = Convert.ToInt32(txnRow["account_id"]);
            var customerId = accountToCustomer.GetValueOrDefault(accountId, 0);
            if (customerId == 0) continue;

            var amount = Convert.ToDecimal(txnRow["amount"]);
            var txnType = txnRow["txn_type"]?.ToString() ?? "";

            if (!customerTxns.ContainsKey(customerId))
                customerTxns[customerId] = (0, 0m, 0, 0);

            var current = customerTxns[customerId];
            var isDebit = txnType == "Debit" ? 1 : 0;
            var isCredit = txnType == "Credit" ? 1 : 0;
            customerTxns[customerId] = (current.count + 1, current.totalAmount + amount, current.debits + isDebit, current.credits + isCredit);
        }

        // Get as_of from first transaction row
        var asOf = transactions.Rows[0]["as_of"];

        // Build output rows
        var outputRows = new List<Row>();
        foreach (var kvp in customerTxns)
        {
            var (count, totalAmount, debits, credits) = kvp.Value;

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = kvp.Key,
                ["as_of"] = asOf,
                ["transaction_count"] = count,
                ["total_amount"] = totalAmount,
                ["debit_count"] = debits,
                ["credit_count"] = credits
            }));
        }

        var df = new DataFrame(outputRows, outputColumns);
        DscWriterUtil.Write("customer_transaction_activity", false, df);
        sharedState["output"] = df;
        return sharedState;
    }
}
