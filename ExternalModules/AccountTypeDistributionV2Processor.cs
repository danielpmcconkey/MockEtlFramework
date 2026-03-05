using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 replacement for AccountDistributionCalculator.
/// Groups accounts by account_type and computes count + percentage of total.
///
/// Tier 2 justification: Transformation.RegisterTable skips empty DataFrames
/// (Transformation.cs:46), which causes "no such table: accounts" errors on
/// weekend dates when accounts has zero rows. This External module provides an
/// explicit empty-data guard. The date range 2024-10-01 to 2024-12-31 contains
/// 26 weekends, making this a guaranteed failure without the guard.
///
/// Anti-patterns addressed:
///   AP1 — Removed unused 'branches' DataSourcing entirely
///   AP4 — Sourcing only 'account_type' (removed account_id, customer_id, account_status, current_balance)
///   AP6 — LINQ GroupBy replaces V1's foreach + Dictionary pattern
/// </summary>
public class AccountTypeDistributionV2Processor : IExternalStep
{
    // Output columns match V1 exactly (AccountDistributionCalculator.cs:10-13)
    private static readonly List<string> OutputColumns = new()
    {
        "account_type", "account_count", "total_accounts", "percentage", "ifw_effective_date"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var accounts = sharedState.TryGetValue("accounts", out var val)
            ? val as DataFrame
            : null;

        // BR-6: Empty/null accounts produces empty DataFrame with correct schema.
        // This guard is the primary reason for Tier 2 escalation —
        // Transformation.RegisterTable skips empty DataFrames (Transformation.cs:46),
        // which would cause "no such table: accounts" errors on weekend dates.
        if (accounts == null || accounts.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // BR-5: ifw_effective_date from first accounts row, applied to all output rows.
        // Preserved as DateOnly for correct CsvFileWriter formatting (MM/dd/yyyy).
        var asOf = accounts.Rows[0]["ifw_effective_date"];
        var totalAccounts = accounts.Count;

        // BR-1, BR-2, BR-3: Group by account_type, compute count and percentage.
        // AP6 fix: LINQ set-based grouping replaces V1's foreach + Dictionary pattern.
        var outputRows = accounts.Rows
            .GroupBy(row => row["account_type"]?.ToString() ?? "")
            .Select(group => new Row(new Dictionary<string, object?>
            {
                ["account_type"] = group.Key,
                ["account_count"] = group.Count(),
                ["total_accounts"] = totalAccounts,
                // V1 uses (double)typeCount / totalAccounts * 100.0
                // (AccountDistributionCalculator.cs:41) — replicated exactly
                ["percentage"] = (double)group.Count() / totalAccounts * 100.0,
                ["ifw_effective_date"] = asOf // Preserved as DateOnly for correct CsvFileWriter formatting
            }))
            .ToList();

        sharedState["output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
