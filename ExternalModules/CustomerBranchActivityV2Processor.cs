using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 replacement for CustomerBranchActivityBuilder.
/// Tier 2 External: handles empty-guard + set-based aggregation via LINQ.
///
/// Justified by framework limitation: Transformation.RegisterTable skips
/// empty DataFrames [Lib/Modules/Transformation.cs:46], causing SQL to fail
/// on missing tables when source data is empty (BR-3, BR-4).
///
/// Anti-patterns eliminated:
///   AP1 — branches DataSourcing removed from V2 config (sourced but never used by V1)
///   AP4 — visit_id, branch_id, visit_purpose removed (only customer_id needed)
///   AP6 — V1's three foreach loops replaced with LINQ GroupBy/ToDictionary/Select
/// </summary>
public class CustomerBranchActivityV2Processor : IExternalStep
{
    // Output schema columns — matches V1 output column order exactly
    private static readonly List<string> OutputColumns = new()
    {
        "customer_id", "first_name", "last_name", "ifw_effective_date", "visit_count"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var branchVisits = sharedState.TryGetValue("branch_visits", out var bvVal)
            ? bvVal as DataFrame
            : null;
        var customers = sharedState.TryGetValue("customers", out var cVal)
            ? cVal as DataFrame
            : null;

        // BR-3: Weekend guard — if customers is null or empty, produce empty output
        if (customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // BR-4: If branch_visits is null or empty, produce empty output
        if (branchVisits == null || branchVisits.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // BR-2: Build customer name lookup (last-write-wins for duplicate customer_ids
        // across ifw_effective_date dates). DataSourcing orders by ifw_effective_date [DataSourcing.cs:85],
        // so the last entry per id is the latest ifw_effective_date date — matching V1's
        // dictionary overwrite behavior [CustomerBranchActivityBuilder.cs:35].
        var customerNames = customers.Rows
            .GroupBy(r => Convert.ToInt32(r["id"]))
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var last = g.Last(); // last-write-wins (latest ifw_effective_date)
                    return (
                        firstName: last["first_name"]?.ToString() ?? "",
                        lastName: last["last_name"]?.ToString() ?? ""
                    );
                }
            );

        // BR-5: Single ifw_effective_date from first branch_visits row, applied to all output rows.
        // V1 behavior: branchVisits.Rows[0]["ifw_effective_date"] [CustomerBranchActivityBuilder.cs:52]
        // DataSourcing orders by ifw_effective_date, so first row is the earliest date in range.
        var asOf = branchVisits.Rows[0]["ifw_effective_date"];

        // BR-1, BR-10: Aggregate visit count per customer across ALL ifw_effective_date dates.
        // LINQ GroupBy preserves group order by first appearance (BR-9: dictionary
        // insertion order — customers ordered by earliest visit date, then row order).
        var visitGroups = branchVisits.Rows
            .GroupBy(r => Convert.ToInt32(r["customer_id"]));

        var outputRows = visitGroups.Select(g =>
        {
            var customerId = g.Key;
            var visitCount = g.Count();

            // BR-6: Null names when customer_id not found in customers lookup
            string? firstName = null;
            string? lastName = null;
            if (customerNames.TryGetValue(customerId, out var names))
            {
                firstName = names.firstName;
                lastName = names.lastName;
            }

            return new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = customerId,
                ["first_name"] = firstName,
                ["last_name"] = lastName,
                ["ifw_effective_date"] = asOf,
                ["visit_count"] = visitCount
            });
        }).ToList();

        sharedState["output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
