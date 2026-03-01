using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class InvestmentAccountOverviewBuilder : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "investment_id", "customer_id", "first_name", "last_name",
            "account_type", "current_value", "risk_profile", "as_of"
        };

        var investments = sharedState.ContainsKey("investments") ? sharedState["investments"] as DataFrame : null;
        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;

        // W1: Sunday skip — return empty DataFrame on Sundays
        var maxDate = sharedState.ContainsKey("__maxEffectiveDate")
            ? (DateOnly)sharedState["__maxEffectiveDate"]
            : DateOnly.FromDateTime(DateTime.Today);

        if (maxDate.DayOfWeek == DayOfWeek.Sunday)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        if (investments == null || investments.Count == 0 || customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

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

        var outputRows = new List<Row>();
        foreach (var row in investments.Rows)
        {
            var customerId = Convert.ToInt32(row["customer_id"]);
            var name = customerLookup.ContainsKey(customerId)
                ? customerLookup[customerId]
                : (firstName: "", lastName: "");

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["investment_id"] = Convert.ToInt32(row["investment_id"]),
                ["customer_id"] = customerId,
                ["first_name"] = name.firstName,
                ["last_name"] = name.lastName,
                ["account_type"] = row["account_type"]?.ToString() ?? "",
                ["current_value"] = Convert.ToDecimal(row["current_value"]),
                ["risk_profile"] = row["risk_profile"]?.ToString() ?? "",
                ["as_of"] = row["as_of"]
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
