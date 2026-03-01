using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class HighRiskMerchantActivityProcessor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "card_txn_id", "merchant_name", "mcc_code", "mcc_description", "amount",
            "txn_timestamp", "as_of"
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
        var mccLookup = new Dictionary<string, (string description, string riskLevel)>();
        if (merchantCategories != null)
        {
            foreach (var mcc in merchantCategories.Rows)
            {
                var code = mcc["mcc_code"]?.ToString() ?? "";
                mccLookup[code] = (
                    mcc["mcc_description"]?.ToString() ?? "",
                    mcc["risk_level"]?.ToString() ?? ""
                );
            }
        }

        // AP6: Row-by-row iteration to join and filter (SQL would work)
        var outputRows = new List<Row>();
        foreach (var txn in cardTransactions.Rows)
        {
            var mccCode = txn["merchant_category_code"]?.ToString() ?? "";
            if (!mccLookup.ContainsKey(mccCode)) continue;

            var mccInfo = mccLookup[mccCode];
            // AP7: Magic value — hardcoded risk level string
            if (mccInfo.riskLevel == "High")
            {
                outputRows.Add(new Row(new Dictionary<string, object?>
                {
                    ["card_txn_id"] = txn["card_txn_id"],
                    ["merchant_name"] = txn["merchant_name"],
                    ["mcc_code"] = mccCode,
                    ["mcc_description"] = mccInfo.description,
                    ["amount"] = txn["amount"],
                    ["txn_timestamp"] = txn["txn_timestamp"],
                    ["as_of"] = txn["as_of"]
                }));
            }
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
