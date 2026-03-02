using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 replacement for CardTransactionDailyProcessor.
/// Handles decimal aggregation, Banker's rounding, and end-of-month MONTHLY_TOTAL row.
/// Receives pre-joined data from SQL Transformation (card_type already enriched via LEFT JOIN).
///
/// Anti-patterns eliminated:
///   AP1 — Dead-end sourcing (accounts, customers removed from DataSourcing)
///   AP3 — Unnecessary External scope reduced (JOIN moved to SQL Transformation; Tier 3 -> Tier 2)
///   AP4 — Unused columns removed from DataSourcing configs
///   AP6 — Row-by-row dictionary lookup replaced by SQL LEFT JOIN
///
/// Output-affecting wrinkles reproduced:
///   W3b — End-of-month MONTHLY_TOTAL summary row
///   W5  — Banker's rounding (MidpointRounding.ToEven) for avg_amount
/// </summary>
public class CardTransactionDailyV2Processor : IExternalStep
{
    // W3b: Label for the end-of-month summary row
    private const string MonthlyTotalLabel = "MONTHLY_TOTAL";

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "card_type", "txn_count", "total_amount", "avg_amount", "as_of"
        };

        var maxDate = sharedState.ContainsKey("__maxEffectiveDate")
            ? (DateOnly)sharedState["__maxEffectiveDate"]
            : DateOnly.FromDateTime(DateTime.Today);

        var enrichedTxns = sharedState.ContainsKey("enriched_txns")
            ? sharedState["enriched_txns"] as DataFrame
            : null;

        // BR-12: Empty input guard — return empty DataFrame with correct schema
        if (enrichedTxns == null || enrichedTxns.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // BR-10: Capture as_of from first row of enriched transactions
        var asOf = enrichedTxns.Rows[0]["as_of"];

        // Group by card_type with decimal accumulation for exact monetary arithmetic
        var groups = new Dictionary<string, (int count, decimal total)>();

        foreach (var row in enrichedTxns.Rows)
        {
            var cardType = row["card_type"]?.ToString() ?? "Unknown";
            var amount = Convert.ToDecimal(row["amount"]);

            if (!groups.ContainsKey(cardType))
                groups[cardType] = (0, 0m);

            var current = groups[cardType];
            groups[cardType] = (current.count + 1, current.total + amount);
        }

        // Build output rows with Banker's rounding for avg_amount
        var outputRows = new List<Row>();

        foreach (var kvp in groups)
        {
            // W5: Banker's rounding (MidpointRounding.ToEven) matches V1's Math.Round default
            var avgAmount = kvp.Value.count > 0
                ? Math.Round(kvp.Value.total / kvp.Value.count, 2, MidpointRounding.ToEven)
                : 0m;

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["card_type"] = kvp.Key,
                ["txn_count"] = kvp.Value.count,
                ["total_amount"] = kvp.Value.total,
                ["avg_amount"] = avgAmount,
                ["as_of"] = asOf
            }));
        }

        // W3b: End-of-month boundary — append MONTHLY_TOTAL summary row
        // when the effective date is the last day of its month
        if (maxDate.Day == DateTime.DaysInMonth(maxDate.Year, maxDate.Month))
        {
            int totalCount = groups.Values.Sum(g => g.count);
            decimal totalAmount = groups.Values.Sum(g => g.total);

            // W5: Same Banker's rounding for MONTHLY_TOTAL avg_amount
            var avgAmount = totalCount > 0
                ? Math.Round(totalAmount / totalCount, 2, MidpointRounding.ToEven)
                : 0m;

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["card_type"] = MonthlyTotalLabel,
                ["txn_count"] = totalCount,
                ["total_amount"] = totalAmount,
                ["avg_amount"] = avgAmount,
                ["as_of"] = asOf
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
