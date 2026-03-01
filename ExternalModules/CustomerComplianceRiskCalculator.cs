using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class CustomerComplianceRiskCalculator : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "first_name", "last_name",
            "compliance_events", "wire_count", "high_txn_count", "risk_score", "as_of"
        };

        var complianceEvents = sharedState.ContainsKey("compliance_events") ? sharedState["compliance_events"] as DataFrame : null;
        var wireTransfers = sharedState.ContainsKey("wire_transfers") ? sharedState["wire_transfers"] as DataFrame : null;
        var transactions = sharedState.ContainsKey("transactions") ? sharedState["transactions"] as DataFrame : null;
        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;

        if (customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // AP3: unnecessary External — SQL could handle this
        // AP6: row-by-row iteration

        // Count compliance events per customer (row-by-row)
        var complianceCountByCustomer = new Dictionary<int, int>();
        if (complianceEvents != null)
        {
            foreach (var row in complianceEvents.Rows)
            {
                var customerId = Convert.ToInt32(row["customer_id"]);
                if (!complianceCountByCustomer.ContainsKey(customerId))
                    complianceCountByCustomer[customerId] = 0;
                complianceCountByCustomer[customerId]++;
            }
        }

        // Count wires per customer (row-by-row)
        var wireCountByCustomer = new Dictionary<int, int>();
        if (wireTransfers != null)
        {
            foreach (var row in wireTransfers.Rows)
            {
                var customerId = Convert.ToInt32(row["customer_id"]);
                if (!wireCountByCustomer.ContainsKey(customerId))
                    wireCountByCustomer[customerId] = 0;
                wireCountByCustomer[customerId]++;
            }
        }

        // Count high-value transactions per customer (amount > 5000, row-by-row)
        var highTxnCountByCustomer = new Dictionary<int, int>();
        if (transactions != null)
        {
            foreach (var row in transactions.Rows)
            {
                var amount = Convert.ToDecimal(row["amount"]);
                if (amount > 5000)
                {
                    var accountId = Convert.ToInt32(row["account_id"]);
                    // Note: transactions don't have customer_id directly; use account_id as proxy
                    // In this simplified model we use account_id as customer_id
                    if (!highTxnCountByCustomer.ContainsKey(accountId))
                        highTxnCountByCustomer[accountId] = 0;
                    highTxnCountByCustomer[accountId]++;
                }
            }
        }

        // Build output per customer
        var outputRows = new List<Row>();
        foreach (var custRow in customers.Rows)
        {
            var customerId = Convert.ToInt32(custRow["id"]);
            var firstName = custRow["first_name"]?.ToString() ?? "";
            var lastName = custRow["last_name"]?.ToString() ?? "";

            var complianceCount = complianceCountByCustomer.GetValueOrDefault(customerId, 0);
            var wireCount = wireCountByCustomer.GetValueOrDefault(customerId, 0);
            var highTxnCount = highTxnCountByCustomer.GetValueOrDefault(customerId, 0);

            // W6: double epsilon — use double arithmetic instead of decimal
            double riskScore = (complianceCount * 30.0) + (wireCount * 20.0) + (highTxnCount * 10.0);

            // W5: banker's rounding
            var roundedScore = Math.Round(riskScore, 2, MidpointRounding.ToEven);

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = customerId,
                ["first_name"] = firstName,
                ["last_name"] = lastName,
                ["compliance_events"] = complianceCount,
                ["wire_count"] = wireCount,
                ["high_txn_count"] = highTxnCount,
                ["risk_score"] = roundedScore,
                ["as_of"] = custRow["as_of"]
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
