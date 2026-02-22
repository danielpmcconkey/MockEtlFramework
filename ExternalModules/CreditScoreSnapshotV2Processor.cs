using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CreditScoreSnapshotV2Processor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "credit_score_id", "customer_id", "bureau", "score", "as_of"
        };

        var creditScores = sharedState.ContainsKey("credit_scores") ? sharedState["credit_scores"] as DataFrame : null;

        if (creditScores == null || creditScores.Count == 0)
        {
            var emptyDf = new DataFrame(new List<Row>(), outputColumns);
            DscWriterUtil.Write("credit_score_snapshot", true, emptyDf);
            sharedState["output"] = emptyDf;
            return sharedState;
        }

        // Pass-through: copy all credit score rows
        var outputRows = new List<Row>();
        foreach (var row in creditScores.Rows)
        {
            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["credit_score_id"] = row["credit_score_id"],
                ["customer_id"] = row["customer_id"],
                ["bureau"] = row["bureau"],
                ["score"] = row["score"],
                ["as_of"] = row["as_of"]
            }));
        }

        var df = new DataFrame(outputRows, outputColumns);
        DscWriterUtil.Write("credit_score_snapshot", true, df);
        sharedState["output"] = df;
        return sharedState;
    }
}
