using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class LargeWireReportBuilder : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "wire_id", "customer_id", "first_name", "last_name",
            "direction", "amount", "counterparty_name", "status", "as_of"
        };

        var wireTransfers = sharedState.ContainsKey("wire_transfers") ? sharedState["wire_transfers"] as DataFrame : null;
        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;

        if (wireTransfers == null || wireTransfers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Build customer lookup
        var customerLookup = new Dictionary<int, (string firstName, string lastName)>();
        if (customers != null)
        {
            foreach (var custRow in customers.Rows)
            {
                var id = Convert.ToInt32(custRow["id"]);
                var firstName = custRow["first_name"]?.ToString() ?? "";
                var lastName = custRow["last_name"]?.ToString() ?? "";
                customerLookup[id] = (firstName, lastName);
            }
        }

        // AP7: magic value — hardcoded $10000 threshold
        var outputRows = new List<Row>();
        foreach (var row in wireTransfers.Rows)
        {
            var amount = Convert.ToDecimal(row["amount"]);

            if (amount > 10000)
            {
                var customerId = Convert.ToInt32(row["customer_id"]);
                var (firstName, lastName) = customerLookup.GetValueOrDefault(customerId, ("", ""));

                // W5: banker's rounding
                var roundedAmount = Math.Round(amount, 2, MidpointRounding.ToEven);

                outputRows.Add(new Row(new Dictionary<string, object?>
                {
                    ["wire_id"] = row["wire_id"],
                    ["customer_id"] = customerId,
                    ["first_name"] = firstName,
                    ["last_name"] = lastName,
                    ["direction"] = row["direction"],
                    ["amount"] = roundedAmount,
                    ["counterparty_name"] = row["counterparty_name"],
                    ["status"] = row["status"],
                    ["as_of"] = row["as_of"]
                }));
            }
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
