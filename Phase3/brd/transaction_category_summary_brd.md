# BRD: TransactionCategorySummary

## Overview
This job produces a daily summary of transaction statistics grouped by transaction type (txn_type), computing total amount, transaction count, and average amount per type per effective date. The output is written to `curated.transaction_category_summary` in Append mode, building a cumulative history.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| transactions | datalake | transaction_id, account_id, txn_type, amount | Aggregated by txn_type and as_of | [JobExecutor/Jobs/transaction_category_summary.json:5-11] DataSourcing config; [transaction_category_summary.json:21-22] SQL |
| segments | datalake | segment_id, segment_name, segment_code | Sourced but NOT used in Transformation SQL | [transaction_category_summary.json:13-18] DataSourcing config; SQL does not reference segments table |

## Business Rules

BR-1: Transactions are grouped by txn_type and as_of, computing total_amount (SUM of amount rounded to 2), transaction_count (COUNT), and avg_amount (AVG of amount rounded to 2).
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/transaction_category_summary.json:22] SQL: `SELECT txn_type, as_of, ROUND(SUM(amount), 2) AS total_amount, COUNT(*) AS transaction_count, ROUND(AVG(amount), 2) AS avg_amount FROM txn_stats GROUP BY txn_type, as_of`
- Evidence: [curated.transaction_category_summary] Sample: txn_type='Credit', as_of='2024-10-01' has total_amount=137903.00, transaction_count=142, avg_amount=971.15

BR-2: The SQL uses a CTE (`txn_stats`) that computes ROW_NUMBER and COUNT window functions partitioned by txn_type and as_of. However, these computed columns (rn, type_count) are NOT used in the outer query — the CTE is functionally equivalent to selecting directly from transactions.
- Confidence: HIGH
- Evidence: [transaction_category_summary.json:22] CTE computes `ROW_NUMBER() OVER (PARTITION BY txn_type, as_of ORDER BY transaction_id) AS rn, COUNT(*) OVER (PARTITION BY txn_type, as_of) AS type_count` — neither rn nor type_count appear in outer SELECT or GROUP BY
- Evidence: The outer query groups by txn_type, as_of and aggregates amount, which works identically on the raw transactions data

BR-3: Results are ordered by as_of ASC, then txn_type ASC.
- Confidence: HIGH
- Evidence: [transaction_category_summary.json:22] `ORDER BY as_of, txn_type`

BR-4: Output is written in Append mode — each daily run adds rows without truncating prior data.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/transaction_category_summary.json:28] `"writeMode": "Append"`
- Evidence: [curated.transaction_category_summary] Contains 31 distinct as_of dates, 2 rows per date (Credit, Debit)

BR-5: Since DataSourcing loads exactly one day's data per run, and there are 2 transaction types (Credit, Debit), each run produces exactly 2 output rows.
- Confidence: HIGH
- Evidence: [datalake.transactions] `SELECT DISTINCT txn_type` yields Credit and Debit
- Evidence: [curated.transaction_category_summary] Consistently 2 rows per as_of

BR-6: All transactions are included in the aggregation — no amount threshold or other filter beyond the redundant `as_of >= '2024-10-01'` in the CTE (which is already handled by DataSourcing single-date loading).
- Confidence: MEDIUM
- Evidence: [transaction_category_summary.json:22] CTE selects from transactions with no explicit WHERE, but inherits the single-date DataSourcing data. Note: The CTE itself has no WHERE clause; the outer SELECT groups from it.
- Evidence: Examination of the SQL shows no WHERE clause at all in the CTE or outer query

BR-7: The segments DataFrame is sourced by the job config but is NOT used in the Transformation SQL.
- Confidence: HIGH
- Evidence: [transaction_category_summary.json:13-18] Segments sourced; SQL only references `txn_stats` CTE (derived from `transactions`)

BR-8: Transactions data exists for all 31 days of October (including weekends), so this job produces output for every calendar day.
- Confidence: HIGH
- Evidence: [datalake.transactions] 31 distinct as_of dates
- Evidence: [curated.transaction_category_summary] 31 distinct as_of dates, 2 rows each

BR-9: The ROUND function is applied to SUM(amount) and AVG(amount), producing values with exactly 2 decimal places.
- Confidence: HIGH
- Evidence: [transaction_category_summary.json:22] `ROUND(SUM(amount), 2)` and `ROUND(AVG(amount), 2)`

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| txn_type | transactions.txn_type | GROUP BY key | [transaction_category_summary.json:22] |
| as_of | transactions.as_of (via DataSourcing) | GROUP BY key | [transaction_category_summary.json:22] |
| total_amount | SUM(transactions.amount) | Rounded to 2 decimal places | [transaction_category_summary.json:22] |
| transaction_count | COUNT(*) | Integer count per txn_type per as_of | [transaction_category_summary.json:22] |
| avg_amount | AVG(transactions.amount) | Rounded to 2 decimal places | [transaction_category_summary.json:22] |

## Edge Cases
- **NULL handling**: No explicit NULL handling. If amount is NULL, SUM/AVG would ignore it per SQL semantics. COUNT(*) counts all rows regardless.
- **Weekend/date fallback**: Transactions have data on weekends, so no empty-day issue.
- **Zero-row behavior**: If no transactions exist for a given txn_type on an effective date, that txn_type would not appear in output (GROUP BY produces no row).
- **Duplicate prevention**: Append mode means re-running the same effective date would create duplicate rows. The framework's gap-fill logic prevents this under normal operation.
- **Unused CTE columns**: The ROW_NUMBER and COUNT window functions in the CTE are computed but not used. This is wasted computation but does not affect correctness.
- **ROUND behavior**: SQLite ROUND is used (banker's rounding). Potential minor discrepancy with PostgreSQL ROUND on exact .5 values.

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [transaction_category_summary.json:22], [curated data verification] |
| BR-2 | [transaction_category_summary.json:22] |
| BR-3 | [transaction_category_summary.json:22] |
| BR-4 | [transaction_category_summary.json:28], [curated data observation] |
| BR-5 | [datalake.transactions txn_type analysis], [curated data observation] |
| BR-6 | [transaction_category_summary.json:22] |
| BR-7 | [transaction_category_summary.json:13-18], [SQL analysis] |
| BR-8 | [datalake.transactions date analysis], [curated data observation] |
| BR-9 | [transaction_category_summary.json:22] |

## Open Questions
- The segments table is sourced but unused. Confidence: MEDIUM that this is an oversight.
- The CTE computes ROW_NUMBER and COUNT window functions that are never used. This could be leftover from development or intended for a different version of the SQL. Confidence: HIGH that these are unused dead code in the SQL — no impact on output.
- SQLite ROUND vs PostgreSQL ROUND: Same concern as MonthlyTransactionTrend — potential rounding edge cases on .5 values. Confidence: MEDIUM.
