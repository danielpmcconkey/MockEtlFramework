using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class FeeRevenueDailyProcessor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "event_date", "charged_fees", "waived_fees", "net_revenue", "as_of"
        };

        var overdraftEvents = sharedState.ContainsKey("overdraft_events")
            ? sharedState["overdraft_events"] as DataFrame
            : null;

        var maxDate = sharedState.ContainsKey("__maxEffectiveDate")
            ? (DateOnly)sharedState["__maxEffectiveDate"]
            : DateOnly.FromDateTime(DateTime.Today);

        if (overdraftEvents == null || overdraftEvents.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // AP10: Over-sourced full date range via config, but External filters to current date only
        var currentDateRows = overdraftEvents.Rows
            .Where(r => r["as_of"]?.ToString() == maxDate.ToString("yyyy-MM-dd") ||
                        (r["as_of"] is DateOnly d && d == maxDate))
            .ToList();

        if (currentDateRows.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // W6: Double epsilon — use double instead of decimal for accumulation
        double chargedFees = 0.0;
        double waivedFees = 0.0;

        foreach (var row in currentDateRows)
        {
            var feeAmount = Convert.ToDouble(row["fee_amount"]);
            var feeWaived = Convert.ToBoolean(row["fee_waived"]);

            if (feeWaived)
                waivedFees += feeAmount;
            else
                chargedFees += feeAmount;
        }

        double netRevenue = chargedFees - waivedFees;

        var outputRows = new List<Row>();
        outputRows.Add(new Row(new Dictionary<string, object?>
        {
            ["event_date"] = maxDate.ToString("yyyy-MM-dd"),
            ["charged_fees"] = chargedFees,
            ["waived_fees"] = waivedFees,
            ["net_revenue"] = netRevenue,
            ["as_of"] = maxDate.ToString("yyyy-MM-dd")
        }));

        // W3b: End-of-month boundary — append monthly summary row
        if (maxDate.Day == DateTime.DaysInMonth(maxDate.Year, maxDate.Month))
        {
            // Sum ALL rows in the source (full month), not just today's filtered rows
            double monthCharged = 0.0;
            double monthWaived = 0.0;

            foreach (var row in overdraftEvents.Rows)
            {
                var feeAmount = Convert.ToDouble(row["fee_amount"]);
                var feeWaived = Convert.ToBoolean(row["fee_waived"]);

                if (feeWaived)
                    monthWaived += feeAmount;
                else
                    monthCharged += feeAmount;
            }

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["event_date"] = "MONTHLY_TOTAL",
                ["charged_fees"] = monthCharged,
                ["waived_fees"] = monthWaived,
                ["net_revenue"] = monthCharged - monthWaived,
                ["as_of"] = maxDate.ToString("yyyy-MM-dd")
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
