using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 replacement for AccountStatusCounter.
/// Groups accounts by (account_type, account_status) and counts per group.
/// Handles empty-data edge case (weekend dates with zero rows).
///
/// Anti-patterns eliminated:
///   AP1 — segments DataSourcing removed from V2 config (never referenced by V1 logic)
///   AP4 — customer_id and current_balance removed from V2 config (never used in output)
///   AP6 — V1's foreach + Dictionary replaced with LINQ GroupBy (set-based)
/// </summary>
public class AccountStatusSummaryV2Processor : IExternalStep
{
    private static readonly List<string> OutputColumns = new()
    {
        "account_type", "account_status", "account_count", "ifw_effective_date"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var accounts = sharedState.TryGetValue("accounts", out var val)
            ? val as DataFrame
            : null;

        // BR-4: Empty/null accounts produces empty DataFrame with correct schema.
        // This guards against weekend dates where the datalake has no account snapshots.
        // The Transformation module's RegisterTable() skips zero-row DataFrames
        // (Transformation.cs:47), which would cause "no such table" errors — this
        // External module exists specifically to handle that edge case gracefully.
        if (accounts == null || accounts.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // BR-3: ifw_effective_date from first accounts row, applied to all output rows.
        // All rows within a single effective date share the same ifw_effective_date value.
        // Preserved as raw DateOnly object so CsvFileWriter renders it as MM/dd/yyyy.
        var asOf = accounts.Rows[0]["ifw_effective_date"];

        // BR-1: Group by (account_type, account_status), count per group.
        // AP6 fix: LINQ set-based grouping replaces V1's foreach + Dictionary pattern.
        var outputRows = accounts.Rows
            .GroupBy(row => (
                type: row["account_type"]?.ToString() ?? "",
                status: row["account_status"]?.ToString() ?? ""
            ))
            .Select(group => new Row(new Dictionary<string, object?>
            {
                ["account_type"] = group.Key.type,
                ["account_status"] = group.Key.status,
                ["account_count"] = group.Count(),
                ["ifw_effective_date"] = asOf // Preserved as DateOnly for correct CsvFileWriter formatting
            }))
            .ToList();

        sharedState["output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
