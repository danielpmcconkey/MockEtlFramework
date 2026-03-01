using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class PortfolioConcentrationCalculator : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "investment_id", "sector",
            "sector_value", "total_value", "sector_pct", "as_of"
        };

        var holdings = sharedState.ContainsKey("holdings") ? sharedState["holdings"] as DataFrame : null;
        var securities = sharedState.ContainsKey("securities") ? sharedState["securities"] as DataFrame : null;
        var investments = sharedState.ContainsKey("investments") ? sharedState["investments"] as DataFrame : null;

        if (holdings == null || holdings.Count == 0 || securities == null || securities.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];

        // Build security_id -> sector lookup
        var sectorLookup = new Dictionary<int, string>();
        foreach (var secRow in securities.Rows)
        {
            var secId = Convert.ToInt32(secRow["security_id"]);
            sectorLookup[secId] = secRow["sector"]?.ToString() ?? "Unknown";
        }

        // AP6: Row-by-row iteration with nested loops
        // First pass: compute total value per customer (using double for W6 epsilon errors)
        var customerTotalValue = new Dictionary<int, double>();
        foreach (var row in holdings.Rows)
        {
            var customerId = Convert.ToInt32(row["customer_id"]);
            // W6: Double arithmetic for accumulation (epsilon errors)
            double value = Convert.ToDouble(row["current_value"]);

            if (!customerTotalValue.ContainsKey(customerId))
                customerTotalValue[customerId] = 0.0;
            customerTotalValue[customerId] += value;
        }

        // Second pass: compute sector value per customer+investment (nested loops, row-by-row)
        var sectorValues = new Dictionary<(int customerId, int investmentId, string sector), double>();
        foreach (var row in holdings.Rows)
        {
            var customerId = Convert.ToInt32(row["customer_id"]);
            var investmentId = Convert.ToInt32(row["investment_id"]);
            var secId = Convert.ToInt32(row["security_id"]);
            var sector = sectorLookup.GetValueOrDefault(secId, "Unknown");
            double value = Convert.ToDouble(row["current_value"]);

            var key = (customerId, investmentId, sector);
            if (!sectorValues.ContainsKey(key))
                sectorValues[key] = 0.0;
            sectorValues[key] += value;
        }

        // Build output rows
        var outputRows = new List<Row>();
        foreach (var kvp in sectorValues)
        {
            var (customerId, investmentId, sector) = kvp.Key;
            double sectorValue = kvp.Value;
            double totalValue = customerTotalValue.GetValueOrDefault(customerId, 0.0);

            // W4: Integer division for percentage — int/int → 0
            int sectorInt = (int)sectorValue;
            int totalInt = (int)totalValue;
            decimal sectorPct = (decimal)(sectorInt / totalInt);

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = customerId,
                ["investment_id"] = investmentId,
                ["sector"] = sector,
                ["sector_value"] = sectorValue,
                ["total_value"] = totalValue,
                ["sector_pct"] = sectorPct,
                ["as_of"] = maxDate
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
