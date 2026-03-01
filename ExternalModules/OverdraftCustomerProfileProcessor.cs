using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class OverdraftCustomerProfileProcessor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "first_name", "last_name", "overdraft_count",
            "total_overdraft_amount", "avg_overdraft", "as_of"
        };

        var maxDate = sharedState.ContainsKey("__maxEffectiveDate")
            ? (DateOnly)sharedState["__maxEffectiveDate"]
            : DateOnly.FromDateTime(DateTime.Today);

        // W2: Weekend fallback — use Friday's data on Sat/Sun
        DateOnly targetDate = maxDate;
        if (maxDate.DayOfWeek == DayOfWeek.Saturday) targetDate = maxDate.AddDays(-1);
        else if (maxDate.DayOfWeek == DayOfWeek.Sunday) targetDate = maxDate.AddDays(-2);

        var overdraftEvents = sharedState.ContainsKey("overdraft_events")
            ? sharedState["overdraft_events"] as DataFrame
            : null;
        var customers = sharedState.ContainsKey("customers")
            ? sharedState["customers"] as DataFrame
            : null;

        // AP1: accounts sourced but never used (dead-end)
        // AP4: prefix, suffix, birthdate sourced from customers but unused

        if (overdraftEvents == null || overdraftEvents.Count == 0 || customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Filter overdraft events to target date
        var filteredEvents = overdraftEvents.Rows
            .Where(r => (r["as_of"] is DateOnly d && d == targetDate) ||
                        r["as_of"]?.ToString() == targetDate.ToString("yyyy-MM-dd"))
            .ToList();

        if (filteredEvents.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Build customer lookup
        var customerLookup = new Dictionary<int, (string firstName, string lastName)>();
        foreach (var cust in customers.Rows)
        {
            var id = Convert.ToInt32(cust["id"]);
            var firstName = cust["first_name"]?.ToString() ?? "";
            var lastName = cust["last_name"]?.ToString() ?? "";
            customerLookup[id] = (firstName, lastName);
        }

        // Group overdraft events by customer
        var customerOverdrafts = new Dictionary<int, (int count, decimal totalAmount)>();
        foreach (var evt in filteredEvents)
        {
            var customerId = Convert.ToInt32(evt["customer_id"]);
            var amount = Convert.ToDecimal(evt["overdraft_amount"]);

            if (!customerOverdrafts.ContainsKey(customerId))
                customerOverdrafts[customerId] = (0, 0m);

            var current = customerOverdrafts[customerId];
            customerOverdrafts[customerId] = (current.count + 1, current.totalAmount + amount);
        }

        var outputRows = new List<Row>();
        foreach (var kvp in customerOverdrafts)
        {
            var custId = kvp.Key;
            var (firstName, lastName) = customerLookup.ContainsKey(custId)
                ? customerLookup[custId]
                : ("", "");
            var avgOverdraft = kvp.Value.count > 0
                ? Math.Round(kvp.Value.totalAmount / kvp.Value.count, 2)
                : 0m;

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = custId,
                ["first_name"] = firstName,
                ["last_name"] = lastName,
                ["overdraft_count"] = kvp.Value.count,
                ["total_overdraft_amount"] = kvp.Value.totalAmount,
                ["avg_overdraft"] = avgOverdraft,
                ["as_of"] = targetDate.ToString("yyyy-MM-dd")
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
