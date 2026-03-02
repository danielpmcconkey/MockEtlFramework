# FeeRevenueDaily — Functional Specification Document

## 1. Job Summary

**Job**: `FeeRevenueDailyV2`
**Config**: `JobExecutor/Jobs/fee_revenue_daily_v2.json`
**Module Tier**: Tier 2 (Framework + Minimal External)

This job calculates daily fee revenue from overdraft events, categorizing fees as charged (where `fee_waived = false`) versus waived (where `fee_waived = true`), and computing net revenue as charged minus waived. On the last day of each month, it appends a `MONTHLY_TOTAL` summary row. However, the monthly total aggregates the **entire hardcoded source date range** (2024-10-01 through 2024-12-31), not just the current month — this is a V1 bug (EC-1) that must be reproduced for output equivalence. Output is a single CSV file in Overwrite mode; each execution replaces the entire file, so only the last effective date's output survives during multi-day auto-advance runs.

---

## 2. V2 Module Chain

**Tier**: 2 — Framework + Minimal External

```
DataSourcing("overdraft_events") → External(FeeRevenueDailyV2Processor) → Transformation(SQL) → CsvFileWriter
```

| Step | Module | Purpose |
|------|--------|---------|
| 1 | DataSourcing | Source `datalake.overdraft_events` with hardcoded date range `2024-10-01` to `2024-12-31`. Hardcoded range is required to reproduce EC-1 (MONTHLY_TOTAL sums the full sourced range). |
| 2 | External | Minimal bridge: reads `__maxEffectiveDate` from shared state, writes a single-row DataFrame `effective_date_ref` containing the date as `yyyy-MM-dd` text. **Zero business logic.** |
| 3 | Transformation | SQL performs all business logic: daily aggregation filtered to the current effective date, conditional MONTHLY_TOTAL on last-day-of-month, double-precision arithmetic via SQLite REAL. |
| 4 | CsvFileWriter | Writes `output` DataFrame to CSV. Overwrite mode, LF line endings, header included, no trailer. |

### Tier Justification

Tier 1 (pure SQL) is insufficient because the `Transformation` module cannot access scalar shared-state values (`__maxEffectiveDate`) — it only registers `DataFrame` values as SQLite tables. The SQL needs the effective date to:

1. Filter daily rows to `as_of = __maxEffectiveDate`
2. Determine whether the effective date is the last day of its month (W3b MONTHLY_TOTAL logic)

A minimal Tier 2 External module resolves this by materializing `__maxEffectiveDate` into a one-row, one-column DataFrame (`effective_date_ref`), making the date accessible in SQL. All business logic stays in SQL, eliminating AP3/AP6.

---

## 3. DataSourcing Config

| Property | Value | Rationale |
|----------|-------|-----------|
| resultName | `overdraft_events` | Same shared-state key as V1 |
| schema | `datalake` | Source schema |
| table | `overdraft_events` | Source table |
| columns | `["fee_amount", "fee_waived", "as_of"]` | **Reduced from 7 to 3** — removed `overdraft_id`, `account_id`, `customer_id`, `overdraft_amount`, `event_timestamp` per AP4 (none are used in output). Added `as_of` explicitly since it's used for day-level filtering. |
| minEffectiveDate | `"2024-10-01"` | Hardcoded to match V1 (BR-1). Required for EC-1: MONTHLY_TOTAL sums the full sourced range. |
| maxEffectiveDate | `"2024-12-31"` | Hardcoded to match V1 (BR-1). Same justification. |

### Effective Date Handling

DataSourcing uses **hardcoded** dates in the config JSON, overriding any executor-injected `__minEffectiveDate`/`__maxEffectiveDate`. This means every run sources the full range `2024-10-01` to `2024-12-31` regardless of the current effective date. The External module then filters to the current effective date for the daily row, and the MONTHLY_TOTAL query scans all sourced rows (full range).

### Column Type Mapping

The following CLR types come from Npgsql when reading `datalake.overdraft_events`:

| Column | Postgres Type | CLR Type | SQLite Type (via Transformation) |
|--------|--------------|----------|----------------------------------|
| fee_amount | numeric | `decimal` | REAL (double-precision) |
| fee_waived | boolean | `bool` | INTEGER (0/1) |
| as_of | date | `DateOnly` | TEXT (`yyyy-MM-dd` via `ToSqliteValue`) |

---

## 4. Transformation SQL

The SQL uses two tables registered in SQLite by the Transformation module:
- `overdraft_events` — full range data sourced by DataSourcing (hardcoded 2024-10-01 to 2024-12-31)
- `effective_date_ref` — single-row table with column `effective_date` (from External module)

```sql
-- Daily aggregation: filter to current effective date
SELECT
    edr.effective_date AS event_date,
    SUM(CASE WHEN oe.fee_waived = 0 THEN oe.fee_amount ELSE 0.0 END) AS charged_fees,
    SUM(CASE WHEN oe.fee_waived = 1 THEN oe.fee_amount ELSE 0.0 END) AS waived_fees,
    SUM(CASE WHEN oe.fee_waived = 0 THEN oe.fee_amount ELSE 0.0 END)
      - SUM(CASE WHEN oe.fee_waived = 1 THEN oe.fee_amount ELSE 0.0 END) AS net_revenue,
    edr.effective_date AS as_of
FROM overdraft_events oe
CROSS JOIN effective_date_ref edr
WHERE oe.as_of = edr.effective_date
GROUP BY edr.effective_date

UNION ALL

-- W3b: MONTHLY_TOTAL row — only emitted on last day of month
-- EC-1: Sums ALL rows in overdraft_events (full hardcoded range), not just current month
SELECT
    'MONTHLY_TOTAL' AS event_date,
    SUM(CASE WHEN oe.fee_waived = 0 THEN oe.fee_amount ELSE 0.0 END) AS charged_fees,
    SUM(CASE WHEN oe.fee_waived = 1 THEN oe.fee_amount ELSE 0.0 END) AS waived_fees,
    SUM(CASE WHEN oe.fee_waived = 0 THEN oe.fee_amount ELSE 0.0 END)
      - SUM(CASE WHEN oe.fee_waived = 1 THEN oe.fee_amount ELSE 0.0 END) AS net_revenue,
    edr.effective_date AS as_of
FROM overdraft_events oe
CROSS JOIN effective_date_ref edr
WHERE edr.effective_date = date(edr.effective_date, 'start of month', '+1 month', '-1 day')
  AND EXISTS (
      SELECT 1 FROM overdraft_events oe2
      WHERE oe2.as_of = edr.effective_date
  )
GROUP BY edr.effective_date
```

### SQL Design Notes

1. **W6 (Double epsilon):** `fee_amount` arrives from DataSourcing as C# `decimal`. The framework's `Transformation.GetSqliteType` maps `decimal` to `"REAL"` [Transformation.cs:101], and `ToSqliteValue` passes the value through as-is [Transformation.cs:113]. SQLite REAL is IEEE 754 double-precision, so `SUM()` operates in double arithmetic — naturally reproducing V1's `double` accumulation without explicit `double` declarations.

2. **W3b (End-of-month boundary):** SQLite's `date(x, 'start of month', '+1 month', '-1 day')` computes the last day of the month for date `x`. The `WHERE` clause in the second `SELECT` ensures the MONTHLY_TOTAL row is emitted only when the effective date IS the last day of its month, matching V1's `maxDate.Day == DateTime.DaysInMonth(maxDate.Year, maxDate.Month)` check [FeeRevenueDailyProcessor.cs:69].

3. **EC-1 (Monthly total scope bug):** The second `SELECT` aggregates ALL rows in `overdraft_events` (no `as_of` filter on `oe`), matching V1's `foreach (var row in overdraftEvents.Rows)` [FeeRevenueDailyProcessor.cs:75] which iterates the entire sourced DataFrame. The hardcoded DataSourcing date range is retained specifically to populate the same full-range dataset.

4. **BR-8 (Empty result guard):** In V1, when `currentDateRows.Count == 0`, the code returns an empty DataFrame immediately — no MONTHLY_TOTAL is emitted [FeeRevenueDailyProcessor.cs:35-38]. The first `SELECT` naturally produces zero rows when no `as_of` matches. The `EXISTS` subquery on the second `SELECT` ensures the MONTHLY_TOTAL is also suppressed when no data exists for the effective date, matching V1 behavior exactly.

5. **fee_waived comparison:** V1 uses `Convert.ToBoolean(row["fee_waived"])` [FeeRevenueDailyProcessor.cs:48]. In V2, `fee_waived` is stored as SQLite INTEGER (0/1) via the `ToSqliteValue` bool→int mapping [Transformation.cs:109]. The SQL uses `fee_waived = 0` (not waived) and `fee_waived = 1` (waived), which correctly maps to the boolean semantics.

---

## 5. Writer Config

| Property | V1 Value | V2 Value | Match? |
|----------|----------|----------|--------|
| type | CsvFileWriter | CsvFileWriter | Yes |
| source | `output` | `output` | Yes |
| outputFile | `Output/curated/fee_revenue_daily.csv` | `Output/double_secret_curated/fee_revenue_daily.csv` | Path changed per V2 convention |
| includeHeader | `true` | `true` | Yes |
| writeMode | `Overwrite` | `Overwrite` | Yes |
| lineEnding | `LF` | `LF` | Yes |
| trailerFormat | not configured | not configured | Yes |

---

## 6. Wrinkle Replication

| W-Code | Wrinkle | V1 Behavior | V2 Replication | Evidence |
|--------|---------|-------------|----------------|----------|
| W3b | End-of-month boundary | Appends MONTHLY_TOTAL summary row on the last day of the month. V1 checks `maxDate.Day == DateTime.DaysInMonth(maxDate.Year, maxDate.Month)` | Reproduced in SQL: `WHERE edr.effective_date = date(edr.effective_date, 'start of month', '+1 month', '-1 day')` detects last-day-of-month. Clean declarative SQL replaces V1's procedural `if` block. | [FeeRevenueDailyProcessor.cs:69] |
| W6 | Double epsilon | V1 accumulates fees in `double` variables: `double chargedFees = 0.0; double waivedFees = 0.0;` with sequential `foreach` addition. | Naturally reproduced: C# `decimal` from DataSourcing maps to SQLite `REAL` (IEEE 754 double) via `GetSqliteType` [Transformation.cs:101]. `SUM()` on REAL columns uses double-precision arithmetic. No explicit `double` declarations needed. | [FeeRevenueDailyProcessor.cs:42-43] |
| W9 | Wrong writeMode | Overwrite mode — each execution replaces the entire CSV. During multi-day auto-advance, only the last effective date's output survives. | Reproduced exactly: `writeMode: "Overwrite"` in V2 config. // V1 uses Overwrite, so multi-day auto-advance only retains the last day's output. | [fee_revenue_daily.json:23] |

---

## 7. Anti-Pattern Elimination

| AP-Code | Anti-Pattern | V1 Problem | V2 Resolution |
|---------|-------------|------------|---------------|
| AP3 | Unnecessary External module | V1 uses a full C# External module (`FeeRevenueDailyProcessor`) for aggregation logic (fee categorization, monthly total, net revenue) that can be expressed entirely in SQL `SUM`/`CASE WHEN`. | **Eliminated.** All business logic moved to SQL Transformation. The remaining External module does exactly one thing: bridge `__maxEffectiveDate` into a DataFrame so SQL can reference it. Zero business logic in the External. |
| AP4 | Unused columns | V1 DataSourcing sources 7 columns: `overdraft_id`, `account_id`, `customer_id`, `overdraft_amount`, `fee_amount`, `fee_waived`, `event_timestamp`. Of these, only `fee_amount`, `fee_waived`, and `as_of` (auto-appended) are used in processing. | **Eliminated.** V2 DataSourcing sources only `fee_amount`, `fee_waived`, `as_of` — the 3 columns actually referenced in the computation. 4 columns removed. |
| AP6 | Row-by-row iteration | V1 uses `foreach` loops to iterate rows and accumulate `chargedFees`/`waivedFees` [FeeRevenueDailyProcessor.cs:45-54, 75-84]. | **Eliminated.** V2 uses SQL `SUM(CASE WHEN ...)` for set-based aggregation. No `foreach` loops. |
| AP10 | Over-sourcing dates | V1 hardcodes `minEffectiveDate: "2024-10-01"`, `maxEffectiveDate: "2024-12-31"`, sourcing the full date range every run. The External then filters down to just the current effective date. | **Partially retained.** The hardcoded date range cannot be eliminated because EC-1 requires it — the MONTHLY_TOTAL row sums ALL sourced data (not just the current month). If EC-1 were a bug to be fixed (scoping monthly total to current month only), the date range could switch to executor-injected dates. Documented as intentional retention for output equivalence. |

---

## 8. Proofmark Config

**File**: `POC3/proofmark_configs/fee_revenue_daily.yaml`

```yaml
comparison_target: "fee_revenue_daily"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

### Justification

- **Reader**: `csv` — both V1 and V2 use CsvFileWriter.
- **header_rows**: `1` — `includeHeader: true` in both configs.
- **trailer_rows**: `0` — no `trailerFormat` configured in V1 or V2.
- **threshold**: `100.0` — start strict. All output is deterministic (BRD confirms no non-deterministic fields).
- **Exclusions**: None. No non-deterministic fields identified.
- **Fuzzy columns**: None initially. However, there is a **potential W6 concern**: V1 accumulates fees via sequential `foreach` loop (addition order determined by row iteration order), while V2 uses SQLite `SUM()` (accumulation order is implementation-defined). For the actual data in this dataset (all fee amounts are exactly `35.00` or `0.00`), double-precision results should be bit-identical. If Proofmark detects epsilon-level differences, add fuzzy tolerance:

```yaml
# Fallback — add ONLY if strict comparison fails due to W6 double-precision differences:
columns:
  fuzzy:
    - name: "charged_fees"
      tolerance: 0.0000001
      tolerance_type: absolute
      reason: "W6: V1 sequential double addition vs V2 SQLite SUM() — accumulation order may differ [FeeRevenueDailyProcessor.cs:42-54]"
    - name: "waived_fees"
      tolerance: 0.0000001
      tolerance_type: absolute
      reason: "W6: Same as charged_fees [FeeRevenueDailyProcessor.cs:42-54]"
    - name: "net_revenue"
      tolerance: 0.0000001
      tolerance_type: absolute
      reason: "W6: Derived from charged_fees - waived_fees, both subject to double epsilon [FeeRevenueDailyProcessor.cs:56]"
```

---

## 9. Open Questions

### BRD Correction: BR-2 is Inaccurate

The BRD states the External module filters to "the prior business day (`__maxEffectiveDate` minus 1 day)" and cites `[FeeRevenueDailyProcessor.cs:30-33]` with code `maxDate.AddDays(-1)`. However, the actual V1 code at those lines filters to `maxDate` itself (the current effective date), NOT `maxDate - 1`:

```csharp
// Actual V1 code at FeeRevenueDailyProcessor.cs:30-32:
var currentDateRows = overdraftEvents.Rows
    .Where(r => r["as_of"]?.ToString() == maxDate.ToString("yyyy-MM-dd") ||
                (r["as_of"] is DateOnly d && d == maxDate))
    .ToList();
```

V2 follows the actual V1 code: `WHERE oe.as_of = edr.effective_date` (filter to `__maxEffectiveDate`, not minus one day). The BRD's BR-2 description should be corrected to say "current effective date" rather than "prior business day." The BRD review passed this in error.

### Monthly Total Scope (EC-1)

The MONTHLY_TOTAL row sums ALL rows in the sourced DataFrame — the full hardcoded range 2024-10-01 to 2024-12-31 — not just the current month. This appears to be a V1 bug (the variable is named `monthCharged`/`monthWaived` but iterates `overdraftEvents.Rows` without a month filter). V2 reproduces this behavior for output equivalence. If this were corrected in a future version, the SQL would add a month filter (`WHERE strftime('%Y-%m', oe.as_of) = strftime('%Y-%m', edr.effective_date)`) and the hardcoded date range could be removed.

### Double-Precision Accumulation Order

V1 accumulates fees via sequential `foreach` loop in row-iteration order. V2 accumulates via SQLite `SUM()`, whose internal accumulation order is unspecified. For the current dataset (all values are exactly `35.00` or `0.00`), this is a non-issue — IEEE 754 double-precision represents these values exactly. For datasets with values that are not exact in binary floating-point, accumulation order differences could produce epsilon-level discrepancies. The Proofmark fuzzy fallback config (Section 8) is designed for this scenario.

---

## 10. External Module Design

### Module: `FeeRevenueDailyV2Processor`
### File: `ExternalModules/FeeRevenueDailyV2Processor.cs`

**Purpose:** Materializes `__maxEffectiveDate` from shared state into a single-row DataFrame (`effective_date_ref`) so the Transformation SQL can access the current effective date.

**This module contains ZERO business logic.** It is a pure bridge between the framework's shared state mechanism and the SQL Transformation module.

### Behavior

1. Read `__maxEffectiveDate` from shared state as `DateOnly`. If missing, fall back to `DateOnly.FromDateTime(DateTime.Today)` (matches V1 fallback per EC-5 [FeeRevenueDailyProcessor.cs:19-20]).
2. Create a single-row DataFrame with one column: `effective_date` (string, formatted as `yyyy-MM-dd`).
3. Store the DataFrame in shared state as `effective_date_ref`.
4. Return shared state unchanged otherwise.

### Input/Output Contract

| Direction | Key | Type | Description |
|-----------|-----|------|-------------|
| Input | `__maxEffectiveDate` | `DateOnly` | Executor-injected effective date |
| Output | `effective_date_ref` | `DataFrame` | Single row: `effective_date = "yyyy-MM-dd"` |

---

## 11. V2 Job Config JSON

```json
{
  "jobName": "FeeRevenueDailyV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "overdraft_events",
      "schema": "datalake",
      "table": "overdraft_events",
      "columns": ["fee_amount", "fee_waived", "as_of"],
      "minEffectiveDate": "2024-10-01",
      "maxEffectiveDate": "2024-12-31"
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.FeeRevenueDailyV2Processor"
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT edr.effective_date AS event_date, SUM(CASE WHEN oe.fee_waived = 0 THEN oe.fee_amount ELSE 0.0 END) AS charged_fees, SUM(CASE WHEN oe.fee_waived = 1 THEN oe.fee_amount ELSE 0.0 END) AS waived_fees, SUM(CASE WHEN oe.fee_waived = 0 THEN oe.fee_amount ELSE 0.0 END) - SUM(CASE WHEN oe.fee_waived = 1 THEN oe.fee_amount ELSE 0.0 END) AS net_revenue, edr.effective_date AS as_of FROM overdraft_events oe CROSS JOIN effective_date_ref edr WHERE oe.as_of = edr.effective_date GROUP BY edr.effective_date UNION ALL SELECT 'MONTHLY_TOTAL' AS event_date, SUM(CASE WHEN oe.fee_waived = 0 THEN oe.fee_amount ELSE 0.0 END) AS charged_fees, SUM(CASE WHEN oe.fee_waived = 1 THEN oe.fee_amount ELSE 0.0 END) AS waived_fees, SUM(CASE WHEN oe.fee_waived = 0 THEN oe.fee_amount ELSE 0.0 END) - SUM(CASE WHEN oe.fee_waived = 1 THEN oe.fee_amount ELSE 0.0 END) AS net_revenue, edr.effective_date AS as_of FROM overdraft_events oe CROSS JOIN effective_date_ref edr WHERE edr.effective_date = date(edr.effective_date, 'start of month', '+1 month', '-1 day') AND EXISTS (SELECT 1 FROM overdraft_events oe2 WHERE oe2.as_of = edr.effective_date) GROUP BY edr.effective_date"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/fee_revenue_daily.csv",
      "includeHeader": true,
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

---

## 12. Traceability Matrix

| BRD Requirement | FSD Section | V2 Implementation |
|-----------------|-------------|-------------------|
| BR-1: Hardcoded date range (2024-10-01 to 2024-12-31) | Section 3, Section 7 (AP10) | DataSourcing `minEffectiveDate: "2024-10-01"`, `maxEffectiveDate: "2024-12-31"`. Retained for EC-1 equivalence. |
| BR-2: Filter to effective date | Section 4 (SQL), Section 9 (BRD Correction) | SQL `WHERE oe.as_of = edr.effective_date`. BRD error corrected — V1 code filters to `__maxEffectiveDate`, not minus 1 day. |
| BR-3: Fee categorization (waived vs. charged) | Section 4 (SQL) | `CASE WHEN fee_waived = 0 THEN fee_amount ELSE 0.0 END` for charged; `CASE WHEN fee_waived = 1 ...` for waived. |
| BR-4: Net revenue = charged - waived | Section 4 (SQL) | `SUM(charged) - SUM(waived) AS net_revenue` |
| BR-5: Double-precision accumulation (W6) | Section 6 (W6) | SQLite REAL type naturally uses double. Framework maps `decimal` → REAL [Transformation.cs:101]. |
| BR-6: MONTHLY_TOTAL on last day of month (W3b) | Section 4 (SQL, second UNION ALL branch), Section 6 (W3b) | `WHERE edr.effective_date = date(edr.effective_date, 'start of month', '+1 month', '-1 day')` |
| BR-7: "MONTHLY_TOTAL" label | Section 4 (SQL) | `'MONTHLY_TOTAL' AS event_date` literal |
| BR-8: Empty result when no events | Section 4 (SQL Design Notes, point 4) | First SELECT returns no rows; EXISTS subquery suppresses MONTHLY_TOTAL. |
| EC-1: Monthly total sums full source range | Section 4 (SQL, EC-1 note), Section 7 (AP10) | Second SELECT has no `as_of` filter on `oe`, scanning all sourced rows. |
| EC-2: Floating-point precision (W6) | Section 6 (W6), Section 8 (Proofmark) | Natural double via SQLite REAL. Proofmark starts strict; fuzzy fallback documented. |
| EC-3: Overwrite on multi-day runs (W9) | Section 6 (W9), Section 5 | `writeMode: "Overwrite"` matches V1. |
| EC-4: No events → empty output | Section 4 (BR-8 guard) | SQL produces zero rows. EXISTS guard prevents orphan MONTHLY_TOTAL. |
| EC-5: Fallback for missing `__maxEffectiveDate` | Section 10 (External Module) | External uses `DateOnly.FromDateTime(DateTime.Today)` fallback. |
