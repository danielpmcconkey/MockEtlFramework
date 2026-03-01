using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class WeekendTransactionPatternProcessor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "day_type", "txn_count", "total_amount", "avg_amount", "as_of"
        };

        var transactions = sharedState.ContainsKey("transactions") ? sharedState["transactions"] as DataFrame : null;

        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];

        if (transactions == null || transactions.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        var dateStr = maxDate.ToString("yyyy-MM-dd");

        // AP10: Over-sourced full date range via config; classify today's txns as weekend/weekday
        int weekendCount = 0;
        decimal weekendTotal = 0m;
        int weekdayCount = 0;
        decimal weekdayTotal = 0m;

        foreach (var row in transactions.Rows)
        {
            var rawAsOf = row["as_of"];
            var asOf = rawAsOf is DateOnly d ? d : DateOnly.FromDateTime((DateTime)rawAsOf!);
            if (asOf != maxDate) continue;

            var amount = Convert.ToDecimal(row["amount"]);

            if (asOf.DayOfWeek == DayOfWeek.Saturday || asOf.DayOfWeek == DayOfWeek.Sunday)
            {
                weekendCount++;
                weekendTotal += amount;
            }
            else
            {
                weekdayCount++;
                weekdayTotal += amount;
            }
        }

        var outputRows = new List<Row>();

        outputRows.Add(new Row(new Dictionary<string, object?>
        {
            ["day_type"] = "Weekday",
            ["txn_count"] = weekdayCount,
            ["total_amount"] = Math.Round(weekdayTotal, 2),
            ["avg_amount"] = weekdayCount > 0 ? Math.Round(weekdayTotal / weekdayCount, 2) : 0m,
            ["as_of"] = dateStr
        }));

        outputRows.Add(new Row(new Dictionary<string, object?>
        {
            ["day_type"] = "Weekend",
            ["txn_count"] = weekendCount,
            ["total_amount"] = Math.Round(weekendTotal, 2),
            ["avg_amount"] = weekendCount > 0 ? Math.Round(weekendTotal / weekendCount, 2) : 0m,
            ["as_of"] = dateStr
        }));

        // W3a: End-of-week boundary — append weekly summary row on Sundays
        if (maxDate.DayOfWeek == DayOfWeek.Sunday)
        {
            // Aggregate the full week (Mon through Sun) from the sourced data
            var mondayOfWeek = maxDate.AddDays(-6);
            int wkWeekendCount = 0;
            decimal wkWeekendTotal = 0m;
            int wkWeekdayCount = 0;
            decimal wkWeekdayTotal = 0m;

            foreach (var row in transactions.Rows)
            {
                var rawAsOf = row["as_of"];
                var asOf = rawAsOf is DateOnly dd ? dd : DateOnly.FromDateTime((DateTime)rawAsOf!);
                if (asOf < mondayOfWeek || asOf > maxDate) continue;

                var amount = Convert.ToDecimal(row["amount"]);

                if (asOf.DayOfWeek == DayOfWeek.Saturday || asOf.DayOfWeek == DayOfWeek.Sunday)
                {
                    wkWeekendCount++;
                    wkWeekendTotal += amount;
                }
                else
                {
                    wkWeekdayCount++;
                    wkWeekdayTotal += amount;
                }
            }

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["day_type"] = "WEEKLY_TOTAL_Weekday",
                ["txn_count"] = wkWeekdayCount,
                ["total_amount"] = Math.Round(wkWeekdayTotal, 2),
                ["avg_amount"] = wkWeekdayCount > 0 ? Math.Round(wkWeekdayTotal / wkWeekdayCount, 2) : 0m,
                ["as_of"] = dateStr
            }));

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["day_type"] = "WEEKLY_TOTAL_Weekend",
                ["txn_count"] = wkWeekendCount,
                ["total_amount"] = Math.Round(wkWeekendTotal, 2),
                ["avg_amount"] = wkWeekendCount > 0 ? Math.Round(wkWeekendTotal / wkWeekendCount, 2) : 0m,
                ["as_of"] = dateStr
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
