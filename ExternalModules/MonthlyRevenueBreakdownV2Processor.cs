using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 replacement for MonthlyRevenueBreakdownBuilder.
/// Reads pre-aggregated revenue_aggregates DataFrame from the Transformation step,
/// applies banker's rounding (W5), injects as_of from shared state (BR-9),
/// and conditionally appends Oct 31 quarterly summary rows (W3c).
///
/// Anti-patterns eliminated:
///   AP1 — customers DataSourcing removed from V2 config (never referenced by V1 logic)
///   AP4 — overdraft_id, account_id, customer_id, transaction_id removed (unused columns)
///   AP6 — V1's foreach row-by-row iteration replaced with SQL aggregation in Transformation step
///   AP3 — V1 Tier 3 (full External) reduced to Tier 2 (minimal External); filtering/aggregation moved to SQL
/// </summary>
public class MonthlyRevenueBreakdownV2Processor : IExternalStep
{
    // AP7: Named constants for fiscal quarter boundary
    // Fiscal quarter boundary: Q4 starts Nov 1, so Oct 31 is the last day of fiscal Q3
    private const int FiscalQ3EndMonth = 10;
    private const int FiscalQ3EndDay = 31;

    private static readonly List<string> OutputColumns = new()
    {
        "revenue_source", "total_revenue", "transaction_count", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        // BR-9: as_of from __maxEffectiveDate shared state, NOT from data rows
        // [MonthlyRevenueBreakdownBuilder.cs:18]
        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];

        // OQ-1: If Transformation failed due to empty source tables (no SQLite table created),
        // revenue_aggregates may not be in shared state. Default to 0/0 per V1 behavior (BR-10).
        // [MonthlyRevenueBreakdownBuilder.cs:21-22,37-38]
        var aggregates = sharedState.TryGetValue("revenue_aggregates", out var val)
            ? val as DataFrame
            : null;

        decimal overdraftRevenue;
        int overdraftCount;
        decimal creditRevenue;
        int creditCount;

        if (aggregates != null && aggregates.Count > 0)
        {
            var row = aggregates.Rows[0];
            overdraftRevenue = Convert.ToDecimal(row["overdraft_revenue"]);
            overdraftCount = Convert.ToInt32(row["overdraft_count"]);
            creditRevenue = Convert.ToDecimal(row["credit_revenue"]);
            creditCount = Convert.ToInt32(row["credit_count"]);
        }
        else
        {
            // BR-10: Default to 0 when no data
            overdraftRevenue = 0m;
            overdraftCount = 0;
            creditRevenue = 0m;
            creditCount = 0;
        }

        // W5: Banker's rounding (MidpointRounding.ToEven) -- correct for financial contexts,
        // explicitly replicating V1 behavior [MonthlyRevenueBreakdownBuilder.cs:58,64]
        var roundedOverdraftRevenue = Math.Round(overdraftRevenue, 2, MidpointRounding.ToEven);
        var roundedCreditRevenue = Math.Round(creditRevenue, 2, MidpointRounding.ToEven);

        // BR-4: Build daily rows (always present)
        var outputRows = new List<Row>
        {
            new Row(new Dictionary<string, object?>
            {
                ["revenue_source"] = "overdraft_fees",
                ["total_revenue"] = roundedOverdraftRevenue,
                ["transaction_count"] = overdraftCount,
                ["as_of"] = maxDate
            }),
            new Row(new Dictionary<string, object?>
            {
                ["revenue_source"] = "credit_interest_proxy",
                ["total_revenue"] = roundedCreditRevenue,
                ["transaction_count"] = creditCount,
                ["as_of"] = maxDate
            })
        };

        // W3c: Fiscal quarter boundary -- Oct 31 is last day of Q3.
        // Quarterly values duplicate the daily values (not accumulated quarter totals).
        // V1 evidence: [MonthlyRevenueBreakdownBuilder.cs:75-78] copies same-day values.
        if (maxDate.Month == FiscalQ3EndMonth && maxDate.Day == FiscalQ3EndDay)
        {
            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["revenue_source"] = "QUARTERLY_TOTAL_overdraft_fees",
                ["total_revenue"] = roundedOverdraftRevenue,
                ["transaction_count"] = overdraftCount,
                ["as_of"] = maxDate
            }));
            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["revenue_source"] = "QUARTERLY_TOTAL_credit_interest_proxy",
                ["total_revenue"] = roundedCreditRevenue,
                ["transaction_count"] = creditCount,
                ["as_of"] = maxDate
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
