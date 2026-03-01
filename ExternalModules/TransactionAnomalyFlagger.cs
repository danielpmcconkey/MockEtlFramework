using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class TransactionAnomalyFlagger : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "transaction_id", "account_id", "customer_id", "amount",
            "account_mean", "account_stddev", "deviation_factor", "as_of"
        };

        var transactions = sharedState.ContainsKey("transactions") ? sharedState["transactions"] as DataFrame : null;
        var accounts = sharedState.ContainsKey("accounts") ? sharedState["accounts"] as DataFrame : null;
        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;

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

        // AP6: Row-by-row iteration to collect per-account amounts
        var accountAmounts = new Dictionary<int, List<decimal>>();
        var txnData = new List<(int txnId, int accountId, decimal amount, object? asOf)>();
        foreach (var row in transactions.Rows)
        {
            var accountId = Convert.ToInt32(row["account_id"]);
            var txnId = Convert.ToInt32(row["transaction_id"]);
            var amount = Convert.ToDecimal(row["amount"]);

            if (!accountAmounts.ContainsKey(accountId))
                accountAmounts[accountId] = new List<decimal>();
            accountAmounts[accountId].Add(amount);

            txnData.Add((txnId, accountId, amount, row["as_of"]));
        }

        // Compute per-account mean and stddev
        var accountStats = new Dictionary<int, (decimal mean, decimal stddev)>();
        foreach (var kvp in accountAmounts)
        {
            var amounts = kvp.Value;
            var mean = amounts.Average();
            var variance = amounts.Select(a => (double)(a - (decimal)mean) * (double)(a - (decimal)mean)).Average();
            var stddev = (decimal)Math.Sqrt(variance);
            accountStats[kvp.Key] = ((decimal)mean, stddev);
        }

        // AP6: Row-by-row iteration to flag anomalies
        var outputRows = new List<Row>();
        foreach (var (txnId, accountId, amount, asOf) in txnData)
        {
            if (!accountStats.ContainsKey(accountId)) continue;
            var (mean, stddev) = accountStats[accountId];

            if (stddev == 0m) continue;

            var deviationFactor = Math.Abs(amount - mean) / stddev;

            // AP7: Magic value — hardcoded 3.0 threshold
            if (deviationFactor > 3.0m)
            {
                var customerId = accountToCustomer.GetValueOrDefault(accountId, 0);

                // W5: Banker's rounding
                outputRows.Add(new Row(new Dictionary<string, object?>
                {
                    ["transaction_id"] = txnId,
                    ["account_id"] = accountId,
                    ["customer_id"] = customerId,
                    ["amount"] = Math.Round(amount, 2, MidpointRounding.ToEven),
                    ["account_mean"] = Math.Round(mean, 2, MidpointRounding.ToEven),
                    ["account_stddev"] = Math.Round(stddev, 2, MidpointRounding.ToEven),
                    ["deviation_factor"] = Math.Round(deviationFactor, 2, MidpointRounding.ToEven),
                    ["as_of"] = asOf
                }));
            }
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
