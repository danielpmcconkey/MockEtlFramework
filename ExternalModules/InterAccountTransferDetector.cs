using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class InterAccountTransferDetector : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "debit_txn_id", "credit_txn_id", "from_account_id", "to_account_id",
            "amount", "txn_timestamp", "as_of"
        };

        var transactions = sharedState.ContainsKey("transactions") ? sharedState["transactions"] as DataFrame : null;
        var accounts = sharedState.ContainsKey("accounts") ? sharedState["accounts"] as DataFrame : null;

        if (transactions == null || transactions.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Collect debits and credits
        var debits = new List<(int txnId, int accountId, decimal amount, string timestamp, object? asOf)>();
        var credits = new List<(int txnId, int accountId, decimal amount, string timestamp, object? asOf)>();

        // AP6: Row-by-row iteration to separate debits and credits
        foreach (var row in transactions.Rows)
        {
            var txnId = Convert.ToInt32(row["transaction_id"]);
            var accountId = Convert.ToInt32(row["account_id"]);
            var amount = Convert.ToDecimal(row["amount"]);
            var timestamp = row["txn_timestamp"]?.ToString() ?? "";
            var txnType = row["txn_type"]?.ToString() ?? "";

            if (txnType == "Debit")
                debits.Add((txnId, accountId, amount, timestamp, row["as_of"]));
            else if (txnType == "Credit")
                credits.Add((txnId, accountId, amount, timestamp, row["as_of"]));
        }

        // AP6: O(n^2) nested loop matching where SQL self-join would work
        var matchedCredits = new HashSet<int>();
        var outputRows = new List<Row>();

        foreach (var debit in debits)
        {
            foreach (var credit in credits)
            {
                if (matchedCredits.Contains(credit.txnId)) continue;

                // Match: same amount, same timestamp, different accounts
                if (debit.amount == credit.amount &&
                    debit.timestamp == credit.timestamp &&
                    debit.accountId != credit.accountId)
                {
                    matchedCredits.Add(credit.txnId);

                    outputRows.Add(new Row(new Dictionary<string, object?>
                    {
                        ["debit_txn_id"] = debit.txnId,
                        ["credit_txn_id"] = credit.txnId,
                        ["from_account_id"] = debit.accountId,
                        ["to_account_id"] = credit.accountId,
                        ["amount"] = debit.amount,
                        ["txn_timestamp"] = debit.timestamp,
                        ["as_of"] = debit.asOf
                    }));

                    break;
                }
            }
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
