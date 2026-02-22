using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CreditScoreAverageV2Processor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "first_name", "last_name", "avg_score",
            "equifax_score", "transunion_score", "experian_score", "as_of"
        };

        var creditScores = sharedState.ContainsKey("credit_scores") ? sharedState["credit_scores"] as DataFrame : null;
        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;

        if (creditScores == null || creditScores.Count == 0 || customers == null || customers.Count == 0)
        {
            var emptyDf = new DataFrame(new List<Row>(), outputColumns);
            DscWriterUtil.Write("credit_score_average", true, emptyDf);
            sharedState["output"] = emptyDf;
            return sharedState;
        }

        // Group credit scores by customer_id
        var scoresByCustomer = new Dictionary<int, List<(string bureau, decimal score)>>();
        foreach (var row in creditScores.Rows)
        {
            var custId = Convert.ToInt32(row["customer_id"]);
            var bureau = row["bureau"]?.ToString() ?? "";
            var score = Convert.ToDecimal(row["score"]);

            if (!scoresByCustomer.ContainsKey(custId))
                scoresByCustomer[custId] = new List<(string bureau, decimal score)>();

            scoresByCustomer[custId].Add((bureau, score));
        }

        // Build customer_id -> (first_name, last_name, as_of) lookup
        var customerNames = new Dictionary<int, (string firstName, string lastName, object? asOf)>();
        foreach (var custRow in customers.Rows)
        {
            var custId = Convert.ToInt32(custRow["id"]);
            var firstName = custRow["first_name"]?.ToString() ?? "";
            var lastName = custRow["last_name"]?.ToString() ?? "";
            customerNames[custId] = (firstName, lastName, custRow["as_of"]);
        }

        // For each customer with credit scores, compute averages and individual bureau scores
        var outputRows = new List<Row>();
        foreach (var kvp in scoresByCustomer)
        {
            var customerId = kvp.Key;
            var scores = kvp.Value;

            if (!customerNames.ContainsKey(customerId))
                continue;

            var (firstName, lastName, asOf) = customerNames[customerId];

            var avgScore = Math.Round(scores.Average(s => s.score), 2);

            // Look up individual bureau scores
            object? equifaxScore = DBNull.Value;
            object? transunionScore = DBNull.Value;
            object? experianScore = DBNull.Value;

            foreach (var (bureau, score) in scores)
            {
                switch (bureau.ToLower())
                {
                    case "equifax":
                        equifaxScore = score;
                        break;
                    case "transunion":
                        transunionScore = score;
                        break;
                    case "experian":
                        experianScore = score;
                        break;
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

        var df = new DataFrame(outputRows, outputColumns);
        DscWriterUtil.Write("credit_score_average", true, df);
        sharedState["output"] = df;
        return sharedState;
    }
}
