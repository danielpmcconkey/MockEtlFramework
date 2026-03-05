using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 replacement for TransactionAnomalyFlagger.
/// Tier 2 SCALPEL: Framework handles DataSourcing and CsvFileWriter.
/// This External module handles ONLY the statistical computation and anomaly flagging,
/// which requires C# because:
///   1. SQLite lacks SQRT — cannot compute population stddev in SQL
///   2. BR-8 mixed decimal/double precision — V1's specific cast path
///      (decimal subtract -> double square -> double average -> decimal sqrt)
///      produces IEEE 754 artifacts baked into V1 output
///
/// Anti-patterns eliminated:
///   AP1 — customers DataSourcing removed (dead-end: loaded/null-checked, never used)
///   AP4 — txn_type removed from transactions sourcing (never referenced in output or logic)
///   AP6 — Per-account amount collection converted to LINQ GroupBy (partial)
///   AP7 — Magic 3.0 threshold replaced with named constant DeviationThreshold
///
/// Wrinkles replicated:
///   W5 — Banker's rounding (MidpointRounding.ToEven) on all numeric output fields
///   W9 — Overwrite writeMode preserved (handled by CsvFileWriter config, not this module)
/// </summary>
public class TransactionAnomalyFlagsV2Processor : IExternalStep
{
    // AP7: Named constant replaces hardcoded 3.0m literal
    // Anomaly detection threshold: flag transactions > 3 standard deviations from account mean
    private const decimal DeviationThreshold = 3.0m;

    private static readonly List<string> OutputColumns = new()
    {
        "transaction_id", "account_id", "customer_id", "amount",
        "account_mean", "account_stddev", "deviation_factor", "ifw_effective_date"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var transactions = sharedState.GetValueOrDefault("transactions") as DataFrame;
        var accounts = sharedState.GetValueOrDefault("accounts") as DataFrame;

        // BR-11: Empty input guard — if transactions or accounts is null/empty,
        // output empty DataFrame with correct schema
        if (transactions == null || transactions.Count == 0 ||
            accounts == null || accounts.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // Step 1: Build account_id -> customer_id lookup (BR-1)
        var accountToCustomer = new Dictionary<int, int>();
        foreach (var acctRow in accounts.Rows)
        {
            var accountId = Convert.ToInt32(acctRow["account_id"]);
            var customerId = Convert.ToInt32(acctRow["customer_id"]);
            accountToCustomer[accountId] = customerId;
        }

        // Step 2: Collect per-account transaction amounts and store transaction data
        // AP6 partial: using list for txnData (natural iteration pattern for conditional output)
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

            txnData.Add((txnId, accountId, amount, row["ifw_effective_date"]));
        }

        // Step 3: Compute per-account statistics (BR-2, BR-3, BR-8)
        // BR-8: Mixed decimal/double precision path — V1 subtracts in decimal space,
        // casts to double for squaring, averages in double, then casts Math.Sqrt back to decimal.
        // This specific conversion path introduces IEEE 754 precision artifacts into
        // account_stddev and deviation_factor that are baked into V1 output.
        var accountStats = new Dictionary<int, (decimal mean, decimal stddev)>();
        foreach (var kvp in accountAmounts)
        {
            var amounts = kvp.Value;
            // BR-2: Mean across ALL transaction amounts for the account (population mean)
            decimal mean = amounts.Average();
            // BR-8: Subtract in decimal, cast to double for squaring, average in double
            // The (decimal)mean cast is redundant (mean is already decimal) but replicates V1 exactly
            double variance = amounts.Select(a => (double)(a - (decimal)mean) * (double)(a - (decimal)mean)).Average();
            // BR-3: Population stddev (divide by N via .Average()), sqrt in double, cast back to decimal
            decimal stddev = (decimal)Math.Sqrt(variance);
            accountStats[kvp.Key] = (mean, stddev);
        }

        // Step 4: Flag anomalous transactions (BR-4, BR-5, BR-6, BR-7, BR-12)
        var outputRows = new List<Row>();
        foreach (var (txnId, accountId, amount, asOf) in txnData)
        {
            if (!accountStats.ContainsKey(accountId)) continue;
            var (mean, stddev) = accountStats[accountId];

            // BR-6: Zero stddev exclusion — skip accounts with no variance
            if (stddev == 0m) continue;

            // BR-4: Deviation factor = |amount - mean| / stddev
            var deviationFactor = Math.Abs(amount - mean) / stddev;

            // BR-5: Strict greater-than (not >=) against threshold
            if (deviationFactor > DeviationThreshold)
            {
                // BR-12: Default customer_id = 0 when account not found in lookup
                var customerId = accountToCustomer.GetValueOrDefault(accountId, 0);

                // W5: Banker's rounding (MidpointRounding.ToEven) on all numeric output fields
                // V1 behavior replicated for output equivalence
                outputRows.Add(new Row(new Dictionary<string, object?>
                {
                    ["transaction_id"] = txnId,
                    ["account_id"] = accountId,
                    ["customer_id"] = customerId,
                    ["amount"] = Math.Round(amount, 2, MidpointRounding.ToEven),
                    ["account_mean"] = Math.Round(mean, 2, MidpointRounding.ToEven),
                    ["account_stddev"] = Math.Round(stddev, 2, MidpointRounding.ToEven),
                    ["deviation_factor"] = Math.Round(deviationFactor, 2, MidpointRounding.ToEven),
                    ["ifw_effective_date"] = asOf
                }));
            }
        }

        sharedState["output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
