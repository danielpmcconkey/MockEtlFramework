using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 of CustomerBranchActivity: counts branch visits per customer, enriched with customer name.
/// Eliminates AP-1 (unused branches sourcing), AP-4 (unused visit_id/branch_id/visit_purpose),
/// AP-6 (row-by-row dictionary building replaced with LINQ).
/// Retains External module for empty-DataFrame guard (customers empty on weekends).
/// </summary>
public class CustomerBranchActivityBuilderV2 : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "customer_id", "first_name", "last_name", "as_of", "visit_count"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var branchVisits = sharedState.ContainsKey("branch_visits")
            ? sharedState["branch_visits"] as DataFrame
            : null;
        var customers = sharedState.ContainsKey("customers")
            ? sharedState["customers"] as DataFrame
            : null;

        // Weekend guard: customers has no data on weekends
        if (customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        if (branchVisits == null || branchVisits.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // Build customer name lookup
        var customerNames = customers.Rows
            .GroupBy(r => Convert.ToInt32(r["id"]))
            .ToDictionary(
                g => g.Key,
                g => {
                    var last = g.Last(); // Last row per customer wins (matches original)
                    return (
                        firstName: last["first_name"]?.ToString() ?? "",
                        lastName: last["last_name"]?.ToString() ?? ""
                    );
                }
            );

        // Get as_of from first branch_visit row
        var asOf = branchVisits.Rows[0]["as_of"];

        // Count visits per customer using LINQ GroupBy
        var visitGroups = branchVisits.Rows
            .GroupBy(r => Convert.ToInt32(r["customer_id"]));

        var outputRows = new List<Row>();
        foreach (var group in visitGroups)
        {
            var customerId = group.Key;
            var visitCount = group.Count();

            string? firstName = null;
            string? lastName = null;
            if (customerNames.TryGetValue(customerId, out var names))
            {
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

        sharedState["output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
