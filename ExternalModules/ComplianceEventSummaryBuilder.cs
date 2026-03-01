using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class ComplianceEventSummaryBuilder : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "event_type", "status", "event_count", "as_of"
        };

        var complianceEvents = sharedState.ContainsKey("compliance_events") ? sharedState["compliance_events"] as DataFrame : null;

        // W1: Sunday skip — return empty on Sundays
        var maxDate = sharedState.ContainsKey("__maxEffectiveDate") ? (DateOnly)sharedState["__maxEffectiveDate"] : DateOnly.FromDateTime(DateTime.Today);
        if (maxDate.DayOfWeek == DayOfWeek.Sunday)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        if (complianceEvents == null || complianceEvents.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // AP1: accounts sourced but never used (dead-end)

        var asOf = complianceEvents.Rows[0]["as_of"];

        // Count events by (event_type, status)
        var counts = new Dictionary<(string eventType, string status), int>();
        foreach (var row in complianceEvents.Rows)
        {
            var eventType = row["event_type"]?.ToString() ?? "";
            var status = row["status"]?.ToString() ?? "";
            var key = (eventType, status);

            if (!counts.ContainsKey(key))
                counts[key] = 0;
            counts[key]++;
        }

        var outputRows = new List<Row>();
        foreach (var kvp in counts)
        {
            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["event_type"] = kvp.Key.eventType,
                ["status"] = kvp.Key.status,
                ["event_count"] = kvp.Value,
                ["as_of"] = asOf
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
