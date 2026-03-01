using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CardTypeDistributionProcessor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "card_type", "card_count", "pct_of_total", "as_of"
        };

        var cards = sharedState.ContainsKey("cards")
            ? sharedState["cards"] as DataFrame
            : null;

        if (cards == null || cards.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        var asOf = cards.Rows[0]["as_of"];

        // Group by card_type
        var counts = new Dictionary<string, int>();
        foreach (var card in cards.Rows)
        {
            var cardType = card["card_type"]?.ToString() ?? "";
            if (!counts.ContainsKey(cardType))
                counts[cardType] = 0;
            counts[cardType]++;
        }

        int totalCards = cards.Count;

        // W6: Double epsilon — use double instead of decimal for percentage
        var outputRows = new List<Row>();
        foreach (var kvp in counts)
        {
            double pct = 0.0;
            double count = kvp.Value;
            double total = totalCards;
            pct = count / total;

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["card_type"] = kvp.Key,
                ["card_count"] = kvp.Value,
                ["pct_of_total"] = pct,
                ["as_of"] = asOf
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
