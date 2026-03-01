using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class PortfolioValueCalculator : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "first_name", "last_name",
            "total_portfolio_value", "holding_count", "as_of"
        };

        var holdings = sharedState.ContainsKey("holdings") ? sharedState["holdings"] as DataFrame : null;
        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;

        if (holdings == null || holdings.Count == 0 || customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // W2: Weekend fallback — use Friday's data on Sat/Sun
        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];
        DateOnly targetDate = maxDate;
        if (maxDate.DayOfWeek == DayOfWeek.Saturday) targetDate = maxDate.AddDays(-1);
        else if (maxDate.DayOfWeek == DayOfWeek.Sunday) targetDate = maxDate.AddDays(-2);

        var filteredHoldings = holdings.Rows
            .Where(r => ((DateOnly)r["as_of"]) == targetDate)
            .ToList();

        // Build customer lookup
        var customerLookup = new Dictionary<int, (string firstName, string lastName)>();
        foreach (var custRow in customers.Rows)
        {
            var custId = Convert.ToInt32(custRow["id"]);
            customerLookup[custId] = (
                custRow["first_name"]?.ToString() ?? "",
                custRow["last_name"]?.ToString() ?? ""
            );
        }

        // AP6: Row-by-row iteration to compute totals (where SQL JOIN+GROUP BY would do)
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

        var outputRows = new List<Row>();
        foreach (var kvp in customerTotals)
        {
            var custId = kvp.Key;
            var (totalValue, holdingCount) = kvp.Value;

            var name = customerLookup.ContainsKey(custId)
                ? customerLookup[custId]
                : (firstName: "", lastName: "");

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = custId,
                ["first_name"] = name.firstName,
                ["last_name"] = name.lastName,
                ["total_portfolio_value"] = Math.Round(totalValue, 2),
                ["holding_count"] = holdingCount,
                ["as_of"] = targetDate
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
