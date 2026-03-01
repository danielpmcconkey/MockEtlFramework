using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class OverdraftRecoveryRateProcessor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "total_events", "charged_count", "waived_count", "recovery_rate", "as_of"
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

        int totalEvents = 0;
        int chargedCount = 0;
        int waivedCount = 0;

        foreach (var row in overdraftEvents.Rows)
        {
            totalEvents++;
            var feeWaived = Convert.ToBoolean(row["fee_waived"]);
            if (feeWaived)
                waivedCount++;
            else
                chargedCount++;
        }

        // W4: Integer division — charged_count / total_count both int → truncates to 0
        decimal recoveryRate = (decimal)(chargedCount / totalEvents);

        // W5: Banker's rounding — MidpointRounding.ToEven
        recoveryRate = Math.Round(recoveryRate, 4, MidpointRounding.ToEven);

        var outputRows = new List<Row>();
        outputRows.Add(new Row(new Dictionary<string, object?>
        {
            ["total_events"] = totalEvents,
            ["charged_count"] = chargedCount,
            ["waived_count"] = waivedCount,
            ["recovery_rate"] = recoveryRate,
            ["as_of"] = maxDate.ToString("yyyy-MM-dd")
        }));

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
