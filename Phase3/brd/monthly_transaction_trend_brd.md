# MonthlyTransactionTrend -- Business Requirements Document

## Overview

This job computes daily transaction statistics (count, total amount, average amount) from raw transaction data and appends one row per effective date. It produces a running log of daily transaction metrics across all processed dates.

## Source Tables

### datalake.transactions
- **Columns used**: `transaction_id` (implicitly via COUNT), `amount`, `as_of`
- **Columns sourced but unused**: `account_id`, `txn_type` (see AP-4)
- **Filter**: `as_of >= '2024-10-01'` in the SQL, but this is redundant because DataSourcing already filters to the current effective date
- **Evidence**: [JobExecutor/Jobs/monthly_transaction_trend.json:22] SQL with `WHERE as_of >= '2024-10-01'`

### datalake.branches (UNUSED)
- **Columns sourced**: `branch_id`, `branch_name`
- **Usage**: NONE -- the Transformation SQL does not reference the `branches` table
- **Evidence**: [JobExecutor/Jobs/monthly_transaction_trend.json:22] SQL only references `transactions` table
- See AP-1.

## Business Rules

BR-1: For each effective date, count all transactions, sum their amounts, and compute the average amount.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/monthly_transaction_trend.json:22] SQL: `COUNT(*) AS daily_transactions, ROUND(SUM(amount), 2) AS daily_amount, ROUND(AVG(amount), 2) AS avg_transaction_amount`
- Evidence: [curated.monthly_transaction_trend] For as_of = 2024-10-01: daily_transactions = 405, daily_amount = 362968.14, avg_transaction_amount = 896.22. Verified by direct query: `SELECT COUNT(*), ROUND(SUM(amount), 2), ROUND(AVG(amount), 2) FROM datalake.transactions WHERE as_of = '2024-10-01'` yields 405, 362968.14, 896.22.

BR-2: Results are grouped by as_of (one row per effective date).
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/monthly_transaction_trend.json:22] `GROUP BY as_of`
- Evidence: [curated.monthly_transaction_trend] Each as_of has exactly 1 row (31 rows for 31 dates)

BR-3: Amount values are rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/monthly_transaction_trend.json:22] `ROUND(SUM(amount), 2)` and `ROUND(AVG(amount), 2)`

BR-4: All transaction types (Debit and Credit) are included -- no txn_type filter.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/monthly_transaction_trend.json:22] SQL has no WHERE clause filtering on txn_type

BR-5: The output uses Append mode -- each effective date's row accumulates in the target table.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/monthly_transaction_trend.json:28] `"writeMode": "Append"`
- Evidence: [curated.monthly_transaction_trend] Contains 31 rows (one per day for Oct 1-31)

BR-6: Results are ordered by as_of.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/monthly_transaction_trend.json:22] `ORDER BY as_of`

## Output Schema

| Column | Source | Transformation |
|--------|--------|----------------|
| as_of | datalake.transactions.as_of (via DataSourcing injection) | GROUP BY key |
| daily_transactions | Derived: COUNT(*) of datalake.transactions | Count of all transactions for the date |
| daily_amount | Derived: SUM(amount) of datalake.transactions | ROUND to 2 decimal places |
| avg_transaction_amount | Derived: AVG(amount) of datalake.transactions | ROUND to 2 decimal places |

## Edge Cases

- **Zero transactions for a date**: Would produce no output row for that date (GROUP BY with no rows yields no results). In practice, all dates in the range have transactions.
- **Append mode**: Rows accumulate across runs. Re-running a date would produce duplicate rows.
- **Hardcoded date filter**: The `WHERE as_of >= '2024-10-01'` filter is redundant since DataSourcing loads only the current effective date's data. It would have no effect even if DataSourcing loaded a range.

## Anti-Patterns Identified

- **AP-1: Redundant Data Sourcing** -- The `branches` DataSourcing module fetches `branch_id` and `branch_name` from `datalake.branches`, but the Transformation SQL only queries the `transactions` table. The branches data is completely unused. V2 approach: Remove the branches DataSourcing module.

- **AP-2: Duplicated Transformation Logic** -- This job has a declared SameDay dependency on `DailyTransactionVolume`, which already computes the exact same daily transaction statistics (count, total amount, average amount) from `datalake.transactions` and writes them to `curated.daily_transaction_volume`. MonthlyTransactionTrend re-derives these same metrics from raw datalake.transactions instead of reading from the upstream curated table. The columns are equivalent: `daily_transactions` = `total_transactions`, `daily_amount` = `total_amount`, `avg_transaction_amount` = `avg_amount`. V2 approach: Read from `curated.daily_transaction_volume` instead of re-deriving from datalake.transactions. Rename columns in a simple SELECT to match expected output.

- **AP-4: Unused Columns Sourced** -- The transactions DataSourcing includes `account_id` and `txn_type`, neither of which is referenced in the Transformation SQL. The SQL only uses implicit `COUNT(*)`, `amount`, and `as_of`. V2 approach: If keeping the datalake source (rather than fixing AP-2), remove `account_id` and `txn_type` from the columns list. If fixing AP-2, the column list is moot.

- **AP-8: Overly Complex SQL** -- The SQL uses a CTE (`WITH base AS (...)`) that wraps a simple GROUP BY query, then does `SELECT ... FROM base ORDER BY as_of`. The CTE adds no value; the ORDER BY could be appended directly to the GROUP BY query. V2 approach: Simplify to a single query without CTE.

- **AP-7: Hardcoded Magic Values** -- The date `'2024-10-01'` in the WHERE clause is hardcoded. Since DataSourcing already handles effective date filtering, this literal date filter is redundant. V2 approach: Remove the hardcoded date filter entirely.

- **AP-9: Misleading Job/Table Names** -- The name "MonthlyTransactionTrend" suggests a monthly-level aggregation or trend analysis. In reality, the job produces daily-level statistics (one row per day). It is not a monthly aggregation nor does it compute trends (e.g., deltas, moving averages). V2 approach: Flag in documentation; do not rename.

- **AP-10: Missing Dependency Declarations** -- Note: a dependency on DailyTransactionVolume IS declared in `control.job_dependencies`. However, the job does not actually use the upstream output. If the V2 fixes AP-2 by reading from curated.daily_transaction_volume, the existing dependency declaration becomes correct and meaningful.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [JobExecutor/Jobs/monthly_transaction_trend.json:22] SQL with COUNT, SUM, AVG |
| BR-2 | [JobExecutor/Jobs/monthly_transaction_trend.json:22] `GROUP BY as_of` |
| BR-3 | [JobExecutor/Jobs/monthly_transaction_trend.json:22] `ROUND(..., 2)` |
| BR-4 | [JobExecutor/Jobs/monthly_transaction_trend.json:22] no txn_type filter |
| BR-5 | [JobExecutor/Jobs/monthly_transaction_trend.json:28] `"writeMode": "Append"` |
| BR-6 | [JobExecutor/Jobs/monthly_transaction_trend.json:22] `ORDER BY as_of` |

## Open Questions

- None. All business rules are directly observable with HIGH confidence.
