using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 External module for credit_score_average.
/// Tier 2 (Minimal External): upstream DataSourcing + Transformation handles joining, grouping,
/// and conditional aggregation. This module performs ONLY:
///   1. Decimal average computation (SUM/COUNT as C# decimal to match V1 LINQ Average() precision)
///   2. DateOnly reconstruction (SQLite passes ifw_effective_date as TEXT; CsvFileWriter needs DateOnly for V1-matching format)
/// </summary>
public class CreditScoreAverageV2Processor : IExternalStep
{
    /// <summary>Output column names in exact V1 order.</summary>
    private static readonly List<string> OutputColumns = new()
    {
        "customer_id", "first_name", "last_name", "avg_score",
        "equifax_score", "transunion_score", "experian_score", "ifw_effective_date"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var grouped = sharedState.ContainsKey("grouped_scores")
            ? sharedState["grouped_scores"] as DataFrame : null;

        // BR-4: Empty input guard -- if no data came through, produce empty output with correct schema
        if (grouped == null || grouped.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        var outputRows = new List<Row>();

        foreach (var row in grouped.Rows)
        {
            // Decimal division matches V1's LINQ Average() precision.
            // SQLite AVG() would return IEEE 754 double, losing significant digits.
            var scoreSum = Convert.ToDecimal(row["score_sum"]);
            var scoreCount = Convert.ToDecimal(row["score_count"]);
            var avgScore = scoreSum / scoreCount;

            // Reconstruct DateOnly from SQLite text representation so CsvFileWriter.FormatField
            // renders it via DateOnly.ToString() (e.g. "10/01/2024") matching V1 output.
            var asOfStr = row["ifw_effective_date"]?.ToString() ?? "";
            var asOf = DateOnly.Parse(asOfStr);

            // Bureau scores: pass through as-is. Convert null to DBNull.Value to match
            // V1 behavior (V1 initializes bureau scores as DBNull.Value and only overwrites
            // on match). CsvFileWriter renders both null and DBNull.Value as empty field.
            object? equifax = ConvertNullToDbNull(row["equifax_score"]);
            object? transunion = ConvertNullToDbNull(row["transunion_score"]);
            object? experian = ConvertNullToDbNull(row["experian_score"]);

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = Convert.ToInt32(row["customer_id"]),
                ["first_name"] = row["first_name"]?.ToString() ?? "",
                ["last_name"] = row["last_name"]?.ToString() ?? "",
                ["avg_score"] = avgScore,
                ["equifax_score"] = equifax,
                ["transunion_score"] = transunion,
                ["experian_score"] = experian,
                ["ifw_effective_date"] = asOf
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }

    /// <summary>
    /// Convert null (from SQLite via Transformation) to DBNull.Value to match V1 behavior.
    /// Non-null values are passed through unchanged.
    /// </summary>
    private static object? ConvertNullToDbNull(object? value)
    {
        if (value is null || value is DBNull)
            return DBNull.Value;
        return value;
    }
}
