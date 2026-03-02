# AccountStatusSummary — Functional Specification Document

## 1. Overview

The V2 implementation (`AccountStatusSummaryV2`) replaces the V1 `AccountStatusSummary` job. It produces a daily CSV summary counting accounts grouped by `(account_type, account_status)`, with a trailer line.

**Module Tier: Tier 2 (SCALPEL)** — `DataSourcing -> External (minimal) -> CsvFileWriter`

### Tier Justification

The V1 business logic (GROUP BY + COUNT) is trivially expressible in SQL, which would normally make this a Tier 1 job. However, the framework's `Transformation` module has a limitation: its `RegisterTable()` method skips DataFrames with zero rows (Transformation.cs:47 — `if (!df.Rows.Any()) return;`). When the `accounts` table has no data for a given effective date (weekends — see BRD Edge Cases), the SQL query would fail with "no such table: accounts" because the table was never registered in SQLite.

Since the job executor processes every calendar day including weekends (JobExecutorService.cs:165-166) and halts gap-fill on failure (JobExecutorService.cs:88), this would block all subsequent date processing. V1 handles this gracefully via an explicit empty-data guard in the External module (AccountStatusCounter.cs:17-21).

A pure Tier 1 solution is not possible without modifying the framework (forbidden). Tier 2 is the minimum escalation: the External module handles the empty-data guard AND the GROUP BY logic (since separating them across External + Transformation would require the External to run before Transformation, but Transformation would overwrite the External's output). The External module uses LINQ for set-based grouping (eliminating AP6) and is otherwise minimal.

## 2. V2 Module Chain

```
DataSourcing (accounts) -> External (AccountStatusSummaryV2Processor) -> CsvFileWriter
```

### Module 1: DataSourcing — `accounts`

| Property | Value |
|----------|-------|
| type | `DataSourcing` |
| resultName | `accounts` |
| schema | `datalake` |
| table | `accounts` |
| columns | `["account_id", "account_type", "account_status"]` |

**Changes from V1:**
- **Removed `customer_id` column** (AP4: never used in output — BRD BR-6)
- **Removed `current_balance` column** (AP4: never used in output — BRD BR-6)
- **Removed `segments` DataSourcing entirely** (AP1: segments table is sourced but never referenced by AccountStatusCounter.cs — BRD BR-2)
- Retained `account_id` for use in `Count()` aggregation, matching V1's per-row counting semantics

### Module 2: External — `AccountStatusSummaryV2Processor`

| Property | Value |
|----------|-------|
| type | `External` |
| assemblyPath | `/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll` |
| typeName | `ExternalModules.AccountStatusSummaryV2Processor` |

**Purpose:** Performs GROUP BY count on accounts data and handles the empty-data edge case (weekend dates). Replaces V1's `AccountStatusCounter` with cleaner, set-based code.

See Section 10 for full External module design.

### Module 3: CsvFileWriter

| Property | Value |
|----------|-------|
| type | `CsvFileWriter` |
| source | `output` |
| outputFile | `Output/double_secret_curated/account_status_summary.csv` |
| includeHeader | `true` |
| trailerFormat | `TRAILER\|{row_count}\|{date}` |
| writeMode | `Overwrite` |
| lineEnding | `LF` |

All writer parameters match V1 exactly. Only the output path changes (`curated/` -> `double_secret_curated/`).

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (W-codes)

| W-Code | Applies? | Analysis |
|--------|----------|----------|
| W1 (Sunday skip) | No | V1 does not skip Sundays; it produces an empty-schema output |
| W2 (Weekend fallback) | No | V1 does not fall back to Friday data on weekends |
| W3a/b/c (Boundary rows) | No | No summary rows appended at period boundaries |
| W4 (Integer division) | No | No division in this job |
| W5 (Banker's rounding) | No | No rounding in this job |
| W6 (Double epsilon) | No | No monetary accumulation in this job |
| W7 (Trailer inflated count) | No | V1 uses framework CsvFileWriter; trailer count is accurate |
| W8 (Trailer stale date) | No | V1 uses framework `{date}` token, which resolves to `__maxEffectiveDate` |
| W9 (Wrong writeMode) | No | Overwrite is appropriate for a daily summary file |
| W10 (Absurd numParts) | No | CSV output, not Parquet |
| W12 (Header every append) | No | Uses Overwrite mode, not Append |

**No W-codes apply to this job.** V1's output behavior is straightforward and correct.

### Code-Quality Anti-Patterns (AP-codes)

| AP-Code | Applies? | V1 Problem | V2 Resolution |
|---------|----------|------------|---------------|
| **AP1** | **YES** | `segments` table sourced but never referenced (BRD BR-2; AccountStatusCounter.cs has no mention of "segments") | **Eliminated.** Removed `segments` DataSourcing from V2 config. |
| **AP3** | **PARTIAL** | V1 uses External module for logic that is mostly SQL-expressible | **Partially addressed.** An External module is still needed due to Transformation's empty-DataFrame limitation (see Tier Justification). However, the External uses LINQ-based set operations instead of manual Dictionary iteration. |
| **AP4** | **YES** | V1 sources `customer_id` and `current_balance` but neither appears in output (BRD BR-6) | **Eliminated.** Removed both columns from DataSourcing config. |
| **AP6** | **YES** | V1 uses `foreach` loop with Dictionary to count groups (AccountStatusCounter.cs:28-37) | **Eliminated.** V2 uses LINQ `GroupBy` + `Count()` for set-based aggregation. |
| AP2 | No | No cross-job duplication identified | N/A |
| AP5 | No | No NULL handling asymmetry | N/A |
| AP7 | No | No magic values | N/A |
| AP8 | No | No SQL in V1 to simplify | N/A |
| AP9 | No | Job name accurately describes its output | N/A |
| AP10 | No | Effective dates are framework-injected correctly | N/A |

## 4. Output Schema

| Column | Source | Transformation | V1 Evidence |
|--------|--------|---------------|-------------|
| `account_type` | `accounts.account_type` | GROUP BY key, no transformation | AccountStatusCounter.cs:30,44 |
| `account_status` | `accounts.account_status` | GROUP BY key, no transformation | AccountStatusCounter.cs:31,45 |
| `account_count` | Computed | COUNT of rows per `(account_type, account_status)` group | AccountStatusCounter.cs:34-36,46 |
| `as_of` | `accounts.as_of` | First row's `as_of` value, applied uniformly to all output rows. Stored as raw `DateOnly` object, rendered by CsvFileWriter as `MM/dd/yyyy` (e.g., `10/01/2024`). | AccountStatusCounter.cs:24,47 |

### Critical: `as_of` Date Format

V1's External module stores `as_of` as a raw `DateOnly` object (AccountStatusCounter.cs:24 — `var asOf = accounts.Rows[0]["as_of"];`). When CsvFileWriter calls `FormatField()` -> `.ToString()` on a `DateOnly`, .NET renders it in `MM/dd/yyyy` format (e.g., `10/01/2024`).

**Verified against actual V1 output:** Running V1 for 2024-10-01 produces `10/01/2024` in the `as_of` column.

The V2 External module must preserve the `as_of` value as a `DateOnly` object in the output DataFrame to ensure CsvFileWriter renders it identically.

### Trailer Line

Format: `TRAILER|{row_count}|{date}`

- `{row_count}` = number of data rows in the output DataFrame (3 on weekdays with current data, 0 on weekends)
- `{date}` = `__maxEffectiveDate` from shared state, formatted as `yyyy-MM-dd` by the framework

Both V1 and V2 use the framework's CsvFileWriter for trailer generation. No special handling needed.

## 5. SQL Design

**Not applicable.** V2 uses Tier 2 with an External module instead of Transformation SQL. See Section 10 for the External module's business logic.

The equivalent SQL (for documentation purposes) would be:

```sql
SELECT
    account_type,
    account_status,
    COUNT(account_id) AS account_count,
    MIN(as_of) AS as_of
FROM accounts
GROUP BY account_type, account_status
```

This cannot be used directly because:
1. The Transformation module skips empty DataFrames (Transformation.cs:47), causing "no such table" errors on weekend dates
2. The Transformation module converts `DateOnly` to `"yyyy-MM-dd"` TEXT (Transformation.cs:110), but V1 outputs `DateOnly.ToString()` format `"MM/dd/yyyy"` — a SQL string-reformatting workaround would be needed

## 6. V2 Job Config

```json
{
  "jobName": "AccountStatusSummaryV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "accounts",
      "schema": "datalake",
      "table": "accounts",
      "columns": ["account_id", "account_type", "account_status"]
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.AccountStatusSummaryV2Processor"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/account_status_summary.csv",
      "includeHeader": true,
      "trailerFormat": "TRAILER|{row_count}|{date}",
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

## 7. Writer Configuration

| Property | V1 Value | V2 Value | Match? |
|----------|----------|----------|--------|
| type | CsvFileWriter | CsvFileWriter | Yes |
| source | `output` | `output` | Yes |
| outputFile | `Output/curated/account_status_summary.csv` | `Output/double_secret_curated/account_status_summary.csv` | Path change only |
| includeHeader | `true` | `true` | Yes |
| trailerFormat | `TRAILER\|{row_count}\|{date}` | `TRAILER\|{row_count}\|{date}` | Yes |
| writeMode | `Overwrite` | `Overwrite` | Yes |
| lineEnding | `LF` | `LF` | Yes |

## 8. Proofmark Config Design

### Starting Position: Zero Exclusions, Zero Fuzzy

| Setting | Value | Justification |
|---------|-------|---------------|
| reader | `csv` | Output is CSV |
| header_rows | `1` | `includeHeader: true` |
| trailer_rows | `1` | `trailerFormat` present + `writeMode: Overwrite` (single trailer at file end) |
| threshold | `100.0` | Strict match required |
| excluded_columns | None | All columns are deterministic and reproducible |
| fuzzy_columns | None | No floating-point or time-dependent values |

### Row Ordering

V1's Dictionary iteration order is non-deterministic (BRD Edge Cases: "Dictionary iteration order is not guaranteed in .NET"). V2's LINQ GroupBy also does not guarantee order. Proofmark should handle row-order differences if it supports order-independent comparison. If Proofmark requires ordered comparison, this may need an `ORDER BY` equivalent in V2 — but since V1 doesn't guarantee order, matching any V1 order is acceptable.

**Decision:** Start with strict comparison. If Proofmark fails on row order, add an explicit ordering in V2's External module (ORDER BY account_type, account_status) and document it. Since V1 uses Overwrite mode, only the last effective date's output is compared, and V1's order for that date is fixed for a given run.

### Proposed Proofmark Config

```yaml
comparison_target: "account_status_summary"
reader: csv
header_rows: 1
trailer_rows: 1
threshold: 100.0
```

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision |
|-----------------|-------------|-----------------|
| BR-1: Group by (account_type, account_status), count accounts per group | Sec 10 (External Module), Sec 4 (Output Schema) | LINQ `GroupBy` on `(account_type, account_status)` tuple with `Count()` |
| BR-2: Segments sourced but unused | Sec 2 (Module 1), Sec 3 (AP1) | Segments DataSourcing removed entirely |
| BR-3: as_of from first accounts row, applied to all output rows | Sec 10 (External Module), Sec 4 (as_of format) | `accounts.Rows[0]["as_of"]` preserved as `DateOnly` in output |
| BR-4: Empty accounts produces empty output with correct schema | Sec 10 (External Module) | Explicit empty-data guard returns empty DataFrame with schema |
| BR-5: Currently 3 output rows (one per account_type, all Active) | Sec 4 (Output Schema) | Confirmed via database query; no special handling needed |
| BR-6: customer_id and current_balance sourced but unused | Sec 2 (Module 1), Sec 3 (AP4) | Both columns removed from DataSourcing |
| BR-7: Trailer format TRAILER\|{row_count}\|{date} | Sec 2 (Module 3), Sec 7 | Identical trailerFormat in CsvFileWriter config |
| BRD Edge Case: Weekend empty data | Sec 1 (Tier Justification), Sec 10 | External module handles empty data gracefully |
| BRD Edge Case: Row ordering non-deterministic | Sec 8 (Proofmark) | Acknowledged; Proofmark handles if needed |

## 10. External Module Design

### File: `ExternalModules/AccountStatusSummaryV2Processor.cs`

### Class: `ExternalModules.AccountStatusSummaryV2Processor`

### Interface: `IExternalStep`

### Purpose

Replaces V1's `AccountStatusCounter` with clean, LINQ-based implementation. Handles:
1. Empty-data guard (weekend dates where accounts has zero rows)
2. GROUP BY + COUNT aggregation using LINQ
3. Preserving `as_of` as `DateOnly` for correct CsvFileWriter formatting

### Pseudocode

```csharp
public class AccountStatusSummaryV2Processor : IExternalStep
{
    // Output columns match V1 exactly
    private static readonly List<string> OutputColumns = new()
    {
        "account_type", "account_status", "account_count", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var accounts = sharedState.TryGetValue("accounts", out var val)
            ? val as DataFrame
            : null;

        // BR-4: Empty/null accounts produces empty DataFrame with correct schema
        if (accounts == null || accounts.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // BR-3: as_of from first accounts row, applied to all output rows
        // All rows share the same as_of within a single effective date run
        var asOf = accounts.Rows[0]["as_of"];

        // BR-1: Group by (account_type, account_status), count per group
        // AP6 fix: LINQ set-based grouping replaces V1's foreach + Dictionary pattern
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
                ["as_of"] = asOf  // Preserved as DateOnly for correct CsvFileWriter formatting
            }))
            .ToList();

        sharedState["output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
```

### Key Differences from V1

| Aspect | V1 (AccountStatusCounter) | V2 (AccountStatusSummaryV2Processor) |
|--------|--------------------------|--------------------------------------|
| Grouping method | `foreach` + `Dictionary<(string,string), int>` (AP6) | LINQ `GroupBy` + `Count()` (set-based) |
| Null handling for type/status | `?.ToString() ?? ""` | `?.ToString() ?? ""` (same — preserves output) |
| as_of handling | `accounts.Rows[0]["as_of"]` as raw object | Same — preserves `DateOnly` type for CSV formatting |
| Empty-data guard | `accounts == null \|\| accounts.Count == 0` | Same check, same empty DataFrame response |
| Segments table | Sourced but unused (AP1) | Not sourced at all |
| Unused columns | `customer_id`, `current_balance` sourced (AP4) | Not sourced at all |

### Output Contract

The External module stores a DataFrame named `output` in shared state with:
- Columns: `["account_type", "account_status", "account_count", "as_of"]`
- Row count: 0 (weekends) or N groups (weekdays; currently 3 with current data)
- `account_count` values: integer counts
- `as_of` values: `DateOnly` objects (CsvFileWriter renders as `MM/dd/yyyy`)
