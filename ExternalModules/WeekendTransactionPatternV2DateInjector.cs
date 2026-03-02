using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 minimal External module for WeekendTransactionPattern (Step 1 of 2).
/// Injects __maxEffectiveDate from shared state into a single-row DataFrame
/// so that the downstream Transformation SQL can filter and classify by date.
///
/// This module does ZERO business logic — it is a bridge for the framework
/// limitation that Transformation SQL cannot access shared state scalars.
///
/// Anti-patterns eliminated:
///   AP3 — V1's monolithic External replaced; business logic moved to SQL
///   AP4 — DataSourcing reduced from 5 columns to 2 (amount, as_of)
///   AP6 — V1's foreach loops replaced with SQL GROUP BY
///
/// Anti-patterns retained:
///   AP10 — Hardcoded date range retained; framework cannot compute dynamic
///           date offsets for weekly summary range
/// </summary>
public class WeekendTransactionPatternV2DateInjector : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        // AP10 retained: framework cannot compute dynamic date offsets for weekly summary range
        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];
        var dateStr = maxDate.ToString("yyyy-MM-dd");

        // W3a: On Sundays, compute the Monday of the current week (6 days prior)
        // for the weekly summary range. Not used on non-Sundays.
        var mondayStr = maxDate.DayOfWeek == DayOfWeek.Sunday
            ? maxDate.AddDays(-6).ToString("yyyy-MM-dd")
            : "";

        var effDateRows = new List<Row>
        {
            new Row(new Dictionary<string, object?>
            {
                ["max_date"] = dateStr,
                ["is_sunday"] = maxDate.DayOfWeek == DayOfWeek.Sunday ? 1 : 0,
                ["monday_of_week"] = mondayStr
            })
        };

        sharedState["effective_date"] = new DataFrame(
            effDateRows,
            new List<string> { "max_date", "is_sunday", "monday_of_week" }
        );

        return sharedState;
    }
}
