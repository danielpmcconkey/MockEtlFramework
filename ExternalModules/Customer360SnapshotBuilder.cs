using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class Customer360SnapshotBuilder : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "first_name", "last_name",
            "account_count", "total_balance", "card_count",
            "investment_count", "total_investment_value", "as_of"
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

        // W2: Weekend fallback to Friday
        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];
        DateOnly targetDate = maxDate;
        if (maxDate.DayOfWeek == DayOfWeek.Saturday) targetDate = maxDate.AddDays(-1);
        else if (maxDate.DayOfWeek == DayOfWeek.Sunday) targetDate = maxDate.AddDays(-2);

        // Filter customers to target date
        var filteredCustomers = customers.Rows.Where(r => ((DateOnly)r["as_of"]) == targetDate).ToList();

        // Build per-customer account counts and balances
        var accountCountByCustomer = new Dictionary<int, int>();
        var balanceByCustomer = new Dictionary<int, decimal>();
        if (accounts != null)
        {
            foreach (var row in accounts.Rows.Where(r => ((DateOnly)r["as_of"]) == targetDate))
            {
                var custId = Convert.ToInt32(row["customer_id"]);
                accountCountByCustomer[custId] = accountCountByCustomer.GetValueOrDefault(custId, 0) + 1;
                balanceByCustomer[custId] = balanceByCustomer.GetValueOrDefault(custId, 0m) + Convert.ToDecimal(row["current_balance"]);
            }
        }

        // Build per-customer card counts
        var cardCountByCustomer = new Dictionary<int, int>();
        if (cards != null)
        {
            foreach (var row in cards.Rows.Where(r => ((DateOnly)r["as_of"]) == targetDate))
            {
                var custId = Convert.ToInt32(row["customer_id"]);
                cardCountByCustomer[custId] = cardCountByCustomer.GetValueOrDefault(custId, 0) + 1;
            }
        }

        // Build per-customer investment counts and values
        var investmentCountByCustomer = new Dictionary<int, int>();
        var investmentValueByCustomer = new Dictionary<int, decimal>();
        if (investments != null)
        {
            foreach (var row in investments.Rows.Where(r => ((DateOnly)r["as_of"]) == targetDate))
            {
                var custId = Convert.ToInt32(row["customer_id"]);
                investmentCountByCustomer[custId] = investmentCountByCustomer.GetValueOrDefault(custId, 0) + 1;
                investmentValueByCustomer[custId] = investmentValueByCustomer.GetValueOrDefault(custId, 0m) + Convert.ToDecimal(row["current_value"]);
            }
        }

        // AP6: Row-by-row iteration building full customer view
        var outputRows = new List<Row>();
        foreach (var custRow in filteredCustomers)
        {
            var customerId = Convert.ToInt32(custRow["id"]);

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = customerId,
                ["first_name"] = custRow["first_name"]?.ToString() ?? "",
                ["last_name"] = custRow["last_name"]?.ToString() ?? "",
                ["account_count"] = accountCountByCustomer.GetValueOrDefault(customerId, 0),
                ["total_balance"] = Math.Round(balanceByCustomer.GetValueOrDefault(customerId, 0m), 2),
                ["card_count"] = cardCountByCustomer.GetValueOrDefault(customerId, 0),
                ["investment_count"] = investmentCountByCustomer.GetValueOrDefault(customerId, 0),
                ["total_investment_value"] = Math.Round(investmentValueByCustomer.GetValueOrDefault(customerId, 0m), 2),
                ["as_of"] = targetDate
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
