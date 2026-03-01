using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class DebitCreditRatioCalculator : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "account_id", "customer_id", "debit_count", "credit_count",
            "debit_credit_ratio", "debit_amount", "credit_amount", "amount_ratio", "as_of"
        };

        var transactions = sharedState.ContainsKey("transactions") ? sharedState["transactions"] as DataFrame : null;
        var accounts = sharedState.ContainsKey("accounts") ? sharedState["accounts"] as DataFrame : null;

        if (transactions == null || transactions.Count == 0 || accounts == null || accounts.Count == 0)
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

        // Aggregate debit/credit counts and amounts per account
        var stats = new Dictionary<int, (int debitCount, int creditCount, double debitAmount, double creditAmount, object? asOf)>();
        foreach (var row in transactions.Rows)
        {
            var accountId = Convert.ToInt32(row["account_id"]);
            var txnType = row["txn_type"]?.ToString() ?? "";
            // W6: Use double arithmetic (epsilon errors)
            double amount = Convert.ToDouble(row["amount"]);

            if (!stats.ContainsKey(accountId))
                stats[accountId] = (0, 0, 0.0, 0.0, row["as_of"]);

            var current = stats[accountId];
            if (txnType == "Debit")
                stats[accountId] = (current.debitCount + 1, current.creditCount, current.debitAmount + amount, current.creditAmount, current.asOf);
            else if (txnType == "Credit")
                stats[accountId] = (current.debitCount, current.creditCount + 1, current.debitAmount, current.creditAmount + amount, current.asOf);
        }

        var outputRows = new List<Row>();
        foreach (var kvp in stats)
        {
            var accountId = kvp.Key;
            var (debitCount, creditCount, debitAmount, creditAmount, asOf) = kvp.Value;
            var customerId = accountToCustomer.GetValueOrDefault(accountId, 0);

            // W4: Integer division — debit_count / credit_count (both int) → truncates to 0
            int debitCreditRatio = creditCount > 0 ? debitCount / creditCount : 0;

            // W6: Double arithmetic for amount ratio (epsilon errors)
            double amountRatio = creditAmount > 0.0 ? debitAmount / creditAmount : 0.0;

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["account_id"] = accountId,
                ["customer_id"] = customerId,
                ["debit_count"] = debitCount,
                ["credit_count"] = creditCount,
                ["debit_credit_ratio"] = debitCreditRatio,
                ["debit_amount"] = debitAmount,
                ["credit_amount"] = creditAmount,
                ["amount_ratio"] = amountRatio,
                ["as_of"] = asOf
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
