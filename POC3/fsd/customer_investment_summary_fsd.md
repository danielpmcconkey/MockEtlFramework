# CustomerInvestmentSummary — Functional Specification Document

## 1. Overview

**Job:** CustomerInvestmentSummaryV2
**Tier:** Tier 1 — Framework Only (DataSourcing -> Transformation (SQL) -> CsvFileWriter)

This job produces a per-customer summary of investment accounts. For each customer who has at least one investment, it outputs the customer's name, the count of their investments, the total portfolio value (Banker's rounded to 2 decimal places), and the effective date. The V1 implementation uses an External module (`CustomerInvestmentSummaryBuilder.cs`) for logic that is entirely expressible in SQL — a textbook AP3 case.

### Tier Justification

All V1 business logic maps cleanly to SQL:
- **Aggregation** (COUNT, SUM with ROUND): native SQL operations
- **LEFT JOIN** (investments to customers): native SQL
- **COALESCE** for null name defaults: native SQL
- **Date injection** via `__maxEffectiveDate`: available in shared state and accessible via Transformation's SQLite registration

There is no procedural logic, no snapshot fallback, no cross-boundary date queries, and no operation that requires C#. Tier 1 is the correct and complete solution.

---

## 2. V2 Module Chain

```
DataSourcing("investments") -> DataSourcing("customers") -> Transformation(SQL) -> CsvFileWriter
```

| Step | Module | Config Key | Purpose |
|------|--------|------------|---------|
| 1 | DataSourcing | `investments` | Source investment data from `datalake.investments` |
| 2 | DataSourcing | `customers` | Source customer data from `datalake.customers` |
| 3 | Transformation | `output` | Aggregate investments per customer, join to customer names, apply Banker's rounding |
| 4 | CsvFileWriter | — | Write output CSV with header, no trailer, LF line endings, Overwrite mode |

**V1 had 5 modules (3 DataSourcing + 1 External + 1 CsvFileWriter). V2 has 4 modules (2 DataSourcing + 1 Transformation + 1 CsvFileWriter).**

The securities DataSourcing module is eliminated (AP1). The External module is replaced by a SQL Transformation (AP3, AP6).

---

## 3. Anti-Pattern Analysis

### Identified Anti-Patterns

| ID | Anti-Pattern | Applies? | V2 Treatment |
|----|-------------|----------|--------------|
| W5 | Banker's rounding | YES | Reproduced. SQLite's `ROUND()` uses Banker's rounding (round-half-to-even) by default. The SQL `ROUND(SUM(current_value), 2)` replicates V1's `Math.Round(totalValue, 2, MidpointRounding.ToEven)`. |
| AP1 | Dead-end sourcing | YES | **Eliminated.** V1 sources `datalake.securities` (security_id, ticker, security_name, security_type, sector) but the External module never references it. V2 removes this DataSourcing entry entirely. |
| AP3 | Unnecessary External module | YES | **Eliminated.** V1's `CustomerInvestmentSummaryBuilder.cs` performs aggregation, join, and rounding — all standard SQL operations. V2 replaces it with a single Transformation module. |
| AP4 | Unused columns | YES | **Eliminated.** V1 sources `birthdate` from customers and `advisor_id` and `investment_id` from investments, but the External module never uses them. V2 sources only the columns needed: `customer_id` and `current_value` from investments; `id`, `first_name`, `last_name` from customers. |
| AP6 | Row-by-row iteration | YES | **Eliminated.** V1 uses nested `foreach` loops to build dictionaries for aggregation and lookup. V2 uses a single SQL GROUP BY with JOIN. |

### Anti-Patterns NOT Present

| ID | Anti-Pattern | Why Not Applicable |
|----|-------------|-------------------|
| W1 | Sunday skip | No Sunday guard in V1 code |
| W2 | Weekend fallback | No weekend date logic in V1 code |
| W3a/b/c | Boundary summary rows | No boundary detection in V1 code |
| W4 | Integer division | V1 uses `Convert.ToDecimal` and `decimal` accumulation — no integer division |
| W6 | Double epsilon | V1 uses `decimal` for monetary accumulation, not `double` |
| W7 | Trailer inflated count | No trailer in V1 output |
| W8 | Trailer stale date | No trailer in V1 output |
| W9 | Wrong writeMode | Overwrite is correct for this job (single-date output with `as_of` column) |
| W10 | Absurd numParts | Not a Parquet job |
| W12 | Header every append | Not an Append-mode job |
| AP2 | Duplicated logic | No cross-job duplication identified within scope |
| AP5 | Asymmetric NULLs | NULL handling is consistent: missing customer names default to empty string |
| AP7 | Magic values | No hardcoded thresholds or magic strings |
| AP8 | Complex SQL / unused CTEs | V1 has no SQL (it's all in External module) |
| AP9 | Misleading names | Job name accurately describes output |
| AP10 | Over-sourcing dates | V1 uses framework effective date injection (no explicit date filters) |

---

## 4. Output Schema

| Column | Type | Source | Transformation | Evidence |
|--------|------|--------|---------------|----------|
| customer_id | INTEGER | investments.customer_id | GROUP BY key, cast via SQL implicit integer handling | [CustomerInvestmentSummaryBuilder.cs:42] |
| first_name | TEXT | customers.first_name | COALESCE to empty string when no matching customer | [CustomerInvestmentSummaryBuilder.cs:33] |
| last_name | TEXT | customers.last_name | COALESCE to empty string when no matching customer | [CustomerInvestmentSummaryBuilder.cs:34] |
| investment_count | INTEGER | COUNT of investment rows per customer_id | COUNT(*) in GROUP BY | [CustomerInvestmentSummaryBuilder.cs:49] |
| total_value | REAL | SUM of investments.current_value per customer_id | ROUND(..., 2) — Banker's rounding | [CustomerInvestmentSummaryBuilder.cs:43,62] |
| as_of | TEXT | `__maxEffectiveDate` from shared state | Injected as a constant via subquery on shared state date | [CustomerInvestmentSummaryBuilder.cs:25,71] |

### Column Order

The output columns MUST appear in this exact order: `customer_id`, `first_name`, `last_name`, `investment_count`, `total_value`, `as_of`. This matches V1's explicit column list at [CustomerInvestmentSummaryBuilder.cs:10-14].

---

## 5. SQL Design

### Approach

The Transformation module executes a single SQL statement against the SQLite in-memory database. The DataSourcing modules register their DataFrames as SQLite tables named `investments` and `customers`, which the SQL references directly.

**Key consideration — `as_of` from shared state:** The framework's Transformation module registers ALL DataFrames in shared state as SQLite tables. The `__maxEffectiveDate` shared state value is a `DateOnly` — NOT a DataFrame — so it will not be registered as a table. However, since each DataSourcing run is for a single effective date range (min = max for daily auto-advance), the `as_of` column in the sourced DataFrames already contains the correct `__maxEffectiveDate` value. We can select it from any sourced row.

For multi-day date ranges (where min != max), V1 aggregates all investment rows across dates without filtering by date, and sets `as_of` to `__maxEffectiveDate`. The investments DataFrame will contain `as_of` values from all dates in the range. We need to use the max `as_of` value from the data itself, which will equal `__maxEffectiveDate`.

### SQL Statement

```sql
SELECT
    i.customer_id,
    COALESCE(c.first_name, '') AS first_name,
    COALESCE(c.last_name, '') AS last_name,
    COUNT(*) AS investment_count,
    ROUND(SUM(i.current_value), 2) AS total_value,
    -- V1 uses __maxEffectiveDate from shared state. In daily auto-advance mode,
    -- max(as_of) from the data equals __maxEffectiveDate.
    MAX(i.as_of) AS as_of
FROM investments i
LEFT JOIN (
    SELECT DISTINCT id, first_name, last_name
    FROM customers
) c ON c.id = i.customer_id
GROUP BY i.customer_id
ORDER BY i.customer_id
```

### SQL Design Notes

1. **COALESCE for missing customers (BR-4):** When an investment's `customer_id` has no matching customer record, the LEFT JOIN produces NULLs for `first_name` and `last_name`. COALESCE maps these to empty strings, matching V1's `?? ""` behavior. [CustomerInvestmentSummaryBuilder.cs:33-34,57-59]

2. **ROUND with Banker's rounding (W5/BR-3):** SQLite's built-in `ROUND()` function uses Banker's rounding (round-half-to-even), which matches V1's `Math.Round(totalValue, 2, MidpointRounding.ToEven)`. This is the correct behavior to reproduce. [CustomerInvestmentSummaryBuilder.cs:62]

3. **DISTINCT on customer subquery:** V1 builds a customer lookup dictionary keyed by `id`. If a customer appears on multiple `as_of` dates within the effective date range, the dictionary's last-write-wins behavior means the last row's name is used. Using `SELECT DISTINCT id, first_name, last_name` deduplicates customers. Since customer name data is stable across dates in a snapshot-based datalake, this produces equivalent results. If names did change across dates, V1 would take the last-encountered value — but this scenario is not evidenced in the data.

4. **ORDER BY customer_id (BR-6):** V1 iterates dictionary insertion order, which is the order of first encounter in the investments data. The BRD notes this as MEDIUM confidence. To produce deterministic, reproducible output, V2 uses an explicit `ORDER BY i.customer_id`. This may produce a different row order than V1 if investments are not naturally ordered by customer_id. **NOTE:** If Proofmark comparison fails on row order, this will need investigation. If V1's dictionary order happens to be ascending customer_id (likely, since investments data is ordered by `as_of` and then by natural insertion order which typically correlates with customer_id), then `ORDER BY i.customer_id` will match. If not, the ORDER BY clause may need adjustment during resolution.

5. **as_of from data (BR-5):** `MAX(i.as_of)` returns the maximum effective date from the investment rows, which equals `__maxEffectiveDate` for both single-day and multi-day runs. This avoids needing to access shared state from SQL.

6. **Empty input handling (BR-2):** If investments or customers DataFrames are empty, the Transformation module's SQLite `RegisterTable` method skips empty DataFrames (returns without creating a table). The SQL query will fail if the `investments` table doesn't exist. However, when there are zero investment rows, DataSourcing returns an empty DataFrame, and the SQL against an unregistered table will error. **IMPORTANT:** We need to verify this behavior during testing. If the Transformation fails on an empty table, we may need to handle this edge case — but in practice, the effective date range always has data for active jobs. If this becomes an issue during Phase D, a Tier 2 escalation with a minimal External module for empty-check would be warranted.

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "CustomerInvestmentSummaryV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "investments",
      "schema": "datalake",
      "table": "investments",
      "columns": ["customer_id", "current_value"]
    },
    {
      "type": "DataSourcing",
      "resultName": "customers",
      "schema": "datalake",
      "table": "customers",
      "columns": ["id", "first_name", "last_name"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT i.customer_id, COALESCE(c.first_name, '') AS first_name, COALESCE(c.last_name, '') AS last_name, COUNT(*) AS investment_count, ROUND(SUM(i.current_value), 2) AS total_value, MAX(i.as_of) AS as_of FROM investments i LEFT JOIN (SELECT DISTINCT id, first_name, last_name FROM customers) c ON c.id = i.customer_id GROUP BY i.customer_id ORDER BY i.customer_id"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/customer_investment_summary.csv",
      "includeHeader": true,
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

### Config Changes from V1

| Aspect | V1 | V2 | Reason |
|--------|----|----|--------|
| jobName | `CustomerInvestmentSummary` | `CustomerInvestmentSummaryV2` | V2 naming convention |
| Securities DataSourcing | Present (5 columns) | **Removed** | AP1: dead-end sourcing — never referenced by External module |
| Investments columns | `investment_id, customer_id, account_type, current_value, advisor_id` | `customer_id, current_value` | AP4: removed unused columns (`investment_id`, `account_type`, `advisor_id`) |
| Customers columns | `id, first_name, last_name, birthdate` | `id, first_name, last_name` | AP4: removed unused column (`birthdate`) |
| External module | `CustomerInvestmentSummaryBuilder` | **Removed** | AP3: replaced by SQL Transformation |
| Transformation | None | Added | Replacement for External module logic |
| Output path | `Output/curated/...` | `Output/double_secret_curated/...` | V2 output directory |

---

## 7. Writer Configuration

**Writer type:** CsvFileWriter (matches V1)

| Parameter | Value | Matches V1? | Evidence |
|-----------|-------|-------------|----------|
| source | `output` | YES | [customer_investment_summary.json:33] |
| outputFile | `Output/double_secret_curated/customer_investment_summary.csv` | Path changed (V2 dir) | [customer_investment_summary.json:34] — V1: `Output/curated/customer_investment_summary.csv` |
| includeHeader | `true` | YES | [customer_investment_summary.json:35] |
| writeMode | `Overwrite` | YES | [customer_investment_summary.json:36] |
| lineEnding | `LF` | YES | [customer_investment_summary.json:37] |
| trailerFormat | Not specified (no trailer) | YES | V1 has no trailerFormat |

---

## 8. Proofmark Config Design

### Configuration

```yaml
comparison_target: "customer_investment_summary"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

### Justification

- **reader: csv** — V1 and V2 both use CsvFileWriter.
- **header_rows: 1** — V1 config has `includeHeader: true`.
- **trailer_rows: 0** — No trailer in V1 or V2.
- **threshold: 100.0** — Full strict match required. All output values are deterministic.
- **No excluded columns** — All columns are deterministic (no timestamps, no UUIDs, no runtime-generated values). The `as_of` column is derived from `__maxEffectiveDate` which is injected by the executor and is identical for V1 and V2 runs on the same effective date.
- **No fuzzy columns** — `total_value` uses Banker's rounding in both V1 (explicit `MidpointRounding.ToEven`) and V2 (SQLite's default `ROUND()` behavior). Both should produce identical decimal values. V1 accumulates with `decimal` (not `double`), so there are no floating-point epsilon issues.

### Risk Assessment

The primary risk is **row ordering** (BR-6). V1 iterates investments in DataFrame row order (as returned by DataSourcing, which is `ORDER BY as_of`), building a dictionary keyed by customer_id. The dictionary preserves insertion order, so output rows appear in order of first customer_id encounter. V2 uses `ORDER BY i.customer_id` (ascending integer order). These will match if customer_ids are first encountered in ascending order in the investment data — which is likely but not guaranteed. If Proofmark fails on row order, the SQL's ORDER BY clause may need adjustment.

---

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision |
|----------------|-------------|-----------------|
| BR-1: Per-customer aggregation (COUNT + SUM) | SQL Design | `GROUP BY i.customer_id` with `COUNT(*)` and `SUM(i.current_value)` |
| BR-2: Empty DataFrame on null/empty input | SQL Design Note 6 | Empty input returns no rows from SQL. Edge case noted for testing. |
| BR-3: Banker's rounding to 2 decimal places | SQL Design, W5 handling | `ROUND(SUM(...), 2)` — SQLite ROUND uses Banker's rounding |
| BR-4: Customer name lookup with empty string default | SQL Design | `LEFT JOIN` + `COALESCE(c.first_name, '')` and `COALESCE(c.last_name, '')` |
| BR-5: as_of from __maxEffectiveDate | SQL Design Note 5 | `MAX(i.as_of)` equals `__maxEffectiveDate` for the sourced date range |
| BR-6: Row ordering by dictionary insertion order | SQL Design Note 4 | `ORDER BY i.customer_id` — may need adjustment if V1 order differs |
| BR-7: Securities sourced but unused | Anti-Pattern Analysis, AP1 | Securities DataSourcing removed entirely |
| BR-8: Birthdate sourced but unused | Anti-Pattern Analysis, AP4 | `birthdate` column removed from customers DataSourcing |
| BR-9: Effective date range from executor injection | V2 Config | No explicit dates in DataSourcing — uses framework injection |
| Edge Case 1: Customer with no investments | SQL Design | Not in output — GROUP BY iterates investments only |
| Edge Case 2: Investment with no matching customer | SQL Design | LEFT JOIN + COALESCE produces empty name fields |
| Edge Case 3: Cross-date aggregation | SQL Design | All rows across date range aggregated together (same as V1) |

---

## 10. External Module Design

**Not applicable.** This is a Tier 1 implementation. No External module is needed.

The V1 External module (`CustomerInvestmentSummaryBuilder.cs`) is fully replaced by the SQL Transformation. All business logic — aggregation, join, null handling, rounding — is expressed in a single SQL statement.
