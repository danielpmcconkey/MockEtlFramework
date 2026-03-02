# MonthlyTransactionTrend — Functional Specification Document

## 1. Overview & Tier Classification

**Job:** MonthlyTransactionTrendV2
**Tier:** 1 (Framework Only) -- `DataSourcing -> Transformation (SQL) -> CsvFileWriter`

This job produces daily transaction metrics (count, total amount, average amount) aggregated across all accounts for each effective date. Output is a vanilla CSV (no trailer), appended per effective date, intended to support monthly trend analysis. The V1 implementation is already a Tier 1 framework job. V2 preserves the same architecture with anti-pattern cleanups.

**Justification for Tier 1:** All business logic (GROUP BY aggregation, COUNT, SUM, AVG, ROUND) is expressible in SQLite SQL. No procedural logic, no cross-date-range queries, no External module needed. V1 is already Tier 1 and V2 stays Tier 1.

---

## 2. V2 Module Chain

```
DataSourcing ("transactions")
    -> Transformation ("monthly_trend")
        -> CsvFileWriter (Append, LF, no trailer)
```

### Module 1: DataSourcing
- **resultName:** `transactions`
- **schema:** `datalake`
- **table:** `transactions`
- **columns:** `["amount"]`
- Effective dates injected by executor via shared state (`__minEffectiveDate`, `__maxEffectiveDate`). The framework automatically appends `as_of` to the result since `as_of` is not in the explicit column list. [DataSourcing.cs:69-72]

### Module 2: Transformation
- **resultName:** `monthly_trend`
- **sql:** Simplified SQL (see Section 5)

### Module 3: CsvFileWriter
- **source:** `monthly_trend`
- **outputFile:** `Output/double_secret_curated/monthly_transaction_trend.csv`
- **includeHeader:** `true`
- **writeMode:** `Append`
- **lineEnding:** `LF`
- No `trailerFormat` (matching V1 -- no trailer)

---

## 3. Anti-Pattern Analysis

### Anti-Patterns Identified and Eliminated

| ID | Name | Applies? | V1 Evidence | V2 Action |
|----|------|----------|-------------|-----------|
| AP1 | Dead-end sourcing | YES | V1 sources a `branches` table (`branch_id`, `branch_name`) that is registered as a SQLite table but never referenced in the SQL transformation. The SQL only queries `transactions`. [monthly_transaction_trend.json:12-17 vs :22] | **ELIMINATED.** V2 removes the `branches` DataSourcing module entirely. No data is sourced that is not used. |
| AP4 | Unused columns | YES | V1 sources `transaction_id`, `account_id`, and `txn_type` from the `transactions` table. None of these columns are referenced in the SQL. `COUNT(*)` counts rows without referencing a specific column. `SUM(amount)` and `AVG(amount)` reference only `amount`. `GROUP BY as_of` references only `as_of` (auto-appended by DataSourcing). [monthly_transaction_trend.json:10 vs :22] | **ELIMINATED.** V2 DataSourcing sources only `["amount"]`. The `as_of` column is automatically appended by the framework. |
| AP8 | Complex SQL / unused CTEs | YES | V1 SQL uses a CTE (`base`) that computes exactly the same columns the outer SELECT re-selects: `as_of, daily_transactions, daily_amount, avg_transaction_amount`. The CTE is a pure pass-through with no additional filtering or transformation in the outer query. It adds structural complexity without benefit. [monthly_transaction_trend.json:22] | **ELIMINATED.** V2 SQL removes the CTE and computes the aggregation directly in a single SELECT statement. |
| AP10 | Over-sourcing dates | PARTIAL | V1 SQL includes `WHERE as_of >= '2024-10-01'` which is redundant with the framework's effective date injection. DataSourcing already filters by `__minEffectiveDate`/`__maxEffectiveDate`, so the SQL-level date filter is unnecessary when the executor controls the date range. [monthly_transaction_trend.json:22, DataSourcing.cs:74-78] | **ELIMINATED.** V2 removes the hardcoded `WHERE as_of >= '2024-10-01'` clause from the SQL. The framework's DataSourcing module already limits data to the effective date range, making the SQL filter redundant. Since the executor runs one effective date at a time [JobExecutorService.cs:100-101], only data for that single date reaches the Transformation module. The hardcoded filter cannot affect output because `firstEffectiveDate` in the job config is `2024-10-01` -- the executor will never inject a date before that. |
| AP3 | Unnecessary External module | NO | V1 does not use an External module. Already Tier 1. | N/A |
| AP2 | Duplicated logic | NO | While this job computes similar metrics to DailyTransactionSummary, cross-job duplication cannot be fixed within a single job's scope. Documented here for awareness. | Documented, no action. |
| AP5 | Asymmetric NULLs | NO | No NULL handling logic present. Aggregation functions (COUNT, SUM, AVG) handle NULLs per SQL standard. | N/A |
| AP6 | Row-by-row iteration | NO | No External module, no procedural logic. | N/A |
| AP7 | Magic values | NO | The only "magic value" was the hardcoded `2024-10-01` date in the SQL WHERE clause, which is addressed under AP10. | Addressed under AP10. |
| AP9 | Misleading names | NO | Job name "MonthlyTransactionTrend" is slightly misleading -- it produces daily aggregates, not monthly aggregates. The name implies monthly granularity but the output is at daily granularity (one row per as_of date). However, per AP9 prescription, we cannot rename V1 jobs. Output filename must match. | Documented. V2 job is named `MonthlyTransactionTrendV2`, preserving the convention. The daily granularity supports downstream monthly trend analysis, so the name is not entirely wrong -- just imprecise. |

### Output-Affecting Wrinkles Identified

| ID | Name | Applies? | Evidence | V2 Action |
|----|------|----------|----------|-----------|
| W1-W12 | (all) | NO | The V1 job is a straightforward Tier 1 framework job: no External module, no integer division, no hardcoded trailer dates, no weekend logic, no wrong write mode, no absurd numParts. The hardcoded `WHERE as_of >= '2024-10-01'` is redundant (not output-affecting given `firstEffectiveDate` alignment) rather than a wrinkle. No trailer means no trailer-related wrinkles (W7, W8, W12). | No wrinkles to reproduce. |

### Summary

| Category | Count | Details |
|----------|-------|---------|
| Anti-patterns eliminated | 4 | AP1 (dead-end `branches` sourcing), AP4 (unused columns: `transaction_id`, `account_id`, `txn_type`), AP8 (unnecessary pass-through CTE), AP10 (redundant hardcoded date filter in SQL) |
| Wrinkles reproduced | 0 | None applicable |
| Anti-patterns not applicable | AP2, AP3, AP5, AP6, AP7, AP9 | No External module, no procedural logic, no NULL asymmetry, no magic values beyond the date filter, misleading name documented but cannot be renamed |

---

## 4. Output Schema

| Column | Type | Source | Transformation | Evidence |
|--------|------|--------|---------------|----------|
| as_of | TEXT (date) | transactions.as_of | Direct from GROUP BY | [monthly_transaction_trend.json:22] |
| daily_transactions | INTEGER | transactions (all rows) | `COUNT(*)` | [monthly_transaction_trend.json:22] |
| daily_amount | REAL | transactions.amount | `ROUND(SUM(amount), 2)` | [monthly_transaction_trend.json:22] |
| avg_transaction_amount | REAL | transactions.amount | `ROUND(AVG(amount), 2)` | [monthly_transaction_trend.json:22] |

**Output ordering:** `ORDER BY as_of ASC` [monthly_transaction_trend.json:22]

**Column count:** 4 (matches V1 exactly)

---

## 5. SQL Design

### V1 SQL (for reference)
```sql
WITH base AS (
    SELECT as_of,
           COUNT(*) AS daily_transactions,
           ROUND(SUM(amount), 2) AS daily_amount,
           ROUND(AVG(amount), 2) AS avg_transaction_amount
    FROM transactions
    WHERE as_of >= '2024-10-01'
    GROUP BY as_of
)
SELECT as_of,
       daily_transactions,
       daily_amount,
       avg_transaction_amount
FROM base
ORDER BY as_of
```

### V2 SQL (simplified -- AP8 and AP10 eliminated)
```sql
SELECT as_of,
       COUNT(*) AS daily_transactions,
       ROUND(SUM(amount), 2) AS daily_amount,
       ROUND(AVG(amount), 2) AS avg_transaction_amount
FROM transactions
GROUP BY as_of
ORDER BY as_of
```

**Changes from V1:**
- **CTE removed (AP8):** The `WITH base AS (...)` wrapper is structurally unnecessary. The outer SELECT simply re-selects all 4 CTE columns with no additional filtering or transformation. V2 computes the aggregation directly in a single SELECT.
- **Hardcoded date filter removed (AP10):** `WHERE as_of >= '2024-10-01'` is redundant. DataSourcing already filters to the effective date range via `__minEffectiveDate`/`__maxEffectiveDate` parameters. The executor runs one date at a time, so only that date's data reaches the Transformation module. The filter cannot change output because `firstEffectiveDate: "2024-10-01"` ensures no earlier dates are ever processed.
- **Output-equivalent:** The V2 SQL produces identical rows in identical order. `ROUND()` in SQLite uses the same rounding behavior as V1's SQLite execution (both run against the same SQLite in-memory engine in the Transformation module). `COUNT(*)`, `SUM()`, `AVG()` operate identically. Removing the CTE and WHERE clause does not change the result set because:
  1. The CTE was a pure pass-through (no additional logic in outer SELECT).
  2. The WHERE clause was redundant with framework-level date filtering.

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "MonthlyTransactionTrendV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "transactions",
      "schema": "datalake",
      "table": "transactions",
      "columns": ["amount"]
    },
    {
      "type": "Transformation",
      "resultName": "monthly_trend",
      "sql": "SELECT as_of, COUNT(*) AS daily_transactions, ROUND(SUM(amount), 2) AS daily_amount, ROUND(AVG(amount), 2) AS avg_transaction_amount FROM transactions GROUP BY as_of ORDER BY as_of"
    },
    {
      "type": "CsvFileWriter",
      "source": "monthly_trend",
      "outputFile": "Output/double_secret_curated/monthly_transaction_trend.csv",
      "includeHeader": true,
      "writeMode": "Append",
      "lineEnding": "LF"
    }
  ]
}
```

**Key differences from V1 config:**
- `jobName`: `MonthlyTransactionTrendV2` (V2 naming convention)
- `branches` DataSourcing module removed entirely (AP1 -- dead-end sourcing eliminated)
- `columns`: `["amount"]` only (AP4 -- removed unused `transaction_id`, `account_id`, `txn_type`)
- `sql`: Simplified, no CTE, no hardcoded date filter (AP8, AP10 eliminated)
- `outputFile`: `Output/double_secret_curated/monthly_transaction_trend.csv` (V2 output path)
- All writer config params match V1 exactly: `includeHeader: true`, `writeMode: Append`, `lineEnding: LF`, no `trailerFormat`

---

## 7. Writer Configuration

| Parameter | Value | Matches V1? | Evidence |
|-----------|-------|-------------|----------|
| Writer type | CsvFileWriter | YES | [monthly_transaction_trend.json:25] |
| source | `monthly_trend` | YES | [monthly_transaction_trend.json:26] |
| outputFile | `Output/double_secret_curated/monthly_transaction_trend.csv` | Path changed (V2 output dir) | [BLUEPRINT.md: V2 output paths] |
| includeHeader | `true` | YES | [monthly_transaction_trend.json:28] |
| trailerFormat | (not specified) | YES | [monthly_transaction_trend.json:25-31 -- no trailerFormat key present] |
| writeMode | `Append` | YES | [monthly_transaction_trend.json:29] |
| lineEnding | `LF` | YES | [monthly_transaction_trend.json:30] |

### Write Mode Behavior (Append)
- First run: creates file with header + data row(s). [CsvFileWriter.cs:47: `if (_includeHeader && !append)`]
- Subsequent runs: appends data row(s) only (header suppressed because `append = true`). [CsvFileWriter.cs:42,47]
- Multi-day output structure: `header + day1_data + day2_data + day3_data + ...`
- No trailer lines are ever written since no `trailerFormat` is specified. [CsvFileWriter.cs:58: `if (_trailerFormat != null)`]
- LF line endings apply to all lines (header and data).
- During auto-advance, each effective date produces one row (GROUP BY as_of with single-date data yields one aggregation row per run). The file grows by one row per day.

---

## 8. Proofmark Config Design

### Rationale

- **Reader:** `csv` (output is a CSV file)
- **header_rows:** `1` (file includes a header row on first write; `includeHeader: true`)
- **trailer_rows:** `0` (no trailer -- `trailerFormat` is not specified in V1)
- **threshold:** `100.0` (strict -- all rows must match)

### Non-Deterministic Fields

None identified. The BRD explicitly states "Non-Deterministic Fields: None identified." [monthly_transaction_trend_brd.md:65-66]. All output columns (`as_of`, `daily_transactions`, `daily_amount`, `avg_transaction_amount`) are deterministic aggregations of source data. No timestamps, UUIDs, or execution-time values appear in the output. There is no trailer with a `{timestamp}` token.

### Column Overrides

None required. All four output columns are deterministic and should be compared strictly.

### Proposed Config

```yaml
comparison_target: "monthly_transaction_trend"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

---

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision | Evidence |
|-----------------|-------------|-----------------|----------|
| BR-1: Transactions aggregated by as_of date | Sec 5 (SQL) | `GROUP BY as_of` in V2 SQL | [monthly_transaction_trend.json:22] |
| BR-2: Hardcoded date filter `WHERE as_of >= '2024-10-01'` | Sec 3 (AP10), Sec 5 (SQL) | Eliminated as redundant with framework effective date injection. DataSourcing limits to effective date range; `firstEffectiveDate: "2024-10-01"` ensures no earlier dates are processed. Output is equivalent because the filter was always redundant. | [monthly_transaction_trend.json:22, DataSourcing.cs:74-78, monthly_transaction_trend.json:3] |
| BR-3: COUNT(*) for daily_transactions | Sec 5 (SQL) | `COUNT(*) AS daily_transactions` | [monthly_transaction_trend.json:22] |
| BR-4: ROUND(SUM(amount), 2) for daily_amount | Sec 5 (SQL) | `ROUND(SUM(amount), 2) AS daily_amount` | [monthly_transaction_trend.json:22] |
| BR-5: ROUND(AVG(amount), 2) for avg_transaction_amount | Sec 5 (SQL) | `ROUND(AVG(amount), 2) AS avg_transaction_amount` | [monthly_transaction_trend.json:22] |
| BR-6: ORDER BY as_of ASC | Sec 5 (SQL) | `ORDER BY as_of` | [monthly_transaction_trend.json:22] |
| BR-7: No trailer line | Sec 7 (Writer) | No `trailerFormat` specified in V2 config, matching V1 | [monthly_transaction_trend.json:26-30] |
| BRD: Append write mode | Sec 7 (Writer) | `writeMode: "Append"` | [monthly_transaction_trend.json:29] |
| BRD: LF line ending | Sec 7 (Writer) | `lineEnding: "LF"` | [monthly_transaction_trend.json:30] |
| BRD: includeHeader = true | Sec 7 (Writer) | `includeHeader: true` (header on first run only per Append behavior) | [monthly_transaction_trend.json:28] |
| BRD: firstEffectiveDate = 2024-10-01 | Sec 6 (Config) | `firstEffectiveDate: "2024-10-01"` | [monthly_transaction_trend.json:3] |
| BRD Edge Case: Unused branches source | Sec 3 (AP1) | Eliminated -- branches DataSourcing removed from V2 | [monthly_transaction_trend.json:12-17] |
| BRD Edge Case: CTE pass-through pattern | Sec 3 (AP8), Sec 5 (SQL) | Eliminated -- CTE removed, direct SELECT | [monthly_transaction_trend.json:22] |
| BRD: Non-deterministic fields = none | Sec 8 (Proofmark) | No exclusions or fuzzy overrides needed | [monthly_transaction_trend_brd.md:65-66] |

---

## 10. External Module Design

**Not applicable.** This job is Tier 1 (Framework Only). No External module is needed.

V1 does not use an External module, and V2 has no reason to introduce one. All business logic (COUNT, SUM, AVG, ROUND, GROUP BY, ORDER BY) is fully expressible in SQLite SQL within the framework's Transformation module.
