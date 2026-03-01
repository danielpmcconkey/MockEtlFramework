using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class WireTransferDailyProcessor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "wire_date", "wire_count", "total_amount", "avg_amount", "as_of"
        };

        var wireTransfers = sharedState.ContainsKey("wire_transfers") ? sharedState["wire_transfers"] as DataFrame : null;

        var maxDate = sharedState.ContainsKey("__maxEffectiveDate")
            ? (DateOnly)sharedState["__maxEffectiveDate"]
            : DateOnly.FromDateTime(DateTime.Today);

        if (wireTransfers == null || wireTransfers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // AP3: unnecessary External — SQL GROUP BY would suffice
        // AP6: row-by-row iteration instead of set-based

        // Group by as_of (used as wire_date) row-by-row — daily rows
        var dailyGroups = new Dictionary<object, (int count, decimal total)>();
        foreach (var row in wireTransfers.Rows)
        {
            var asOf = row["as_of"];
            var amount = Convert.ToDecimal(row["amount"]);

            if (asOf == null) continue;

            if (!dailyGroups.ContainsKey(asOf))
                dailyGroups[asOf] = (0, 0m);

            var current = dailyGroups[asOf];
            dailyGroups[asOf] = (current.count + 1, current.total + amount);
        }

        var outputRows = new List<Row>();
        foreach (var kvp in dailyGroups)
        {
            var wireDate = kvp.Key;
            var wireCount = kvp.Value.count;
            var totalAmount = kvp.Value.total;
            var avgAmount = wireCount > 0 ? Math.Round(totalAmount / wireCount, 2) : 0m;

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["wire_date"] = wireDate,
                ["wire_count"] = wireCount,
                ["total_amount"] = Math.Round(totalAmount, 2),
                ["avg_amount"] = avgAmount,
                ["as_of"] = wireDate
            }));
        }

        // W3b: End-of-month boundary — append monthly summary row
        if (maxDate.Day == DateTime.DaysInMonth(maxDate.Year, maxDate.Month))
        {
            int totalWires = dailyGroups.Values.Sum(g => g.count);
            decimal totalAmt = dailyGroups.Values.Sum(g => g.total);

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["wire_date"] = "MONTHLY_TOTAL",
                ["wire_count"] = totalWires,
                ["total_amount"] = Math.Round(totalAmt, 2),
                ["avg_amount"] = totalWires > 0 ? Math.Round(totalAmt / totalWires, 2) : 0m,
                ["as_of"] = maxDate
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
