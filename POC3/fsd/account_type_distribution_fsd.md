# AccountTypeDistribution -- Functional Specification Document

## 1. Overview

V2 replaces the V1 External module (`AccountDistributionCalculator`) with a clean, LINQ-based External module that eliminates AP3 (partially -- External is still needed), AP4, AP6, and AP1. The V1 job groups accounts by `account_type` and computes count and percentage of total.

**Tier: 2 (Framework + Minimal External)** -- `DataSourcing -> External (AccountTypeDistributionV2Processor) -> CsvFileWriter`

### Tier Justification

The V1 business logic (GROUP BY + COUNT + percentage) is trivially expressible in SQL, which would normally make this a Tier 1 job. However, the framework's `Transformation` module has a limitation: its `RegisterTable()` method skips DataFrames with zero rows (Transformation.cs:46 -- `if (!df.Rows.Any()) return;`). When the `accounts` table has no data for a given effective date (weekends -- see BRD Edge Cases), the SQL query would fail with "no such table: accounts" because the table was never registered in SQLite.

Since the job executor processes every calendar day including weekends (JobExecutorService.cs:165-166) and halts gap-fill on failure (JobExecutorService.cs:88), this would block all subsequent date processing. V1 handles this gracefully via an explicit empty-data guard in the External module (AccountDistributionCalculator.cs:17-21). The date range 2024-10-01 to 2024-12-31 contains 26 weekend days, making this failure guaranteed -- not theoretical.

A pure Tier 1 solution is not possible without modifying the framework (forbidden). Tier 2 is the minimum escalation: the External module handles the empty-data guard AND the GROUP BY logic (since separating them across External + Transformation would require the External to run before Transformation, but Transformation would overwrite the External's output). The External module uses LINQ for set-based grouping (eliminating AP6) and is otherwise minimal.

**Sister job precedent:** `account_status_summary` faces the identical Transformation.RegisterTable limitation and uses the same Tier 2 pattern (see `account_status_summary_fsd.md`).

---

## 2. V2 Module Chain

```
DataSourcing (accounts) -> External (AccountTypeDistributionV2Processor) -> CsvFileWriter
```

### Module 1: DataSourcing -- `accounts`

| Property | Value |
|----------|-------|
| type | `DataSourcing` |
| resultName | `accounts` |
| schema | `datalake` |
| table | `accounts` |
| columns | `["account_type"]` |

**Changes from V1:**
- **Removed `account_id`, `customer_id`, `account_status`, `current_balance`** -- none are used by the business logic (AP4 elimination). Evidence: `AccountDistributionCalculator.cs:28-34` only accesses `acctRow["account_type"]`; line 24 accesses `accounts.Rows[0]["as_of"]` (auto-appended by DataSourcing); line 25 uses `accounts.Count` (row count, not a column).
- **Removed `branches` DataSourcing entirely** -- V1 sources branches but the External module never references it (AP1 elimination). Evidence: `AccountDistributionCalculator.cs:8-56` -- no mention of "branches" anywhere in the module.
- The `as_of` column is automatically appended by the DataSourcing module when not explicitly listed (per `DataSourcing.cs:69-72`).
- Effective dates are injected at runtime by the executor via shared state keys `__minEffectiveDate` / `__maxEffectiveDate`.

### Module 2: External -- `AccountTypeDistributionV2Processor`

| Property | Value |
|----------|-------|
| type | `External` |
| assemblyPath | `/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll` |
| typeName | `ExternalModules.AccountTypeDistributionV2Processor` |

**Purpose:** Performs GROUP BY count + percentage on accounts data and handles the empty-data edge case (weekend dates). Replaces V1's `AccountDistributionCalculator` with cleaner, set-based code.

See Section 10 for full External module design.

### Module 3: CsvFileWriter

| Property | Value |
|----------|-------|
| type | `CsvFileWriter` |
| source | `output` |
| outputFile | `Output/double_secret_curated/account_type_distribution.csv` |
| includeHeader | `true` |
| trailerFormat | `END\|{row_count}` |
| writeMode | `Overwrite` |
| lineEnding | `LF` |

**Writer config matches V1 exactly** (same writer type, same header/trailer/writeMode/lineEnding). Only the output path changes to `Output/double_secret_curated/`.

---

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles to Reproduce

**None identified.** This job has no W-code wrinkles. The percentage is computed using `double` arithmetic in V1, but this is correct behavior (not a bug) -- SQLite REAL is IEEE 754 double, so an equivalent SQL approach would naturally reproduce the same floating-point values. However, since we are using a Tier 2 External module, the V2 code uses `(double)typeCount / totalAccounts * 100.0` directly, which is identical to V1's computation at `AccountDistributionCalculator.cs:41`.

### Code-Quality Anti-Patterns to Eliminate

| AP Code | V1 Problem | V2 Resolution |
|---------|-----------|---------------|
| **AP1** | `branches` table sourced but never used by the External module | Removed entirely from V2 DataSourcing config. Evidence: `AccountDistributionCalculator.cs:8-56` has zero references to "branches". |
| **AP3** | Unnecessary External module -- V1 uses C# `foreach` + Dictionary for a simple GROUP BY + COUNT + percentage | **Partially addressed.** An External module is still needed due to Transformation's empty-DataFrame limitation (see Tier Justification). However, the External uses LINQ-based set operations instead of manual Dictionary iteration. |
| **AP4** | V1 sources `account_id`, `customer_id`, `account_status`, `current_balance` from accounts -- none are used in the computation | V2 sources only `account_type`. Evidence: `AccountDistributionCalculator.cs:28-34` only accesses `account_type`; line 24 accesses `as_of` (auto-appended); line 25 uses `.Count` (row count). |
| **AP6** | Row-by-row `foreach` iteration to count account types | Replaced with LINQ `GroupBy` + `Count()` -- a set-based operation. |

---

## 4. Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| `account_type` | `accounts.account_type` | GROUP BY key -- passed through | `AccountDistributionCalculator.cs:43,45`, BRD BR-1 |
| `account_count` | Computed | `Count()` per `account_type` group | `AccountDistributionCalculator.cs:44,46`, BRD BR-1 |
| `total_accounts` | Computed | Total row count across all account types | `AccountDistributionCalculator.cs:25,47`, BRD BR-3 |
| `percentage` | Computed | `(double)typeCount / totalAccounts * 100.0` -- double-precision float | `AccountDistributionCalculator.cs:41,48`, BRD BR-2 |
| `as_of` | `accounts.as_of` | Value from first row of accounts, applied uniformly. Preserved as raw `DateOnly` object for correct CsvFileWriter formatting (`MM/dd/yyyy`). | `AccountDistributionCalculator.cs:24,49`, BRD BR-5 |

**Column ordering:** The External module's `OutputColumns` list defines columns in the exact order above, matching V1's `outputColumns` list at `AccountDistributionCalculator.cs:10-13`.

---

## 5. SQL Design

**Not applicable.** V2 uses Tier 2 with an External module instead of Transformation SQL. See Section 10 for the External module's business logic.

The equivalent SQL (for documentation purposes) would be:

```sql
SELECT
    account_type,
    COUNT(*) AS account_count,
    (SELECT COUNT(*) FROM accounts) AS total_accounts,
    CAST(COUNT(*) AS REAL) / (SELECT COUNT(*) FROM accounts) * 100.0 AS percentage,
    (SELECT as_of FROM accounts LIMIT 1) AS as_of
FROM accounts
GROUP BY account_type
ORDER BY account_type
```

This cannot be used directly because:
1. The Transformation module skips empty DataFrames (Transformation.cs:46), causing "no such table" errors on weekend dates.
2. The date range 2024-10-01 to 2024-12-31 contains 26 weekends where `accounts` will have zero rows, making this a guaranteed runtime failure -- not a theoretical edge case.

---

## 6. V2 Job Config

```json
{
  "jobName": "AccountTypeDistributionV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "accounts",
      "schema": "datalake",
      "table": "accounts",
      "columns": ["account_type"]
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.AccountTypeDistributionV2Processor"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/account_type_distribution.csv",
      "includeHeader": true,
      "trailerFormat": "END|{row_count}",
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

---

## 7. Writer Configuration

| Property | V1 Value | V2 Value | Match? |
|----------|---------|---------|--------|
| Writer type | `CsvFileWriter` | `CsvFileWriter` | Yes |
| `source` | `output` | `output` | Yes |
| `outputFile` | `Output/curated/account_type_distribution.csv` | `Output/double_secret_curated/account_type_distribution.csv` | Path change only |
| `includeHeader` | `true` | `true` | Yes |
| `trailerFormat` | `END\|{row_count}` | `END\|{row_count}` | Yes |
| `writeMode` | `Overwrite` | `Overwrite` | Yes |
| `lineEnding` | `LF` | `LF` | Yes |

Note: The trailer uses `END` prefix, not the more common `TRAILER` prefix used by other jobs (BRD BR-8).

---

## 8. Proofmark Config Design

Starting point: **zero exclusions, zero fuzzy overrides**.

```yaml
comparison_target: "account_type_distribution"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

### Rationale

- **`reader: csv`**: V1 and V2 both use CsvFileWriter.
- **`header_rows: 1`**: Both V1 and V2 have `includeHeader: true`.
- **`trailer_rows: 1`**: Both V1 and V2 have `trailerFormat` set and `writeMode: Overwrite`. Overwrite mode produces exactly one trailer at the file's end.
- **No excluded columns**: The BRD identifies zero non-deterministic fields. All output values are deterministic given the same input data.
- **No fuzzy columns**: The `percentage` column uses `double` arithmetic in both V1 (explicit cast in C#: `(double)typeCount / totalAccounts * 100.0`) and V2 (identical C# cast in the External module). Both are IEEE 754 double-precision, same operation, same inputs -- identical results. No epsilon tolerance needed.

### Row Ordering

V1's Dictionary iteration order is non-deterministic (BRD Edge Cases). V2's LINQ GroupBy also does not guarantee order. Proofmark should handle row-order differences if it supports order-independent comparison. If Proofmark requires ordered comparison, this may need an `ORDER BY` equivalent in V2 -- but since V1 doesn't guarantee order, matching any V1 order is acceptable.

**Decision:** Start with strict comparison. If Proofmark fails on row order, add an explicit ordering in V2's External module (OrderBy account_type) and document it. Since V1 uses Overwrite mode, only the last effective date's output is compared, and V1's order for that date is fixed for a given run.

---

## 9. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|-------------|----------------|----------|
| Tier 2 selection (empty-data guard) | BR-6 (empty accounts produces empty output) | Transformation.cs:46 skips empty DataFrames; AccountDistributionCalculator.cs:17-21 handles gracefully |
| GROUP BY `account_type` with COUNT (LINQ) | BR-1 | `AccountDistributionCalculator.cs:28-35` |
| Percentage as `(double)typeCount / total * 100.0` (double-precision) | BR-2 | `AccountDistributionCalculator.cs:41` |
| `total_accounts` = total row count of all accounts | BR-3 | `AccountDistributionCalculator.cs:25` |
| Remove `branches` DataSourcing (AP1 elimination) | BR-4 | `AccountDistributionCalculator.cs:8-56` -- no "branches" reference |
| `as_of` from first row of accounts, preserved as `DateOnly` | BR-5 | `AccountDistributionCalculator.cs:24` |
| Zero-row handling (empty DataFrame with correct schema) | BR-6 | `AccountDistributionCalculator.cs:17-21` |
| Expect 3 output rows (Checking, Savings, Credit) | BR-7 | DB query: 3 distinct account_type values |
| Trailer format `END\|{row_count}` | BR-8 | `account_type_distribution.json:29` |
| Overwrite write mode | BRD Write Mode | `account_type_distribution.json:30` |
| Remove unused columns (AP4) | BRD Source Tables | `AccountDistributionCalculator.cs` -- only `account_type` and `as_of` accessed |
| Replace foreach with LINQ (AP6) | Module Hierarchy / Anti-Patterns | Set-based GroupBy replaces row-by-row iteration |

---

## 10. External Module Design

### File: `ExternalModules/AccountTypeDistributionV2Processor.cs`

### Class: `ExternalModules.AccountTypeDistributionV2Processor`

### Interface: `IExternalStep`

### Purpose

Replaces V1's `AccountDistributionCalculator` with clean, LINQ-based implementation. Handles:
1. Empty-data guard (weekend dates where accounts has zero rows)
2. GROUP BY + COUNT + percentage aggregation using LINQ
3. Preserving `as_of` as `DateOnly` for correct CsvFileWriter formatting

### Pseudocode

```csharp
public class AccountTypeDistributionV2Processor : IExternalStep
{
    // Output columns match V1 exactly (AccountDistributionCalculator.cs:10-13)
    private static readonly List<string> OutputColumns = new()
    {
        "account_type", "account_count", "total_accounts", "percentage", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var accounts = sharedState.TryGetValue("accounts", out var val)
            ? val as DataFrame
            : null;

        // BR-6: Empty/null accounts produces empty DataFrame with correct schema
        // This guard is the primary reason for Tier 2 escalation --
        // Transformation.RegisterTable skips empty DataFrames (Transformation.cs:46),
        // which would cause "no such table: accounts" errors on weekend dates.
        if (accounts == null || accounts.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // BR-5: as_of from first accounts row, applied to all output rows
        // Preserved as DateOnly for correct CsvFileWriter formatting (MM/dd/yyyy)
        var asOf = accounts.Rows[0]["as_of"];
        var totalAccounts = accounts.Count;

        // BR-1, BR-2, BR-3: Group by account_type, compute count and percentage
        // AP6 fix: LINQ set-based grouping replaces V1's foreach + Dictionary pattern
        var outputRows = accounts.Rows
            .GroupBy(row => row["account_type"]?.ToString() ?? "")
            .Select(group => new Row(new Dictionary<string, object?>
            {
                ["account_type"] = group.Key,
                ["account_count"] = group.Count(),
                ["total_accounts"] = totalAccounts,
                // V1 uses (double)typeCount / totalAccounts * 100.0
                // (AccountDistributionCalculator.cs:41) -- replicated exactly
                ["percentage"] = (double)group.Count() / totalAccounts * 100.0,
                ["as_of"] = asOf  // Preserved as DateOnly for correct CsvFileWriter formatting
            }))
            .ToList();

        sharedState["output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
```

### Key Differences from V1

| Aspect | V1 (AccountDistributionCalculator) | V2 (AccountTypeDistributionV2Processor) |
|--------|-----------------------------------|----------------------------------------|
| Grouping method | `foreach` + `Dictionary<string, int>` (AP6) | LINQ `GroupBy` + `Count()` (set-based) |
| Null handling for type | `?.ToString() ?? ""` | `?.ToString() ?? ""` (same -- preserves output) |
| as_of handling | `accounts.Rows[0]["as_of"]` as raw object | Same -- preserves `DateOnly` type for CSV formatting |
| Percentage computation | `(double)typeCount / totalAccounts * 100.0` | Same computation via LINQ |
| Empty-data guard | `accounts == null \|\| accounts.Count == 0` | Same check, same empty DataFrame response |
| Branches table | Sourced but unused (AP1) | Not sourced at all |
| Unused columns | `account_id`, `customer_id`, etc. sourced (AP4) | Not sourced at all |

### Output Contract

The External module stores a DataFrame named `output` in shared state with:
- Columns: `["account_type", "account_count", "total_accounts", "percentage", "as_of"]`
- Row count: 0 (weekends) or N groups (weekdays; currently 3 with current data)
- `percentage` values: IEEE 754 double, computed as `(double)count / total * 100.0`
- `as_of` values: `DateOnly` objects (CsvFileWriter renders as `MM/dd/yyyy`)

---

## Appendix: Risk Register

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Row ordering mismatch between V1 Dictionary iteration and V2 LINQ GroupBy | MEDIUM -- both are insertion-order dependent | LOW -- fixable in V2 module or proofmark config | Review V1 output during Phase D comparison. Add explicit `.OrderBy()` in V2 if needed. |
| `DateOnly` rendering format differs between environments | VERY LOW -- both V1 and V2 run in same Docker container | LOW -- format divergence would show in Proofmark | Verified: `DateOnly.ToString()` renders as `MM/dd/yyyy` in .NET on Linux with invariant culture. |
