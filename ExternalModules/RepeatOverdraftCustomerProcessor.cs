using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class RepeatOverdraftCustomerProcessor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "first_name", "last_name", "overdraft_count", "total_overdraft_amount", "as_of"
        };

        var overdraftEvents = sharedState.ContainsKey("overdraft_events")
            ? sharedState["overdraft_events"] as DataFrame
            : null;
        var customers = sharedState.ContainsKey("customers")
            ? sharedState["customers"] as DataFrame
            : null;

        if (overdraftEvents == null || overdraftEvents.Count == 0 || customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        var asOf = overdraftEvents.Rows[0]["as_of"];

        // Build customer lookup
        var customerLookup = new Dictionary<int, (string firstName, string lastName)>();
        foreach (var cust in customers.Rows)
        {
            var id = Convert.ToInt32(cust["id"]);
            var firstName = cust["first_name"]?.ToString() ?? "";
            var lastName = cust["last_name"]?.ToString() ?? "";
            customerLookup[id] = (firstName, lastName);
        }

        // AP6: Row-by-row iteration to count overdrafts per customer
        var customerOverdrafts = new Dictionary<int, (int count, decimal totalAmount)>();
        foreach (var evt in overdraftEvents.Rows)
        {
            var customerId = Convert.ToInt32(evt["customer_id"]);
            var amount = Convert.ToDecimal(evt["overdraft_amount"]);

            if (!customerOverdrafts.ContainsKey(customerId))
                customerOverdrafts[customerId] = (0, 0m);

            var current = customerOverdrafts[customerId];
            customerOverdrafts[customerId] = (current.count + 1, current.totalAmount + amount);
        }

        // AP7: Magic threshold — filter to customers with 2+ overdrafts
        var outputRows = new List<Row>();
        foreach (var kvp in customerOverdrafts)
        {
            if (kvp.Value.count < 2)
                continue;

            var custId = kvp.Key;
            var (firstName, lastName) = customerLookup.ContainsKey(custId)
                ? customerLookup[custId]
                : ("", "");

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = custId,
                ["first_name"] = firstName,
                ["last_name"] = lastName,
                ["overdraft_count"] = kvp.Value.count,
                ["total_overdraft_amount"] = kvp.Value.totalAmount,
                ["as_of"] = asOf
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
