using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 replacement for InterAccountTransferDetector.
/// Greedy first-match-wins assignment only. DataSourcing handles data retrieval,
/// Transformation produces candidate debit-credit pairs via SQL self-join,
/// and this module applies the sequential matching algorithm.
///
/// Anti-patterns eliminated:
///   AP1 — accounts DataSourcing removed from V2 config (never referenced by V1 matching logic)
///   AP3 — demoted from Tier 3 to Tier 2: debit/credit separation and candidate pair generation
///          moved to SQL Transformation; External handles only greedy matching assignment
///   AP4 — eliminated via AP1 (entire accounts source removed)
///   AP6 — partially eliminated: V1's O(n^2) nested loop replaced with single-pass over
///          pre-joined candidates; SQL JOIN handles the cross-product
///
/// Wrinkles reproduced:
///   W9 — Overwrite writeMode preserved (only last day's output survives in multi-day gap-fill)
/// </summary>
public class InterAccountTransfersV2Processor : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "debit_txn_id", "credit_txn_id", "from_account_id", "to_account_id",
        "amount", "txn_timestamp", "ifw_effective_date"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var candidates = sharedState.TryGetValue("candidates", out var val)
            ? val as DataFrame
            : null;

        // BR-7: Empty output on null/empty candidates
        if (candidates == null || candidates.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // Greedy first-match-wins assignment (BR-3, BR-4, BR-8).
        // Candidates are pre-sorted by (debit_txn_id, credit_txn_id) from SQL ORDER BY.
        // Each debit matches at most one credit (first eligible in order).
        // Each credit matches at most one debit (first to claim it).
        var matchedDebits = new HashSet<int>();
        var matchedCredits = new HashSet<int>();
        var outputRows = new List<Row>();

        foreach (var row in candidates.Rows)
        {
            var debitId = Convert.ToInt32(row["debit_txn_id"]);
            var creditId = Convert.ToInt32(row["credit_txn_id"]);

            // BR-4: This debit already matched — skip
            if (matchedDebits.Contains(debitId)) continue;

            // BR-3: This credit already matched — skip
            if (matchedCredits.Contains(creditId)) continue;

            matchedDebits.Add(debitId);
            matchedCredits.Add(creditId);

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["debit_txn_id"] = debitId,
                ["credit_txn_id"] = creditId,
                ["from_account_id"] = Convert.ToInt32(row["debit_account_id"]),
                ["to_account_id"] = Convert.ToInt32(row["credit_account_id"]),
                ["amount"] = Convert.ToDecimal(row["amount"]),
                ["txn_timestamp"] = row["debit_timestamp"]?.ToString() ?? "",  // string, from debit row
                ["ifw_effective_date"] = row["debit_ifw_effective_date"]  // from debit row (BR-5)
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
