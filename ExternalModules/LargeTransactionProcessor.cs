using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class LargeTransactionProcessor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "transaction_id", "account_id", "customer_id", "first_name", "last_name",
            "txn_type", "amount", "description", "txn_timestamp", "as_of"
        };

        var accounts = sharedState.ContainsKey("accounts") ? sharedState["accounts"] as DataFrame : null;
        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;

        if (accounts == null || accounts.Count == 0 || customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        var transactions = sharedState.ContainsKey("transactions") ? sharedState["transactions"] as DataFrame : null;
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

        // Build customer_id -> (first_name, last_name) lookup
        var customerNames = new Dictionary<int, (string firstName, string lastName)>();
        foreach (var custRow in customers.Rows)
        {
            var custId = Convert.ToInt32(custRow["id"]);
            var firstName = custRow["first_name"]?.ToString() ?? "";
            var lastName = custRow["last_name"]?.ToString() ?? "";
            customerNames[custId] = (firstName, lastName);
        }

        // Iterate transactions, filter amount > 500
        var outputRows = new List<Row>();
        foreach (var txnRow in transactions.Rows)
        {
            var amount = Convert.ToDecimal(txnRow["amount"]);
            if (amount > 500)
            {
                var accountId = Convert.ToInt32(txnRow["account_id"]);
                var customerId = accountToCustomer.GetValueOrDefault(accountId, 0);
                var (firstName, lastName) = customerNames.GetValueOrDefault(customerId, ("", ""));

                outputRows.Add(new Row(new Dictionary<string, object?>
                {
                    ["transaction_id"] = txnRow["transaction_id"],
                    ["account_id"] = txnRow["account_id"],
                    ["customer_id"] = customerId,
                    ["first_name"] = firstName,
                    ["last_name"] = lastName,
                    ["txn_type"] = txnRow["txn_type"],
                    ["amount"] = txnRow["amount"],
                    ["description"] = txnRow["description"],
                    ["txn_timestamp"] = txnRow["txn_timestamp"],
                    ["as_of"] = txnRow["as_of"]
                }));
            }
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
