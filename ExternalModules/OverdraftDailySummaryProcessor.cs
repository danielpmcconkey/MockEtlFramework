using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class OverdraftDailySummaryProcessor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "event_date", "overdraft_count", "total_overdraft_amount", "total_fees", "as_of"
        };

        var maxDate = sharedState.ContainsKey("__maxEffectiveDate")
            ? (DateOnly)sharedState["__maxEffectiveDate"]
            : DateOnly.FromDateTime(DateTime.Today);

        var overdraftEvents = sharedState.ContainsKey("overdraft_events")
            ? sharedState["overdraft_events"] as DataFrame
            : null;

        // AP1: transactions sourced but never used (dead-end)

        if (overdraftEvents == null || overdraftEvents.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Group by as_of date for daily summary
        var groups = new Dictionary<string, (int count, decimal totalAmount, decimal totalFees)>();

        foreach (var row in overdraftEvents.Rows)
        {
            var asOf = row["as_of"]?.ToString() ?? "";
            var amount = Convert.ToDecimal(row["overdraft_amount"]);
            var fee = Convert.ToDecimal(row["fee_amount"]);

            if (!groups.ContainsKey(asOf))
                groups[asOf] = (0, 0m, 0m);

            var current = groups[asOf];
            groups[asOf] = (current.count + 1, current.totalAmount + amount, current.totalFees + fee);
        }

        var outputRows = new List<Row>();
        foreach (var kvp in groups)
        {
            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["event_date"] = kvp.Key,
                ["overdraft_count"] = kvp.Value.count,
                ["total_overdraft_amount"] = kvp.Value.totalAmount,
                ["total_fees"] = kvp.Value.totalFees,
                ["as_of"] = kvp.Key
            }));
        }

        // W3a: End-of-week boundary — append weekly summary row on Sundays
        if (maxDate.DayOfWeek == DayOfWeek.Sunday)
        {
            int weeklyCount = groups.Values.Sum(g => g.count);
            decimal weeklyAmount = groups.Values.Sum(g => g.totalAmount);
            decimal weeklyFees = groups.Values.Sum(g => g.totalFees);

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["event_date"] = "WEEKLY_TOTAL",
                ["overdraft_count"] = weeklyCount,
                ["total_overdraft_amount"] = weeklyAmount,
                ["total_fees"] = weeklyFees,
                ["as_of"] = maxDate.ToString("yyyy-MM-dd")
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
