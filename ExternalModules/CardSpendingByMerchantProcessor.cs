using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CardSpendingByMerchantProcessor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "mcc_code", "mcc_description", "txn_count", "total_spending", "as_of"
        };

        var cardTransactions = sharedState.ContainsKey("card_transactions")
            ? sharedState["card_transactions"] as DataFrame
            : null;
        var merchantCategories = sharedState.ContainsKey("merchant_categories")
            ? sharedState["merchant_categories"] as DataFrame
            : null;

        if (cardTransactions == null || cardTransactions.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Build MCC lookup
        var mccLookup = new Dictionary<string, string>();
        if (merchantCategories != null)
        {
            foreach (var mcc in merchantCategories.Rows)
            {
                var code = mcc["mcc_code"]?.ToString() ?? "";
                var desc = mcc["mcc_description"]?.ToString() ?? "";
                mccLookup[code] = desc;
            }
        }

        var asOf = cardTransactions.Rows[0]["as_of"];

        // AP6: Row-by-row iteration to group by MCC, when SQL GROUP BY would work
        var groups = new Dictionary<string, (int count, decimal total)>();
        foreach (var txn in cardTransactions.Rows)
        {
            var mccCode = txn["merchant_category_code"]?.ToString() ?? "";
            var amount = Convert.ToDecimal(txn["amount"]);

            if (!groups.ContainsKey(mccCode))
                groups[mccCode] = (0, 0m);

            var current = groups[mccCode];
            groups[mccCode] = (current.count + 1, current.total + amount);
        }

        var outputRows = new List<Row>();
        foreach (var kvp in groups)
        {
            var desc = mccLookup.ContainsKey(kvp.Key) ? mccLookup[kvp.Key] : "";
            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["mcc_code"] = kvp.Key,
                ["mcc_description"] = desc,
                ["txn_count"] = kvp.Value.count,
                ["total_spending"] = kvp.Value.total,
                ["as_of"] = asOf
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
