using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CardFraudFlagsProcessor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "card_txn_id", "card_id", "customer_id", "merchant_name", "mcc_code",
            "risk_level", "amount", "txn_timestamp", "as_of"
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

        // Build MCC -> risk_level lookup
        var riskLookup = new Dictionary<string, string>();
        if (merchantCategories != null)
        {
            foreach (var mcc in merchantCategories.Rows)
            {
                var code = mcc["mcc_code"]?.ToString() ?? "";
                var risk = mcc["risk_level"]?.ToString() ?? "";
                riskLookup[code] = risk;
            }
        }

        var outputRows = new List<Row>();
        foreach (var txn in cardTransactions.Rows)
        {
            var mccCode = txn["merchant_category_code"]?.ToString() ?? "";
            var riskLevel = riskLookup.ContainsKey(mccCode) ? riskLookup[mccCode] : "";
            // W5: Banker's rounding on amount
            var amount = Math.Round(Convert.ToDecimal(txn["amount"]), 2, MidpointRounding.ToEven);

            // AP7: Magic value — hardcoded $500 threshold
            if (riskLevel == "High" && amount > 500m)
            {
                outputRows.Add(new Row(new Dictionary<string, object?>
                {
                    ["card_txn_id"] = txn["card_txn_id"],
                    ["card_id"] = txn["card_id"],
                    ["customer_id"] = txn["customer_id"],
                    ["merchant_name"] = txn["merchant_name"],
                    ["mcc_code"] = mccCode,
                    ["risk_level"] = riskLevel,
                    ["amount"] = amount,
                    ["txn_timestamp"] = txn["txn_timestamp"],
                    ["as_of"] = txn["as_of"]
                }));
            }
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
