using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CrossSellCandidateFinder : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "first_name", "last_name",
            "has_checking", "has_savings", "has_credit",
            "has_card", "has_investment", "missing_products", "as_of"
        };

        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;
        var accounts = sharedState.ContainsKey("accounts") ? sharedState["accounts"] as DataFrame : null;
        var cards = sharedState.ContainsKey("cards") ? sharedState["cards"] as DataFrame : null;
        var investments = sharedState.ContainsKey("investments") ? sharedState["investments"] as DataFrame : null;

        if (customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];

        // Build per-customer account type sets
        var accountTypesByCustomer = new Dictionary<int, HashSet<string>>();
        if (accounts != null)
        {
            foreach (var row in accounts.Rows)
            {
                var custId = Convert.ToInt32(row["customer_id"]);
                if (!accountTypesByCustomer.ContainsKey(custId))
                    accountTypesByCustomer[custId] = new HashSet<string>();
                accountTypesByCustomer[custId].Add(row["account_type"]?.ToString() ?? "");
            }
        }

        // Build per-customer card presence
        var customersWithCards = new HashSet<int>();
        if (cards != null)
        {
            foreach (var row in cards.Rows)
            {
                customersWithCards.Add(Convert.ToInt32(row["customer_id"]));
            }
        }

        // Build per-customer investment presence
        var customersWithInvestments = new HashSet<int>();
        if (investments != null)
        {
            foreach (var row in investments.Rows)
            {
                customersWithInvestments.Add(Convert.ToInt32(row["customer_id"]));
            }
        }

        // AP6: Row-by-row iteration through customers
        var outputRows = new List<Row>();
        foreach (var custRow in customers.Rows)
        {
            var customerId = Convert.ToInt32(custRow["id"]);
            var acctTypes = accountTypesByCustomer.GetValueOrDefault(customerId, new HashSet<string>());

            var hasChecking = acctTypes.Contains("Checking");
            var hasSavings = acctTypes.Contains("Savings");
            var hasCredit = acctTypes.Contains("Credit");
            var hasCard = customersWithCards.Contains(customerId);
            var hasInvestment = customersWithInvestments.Contains(customerId);

            var missing = new List<string>();
            if (!hasChecking) missing.Add("Checking");
            if (!hasSavings) missing.Add("Savings");
            if (!hasCredit) missing.Add("Credit");

            // AP5: Asymmetric NULL handling — no card → "No Card" string
            if (!hasCard) missing.Add("No Card");

            // AP5: Asymmetric NULL handling — no investment → 0 (different strategy)
            var investmentValue = hasInvestment ? 1 : 0;

            var missingProducts = missing.Count > 0 ? string.Join("; ", missing) : "None";

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = customerId,
                ["first_name"] = custRow["first_name"]?.ToString() ?? "",
                ["last_name"] = custRow["last_name"]?.ToString() ?? "",
                ["has_checking"] = hasChecking,
                ["has_savings"] = hasSavings,
                ["has_credit"] = hasCredit,
                ["has_card"] = hasCard ? "Yes" : "No Card",
                ["has_investment"] = investmentValue,
                ["missing_products"] = missingProducts,
                ["as_of"] = maxDate
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
