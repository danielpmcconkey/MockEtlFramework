using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class SuspiciousWireFlagProcessor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "wire_id", "customer_id", "direction", "amount",
            "counterparty_name", "status", "flag_reason", "as_of"
        };

        var wireTransfers = sharedState.ContainsKey("wire_transfers") ? sharedState["wire_transfers"] as DataFrame : null;

        // AP1: accounts sourced but never used (dead-end)
        // AP4: unused columns — counterparty_bank from wire_transfers, suffix from customers

        if (wireTransfers == null || wireTransfers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // AP7: magic values — hardcoded "OFFSHORE" and 50000
        var outputRows = new List<Row>();
        foreach (var row in wireTransfers.Rows)
        {
            var counterpartyName = row["counterparty_name"]?.ToString() ?? "";
            var amount = Convert.ToDecimal(row["amount"]);
            string? flagReason = null;

            if (counterpartyName.Contains("OFFSHORE"))
            {
                flagReason = "OFFSHORE_COUNTERPARTY";
            }
            else if (amount > 50000)
            {
                flagReason = "HIGH_AMOUNT";
            }

            if (flagReason != null)
            {
                outputRows.Add(new Row(new Dictionary<string, object?>
                {
                    ["wire_id"] = row["wire_id"],
                    ["customer_id"] = row["customer_id"],
                    ["direction"] = row["direction"],
                    ["amount"] = amount,
                    ["counterparty_name"] = counterpartyName,
                    ["status"] = row["status"],
                    ["flag_reason"] = flagReason,
                    ["as_of"] = row["as_of"]
                }));
            }
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
