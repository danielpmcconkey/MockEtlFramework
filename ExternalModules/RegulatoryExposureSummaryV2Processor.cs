using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 minimal External module for RegulatoryExposureSummary.
/// Receives pre-aggregated data from the SQL Transformation step and applies ONLY:
///   1. Banker's rounding on total_balance (BR-6, W5)
///   2. Decimal arithmetic for exposure_score formula (BR-4, BR-5, W5)
///   3. ifw_effective_date assignment from target_date (BR-10)
///
/// All aggregation, joining, customer filtering, weekend fallback, and NULL coalescing
/// are handled in the upstream Transformation SQL.
///
/// Anti-patterns eliminated:
///   AP3 — Reduced from full External (Tier 3) to minimal External (Tier 2);
///          aggregation/joining/filtering moved to SQL Transformation
///   AP4 — Unused columns removed from DataSourcing configs
///   AP6 — V1's row-by-row Dictionary accumulation replaced with SQL GROUP BY
///   AP7 — Magic values replaced with named constants (ComplianceWeight, WireWeight, BalanceDivisor)
///
/// Wrinkles preserved:
///   W2 — Weekend fallback (handled in SQL, target_date passed through)
///   W5 — Banker's rounding via Math.Round(decimal, 2) with MidpointRounding.ToEven
/// </summary>
public class RegulatoryExposureSummaryV2Processor : IExternalStep
{
    // AP7: Named constants for exposure score formula weights
    // BR-4: exposure_score = (compliance_events * 30) + (wire_count * 20) + (total_balance / 10000)
    private const decimal ComplianceWeight = 30.0m;
    private const decimal WireWeight = 20.0m;
    private const decimal BalanceDivisor = 10000.0m;

    private static readonly List<string> OutputColumns = new()
    {
        "customer_id", "first_name", "last_name", "account_count",
        "total_balance", "compliance_events", "wire_count", "exposure_score", "ifw_effective_date"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var intermediate = sharedState.TryGetValue("output", out var val)
            ? val as DataFrame
            : null;

        // BR-9: Empty input guard — if no rows from Transformation, return empty DataFrame
        if (intermediate == null || intermediate.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        var outputRows = new List<Row>();
        foreach (var row in intermediate.Rows)
        {
            var customerId = Convert.ToInt32(row["customer_id"]);
            var firstName = row["first_name"]?.ToString() ?? "";
            var lastName = row["last_name"]?.ToString() ?? "";
            var accountCount = Convert.ToInt32(row["account_count"]);
            var rawTotalBalance = Convert.ToDecimal(row["raw_total_balance"]);
            var complianceEvents = Convert.ToInt32(row["compliance_events"]);
            var wireCount = Convert.ToInt32(row["wire_count"]);
            var targetDate = row["target_date"]?.ToString();

            // BR-6, W5: Banker's rounding on total_balance
            // Math.Round(decimal, int) defaults to MidpointRounding.ToEven
            var totalBalance = Math.Round(rawTotalBalance, 2);

            // BR-4, BR-5, W5: Exposure score with decimal arithmetic + banker's rounding
            // Formula: (compliance_events * 30) + (wire_count * 20) + (total_balance / 10000)
            var exposureScore = Math.Round(
                (complianceEvents * ComplianceWeight)
                + (wireCount * WireWeight)
                + (totalBalance / BalanceDivisor),
                2);

            // BR-10: ifw_effective_date = target date (after weekend fallback, computed in SQL)
            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = customerId,
                ["first_name"] = firstName,
                ["last_name"] = lastName,
                ["account_count"] = accountCount,
                ["total_balance"] = totalBalance,
                ["compliance_events"] = complianceEvents,
                ["wire_count"] = wireCount,
                ["exposure_score"] = exposureScore,
                ["ifw_effective_date"] = DateOnly.Parse(targetDate!)
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
