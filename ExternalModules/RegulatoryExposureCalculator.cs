using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class RegulatoryExposureCalculator : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "first_name", "last_name",
            "account_count", "total_balance", "compliance_events", "wire_count",
            "exposure_score", "as_of"
        };

        var complianceEvents = sharedState.ContainsKey("compliance_events") ? sharedState["compliance_events"] as DataFrame : null;
        var wireTransfers = sharedState.ContainsKey("wire_transfers") ? sharedState["wire_transfers"] as DataFrame : null;
        var accounts = sharedState.ContainsKey("accounts") ? sharedState["accounts"] as DataFrame : null;
        var customers = sharedState.ContainsKey("customers") ? sharedState["customers"] as DataFrame : null;

        if (customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // W2: Weekend fallback — use Friday's data on Sat/Sun
        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];
        DateOnly targetDate = maxDate;
        if (maxDate.DayOfWeek == DayOfWeek.Saturday) targetDate = maxDate.AddDays(-1);
        else if (maxDate.DayOfWeek == DayOfWeek.Sunday) targetDate = maxDate.AddDays(-2);

        // AP2: duplicated logic — re-derives compliance risk similar to job #26
        // AP6: row-by-row iteration

        // Filter all data to target date
        var targetCustomers = customers.Rows.Where(r => ((DateOnly)r["as_of"]) == targetDate).ToList();
        if (targetCustomers.Count == 0)
        {
            // Fall back to all rows if no exact date match
            targetCustomers = customers.Rows.ToList();
        }

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

        // Compute account count and total balance per customer (row-by-row)
        var accountCountByCustomer = new Dictionary<int, int>();
        var balanceByCustomer = new Dictionary<int, decimal>();
        if (accounts != null)
        {
            foreach (var row in accounts.Rows)
            {
                var customerId = Convert.ToInt32(row["customer_id"]);
                var balance = Convert.ToDecimal(row["current_balance"]);

                if (!accountCountByCustomer.ContainsKey(customerId))
                    accountCountByCustomer[customerId] = 0;
                accountCountByCustomer[customerId]++;

                if (!balanceByCustomer.ContainsKey(customerId))
                    balanceByCustomer[customerId] = 0m;
                balanceByCustomer[customerId] += balance;
            }
        }

        // Build output per customer
        var outputRows = new List<Row>();
        foreach (var custRow in targetCustomers)
        {
            var customerId = Convert.ToInt32(custRow["id"]);
            var firstName = custRow["first_name"]?.ToString() ?? "";
            var lastName = custRow["last_name"]?.ToString() ?? "";

            var accountCount = accountCountByCustomer.GetValueOrDefault(customerId, 0);
            var totalBalance = balanceByCustomer.GetValueOrDefault(customerId, 0m);
            var complianceCount = complianceCountByCustomer.GetValueOrDefault(customerId, 0);
            var wireCount = wireCountByCustomer.GetValueOrDefault(customerId, 0);

            // Exposure = (compliance_events * 30) + (wire_count * 20) + (total_balance / 10000)
            var exposureScore = Math.Round(
                (complianceCount * 30.0m) + (wireCount * 20.0m) + (totalBalance / 10000.0m), 2);

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = customerId,
                ["first_name"] = firstName,
                ["last_name"] = lastName,
                ["account_count"] = accountCount,
                ["total_balance"] = Math.Round(totalBalance, 2),
                ["compliance_events"] = complianceCount,
                ["wire_count"] = wireCount,
                ["exposure_score"] = exposureScore,
                ["as_of"] = targetDate
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
