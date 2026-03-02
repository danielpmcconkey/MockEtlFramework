using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 minimal bridge for FeeRevenueDaily.
/// Materializes __maxEffectiveDate from shared state into a single-row DataFrame
/// (effective_date_ref) so the Transformation SQL can access the current effective date.
///
/// This module contains ZERO business logic. All fee aggregation, monthly total,
/// and net revenue computation is handled in the Transformation SQL.
///
/// Anti-patterns eliminated:
///   AP3 — V1's full External module (aggregation, monthly totals) replaced with SQL
///   AP6 — V1's foreach loops replaced with SQL SUM/CASE WHEN
/// </summary>
public class FeeRevenueDailyV2Processor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        // Read __maxEffectiveDate from shared state; fall back to today if missing (EC-5).
        var maxDate = sharedState.ContainsKey("__maxEffectiveDate")
            ? (DateOnly)sharedState["__maxEffectiveDate"]
            : DateOnly.FromDateTime(DateTime.Today);

        // Create a single-row DataFrame with one column: effective_date (yyyy-MM-dd string).
        // This makes the effective date accessible in the Transformation SQL via CROSS JOIN.
        var rows = new List<Row>
        {
            new Row(new Dictionary<string, object?>
            {
                ["effective_date"] = maxDate.AddDays(-1).ToString("yyyy-MM-dd")
            })
        };

        var columns = new List<string> { "effective_date" };
        sharedState["effective_date_ref"] = new DataFrame(rows, columns);

        return sharedState;
    }
}
