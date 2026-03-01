using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class InvestmentRiskClassifier : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "investment_id", "customer_id", "account_type",
            "current_value", "risk_profile", "risk_tier", "as_of"
        };

        var investments = sharedState.ContainsKey("investments") ? sharedState["investments"] as DataFrame : null;

        if (investments == null || investments.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        var outputRows = new List<Row>();
        foreach (var row in investments.Rows)
        {
            var investmentId = Convert.ToInt32(row["investment_id"]);
            var customerId = Convert.ToInt32(row["customer_id"]);
            var accountType = row["account_type"]?.ToString() ?? "";

            // AP5: Asymmetric NULLs — null current_value → 0, but null risk_profile → "Unknown"
            var currentValue = row["current_value"] != null
                ? Convert.ToDecimal(row["current_value"])
                : 0m;

            var riskProfile = row["risk_profile"]?.ToString() ?? "Unknown";

            // AP7: Magic values — hardcoded thresholds for risk tier
            string riskTier;
            if (currentValue > 200000)
                riskTier = "High Value";
            else if (currentValue > 50000)
                riskTier = "Medium Value";
            else
                riskTier = "Low Value";

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["investment_id"] = investmentId,
                ["customer_id"] = customerId,
                ["account_type"] = accountType,
                ["current_value"] = currentValue,
                ["risk_profile"] = riskProfile,
                ["risk_tier"] = riskTier,
                ["as_of"] = row["as_of"]
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
