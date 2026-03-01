using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CardCustomerSpendingProcessor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "first_name", "last_name", "txn_count", "total_spending", "as_of"
        };

        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];

        // W2: Weekend fallback — use Friday's data on Sat/Sun
        DateOnly targetDate = maxDate;
        if (maxDate.DayOfWeek == DayOfWeek.Saturday) targetDate = maxDate.AddDays(-1);
        else if (maxDate.DayOfWeek == DayOfWeek.Sunday) targetDate = maxDate.AddDays(-2);

        var cardTransactions = sharedState.ContainsKey("card_transactions")
            ? sharedState["card_transactions"] as DataFrame
            : null;
        var customers = sharedState.ContainsKey("customers")
            ? sharedState["customers"] as DataFrame
            : null;

        if (cardTransactions == null || cardTransactions.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Filter to target date
        var filteredTxns = cardTransactions.Rows
            .Where(r => ((DateOnly)r["as_of"]) == targetDate)
            .ToList();

        if (filteredTxns.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Build customer lookup
        var customerLookup = new Dictionary<int, (string first, string last)>();
        if (customers != null)
        {
            foreach (var c in customers.Rows)
            {
                var custId = Convert.ToInt32(c["id"]);
                customerLookup[custId] = (
                    c["first_name"]?.ToString() ?? "",
                    c["last_name"]?.ToString() ?? ""
                );
            }
        }

        // Group by customer
        var groups = new Dictionary<int, (int count, decimal total)>();
        foreach (var txn in filteredTxns)
        {
            var custId = Convert.ToInt32(txn["customer_id"]);
            var amount = Convert.ToDecimal(txn["amount"]);

            if (!groups.ContainsKey(custId))
                groups[custId] = (0, 0m);

            var current = groups[custId];
            groups[custId] = (current.count + 1, current.total + amount);
        }

        var outputRows = new List<Row>();
        foreach (var kvp in groups)
        {
            var name = customerLookup.GetValueOrDefault(kvp.Key, ("", ""));

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = kvp.Key,
                ["first_name"] = name.Item1,
                ["last_name"] = name.Item2,
                ["txn_count"] = kvp.Value.count,
                ["total_spending"] = kvp.Value.total,
                ["as_of"] = targetDate
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
