using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 minimal External module for WeekendTransactionPattern (Step 2 of 2).
/// Post-Transformation rounding fixer that applies banker's rounding
/// (MidpointRounding.ToEven) to total_amount and avg_amount columns.
///
/// Why this exists:
///   W5 — V1 uses Math.Round(decimal, 2) which defaults to MidpointRounding.ToEven
///         (banker's rounding). SQLite's ROUND() uses round-half-away-from-zero.
///         These produce different results at exact midpoints (e.g., 125.125 rounds
///         to 125.12 with banker's rounding but 125.13 with half-away-from-zero).
///         Moving rounding into C# guarantees V1-equivalent output.
///
/// This module does ZERO business logic — it reads the pre-computed pre_output
/// DataFrame from the Transformation and applies only the rounding correction,
/// storing the result as "output" for the downstream CsvFileWriter.
/// </summary>
public class WeekendTransactionPatternV2Rounder : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var preOutput = sharedState.TryGetValue("pre_output", out var val)
            ? val as DataFrame
            : null;

        if (preOutput == null || preOutput.Count == 0)
        {
            // Pass through empty DataFrame as-is
            sharedState["output"] = preOutput ?? new DataFrame(
                new List<Row>(),
                new List<string> { "day_type", "txn_count", "total_amount", "avg_amount", "as_of" }
            );
            return sharedState;
        }

        var outputColumns = new List<string>
        {
            "day_type", "txn_count", "total_amount", "avg_amount", "as_of"
        };

        var outputRows = new List<Row>();
        foreach (var row in preOutput.Rows)
        {
            var totalAmount = Convert.ToDecimal(row["total_amount"]);
            var avgAmount = Convert.ToDecimal(row["avg_amount"]);

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["day_type"] = row["day_type"],
                ["txn_count"] = row["txn_count"],
                // W5: Apply banker's rounding (MidpointRounding.ToEven) to match V1's
                // Math.Round(decimal, 2) behavior [WeekendTransactionPatternProcessor.cs:59,67,107,115]
                ["total_amount"] = Math.Round(totalAmount, 2, MidpointRounding.ToEven),
                ["avg_amount"] = Math.Round(avgAmount, 2, MidpointRounding.ToEven),
                ["as_of"] = row["as_of"]
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
