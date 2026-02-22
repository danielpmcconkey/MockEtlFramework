using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 of CreditScoreAverage: computes per-customer average credit score with bureau-level
/// pivot and customer name enrichment.
/// Eliminates AP-1 (unused segments), AP-4 (unused credit_score_id), AP-6 (row-by-row iteration).
/// Uses LINQ-based set operations instead of manual dictionary building.
/// Retains External module for empty-DataFrame guard (framework limitation).
/// </summary>
public class CreditScoreAveragerV2 : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "customer_id", "first_name", "last_name", "avg_score",
        "equifax_score", "transunion_score", "experian_score", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var creditScores = sharedState.ContainsKey("credit_scores")
            ? sharedState["credit_scores"] as DataFrame
            : null;
        var customers = sharedState.ContainsKey("customers")
            ? sharedState["customers"] as DataFrame
            : null;

        // Empty-DataFrame guard: on weekends neither table has data
        if (creditScores == null || creditScores.Count == 0 ||
            customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // Build customer lookup: last row per customer_id wins (matches original behavior)
        var customerLookup = new Dictionary<int, (string firstName, string lastName, object? asOf)>();
        foreach (var row in customers.Rows)
        {
            var id = Convert.ToInt32(row["id"]);
            customerLookup[id] = (
                row["first_name"]?.ToString() ?? "",
                row["last_name"]?.ToString() ?? "",
                row["as_of"]
            );
        }

        // Group scores by customer, compute aggregates using LINQ
        var scoreGroups = creditScores.Rows
            .GroupBy(r => Convert.ToInt32(r["customer_id"]))
            .Where(g => customerLookup.ContainsKey(g.Key)); // INNER JOIN: skip customers not in customers table

        var outputRows = new List<Row>();
        foreach (var group in scoreGroups)
        {
            var customerId = group.Key;
            var (firstName, lastName, asOf) = customerLookup[customerId];

            var scores = group.Select(r => (
                bureau: r["bureau"]?.ToString()?.ToLower() ?? "",
                score: Convert.ToDecimal(r["score"])
            )).ToList();

            // Compute average rounded to 2 decimal places to match curated NUMERIC(6,2)
            var avgScore = Math.Round(scores.Average(s => s.score), 2);

            // Pivot bureau scores: NULL if bureau not present
            object? equifaxScore = DBNull.Value;
            object? transunionScore = DBNull.Value;
            object? experianScore = DBNull.Value;

            foreach (var (bureau, score) in scores)
            {
                switch (bureau)
                {
                    case "equifax": equifaxScore = score; break;
                    case "transunion": transunionScore = score; break;
                    case "experian": experianScore = score; break;
                }
            }

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = customerId,
                ["first_name"] = firstName,
                ["last_name"] = lastName,
                ["avg_score"] = avgScore,
                ["equifax_score"] = equifaxScore,
                ["transunion_score"] = transunionScore,
                ["experian_score"] = experianScore,
                ["as_of"] = asOf
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
