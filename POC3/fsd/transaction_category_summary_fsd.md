# TransactionCategorySummary -- Functional Specification Document

## 1. Job Summary

**Job:** TransactionCategorySummaryV2
**V1 Config:** `JobExecutor/Jobs/transaction_category_summary.json`

TransactionCategorySummary produces a per-date, per-transaction-type (Debit/Credit) summary showing total amount, transaction count, and average amount. Output is a CSV file in Append mode with an `END|{row_count}` trailer appended after each effective date's data rows. The V2 rewrite eliminates all identified code-quality anti-patterns (dead-end sourcing of the `segments` table, unused columns, and a vestigial CTE with window functions) while preserving byte-identical output through a simplified Tier 1 framework-only module chain.

## 2. V2 Module Chain

**Tier:** 1 (Framework Only) -- `DataSourcing -> Transformation (SQL) -> CsvFileWriter`

**Justification:** The V1 job is already a Tier 1 implementation: DataSourcing, SQL Transformation, and CsvFileWriter. All business logic (GROUP BY aggregation, ROUND, SUM, COUNT, AVG, ORDER BY) is standard SQL expressible in SQLite. No External module is involved in V1 and none is needed for V2. The only changes are eliminating dead-end data sources, unused columns, and unnecessary CTE complexity.

```
DataSourcing ("transactions")
    -> Transformation ("txn_cat_summary")
        -> CsvFileWriter (Append, trailer, LF)
```

**Removed from V1 chain:**
- DataSourcing for `segments` table (AP1: sourced but never referenced in SQL)

## 3. DataSourcing Config

### V2 DataSourcing: `transactions`

| Property | Value | Notes |
|----------|-------|-------|
| resultName | `transactions` | Same as V1 |
| schema | `datalake` | Same as V1 |
| table | `transactions` | Same as V1 |
| columns | `["account_id", "txn_type", "amount"]` | Reduced from V1 (see AP4 below) |

**Effective date handling:** No explicit `minEffectiveDate` / `maxEffectiveDate` in the DataSourcing config. The executor injects these into shared state via `__minEffectiveDate` / `__maxEffectiveDate`, and DataSourcing picks them up automatically. This matches V1 behavior. Evidence: [transaction_category_summary.json:6-10] -- no date fields in V1 DataSourcing config; [Architecture.md] -- executor injects dates; [Lib/Modules/DataSourcing.cs] -- module reads `__minEffectiveDate` / `__maxEffectiveDate` from shared state.

**Note on `as_of` column:** The `as_of` column is NOT listed in the `columns` array because DataSourcing automatically appends it to the DataFrame when it is not explicitly included. Evidence: [Lib/Modules/DataSourcing.cs] -- `as_of` is injected by the module. The SQL references `as_of` for GROUP BY and ORDER BY, which works because it is present in the registered SQLite table.

### V1 DataSourcing: `segments` (REMOVED in V2)

The V1 config sources `datalake.segments` with columns `["segment_id", "segment_name", "segment_code"]` and registers it as a SQLite table, but the Transformation SQL never references the `segments` table. This is a dead-end data source (AP1). V2 removes it entirely.

Evidence: [transaction_category_summary.json:12-17] sources segments; [transaction_category_summary.json:22] SQL only references `transactions` (via the CTE alias `txn_stats`).

### Columns Removed (AP4)

V1 sources `["transaction_id", "account_id", "txn_type", "amount"]` from `transactions`. Of these:

| Column | Used in SQL? | V2 Action |
|--------|-------------|-----------|
| `transaction_id` | Only inside CTE window function `ORDER BY transaction_id` which is itself unused (AP8) | **Removed** |
| `account_id` | Not referenced in the Transformation SQL outer SELECT or GROUP BY | **Removed** |
| `txn_type` | Yes -- GROUP BY key, output column | **Kept** |
| `amount` | Yes -- SUM, COUNT, AVG aggregations | **Kept** |

Evidence: [transaction_category_summary.json:10] V1 columns list; [transaction_category_summary.json:22] SQL only uses `txn_type`, `as_of`, and `amount` in the outer aggregation that produces output.

## 4. Transformation SQL

### V1 SQL (for reference)

```sql
WITH txn_stats AS (
    SELECT txn_type, as_of, amount,
           ROW_NUMBER() OVER (PARTITION BY txn_type, as_of ORDER BY transaction_id) AS rn,
           COUNT(*) OVER (PARTITION BY txn_type, as_of) AS type_count
    FROM transactions
)
SELECT txn_type, as_of,
       ROUND(SUM(amount), 2) AS total_amount,
       COUNT(*) AS transaction_count,
       ROUND(AVG(amount), 2) AS avg_amount
FROM txn_stats
GROUP BY txn_type, as_of
ORDER BY as_of, txn_type
```

**V1 SQL analysis:**
- The CTE `txn_stats` computes `ROW_NUMBER()` and `COUNT(*) OVER(...)` partitioned by `txn_type, as_of`. These window function results (`rn`, `type_count`) are **never used** in the outer query. The outer query re-aggregates with its own `GROUP BY txn_type, as_of` using `SUM`, `COUNT(*)`, and `AVG` -- none of which reference `rn` or `type_count`.
- The CTE is pure computational waste (AP8). It adds overhead but does not change the output because the outer GROUP BY produces the same aggregation result regardless of whether the input rows have extra columns.
- The `ORDER BY transaction_id` inside ROW_NUMBER is also irrelevant because `rn` is never used.

### V2 SQL (simplified -- AP8 eliminated)

```sql
SELECT
    t.txn_type,
    t.as_of,
    ROUND(SUM(t.amount), 2) AS total_amount,
    COUNT(*) AS transaction_count,
    ROUND(AVG(t.amount), 2) AS avg_amount
FROM transactions t
GROUP BY t.txn_type, t.as_of
ORDER BY t.as_of, t.txn_type
```

**Changes from V1:**
1. **Removed CTE with unused window functions** (AP8): The `txn_stats` CTE computed `ROW_NUMBER()` and `COUNT(*) OVER(...)` that were never referenced in the output. V2 queries `transactions` directly. The GROUP BY, aggregation functions, and ORDER BY are identical, so output is unchanged.
2. **Identical aggregation logic**: `ROUND(SUM(amount), 2)`, `COUNT(*)`, `ROUND(AVG(amount), 2)` are preserved verbatim.
3. **Identical grouping**: `GROUP BY txn_type, as_of` preserved.
4. **Identical ordering**: `ORDER BY as_of, txn_type` preserved -- "Credit" sorts before "Debit" alphabetically for any given date.
5. **Identical column order**: `txn_type, as_of, total_amount, transaction_count, avg_amount` matches V1 output column order exactly.

**Why this is safe:** The CTE merely passes through all original rows with two extra columns (`rn`, `type_count`) that are immediately discarded by the outer GROUP BY aggregation. Removing the CTE changes the FROM source from `txn_stats` to `transactions`, but the set of `(txn_type, as_of, amount)` tuples is identical. GROUP BY + SUM/COUNT/AVG over the same input produces the same output.

## 5. Writer Config

| Parameter | V1 Value | V2 Value | Match? |
|-----------|----------|----------|--------|
| Writer type | CsvFileWriter | CsvFileWriter | YES |
| source | `txn_cat_summary` | `txn_cat_summary` | YES |
| outputFile | `Output/curated/transaction_category_summary.csv` | `Output/double_secret_curated/transaction_category_summary.csv` | Path changed per V2 spec |
| includeHeader | `true` | `true` | YES |
| trailerFormat | `END\|{row_count}` | `END\|{row_count}` | YES |
| writeMode | `Append` | `Append` | YES |
| lineEnding | `LF` | `LF` | YES |

### Append + Header Behavior

Per CsvFileWriter source [Lib/Modules/CsvFileWriter.cs:42,47]:
- On the **first run** (file does not exist): header is written, then data rows, then trailer.
- On **subsequent runs** (file already exists, `_writeMode == Append`): header is suppressed, data rows are appended, then trailer is appended.

This produces the expected multi-day structure:
```
txn_type,as_of,total_amount,transaction_count,avg_amount
Credit,2024-10-01,{value},{value},{value}
Debit,2024-10-01,{value},{value},{value}
END|2
Credit,2024-10-02,{value},{value},{value}
Debit,2024-10-02,{value},{value},{value}
END|2
...
```

### Trailer Token Resolution

Per CsvFileWriter source [Lib/Modules/CsvFileWriter.cs:58-68]:
- `{row_count}` resolves to `df.Count` (number of data rows in the DataFrame for that run -- typically 2, one for Credit and one for Debit)
- The trailer format `END|{row_count}` does NOT include `{date}`, so no date token is resolved.
- Trailer is written on every run regardless of Append mode (no append guard on trailer logic).

## 6. Wrinkle Replication

| ID | Name | Applies? | V2 Action |
|----|------|----------|-----------|
| W1-W12 | All wrinkles | NO | None of the cataloged output-affecting wrinkles apply to this job. |

**Detailed assessment:**
- **W1 (Sunday skip):** No Sunday logic in V1. The job runs for all dates.
- **W2 (Weekend fallback):** No weekend date fallback logic.
- **W3a/b/c (Boundary rows):** No weekly/monthly/quarterly summary rows appended.
- **W4 (Integer division):** No integer division. All computations use SQL ROUND/SUM/COUNT/AVG on `amount` (which is a numeric type).
- **W5 (Banker's rounding):** ROUND in SQLite uses "round half away from zero" by default, which is consistent. No `MidpointRounding.ToEven` concern since computation is in SQL.
- **W6 (Double epsilon):** No double-precision accumulation in C# code. All monetary computation is in SQLite SQL, which uses 64-bit IEEE 754 internally but ROUND(..., 2) produces deterministic output.
- **W7 (Trailer inflated count):** The trailer uses `{row_count}` which resolves to `df.Count` -- the number of output rows (post-aggregation), not input rows. No inflation. Evidence: [Lib/Modules/CsvFileWriter.cs:63] `df.Count.ToString()`.
- **W8 (Trailer stale date):** The trailer format is `END|{row_count}` -- no `{date}` token, so no stale date concern.
- **W9 (Wrong writeMode):** Append is the correct mode for this job -- data accumulates across dates. Evidence: [transaction_category_summary_brd.md] confirms Append is intentional.
- **W10 (Absurd numParts):** Not applicable -- this is a CsvFileWriter, not ParquetFileWriter.
- **W12 (Header every append):** The framework's CsvFileWriter correctly suppresses headers on append runs [Lib/Modules/CsvFileWriter.cs:47]. No repeated headers.

**Summary:** This is a clean job with no output-affecting wrinkles. All computation is standard SQL aggregation via the framework.

## 7. Anti-Pattern Elimination

| ID | Name | Applies? | V2 Action | Evidence |
|----|------|----------|-----------|----------|
| AP1 | Dead-end sourcing | **YES** | **Eliminated.** V1 sources `datalake.segments` (segment_id, segment_name, segment_code) but the Transformation SQL never references the `segments` table. V2 removes this DataSourcing entry entirely. | [transaction_category_summary.json:12-17] sources segments; [transaction_category_summary.json:22] SQL only references `transactions` via the `txn_stats` CTE. |
| AP4 | Unused columns | **YES** | **Eliminated.** V1 sources `transaction_id` and `account_id` from `transactions`, but neither appears in the output-producing outer query. `transaction_id` is only used inside the removed CTE's `ORDER BY` for `ROW_NUMBER()` (which is itself unused). `account_id` is sourced but never referenced anywhere in the SQL. V2 sources only `["txn_type", "amount"]` (plus auto-injected `as_of`). | [transaction_category_summary.json:10] columns list; [transaction_category_summary.json:22] outer SELECT uses only `txn_type`, `as_of`, `amount`. |
| AP8 | Complex SQL / unused CTEs | **YES** | **Eliminated.** V1 uses a CTE `txn_stats` that computes `ROW_NUMBER() OVER (PARTITION BY txn_type, as_of ORDER BY transaction_id)` and `COUNT(*) OVER (PARTITION BY txn_type, as_of)`, but neither `rn` nor `type_count` is referenced in the outer query. The outer query re-aggregates from scratch with GROUP BY. V2 removes the CTE entirely and queries `transactions` directly. | [transaction_category_summary.json:22] CTE computes rn and type_count; outer SELECT uses only SUM, COUNT(*), AVG -- none reference rn or type_count. |
| AP3 | Unnecessary External module | NO | N/A -- V1 does not use an External module. | |
| AP10 | Over-sourcing dates | NO | V1 correctly relies on executor-injected effective dates via shared state. No explicit date filters in SQL. | |
| AP2 | Duplicated logic | NO | No cross-job duplication identified for this specific aggregation pattern. | |
| AP5 | Asymmetric NULLs | NO | No NULL handling asymmetries -- standard SQL aggregation functions ignore NULLs consistently. | |
| AP6 | Row-by-row iteration | NO | No C# iteration -- all logic is in SQL. | |
| AP7 | Magic values | NO | No hardcoded thresholds or magic strings in the SQL. | |
| AP9 | Misleading names | NO | The job name accurately describes its output: a summary of transactions by category (txn_type). | |

## 8. Proofmark Config

```yaml
comparison_target: "transaction_category_summary"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

**Rationale:**
- `reader: csv` -- Both V1 and V2 produce CSV output via CsvFileWriter.
- `header_rows: 1` -- Both V1 and V2 write a header row on the first run (`includeHeader: true`).
- `trailer_rows: 0` -- This is an **Append-mode** file. Trailers (`END|{row_count}`) are embedded throughout the file (one after each day's data rows), not just at the end. Per CONFIG_GUIDE.md Example 4: "For Append-mode files with embedded trailers, set `trailer_rows: 0` -- the trailers are part of the data."
- `threshold: 100.0` -- All computations are deterministic SQL aggregation. Exact match required.
- **No excluded columns** -- No non-deterministic fields identified in the BRD. All output columns (`txn_type`, `as_of`, `total_amount`, `transaction_count`, `avg_amount`) are fully deterministic.
- **No fuzzy columns** -- All monetary columns use SQLite `ROUND(..., 2)` which is deterministic. No double-precision accumulation in C# code (W6 does not apply). Both V1 and V2 use the same SQLite ROUND function, so results will be identical.

## 9. V2 Job Config JSON

```json
{
  "jobName": "TransactionCategorySummaryV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "transactions",
      "schema": "datalake",
      "table": "transactions",
      "columns": ["txn_type", "amount"]
    },
    {
      "type": "Transformation",
      "resultName": "txn_cat_summary",
      "sql": "SELECT t.txn_type, t.as_of, ROUND(SUM(t.amount), 2) AS total_amount, COUNT(*) AS transaction_count, ROUND(AVG(t.amount), 2) AS avg_amount FROM transactions t GROUP BY t.txn_type, t.as_of ORDER BY t.as_of, t.txn_type"
    },
    {
      "type": "CsvFileWriter",
      "source": "txn_cat_summary",
      "outputFile": "Output/double_secret_curated/transaction_category_summary.csv",
      "includeHeader": true,
      "trailerFormat": "END|{row_count}",
      "writeMode": "Append",
      "lineEnding": "LF"
    }
  ]
}
```

**Key differences from V1 config:**
- `jobName`: `TransactionCategorySummaryV2` (V2 naming convention)
- DataSourcing for `segments` removed (AP1)
- DataSourcing `columns` reduced from `["transaction_id", "account_id", "txn_type", "amount"]` to `["txn_type", "amount"]` (AP4). Note: `as_of` is automatically appended by the DataSourcing module when not explicitly listed.
- SQL simplified: CTE with unused window functions removed (AP8); direct query against `transactions` table
- `outputFile` path changed to `Output/double_secret_curated/transaction_category_summary.csv`
- All writer config params preserved identically: `includeHeader: true`, `trailerFormat: "END|{row_count}"`, `writeMode: "Append"`, `lineEnding: "LF"`
- `firstEffectiveDate` preserved: `"2024-10-01"`

## 10. Output Schema

| # | Column | Type | Source | Transformation | Evidence |
|---|--------|------|--------|---------------|----------|
| 1 | txn_type | TEXT | transactions.txn_type | Direct (GROUP BY key) | [transaction_category_summary.json:22] |
| 2 | as_of | TEXT | transactions.as_of | Direct (GROUP BY key, auto-injected by DataSourcing) | [transaction_category_summary.json:22] |
| 3 | total_amount | REAL | transactions.amount | `ROUND(SUM(amount), 2)` | [transaction_category_summary.json:22] |
| 4 | transaction_count | INTEGER | transactions | `COUNT(*)` | [transaction_category_summary.json:22] |
| 5 | avg_amount | REAL | transactions.amount | `ROUND(AVG(amount), 2)` | [transaction_category_summary.json:22] |

**Column order** matches V1 exactly: txn_type, as_of, total_amount, transaction_count, avg_amount.

**Expected output per day:** Typically 2 data rows (one for "Credit", one for "Debit") since the database contains exactly these two txn_type values. "Credit" appears before "Debit" alphabetically in the `ORDER BY as_of, txn_type` sort.

## 11. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision | Evidence |
|-----------------|-------------|-----------------|----------|
| BR-1: Group by txn_type, as_of | SQL Design (Sec 4) | `GROUP BY t.txn_type, t.as_of` preserved | [transaction_category_summary.json:22] |
| BR-2: CTE with unused window functions | Anti-Pattern Elimination (Sec 7) | **Eliminated** (AP8). CTE with ROW_NUMBER and COUNT OVER removed -- outer GROUP BY already performs aggregation. | [transaction_category_summary.json:22] |
| BR-3: total_amount = ROUND(SUM(amount), 2) | SQL Design (Sec 4) | `ROUND(SUM(t.amount), 2) AS total_amount` preserved verbatim | [transaction_category_summary.json:22] |
| BR-4: transaction_count = COUNT(*) | SQL Design (Sec 4) | `COUNT(*) AS transaction_count` preserved verbatim | [transaction_category_summary.json:22] |
| BR-5: avg_amount = ROUND(AVG(amount), 2) | SQL Design (Sec 4) | `ROUND(AVG(t.amount), 2) AS avg_amount` preserved verbatim | [transaction_category_summary.json:22] |
| BR-6: ORDER BY as_of ASC, txn_type ASC | SQL Design (Sec 4) | `ORDER BY t.as_of, t.txn_type` preserved | [transaction_category_summary.json:22] |
| BR-7: Trailer format END\|{row_count} | Writer Config (Sec 5) | `trailerFormat: "END\|{row_count}"` preserved verbatim | [transaction_category_summary.json:29] |
| BRD: firstEffectiveDate = 2024-10-01 | Job Config (Sec 9) | `firstEffectiveDate: "2024-10-01"` preserved | [transaction_category_summary.json:3] |
| BRD: Append writeMode | Writer Config (Sec 5) | `writeMode: "Append"` preserved | [transaction_category_summary.json:30] |
| BRD: LF lineEnding | Writer Config (Sec 5) | `lineEnding: "LF"` preserved | [transaction_category_summary.json:31] |
| BRD: includeHeader = true | Writer Config (Sec 5) | `includeHeader: true` preserved | [transaction_category_summary.json:28] |
| BRD: Unused segments source | Anti-Pattern Elimination (Sec 7) | **Eliminated** (AP1). Not sourced in V2. | [transaction_category_summary.json:12-17] vs SQL |
| BRD: Unused transaction_id, account_id columns | Anti-Pattern Elimination (Sec 7) | **Eliminated** (AP4). Not sourced in V2. | [transaction_category_summary.json:10] vs SQL |
| BRD: Edge case -- no transactions for a date | SQL Design (Sec 4) | GROUP BY produces 0 rows; writer emits `END\|0` trailer | [Lib/Modules/CsvFileWriter.cs:58-68] |
| BRD: Edge case -- only one txn_type on a date | SQL Design (Sec 4) | GROUP BY produces 1 row; trailer shows `END\|1` | SQL semantics |
| BRD: Edge case -- Credit before Debit ordering | SQL Design (Sec 4) | `ORDER BY t.as_of, t.txn_type` -- alphabetical sort ensures Credit < Debit | SQL string ordering |

## 12. Open Questions

None. This is a straightforward Tier 1 job with well-documented business rules (all HIGH confidence in the BRD), no External modules, no non-deterministic fields, and no output-affecting wrinkles. The only V1 issues are three code-quality anti-patterns (AP1, AP4, AP8), all of which are cleanly eliminated without affecting output.
