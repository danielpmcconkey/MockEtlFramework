using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class ComplianceOpenItemsBuilder : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "event_id", "customer_id", "first_name", "last_name",
            "event_type", "event_date", "status", "as_of"
        };

        var complianceEvents = sharedState.ContainsKey("compliance_events") ? sharedState["compliance_events"] as DataFrame : null;
        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;

        if (complianceEvents == null || complianceEvents.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // W2: Weekend fallback — use Friday's data on Sat/Sun
        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];
        DateOnly targetDate = maxDate;
        if (maxDate.DayOfWeek == DayOfWeek.Saturday) targetDate = maxDate.AddDays(-1);
        else if (maxDate.DayOfWeek == DayOfWeek.Sunday) targetDate = maxDate.AddDays(-2);

        // AP4: unused columns sourced — review_date from compliance_events, prefix/suffix from customers

        // Build customer lookup
        var customerLookup = new Dictionary<int, (string firstName, string lastName)>();
        if (customers != null)
        {
            foreach (var custRow in customers.Rows)
            {
                var id = Convert.ToInt32(custRow["id"]);
                var firstName = custRow["first_name"]?.ToString() ?? "";
                var lastName = custRow["last_name"]?.ToString() ?? "";
                customerLookup[id] = (firstName, lastName);
            }
        }

        // Filter to target date rows and Open/Escalated status
        var filteredRows = complianceEvents.Rows
            .Where(r => ((DateOnly)r["as_of"]) == targetDate)
            .Where(r =>
            {
                var status = r["status"]?.ToString() ?? "";
                return status == "Open" || status == "Escalated";
            })
            .ToList();

        var outputRows = new List<Row>();
        foreach (var row in filteredRows)
        {
            var customerId = Convert.ToInt32(row["customer_id"]);
            var (firstName, lastName) = customerLookup.GetValueOrDefault(customerId, ("", ""));

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["event_id"] = row["event_id"],
                ["customer_id"] = customerId,
                ["first_name"] = firstName,
                ["last_name"] = lastName,
                ["event_type"] = row["event_type"],
                ["event_date"] = row["event_date"],
                ["status"] = row["status"],
                ["as_of"] = targetDate
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
