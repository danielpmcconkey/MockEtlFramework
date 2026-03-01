using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class BondMaturityScheduleBuilder : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "security_id", "ticker", "security_name", "sector",
            "total_held_value", "holder_count", "as_of"
        };

        var securities = sharedState.ContainsKey("securities") ? sharedState["securities"] as DataFrame : null;
        var holdings = sharedState.ContainsKey("holdings") ? sharedState["holdings"] as DataFrame : null;

        if (securities == null || securities.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];

        // AP3: Unnecessary External — this could be done in SQL
        // Filter to bonds only
        var bonds = securities.Rows
            .Where(r => r["security_type"]?.ToString() == "Bond")
            .ToList();

        if (bonds.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Build bond lookup
        var bondLookup = new Dictionary<int, (string ticker, string name, string sector)>();
        foreach (var bond in bonds)
        {
            var secId = Convert.ToInt32(bond["security_id"]);
            bondLookup[secId] = (
                bond["ticker"]?.ToString() ?? "",
                bond["security_name"]?.ToString() ?? "",
                bond["sector"]?.ToString() ?? ""
            );
        }

        // AP6: Row-by-row iteration to join with holdings
        var bondTotals = new Dictionary<int, (decimal totalValue, int holderCount)>();
        if (holdings != null)
        {
            foreach (var row in holdings.Rows)
            {
                var secId = Convert.ToInt32(row["security_id"]);
                if (!bondLookup.ContainsKey(secId)) continue;

                var value = Convert.ToDecimal(row["current_value"]);

                if (!bondTotals.ContainsKey(secId))
                    bondTotals[secId] = (0m, 0);

                var current = bondTotals[secId];
                bondTotals[secId] = (current.totalValue + value, current.holderCount + 1);
            }
        }

        var outputRows = new List<Row>();
        foreach (var bond in bonds)
        {
            var secId = Convert.ToInt32(bond["security_id"]);
            var info = bondLookup[secId];
            var totals = bondTotals.ContainsKey(secId)
                ? bondTotals[secId]
                : (totalValue: 0m, holderCount: 0);

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["security_id"] = secId,
                ["ticker"] = info.ticker,
                ["security_name"] = info.name,
                ["sector"] = info.sector,
                ["total_held_value"] = Math.Round(totals.totalValue, 2),
                ["holder_count"] = totals.holderCount,
                ["as_of"] = maxDate
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
