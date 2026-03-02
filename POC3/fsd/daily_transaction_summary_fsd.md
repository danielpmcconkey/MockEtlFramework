# DailyTransactionSummary — Functional Specification Document

## 1. Overview & Tier Selection

**Job:** DailyTransactionSummaryV2
**Tier:** 1 (Framework Only) -- `DataSourcing -> Transformation (SQL) -> CsvFileWriter`

**Justification:** The V1 job is already a clean Tier 1 implementation: two DataSourcing steps, one Transformation with standard SQL aggregation, and a CsvFileWriter with Append mode and trailer. All business logic (GROUP BY, CASE WHEN, ROUND, ORDER BY) is expressible in SQL. No External module is needed.

The only change from V1 is the elimination of the dead-end `branches` data source (AP1) and the removal of unused columns (AP4). The SQL itself can be simplified by removing the unnecessary subquery wrapper (AP8), but care must be taken to verify that the column ordering and rounding behavior remain identical.

## 2. V2 Module Chain

```
DataSourcing ("transactions")
    -> Transformation ("daily_txn_summary")
        -> CsvFileWriter (Append, trailer, LF)
```

**Removed from V1 chain:**
- DataSourcing for `branches` table (AP1: sourced but never used in SQL)

## 3. Anti-Pattern Analysis

### Anti-Patterns Identified

| ID | Name | Applies? | V2 Action |
|----|------|----------|-----------|
| AP1 | Dead-end sourcing | YES | **Eliminated.** The V1 config sources `datalake.branches` (branch_id, branch_name) but the Transformation SQL never references it. V2 removes this DataSourcing entry entirely. Evidence: [daily_transaction_summary.json:13-18] sources branches; [daily_transaction_summary.json:22] SQL references only `transactions t`. |
| AP4 | Unused columns | YES | **Eliminated.** V1 sources `transaction_id` and `description` from the transactions table, but the SQL only uses `account_id`, `txn_type`, `amount`, and `as_of`. V2 removes `transaction_id` and `description` from the column list. Evidence: [daily_transaction_summary.json:10] columns list vs [daily_transaction_summary.json:22] SQL column references. |
| AP8 | Complex SQL / unused CTEs | YES | **Eliminated.** V1 wraps the aggregation in a subquery (`SELECT sub.* FROM (...) sub ORDER BY ...`). The outer SELECT merely reorders columns and applies ORDER BY -- both can be done directly in the inner query. V2 flattens this to a single SELECT with GROUP BY and ORDER BY. Evidence: [daily_transaction_summary.json:22] subquery wrapper adds no transformation. |
| AP3 | Unnecessary External module | NO | N/A -- V1 does not use an External module. |
| AP10 | Over-sourcing dates | NO | V1 correctly relies on executor-injected effective dates via shared state (`__minEffectiveDate` / `__maxEffectiveDate`). No explicit date filters in SQL. |

### Output-Affecting Wrinkles Identified

| ID | Name | Applies? | V2 Action |
|----|------|----------|-----------|
| W1-W12 | All wrinkles | NO | None of the cataloged wrinkles apply to this job. The job uses standard framework modules with no bugs, hardcoded dates, integer division, or special date logic. The trailer uses `{date}` token which resolves from `__maxEffectiveDate` correctly. The writeMode is Append as intended (data accumulates across days). |

**Summary:** This is a clean job with no output-affecting wrinkles. The only V1 issues are code-quality anti-patterns (AP1, AP4, AP8) that can be eliminated without affecting output.

## 4. Output Schema

| # | Column | Type | Source | Transformation | Evidence |
|---|--------|------|--------|---------------|----------|
| 1 | account_id | TEXT | transactions.account_id | Direct (GROUP BY key) | [daily_transaction_summary.json:22] |
| 2 | as_of | TEXT | transactions.as_of | Direct (GROUP BY key) | [daily_transaction_summary.json:22] |
| 3 | total_amount | REAL | transactions.amount | `ROUND(SUM(debit) + SUM(credit), 2)` | [daily_transaction_summary.json:22] |
| 4 | transaction_count | INTEGER | transactions | `COUNT(*)` | [daily_transaction_summary.json:22] |
| 5 | debit_total | REAL | transactions.amount | `ROUND(SUM(CASE WHEN txn_type='Debit' THEN amount ELSE 0 END), 2)` | [daily_transaction_summary.json:22] |
| 6 | credit_total | REAL | transactions.amount | `ROUND(SUM(CASE WHEN txn_type='Credit' THEN amount ELSE 0 END), 2)` | [daily_transaction_summary.json:22] |

**Column order** matches V1 exactly: account_id, as_of, total_amount, transaction_count, debit_total, credit_total. The V1 outer SELECT explicitly orders these columns, and V2 preserves the same SELECT column order.

**Trailer row:** `TRAILER|{row_count}|{date}` appended after each day's data rows. `{row_count}` = number of data rows for that day. `{date}` = `__maxEffectiveDate` for that run in `yyyy-MM-dd` format.

## 5. SQL Design

### V1 SQL (for reference)

```sql
SELECT sub.account_id, sub.as_of, sub.total_amount, sub.transaction_count,
       sub.debit_total, sub.credit_total
FROM (
    SELECT t.account_id, t.as_of,
           ROUND(SUM(CASE WHEN t.txn_type = 'Debit' THEN t.amount ELSE 0 END)
               + SUM(CASE WHEN t.txn_type = 'Credit' THEN t.amount ELSE 0 END), 2) AS total_amount,
           COUNT(*) AS transaction_count,
           ROUND(SUM(CASE WHEN t.txn_type = 'Debit' THEN t.amount ELSE 0 END), 2) AS debit_total,
           ROUND(SUM(CASE WHEN t.txn_type = 'Credit' THEN t.amount ELSE 0 END), 2) AS credit_total
    FROM transactions t
    GROUP BY t.account_id, t.as_of
) sub
ORDER BY sub.as_of, sub.account_id
```

### V2 SQL (simplified -- AP8 eliminated)

```sql
SELECT
    t.account_id,
    t.as_of,
    ROUND(
        SUM(CASE WHEN t.txn_type = 'Debit' THEN t.amount ELSE 0 END)
        + SUM(CASE WHEN t.txn_type = 'Credit' THEN t.amount ELSE 0 END),
        2
    ) AS total_amount,
    COUNT(*) AS transaction_count,
    ROUND(SUM(CASE WHEN t.txn_type = 'Debit' THEN t.amount ELSE 0 END), 2) AS debit_total,
    ROUND(SUM(CASE WHEN t.txn_type = 'Credit' THEN t.amount ELSE 0 END), 2) AS credit_total
FROM transactions t
GROUP BY t.account_id, t.as_of
ORDER BY t.as_of, t.account_id
```

**Changes from V1:**
1. **Removed subquery wrapper** (AP8): The outer SELECT did nothing -- it reordered columns (which is just a SELECT list) and applied ORDER BY, both of which can be done in the single query. The column order in the V2 SELECT list matches V1's outer SELECT exactly.
2. **Identical aggregation logic**: All ROUND, SUM, CASE WHEN, COUNT expressions are preserved verbatim.
3. **Identical ordering**: `ORDER BY as_of, account_id` is preserved.

**Why this is safe:** SQLite evaluates GROUP BY before ORDER BY in a single query. The V1 subquery wrapper does not change row filtering, column computation, or ordering semantics. The only effect of the wrapper was readability style -- the outer SELECT merely aliased the inner columns 1:1 and applied ORDER BY.

## 6. V2 Job Config JSON

```json
{
  "jobName": "DailyTransactionSummaryV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "transactions",
      "schema": "datalake",
      "table": "transactions",
      "columns": ["account_id", "txn_type", "amount"]
    },
    {
      "type": "Transformation",
      "resultName": "daily_txn_summary",
      "sql": "SELECT t.account_id, t.as_of, ROUND(SUM(CASE WHEN t.txn_type = 'Debit' THEN t.amount ELSE 0 END) + SUM(CASE WHEN t.txn_type = 'Credit' THEN t.amount ELSE 0 END), 2) AS total_amount, COUNT(*) AS transaction_count, ROUND(SUM(CASE WHEN t.txn_type = 'Debit' THEN t.amount ELSE 0 END), 2) AS debit_total, ROUND(SUM(CASE WHEN t.txn_type = 'Credit' THEN t.amount ELSE 0 END), 2) AS credit_total FROM transactions t GROUP BY t.account_id, t.as_of ORDER BY t.as_of, t.account_id"
    },
    {
      "type": "CsvFileWriter",
      "source": "daily_txn_summary",
      "outputFile": "Output/double_secret_curated/daily_transaction_summary.csv",
      "includeHeader": true,
      "trailerFormat": "TRAILER|{row_count}|{date}",
      "writeMode": "Append",
      "lineEnding": "LF"
    }
  ]
}
```

**Key differences from V1 config:**
- `jobName`: `DailyTransactionSummaryV2` (V2 naming convention)
- DataSourcing for `branches` removed (AP1)
- DataSourcing `columns` reduced to `["account_id", "txn_type", "amount"]` -- removed `transaction_id` and `description` (AP4). Note: `as_of` is automatically appended by the DataSourcing module when not explicitly listed [Lib/Modules/DataSourcing.cs:69-73], so it does not need to be in the columns array.
- SQL simplified: subquery wrapper removed (AP8)
- `outputFile` path changed to `Output/double_secret_curated/daily_transaction_summary.csv`
- All writer config params preserved: `includeHeader: true`, `trailerFormat: "TRAILER|{row_count}|{date}"`, `writeMode: "Append"`, `lineEnding: "LF"`

## 7. Writer Config

| Parameter | V1 Value | V2 Value | Match? |
|-----------|----------|----------|--------|
| Writer type | CsvFileWriter | CsvFileWriter | YES |
| source | daily_txn_summary | daily_txn_summary | YES |
| outputFile | Output/curated/daily_transaction_summary.csv | Output/double_secret_curated/daily_transaction_summary.csv | Path changed per spec |
| includeHeader | true | true | YES |
| trailerFormat | TRAILER\|{row_count}\|{date} | TRAILER\|{row_count}\|{date} | YES |
| writeMode | Append | Append | YES |
| lineEnding | LF | LF | YES |

### Append + Header Behavior

Per CsvFileWriter source [Lib/Modules/CsvFileWriter.cs:42,47]:
- On the **first run** (file does not exist): header is written, then data rows, then trailer.
- On **subsequent runs** (file already exists, `_writeMode == Append`): header is suppressed, data rows are appended, then trailer is appended.

This produces the expected multi-day structure:
```
header
day1_row1
day1_row2
...
TRAILER|{count}|{date1}
day2_row1
day2_row2
...
TRAILER|{count}|{date2}
...
```

### Trailer Token Resolution

Per CsvFileWriter source [Lib/Modules/CsvFileWriter.cs:58-68]:
- `{row_count}` resolves to `df.Count` (number of data rows in the DataFrame for that run)
- `{date}` resolves to `__maxEffectiveDate` formatted as `yyyy-MM-dd`
- Trailer is written on every run regardless of Append mode (no append guard on trailer logic)

## 8. Proofmark Config Design

```yaml
comparison_target: "daily_transaction_summary"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

**Rationale:**
- `reader: csv` -- V1 and V2 both produce CSV output.
- `header_rows: 1` -- Both V1 and V2 write a header row on the first run (`includeHeader: true`).
- `trailer_rows: 0` -- This is an Append-mode file. Trailers are embedded throughout the file (one per day), not just at the end. Per CONFIG_GUIDE.md: "For Append-mode files with embedded trailers, set `trailer_rows: 0` -- the trailers are part of the data."
- `threshold: 100.0` -- All computations are deterministic. No non-deterministic fields identified. Exact match required.
- **No excluded columns** -- All columns are deterministic.
- **No fuzzy columns** -- All monetary columns use SQLite ROUND(..., 2) which is deterministic. No double-precision accumulation issues (W6 does not apply -- all computation is in SQL via SQLite).

## 9. Traceability Matrix

| BRD Req | FSD Section | Design Decision | Evidence |
|---------|-------------|-----------------|----------|
| BR-1: Group by account_id, as_of | SQL Design | GROUP BY t.account_id, t.as_of preserved | [daily_transaction_summary.json:22] |
| BR-2: total_amount = SUM(debit) + SUM(credit), ROUND 2 | SQL Design | ROUND(SUM(CASE debit) + SUM(CASE credit), 2) preserved | [daily_transaction_summary.json:22] |
| BR-3: transaction_count = COUNT(*) | SQL Design | COUNT(*) AS transaction_count preserved | [daily_transaction_summary.json:22] |
| BR-4: debit_total/credit_total with CASE WHEN | SQL Design | Both ROUND(SUM(CASE WHEN ...)) expressions preserved | [daily_transaction_summary.json:22] |
| BR-5: ORDER BY as_of ASC, account_id ASC | SQL Design | ORDER BY t.as_of, t.account_id preserved | [daily_transaction_summary.json:22] |
| BR-6: Subquery wrapping pattern | SQL Design | **Eliminated** (AP8). Outer SELECT added no transformation. V2 produces identical results with flat query. | [daily_transaction_summary.json:22] |
| BR-7: Trailer format TRAILER\|{row_count}\|{date} | Writer Config | trailerFormat preserved verbatim | [daily_transaction_summary.json:29] |
| BRD: firstEffectiveDate = 2024-10-01 | Job Config | firstEffectiveDate preserved | [daily_transaction_summary.json:3] |
| BRD: Append writeMode | Writer Config | writeMode: Append preserved | [daily_transaction_summary.json:30] |
| BRD: LF lineEnding | Writer Config | lineEnding: LF preserved | [daily_transaction_summary.json:31] |
| BRD: includeHeader = true | Writer Config | includeHeader: true preserved | [daily_transaction_summary.json:28] |
| BRD: Unused branches source | Anti-Pattern Analysis | **Eliminated** (AP1). Not sourced in V2. | [daily_transaction_summary.json:13-18] |
| BRD: Edge case -- no transactions | SQL Design | GROUP BY produces 0 rows; writer emits header + 0-row trailer | [Lib/Modules/CsvFileWriter.cs:52-56] |
| BRD: Edge case -- Debits only | SQL Design | credit_total = 0.00 via ELSE 0 in CASE | [daily_transaction_summary.json:22] |
| BRD: Edge case -- Credits only | SQL Design | debit_total = 0.00 via ELSE 0 in CASE | [daily_transaction_summary.json:22] |

## 10. External Module Design

**Not required.** This job is Tier 1 (Framework Only). All business logic is expressed in SQL, and the CsvFileWriter handles output formatting including header, trailer, and Append mode. No External module is needed.
