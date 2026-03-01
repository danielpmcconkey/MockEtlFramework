using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CardTransactionDailyProcessor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "card_type", "txn_count", "total_amount", "avg_amount", "as_of"
        };

        var maxDate = sharedState.ContainsKey("__maxEffectiveDate")
            ? (DateOnly)sharedState["__maxEffectiveDate"]
            : DateOnly.FromDateTime(DateTime.Today);

        var cardTransactions = sharedState.ContainsKey("card_transactions")
            ? sharedState["card_transactions"] as DataFrame
            : null;
        var cards = sharedState.ContainsKey("cards")
            ? sharedState["cards"] as DataFrame
            : null;

        // AP1: accounts and customers sourced but never used (dead-end)

        if (cardTransactions == null || cardTransactions.Count == 0 || cards == null || cards.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Build card_id -> card_type lookup
        var cardTypeLookup = new Dictionary<int, string>();
        foreach (var card in cards.Rows)
        {
            var cardId = Convert.ToInt32(card["card_id"]);
            var cardType = card["card_type"]?.ToString() ?? "";
            cardTypeLookup[cardId] = cardType;
        }

        // Group transactions by card_type — daily rows
        var groups = new Dictionary<string, (int count, decimal total)>();
        var asOf = cardTransactions.Rows[0]["as_of"];

        foreach (var txn in cardTransactions.Rows)
        {
            var cardId = Convert.ToInt32(txn["card_id"]);
            var amount = Convert.ToDecimal(txn["amount"]);
            var cardType = cardTypeLookup.ContainsKey(cardId) ? cardTypeLookup[cardId] : "Unknown";

            if (!groups.ContainsKey(cardType))
                groups[cardType] = (0, 0m);

            var current = groups[cardType];
            groups[cardType] = (current.count + 1, current.total + amount);
        }

        var outputRows = new List<Row>();
        foreach (var kvp in groups)
        {
            var avgAmount = kvp.Value.count > 0
                ? Math.Round(kvp.Value.total / kvp.Value.count, 2)
                : 0m;

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["card_type"] = kvp.Key,
                ["txn_count"] = kvp.Value.count,
                ["total_amount"] = kvp.Value.total,
                ["avg_amount"] = avgAmount,
                ["as_of"] = asOf
            }));
        }

        // W3b: End-of-month boundary — append monthly summary row
        if (maxDate.Day == DateTime.DaysInMonth(maxDate.Year, maxDate.Month))
        {
            int totalCount = groups.Values.Sum(g => g.count);
            decimal totalAmount = groups.Values.Sum(g => g.total);

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["card_type"] = "MONTHLY_TOTAL",
                ["txn_count"] = totalCount,
                ["total_amount"] = totalAmount,
                ["avg_amount"] = totalCount > 0 ? Math.Round(totalAmount / totalCount, 2) : 0m,
                ["as_of"] = asOf
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
