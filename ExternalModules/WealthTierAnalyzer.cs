using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class WealthTierAnalyzer : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "wealth_tier", "customer_count", "total_wealth",
            "avg_wealth", "pct_of_customers", "as_of"
        };

        var accounts = sharedState.ContainsKey("accounts") ? sharedState["accounts"] as DataFrame : null;
        var investments = sharedState.ContainsKey("investments") ? sharedState["investments"] as DataFrame : null;
        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;

        if (customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];

        // Compute total wealth per customer (accounts + investments)
        var wealthByCustomer = new Dictionary<int, decimal>();

        if (accounts != null)
        {
            foreach (var row in accounts.Rows)
            {
                var custId = Convert.ToInt32(row["customer_id"]);
                wealthByCustomer[custId] = wealthByCustomer.GetValueOrDefault(custId, 0m) + Convert.ToDecimal(row["current_balance"]);
            }
        }

        if (investments != null)
        {
            foreach (var row in investments.Rows)
            {
                var custId = Convert.ToInt32(row["customer_id"]);
                wealthByCustomer[custId] = wealthByCustomer.GetValueOrDefault(custId, 0m) + Convert.ToDecimal(row["current_value"]);
            }
        }

        // AP7: Magic value thresholds for tier assignment
        var tierGroups = new Dictionary<string, (int count, decimal totalWealth)>
        {
            ["Bronze"] = (0, 0m),
            ["Silver"] = (0, 0m),
            ["Gold"] = (0, 0m),
            ["Platinum"] = (0, 0m)
        };

        foreach (var kvp in wealthByCustomer)
        {
            var wealth = kvp.Value;
            string tier;
            if (wealth < 10000m) tier = "Bronze";
            else if (wealth < 100000m) tier = "Silver";
            else if (wealth < 500000m) tier = "Gold";
            else tier = "Platinum";

            var current = tierGroups[tier];
            tierGroups[tier] = (current.count + 1, current.totalWealth + wealth);
        }

        var totalCustomers = wealthByCustomer.Count;
        var outputRows = new List<Row>();

        foreach (var tier in new[] { "Bronze", "Silver", "Gold", "Platinum" })
        {
            var (count, totalWealth) = tierGroups[tier];
            var avgWealth = count > 0 ? totalWealth / count : 0m;
            // W5: Banker's rounding
            var pctOfCustomers = totalCustomers > 0
                ? Math.Round((decimal)count / totalCustomers * 100m, 2, MidpointRounding.ToEven)
                : 0m;

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["wealth_tier"] = tier,
                ["customer_count"] = count,
                ["total_wealth"] = Math.Round(totalWealth, 2, MidpointRounding.ToEven),
                ["avg_wealth"] = Math.Round(avgWealth, 2, MidpointRounding.ToEven),
                ["pct_of_customers"] = pctOfCustomers,
                ["as_of"] = maxDate
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
