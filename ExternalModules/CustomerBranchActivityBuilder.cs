using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CustomerBranchActivityBuilder : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "first_name", "last_name", "as_of", "visit_count"
        };

        var branchVisits = sharedState.ContainsKey("branch_visits") ? sharedState["branch_visits"] as DataFrame : null;
        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;

        // Weekend guard on customers empty
        if (customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        if (branchVisits == null || branchVisits.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Build customer lookup: customer_id -> (first_name, last_name)
        var customerNames = new Dictionary<int, (string firstName, string lastName)>();
        foreach (var custRow in customers.Rows)
        {
            var custId = Convert.ToInt32(custRow["id"]);
            var firstName = custRow["first_name"]?.ToString() ?? "";
            var lastName = custRow["last_name"]?.ToString() ?? "";
            customerNames[custId] = (firstName, lastName);
        }

        // Group branch_visits by customer_id, count visits per customer
        var visitCounts = new Dictionary<int, int>();
        foreach (var visitRow in branchVisits.Rows)
        {
            var custId = Convert.ToInt32(visitRow["customer_id"]);
            if (!visitCounts.ContainsKey(custId))
                visitCounts[custId] = 0;
            visitCounts[custId]++;
        }

        // Get as_of from first branch_visit row
        var asOf = branchVisits.Rows[0]["as_of"];

        // Build output rows
        var outputRows = new List<Row>();
        foreach (var kvp in visitCounts)
        {
            var customerId = kvp.Key;
            var visitCount = kvp.Value;

            string? firstName = null;
            string? lastName = null;
            if (customerNames.ContainsKey(customerId))
            {
                var names = customerNames[customerId];
                firstName = names.firstName;
                lastName = names.lastName;
            }

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = customerId,
                ["first_name"] = firstName,
                ["last_name"] = lastName,
                ["as_of"] = asOf,
                ["visit_count"] = visitCount
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
