using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 replacement for PortfolioValueCalculator.
/// Produces a per-customer portfolio value summary by aggregating holdings data
/// and enriching with customer name lookup.
///
/// Anti-patterns eliminated:
///   AP1 — investments DataSourcing removed from V2 config (never referenced by V1 logic)
///   AP4 — Unused columns removed from holdings (holding_id, investment_id, security_id, quantity)
///
/// Wrinkles preserved:
///   W2 — Weekend fallback: uses Friday's data on Saturday (maxDate - 1) and Sunday (maxDate - 2)
///   W5 — Banker's rounding: Math.Round(totalValue, 2) uses default MidpointRounding.ToEven (matches V1)
/// </summary>
public class PortfolioValueSummaryV2Processor : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "customer_id", "first_name", "last_name",
        "total_portfolio_value", "holding_count", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var holdings = sharedState.TryGetValue("holdings", out var hVal)
            ? hVal as DataFrame
            : null;
        var customers = sharedState.TryGetValue("customers", out var cVal)
            ? cVal as DataFrame
            : null;

        // BR-5: Empty input guard — if holdings or customers is null/empty,
        // return empty DataFrame with correct schema. V1 returns empty output
        // when either source is missing, even if the other has data.
        if (holdings == null || holdings.Count == 0 ||
            customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // W2: Weekend fallback — use Friday's data on Sat/Sun
        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];
        DateOnly targetDate = maxDate;
        if (maxDate.DayOfWeek == DayOfWeek.Saturday) targetDate = maxDate.AddDays(-1);
        else if (maxDate.DayOfWeek == DayOfWeek.Sunday) targetDate = maxDate.AddDays(-2);

        // Filter holdings to rows where as_of == targetDate
        var filteredHoldings = holdings.Rows
            .Where(r => ((DateOnly)r["as_of"]) == targetDate)
            .ToList();

        // If no rows remain after date filter, return empty DataFrame
        if (filteredHoldings.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // Build customer lookup: id -> (firstName, lastName)
        // Null names default to "" (BR-6)
        var customerLookup = new Dictionary<int, (string firstName, string lastName)>();
        foreach (var custRow in customers.Rows)
        {
            var custId = Convert.ToInt32(custRow["id"]);
            customerLookup[custId] = (
                custRow["first_name"]?.ToString() ?? "",
                custRow["last_name"]?.ToString() ?? ""
            );
        }

        // Aggregate holdings per customer_id: accumulate totalValue and holdingCount
        var customerTotals = new Dictionary<int, (decimal totalValue, int holdingCount)>();
        foreach (var row in filteredHoldings)
        {
            var customerId = Convert.ToInt32(row["customer_id"]);
            var value = Convert.ToDecimal(row["current_value"]);

            if (!customerTotals.ContainsKey(customerId))
                customerTotals[customerId] = (0m, 0);

            var current = customerTotals[customerId];
            customerTotals[customerId] = (current.totalValue + value, current.holdingCount + 1);
        }

        // Build output rows — row order follows dictionary insertion order (BR-10)
        var outputRows = new List<Row>();
        foreach (var kvp in customerTotals)
        {
            var custId = kvp.Key;
            var (totalValue, holdingCount) = kvp.Value;

            // BR-6: Customer name lookup with empty-string default for missing customers
            var name = customerLookup.ContainsKey(custId)
                ? customerLookup[custId]
                : (firstName: "", lastName: "");

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                // W5: Math.Round(totalValue, 2) uses default MidpointRounding.ToEven (Banker's rounding)
                // Same as V1 — both use the C# default, so output is identical.
                ["customer_id"] = custId,
                ["first_name"] = name.firstName,
                ["last_name"] = name.lastName,
                ["total_portfolio_value"] = Math.Round(totalValue, 2),
                ["holding_count"] = holdingCount,
                ["as_of"] = targetDate  // BR-7: as_of from weekend-adjusted targetDate
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
