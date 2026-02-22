using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 of CreditScoreSnapshot: pass-through of credit score records to curated schema.
/// Uses External module only for empty-DataFrame guard (framework Transformation does
/// not register empty DataFrames as SQLite tables, causing SQL to fail on empty input).
/// Eliminates AP-1 (unused branches sourcing), AP-6 (row-by-row copy).
/// </summary>
public class CreditScoreSnapshotV2 : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "credit_score_id", "customer_id", "bureau", "score", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var creditScores = sharedState.ContainsKey("credit_scores")
            ? sharedState["credit_scores"] as DataFrame
            : null;

        if (creditScores == null || creditScores.Count == 0)
        {
            // Weekend/empty input: return empty DataFrame so DataFrameWriter can truncate cleanly
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // Direct pass-through: no row-by-row copy needed
        sharedState["output"] = creditScores;
        return sharedState;
    }
}
