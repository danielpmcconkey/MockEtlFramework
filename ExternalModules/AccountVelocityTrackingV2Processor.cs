using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 minimal External module for AccountVelocityTracking.
/// Business logic is handled by the upstream SQL Transformation (Tier 2).
/// This module ONLY handles:
///   1. Injecting the as_of column (__maxEffectiveDate) — BR-5
///   2. W12: Direct CSV write with header re-emitted on every append run
///
/// Anti-patterns eliminated:
///   AP1  — credit_limit, apr removed from accounts DataSourcing (never used)
///   AP3  — Business logic moved from External to SQL Transformation (partial; External retained for W12)
///   AP4  — transaction_id, txn_timestamp, txn_type, description removed from transactions DataSourcing
///   AP6  — V1's foreach grouping + dictionary lookup replaced with SQL GROUP BY + LEFT JOIN
/// </summary>
public class AccountVelocityTrackingV2Processor : IExternalStep
{
    // Output column order must match V1 exactly
    private static readonly List<string> OutputColumns = new()
    {
        "account_id", "customer_id", "txn_date", "txn_count", "total_amount", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var transactions = sharedState.TryGetValue("transactions", out var txnVal)
            ? txnVal as DataFrame : null;
        var accounts = sharedState.TryGetValue("accounts", out var acctVal)
            ? acctVal as DataFrame : null;

        // V1 behavior (lines 18-23): if either input is null or empty, write header-only CSV.
        // On weekends, accounts data is absent (0 rows), so this guard fires and produces
        // a header-only append matching V1's empty-output behavior.
        if (transactions == null || transactions.Count == 0
            || accounts == null || accounts.Count == 0)
        {
            // W12: Even for empty input, append a header row to the CSV
            WriteDirectCsv(new List<Row>());
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];
        var dateStr = maxDate.ToString("yyyy-MM-dd");

        // Read the SQL-produced velocity_output from Transformation step.
        // The SQL handles all business logic: GROUP BY, COUNT, SUM, LEFT JOIN, COALESCE, ORDER BY.
        var velocityOutput = sharedState.TryGetValue("velocity_output", out var voVal)
            ? voVal as DataFrame : null;

        if (velocityOutput == null || velocityOutput.Count == 0)
        {
            // Defensive: if Transformation produced no output despite non-empty inputs,
            // write header-only CSV (same as V1 empty-output behavior)
            WriteDirectCsv(new List<Row>());
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // Inject as_of column (__maxEffectiveDate) per BR-5.
        // The SQL Transformation cannot access shared state scalar values,
        // so this column is appended here.
        var outputRows = new List<Row>();
        foreach (var row in velocityOutput.Rows)
        {
            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["account_id"] = row["account_id"],
                ["customer_id"] = row["customer_id"],
                ["txn_date"] = row["txn_date"],
                ["txn_count"] = row["txn_count"],
                ["total_amount"] = row["total_amount"],
                ["as_of"] = dateStr
            }));
        }

        // W12: Direct CSV write with header re-emitted on every append run.
        // Framework CsvFileWriter suppresses headers in append mode
        // (CsvFileWriter.cs:47: if (_includeHeader && !append)), so direct I/O is required.
        WriteDirectCsv(outputRows);

        // Set output to empty DataFrame — framework must not write output (BR-7).
        // No CsvFileWriter module exists in the job config, but this is defensive.
        sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
        return sharedState;
    }

    /// <summary>
    /// W12: Writes CSV in append mode with header re-emitted on every run.
    /// V1 behavior: each execution appends a header line followed by data rows,
    /// producing a file with interleaved headers among data across multi-day runs.
    /// </summary>
    private void WriteDirectCsv(List<Row> rows)
    {
        var solutionRoot = GetSolutionRoot();
        var outputPath = Path.Combine(solutionRoot,
            "Output", "double_secret_curated", "account_velocity_tracking.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // W12: Append mode with header re-emitted on every run.
        // V1 re-emits header on every append (AccountVelocityTracker.cs:84,88).
        // Framework CsvFileWriter suppresses headers in append mode, so direct I/O is required.
        using var writer = new StreamWriter(outputPath, append: true);
        writer.NewLine = "\n";

        // Header always written — this IS the W12 behavior
        writer.WriteLine(string.Join(",", OutputColumns));

        foreach (var row in rows)
        {
            var values = OutputColumns
                .Select(c => row[c]?.ToString() ?? "")
                .ToArray();
            writer.WriteLine(string.Join(",", values));
        }
    }

    private static string GetSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Solution root not found");
    }
}
