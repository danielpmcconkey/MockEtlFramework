# AccountVelocityTracking — Functional Specification Document

## 1. Overview

AccountVelocityTrackingV2 tracks daily transaction velocity per account, aggregating transaction counts and total amounts grouped by account and transaction date, enriched with customer_id via an accounts lookup. The V2 implementation uses **Tier 2 (Framework + Minimal External)** because:

- **The business logic is pure SQL** — GROUP BY, COUNT, SUM, LEFT JOIN with COALESCE for default values. This eliminates the V1 External module's row-by-row iteration (AP6).
- **The I/O quirk (W12) requires an External module.** V1 writes CSV directly in append mode, re-emitting the header on every run. The framework's CsvFileWriter suppresses headers in append mode (`CsvFileWriter.cs:47`: `if (_includeHeader && !append)`), making it impossible to replicate W12 behavior with the framework writer alone.
- **The External module is minimal** — it receives a fully-formed DataFrame from the Transformation step and only handles the direct CSV write with header-every-append behavior. Zero business logic in the External.

**Module chain:** DataSourcing (transactions) → DataSourcing (accounts) → Transformation (SQL) → External (W12 direct CSV write)

## 2. V2 Module Chain

### Module 1: DataSourcing — transactions

| Property | Value |
|----------|-------|
| type | DataSourcing |
| resultName | transactions |
| schema | datalake |
| table | transactions |
| columns | `["account_id", "amount"]` |

**Changes from V1:**
- Removed `transaction_id`, `txn_timestamp`, `txn_type`, `description` — none are used in the grouping or output logic (AP4 elimination). The V1 External module only reads `account_id`, `amount`, and the framework-injected `as_of` column.
- The `as_of` column is automatically appended by the DataSourcing module when not explicitly listed (per `DataSourcing.cs:69-72`).

### Module 2: DataSourcing — accounts

| Property | Value |
|----------|-------|
| type | DataSourcing |
| resultName | accounts |
| schema | datalake |
| table | accounts |
| columns | `["account_id", "customer_id"]` |

**Changes from V1:**
- Removed `credit_limit`, `apr` — sourced in V1 but never used (AP1 + AP4 elimination, per BR-9).

### Module 3: Transformation — velocity aggregation

| Property | Value |
|----------|-------|
| type | Transformation |
| resultName | velocity_output |
| sql | See SQL Design (Section 5) |

This replaces the V1 External module's foreach-based grouping logic (AP3 partial + AP6 elimination). All business rules (BR-1 through BR-6) are implemented in SQL.

### Module 4: External — W12 direct CSV writer

| Property | Value |
|----------|-------|
| type | External |
| assemblyPath | `/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll` |
| typeName | `ExternalModules.AccountVelocityTrackingV2Processor` |

**Responsibility:** Reads the `velocity_output` DataFrame from shared state, writes it as CSV to `Output/double_secret_curated/account_velocity_tracking.csv` in append mode with the header re-emitted on every run (W12). Sets `sharedState["output"]` to an empty DataFrame so the framework does not attempt to write output.

This External module contains ZERO business logic. It is purely an I/O adapter for the W12 quirk.

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (Reproduce)

| W-Code | Applies? | V1 Behavior | V2 Approach |
|--------|----------|-------------|-------------|
| W12 | **YES** | External module writes CSV in append mode, re-emitting the header row on every execution | Reproduced in the V2 External module. The framework's CsvFileWriter cannot replicate this behavior (`CsvFileWriter.cs:47` suppresses headers in append mode). The V2 External uses `StreamWriter(path, append: true)` with an unconditional header write, matching V1 exactly. Code includes comment: `// W12: V1 re-emits header on every append run. Framework CsvFileWriter suppresses headers in append mode, so direct I/O is required.` |

### Code-Quality Anti-Patterns (Eliminate)

| AP-Code | Applies? | V1 Problem | V2 Elimination |
|---------|----------|------------|----------------|
| AP1 | **YES** | `credit_limit` and `apr` sourced from accounts but never used (BR-9) | Removed from V2 DataSourcing config. Only `account_id` and `customer_id` are sourced. |
| AP3 | **PARTIAL** | V1 uses a full External module where most logic could be SQL | Business logic moved to SQL Transformation (Tier 2). External retained ONLY for W12 I/O quirk. |
| AP4 | **YES** | `transaction_id`, `txn_timestamp`, `txn_type`, `description` sourced but never used | Removed from V2 DataSourcing config. Only `account_id` and `amount` are sourced (plus framework-injected `as_of`). |
| AP6 | **YES** | `foreach` loops for grouping transactions and building lookup dictionary | Replaced with SQL `GROUP BY`, `LEFT JOIN`, and `COALESCE`. |

## 4. Output Schema

| Column | Type | Source | Transformation | BRD Ref |
|--------|------|--------|---------------|---------|
| account_id | INTEGER | transactions.account_id | Group key, cast to integer via SQL CAST | BR-1 |
| customer_id | INTEGER | accounts.customer_id | LEFT JOIN on account_id; COALESCE to 0 if no match | BR-2 |
| txn_date | TEXT | transactions.as_of | Group key, converted to string via SQL CAST | BR-6 |
| txn_count | INTEGER | Computed | COUNT(*) per (account_id, as_of) group | BR-1 |
| total_amount | REAL | transactions.amount | SUM per group, ROUND to 2 decimal places | BR-3 |
| as_of | TEXT | __maxEffectiveDate | Injected from shared state in the External module, formatted as yyyy-MM-dd | BR-5 |

### Notes on `as_of` column

The output `as_of` column is set to `__maxEffectiveDate` (BR-5), NOT the transaction's `as_of` date. This value cannot be injected via SQL alone because the Transformation module's SQLite environment does not have access to the `__maxEffectiveDate` shared state key as a scalar value — it only registers DataFrames as tables.

**Design decision:** The SQL query produces the first 5 columns (account_id through total_amount). The External module appends the `as_of` column using the `__maxEffectiveDate` value from shared state. This keeps the SQL clean and avoids the need to fabricate a single-row table just to pass a date value into SQL.

**Alternative considered:** Create a single-row DataFrame with the maxEffectiveDate value before Transformation, register it as a SQLite table, and cross-join in SQL. This was rejected as unnecessarily complex for a single scalar value — the External module already exists for W12 and can trivially add this column.

## 5. SQL Design

```sql
SELECT
    CAST(t.account_id AS INTEGER) AS account_id,
    COALESCE(a.customer_id, 0) AS customer_id,
    CAST(t.as_of AS TEXT) AS txn_date,
    COUNT(*) AS txn_count,
    ROUND(SUM(CAST(t.amount AS REAL)), 2) AS total_amount
FROM transactions t
LEFT JOIN (
    SELECT DISTINCT account_id, customer_id
    FROM accounts
) a ON CAST(t.account_id AS INTEGER) = CAST(a.account_id AS INTEGER)
GROUP BY t.account_id, t.as_of
ORDER BY CAST(t.as_of AS TEXT) ASC, CAST(t.account_id AS INTEGER) ASC
```

### SQL Design Rationale

1. **LEFT JOIN with DISTINCT subquery for accounts** (BR-2): The accounts DataSourcing may return multiple rows per account_id (one per as_of date in the effective date range). The V1 External builds a dictionary keyed by account_id, meaning later rows overwrite earlier ones. Using `DISTINCT account_id, customer_id` produces the same result — the customer_id for a given account_id does not change across snapshot dates, so DISTINCT correctly deduplicates. The LEFT JOIN with COALESCE(customer_id, 0) replicates the `GetValueOrDefault(accountId, 0)` behavior.

2. **GROUP BY account_id, as_of** (BR-1): Groups transactions by the composite key of account_id and transaction date (as_of). COUNT(*) gives txn_count, SUM(amount) gives total_amount.

3. **ROUND(SUM(...), 2)** (BR-3): V1 uses `Math.Round(total, 2)` which defaults to `MidpointRounding.ToEven` (banker's rounding). SQLite's ROUND function also uses banker's rounding, so this produces identical results.

4. **ORDER BY as_of ASC, account_id ASC** (BR-4): Matches V1's `.OrderBy(k => k.Key.txnDate).ThenBy(k => k.Key.accountId)`.

5. **CAST(t.as_of AS TEXT) AS txn_date** (BR-6): The V1 code uses `row["as_of"]?.ToString()` to convert the as_of value to a string for the txn_date column. The DataSourcing module returns `as_of` as a `DateOnly` value, which SQLite stores as TEXT in `yyyy-MM-dd` format (per `Transformation.cs:110`: `DateOnly d => d.ToString("yyyy-MM-dd")`). The CAST ensures consistent string representation.

6. **No as_of output column in SQL**: The output `as_of` column is __maxEffectiveDate, not a per-row value from transactions. This is injected by the External module (see Section 4 notes).

### Edge Case: Null as_of fallback (BR-6)

V1 falls back to `dateStr` (maxEffectiveDate) if `row["as_of"]` is null. In practice, the DataSourcing module filters by `as_of >= @minDate AND as_of <= @maxDate`, so null as_of rows are excluded at the source. This fallback is effectively dead code in V1. The SQL query naturally excludes null as_of rows via the WHERE clause in DataSourcing, producing identical behavior.

### Edge Case: Weekend accounts (BRD Edge Cases)

Accounts data does not exist on weekends. When the effective date is a weekend day, the accounts DataSourcing may return 0 rows. If the accounts table is empty, the DISTINCT subquery returns 0 rows, and the LEFT JOIN produces NULL for customer_id, which COALESCE converts to 0. This matches V1's behavior where an empty accountToCustomer dictionary causes all lookups to return `GetValueOrDefault(accountId, 0)` = 0.

**However**, note that the Transformation module's `RegisterTable` method (`Transformation.cs:46`) returns early without creating a table if the DataFrame has no rows: `if (!df.Rows.Any()) return;`. If the accounts DataFrame is empty, the `accounts` table will not be registered in SQLite, and the LEFT JOIN will fail with a "no such table: accounts" error.

**Mitigation:** The V2 External module must handle this edge case. If `velocity_output` is not in shared state (because the Transformation SQL failed due to missing accounts table), the External module should fall back to writing output with customer_id = 0 for all rows. Alternatively, we can restructure the SQL to avoid referencing the accounts table when it might not exist.

**Chosen approach:** The External module will check if `velocity_output` exists in shared state. The V1 code also handles empty accounts/transactions at lines 18-23 by writing an empty CSV. Since V1 checks `accounts.Count == 0` and returns early with an empty output, V2 must replicate this: if either source DataFrame is empty, write just the header (no data rows). The External module handles this check before attempting to read `velocity_output`.

**Revised design:** The External module must handle two scenarios:
1. If transactions or accounts DataFrames are empty → write header-only CSV (matching V1 lines 18-23)
2. If both have data → the Transformation SQL produces `velocity_output`, and the External writes it with the as_of column appended

## 6. V2 Job Config

```json
{
  "jobName": "AccountVelocityTrackingV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "transactions",
      "schema": "datalake",
      "table": "transactions",
      "columns": ["account_id", "amount"]
    },
    {
      "type": "DataSourcing",
      "resultName": "accounts",
      "schema": "datalake",
      "table": "accounts",
      "columns": ["account_id", "customer_id"]
    },
    {
      "type": "Transformation",
      "resultName": "velocity_output",
      "sql": "SELECT CAST(t.account_id AS INTEGER) AS account_id, COALESCE(a.customer_id, 0) AS customer_id, CAST(t.as_of AS TEXT) AS txn_date, COUNT(*) AS txn_count, ROUND(SUM(CAST(t.amount AS REAL)), 2) AS total_amount FROM transactions t LEFT JOIN (SELECT DISTINCT account_id, customer_id FROM accounts) a ON CAST(t.account_id AS INTEGER) = CAST(a.account_id AS INTEGER) GROUP BY t.account_id, t.as_of ORDER BY CAST(t.as_of AS TEXT) ASC, CAST(t.account_id AS INTEGER) ASC"
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.AccountVelocityTrackingV2Processor"
    }
  ]
}
```

## 7. Writer Configuration

| Property | V1 Value | V2 Value | Notes |
|----------|----------|----------|-------|
| Writer type | Direct file I/O (External) | Direct file I/O (External) | Same — framework CsvFileWriter cannot replicate W12 |
| Output path | `Output/curated/account_velocity_tracking.csv` | `Output/double_secret_curated/account_velocity_tracking.csv` | Path change per V2 convention |
| Write mode | Append (`StreamWriter` with `append: true`) | Append (`StreamWriter` with `append: true`) | Same — data accumulates across runs |
| Line ending | LF (`writer.NewLine = "\n"`) | LF (`writer.NewLine = "\n"`) | Same |
| Header | Re-emitted on every run (W12) | Re-emitted on every run (W12) | Same — this is the reason for the External module |
| Trailer | None | None | Same |
| Encoding | Default (UTF-8) | UTF-8 (no BOM) | Same |

## 8. Proofmark Config Design

```yaml
comparison_target: "account_velocity_tracking"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

### Rationale

- **reader: csv** — V1 outputs a CSV file via direct file I/O.
- **header_rows: 1** — The file has a header row. In append mode with W12, the file has MULTIPLE header rows interspersed with data. Setting `header_rows: 1` strips only the first header from the top of the file. The subsequent embedded headers are part of the data and will appear in both V1 and V2 output identically (both re-emit headers on every run), so they will match during comparison.
- **trailer_rows: 0** — No trailer is produced (BR-7 / BRD).
- **No exclusions** — All fields are deterministic (BRD: "Non-Deterministic Fields: None identified").
- **No fuzzy columns** — V1 uses `decimal` for monetary accumulation and `Math.Round(total, 2)` for rounding (BR-3). SQLite's ROUND function with REAL arithmetic should produce identical results. If comparison reveals epsilon differences, a fuzzy tolerance on `total_amount` would be the appropriate fix, but we start strict per best practice.

## 9. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|-------------|----------------|----------|
| DataSourcing: transactions with `[account_id, amount]` | BR-1 (group by account_id), BR-3 (sum of amounts) | AccountVelocityTracker.cs:32,41,49 |
| DataSourcing: accounts with `[account_id, customer_id]` | BR-2 (customer_id lookup), BR-9 (credit_limit/apr unused) | AccountVelocityTracker.cs:29-35 |
| Removed credit_limit, apr from accounts (AP1/AP4) | BR-9 | AccountVelocityTracker.cs:28-35 — only account_id and customer_id extracted |
| Removed transaction_id, txn_timestamp, txn_type, description (AP4) | BR-1 — only account_id, as_of, amount used | AccountVelocityTracker.cs:38-49 |
| SQL GROUP BY account_id, as_of | BR-1 | AccountVelocityTracker.cs:38-49 |
| SQL COALESCE(customer_id, 0) | BR-2 | AccountVelocityTracker.cs:57 — `GetValueOrDefault(accountId, 0)` |
| SQL ROUND(SUM(amount), 2) | BR-3 | AccountVelocityTracker.cs:49,65 |
| SQL ORDER BY as_of ASC, account_id ASC | BR-4 | AccountVelocityTracker.cs:53 |
| External injects __maxEffectiveDate as `as_of` column | BR-5 | AccountVelocityTracker.cs:25-26,66 |
| SQL CAST(as_of AS TEXT) for txn_date | BR-6 | AccountVelocityTracker.cs:42 |
| External sets output to empty DataFrame | BR-7 | AccountVelocityTracker.cs:71-73 |
| External writes CSV with header-every-append (W12) | BR-8, W12 | AccountVelocityTracker.cs:84,88 |
| Tier 2 (SQL + minimal External) | AP3, AP6 | V1 uses full External for logic that is SQL-expressible; W12 I/O quirk justifies minimal External |
| Empty input handling: header-only CSV | BRD Edge Cases | AccountVelocityTracker.cs:18-23 |

## 10. External Module Design

### Class: `ExternalModules.AccountVelocityTrackingV2Processor`

**File:** `ExternalModules/AccountVelocityTrackingV2Processor.cs`

**Responsibility:** ONLY the W12 direct CSV write + as_of column injection. Zero business logic.

### Interface

Implements `IExternalStep` with the standard `Execute(Dictionary<string, object> sharedState)` signature.

### Behavior

```
1. Read __maxEffectiveDate from sharedState, format as "yyyy-MM-dd" string
2. Read transactions and accounts DataFrames from sharedState
3. If either is null or empty:
   a. Write header-only CSV to output path (W12: header always written)
   b. Set sharedState["output"] = empty DataFrame
   c. Return
4. Read velocity_output DataFrame from sharedState (produced by Transformation)
5. For each row in velocity_output:
   a. Append as_of column with __maxEffectiveDate string value
6. Write to Output/double_secret_curated/account_velocity_tracking.csv:
   a. Open file in append mode
   b. Set NewLine = "\n" (LF)
   c. Write header row: "account_id,customer_id,txn_date,txn_count,total_amount,as_of"
   d. Write each data row as comma-separated values
7. Set sharedState["output"] = empty DataFrame (prevent framework from writing)
8. Return sharedState
```

### Output Column Order

The output columns must match V1 exactly: `account_id, customer_id, txn_date, txn_count, total_amount, as_of`

### Code Skeleton

```csharp
using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

/// <summary>
/// V2 minimal External module for AccountVelocityTracking.
/// Business logic is handled by the upstream SQL Transformation.
/// This module ONLY handles:
///   1. Injecting the as_of column (__maxEffectiveDate)
///   2. W12: Direct CSV write with header re-emitted on every append run
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
        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];
        var dateStr = maxDate.ToString("yyyy-MM-dd");

        var transactions = sharedState.ContainsKey("transactions")
            ? sharedState["transactions"] as DataFrame : null;
        var accounts = sharedState.ContainsKey("accounts")
            ? sharedState["accounts"] as DataFrame : null;

        // V1 behavior: if either input is null or empty, write header-only CSV
        if (transactions == null || transactions.Count == 0
            || accounts == null || accounts.Count == 0)
        {
            WriteDirectCsv(new List<Row>());
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // Read the SQL-produced velocity_output
        var velocityOutput = sharedState["velocity_output"] as DataFrame
            ?? throw new InvalidOperationException(
                "velocity_output DataFrame not found in shared state");

        // Inject as_of column (__maxEffectiveDate) per BR-5
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
        // (CsvFileWriter.cs:47), so direct I/O is required.
        WriteDirectCsv(outputRows);

        // Set output to empty DataFrame — framework must not write output (BR-7)
        sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
        return sharedState;
    }

    /// <summary>
    /// W12: Writes CSV in append mode with header re-emitted on every run.
    /// V1 behavior: each run appends a header line followed by data rows,
    /// producing a file with interleaved headers among data.
    /// </summary>
    private void WriteDirectCsv(List<Row> rows)
    {
        var solutionRoot = GetSolutionRoot();
        var outputPath = Path.Combine(solutionRoot,
            "Output", "double_secret_curated", "account_velocity_tracking.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // W12: Append mode with header re-emitted on every run
        using var writer = new StreamWriter(outputPath, append: true);
        writer.NewLine = "\n";

        // Header always written — this is the W12 behavior
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
```

### Design Constraints

- **No business logic in the External module.** All aggregation, joining, and sorting is handled by the SQL Transformation.
- **The External module reads `velocity_output`**, not `transactions`/`accounts`. It only checks the source DataFrames for the empty-input guard (matching V1 behavior).
- **`decimal` vs `double`**: V1 accumulates with `decimal` (AccountVelocityTracker.cs:38,49). The SQL Transformation uses SQLite's REAL type (64-bit float / double). This is a potential output difference. However, V1's `Math.Round(total, 2)` after decimal accumulation should produce the same results as SQLite's `ROUND(SUM(CAST(amount AS REAL)), 2)` for typical monetary values. If Proofmark reveals epsilon differences, the `total_amount` column should be added as a fuzzy column with absolute tolerance 0.005.
- **as_of column injection**: The `as_of` value is `__maxEffectiveDate` formatted as `yyyy-MM-dd`. This cannot be computed in SQL because the Transformation module does not expose shared state scalar values to the SQL environment.

### Risk: SQLite REAL vs C# decimal

V1 uses `decimal` for amount accumulation (`Convert.ToDecimal(row["amount"])`). SQLite's REAL type is IEEE 754 double-precision. For the vast majority of monetary values with 2 decimal places, `ROUND(SUM(REAL), 2)` produces identical results to `Math.Round(decimal_sum, 2)`. However, edge cases exist where double-precision accumulation introduces floating-point errors that round differently.

**Mitigation strategy:**
1. Start with REAL arithmetic in SQL (simpler, idiomatic)
2. Run Proofmark comparison
3. If `total_amount` differences appear, options:
   a. Add fuzzy tolerance of 0.005 on `total_amount` (if differences are < 1 cent)
   b. Move SUM accumulation to the External module using `decimal` (if strict match is required)
   c. Use SQLite's text-based decimal arithmetic workaround (complex, not recommended)

This risk is documented here so the Developer and Resolution agents are aware of it.
