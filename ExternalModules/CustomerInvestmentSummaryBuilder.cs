using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CustomerInvestmentSummaryBuilder : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "first_name", "last_name",
            "investment_count", "total_value", "as_of"
        };

        var investments = sharedState.ContainsKey("investments") ? sharedState["investments"] as DataFrame : null;
        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;

        if (investments == null || investments.Count == 0 || customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];

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

        // Aggregate investments per customer
        var customerAgg = new Dictionary<int, (int count, decimal totalValue)>();
        foreach (var row in investments.Rows)
        {
            var customerId = Convert.ToInt32(row["customer_id"]);
            var value = Convert.ToDecimal(row["current_value"]);

            if (!customerAgg.ContainsKey(customerId))
                customerAgg[customerId] = (0, 0m);

            var current = customerAgg[customerId];
            customerAgg[customerId] = (current.count + 1, current.totalValue + value);
        }

        var outputRows = new List<Row>();
        foreach (var kvp in customerAgg)
        {
            var custId = kvp.Key;
            var (count, totalValue) = kvp.Value;
            var name = customerLookup.ContainsKey(custId)
                ? customerLookup[custId]
                : (firstName: "", lastName: "");

            // W5: Banker's rounding (MidpointRounding.ToEven)
            var roundedValue = Math.Round(totalValue, 2, MidpointRounding.ToEven);

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = custId,
                ["first_name"] = name.firstName,
                ["last_name"] = name.lastName,
                ["investment_count"] = count,
                ["total_value"] = roundedValue,
                ["as_of"] = maxDate
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
