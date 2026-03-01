using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class DailyBalanceMovementCalculator : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "account_id", "customer_id", "debit_total", "credit_total", "net_movement", "as_of"
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

        // W6: Use double arithmetic instead of decimal (epsilon errors)
        var stats = new Dictionary<int, (double debitTotal, double creditTotal, object? asOf)>();
        foreach (var row in transactions.Rows)
        {
            var accountId = Convert.ToInt32(row["account_id"]);
            var txnType = row["txn_type"]?.ToString() ?? "";
            double amount = Convert.ToDouble(row["amount"]);

            if (!stats.ContainsKey(accountId))
                stats[accountId] = (0.0, 0.0, row["as_of"]);

            var current = stats[accountId];
            if (txnType == "Debit")
                stats[accountId] = (current.debitTotal + amount, current.creditTotal, current.asOf);
            else if (txnType == "Credit")
                stats[accountId] = (current.debitTotal, current.creditTotal + amount, current.asOf);
        }

        var outputRows = new List<Row>();
        foreach (var kvp in stats)
        {
            var accountId = kvp.Key;
            var (debitTotal, creditTotal, asOf) = kvp.Value;
            var customerId = accountToCustomer.GetValueOrDefault(accountId, 0);

            // W6: net_movement computed with double arithmetic (epsilon errors accumulate)
            double netMovement = creditTotal - debitTotal;

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["account_id"] = accountId,
                ["customer_id"] = customerId,
                ["debit_total"] = debitTotal,
                ["credit_total"] = creditTotal,
                ["net_movement"] = netMovement,
                ["as_of"] = asOf
            }));
        }

        // W9: writeMode in JSON is "Overwrite" (wrong — should be Append, loses prior days)
        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
