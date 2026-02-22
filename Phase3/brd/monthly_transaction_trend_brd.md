# BRD: MonthlyTransactionTrend

## Overview
This job produces a daily summary of transaction activity (count, total amount, average amount) for each effective date. Despite the name suggesting monthly aggregation, the job actually computes per-day statistics and appends them to a running trend table. The output is written to `curated.monthly_transaction_trend` in Append mode.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| transactions | datalake | transaction_id, account_id, txn_type, amount | Aggregated by as_of: COUNT, SUM(amount), AVG(amount) | [JobExecutor/Jobs/monthly_transaction_trend.json:5-11] DataSourcing config; [monthly_transaction_trend.json:21-22] Transformation SQL |
| branches | datalake | branch_id, branch_name | Sourced but NOT used in Transformation SQL | [monthly_transaction_trend.json:13-18] DataSourcing config; SQL does not reference branches table |

## Business Rules

BR-1: The job aggregates transactions by as_of date, computing daily_transactions (COUNT), daily_amount (SUM of amount rounded to 2 decimals), and avg_transaction_amount (AVG of amount rounded to 2 decimals).
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/monthly_transaction_trend.json:22] SQL: `COUNT(*) AS daily_transactions, ROUND(SUM(amount), 2) AS daily_amount, ROUND(AVG(amount), 2) AS avg_transaction_amount`
- Evidence: [curated.monthly_transaction_trend] Sample: as_of 2024-10-01 has daily_transactions=405, daily_amount=362968.14, avg_transaction_amount=896.22

BR-2: The SQL includes a WHERE clause `as_of >= '2024-10-01'` — however, since DataSourcing only loads data for the single effective date (min and max effective dates are set to the same day by the executor), this filter is effectively redundant for dates on or after 2024-10-01.
- Confidence: HIGH
- Evidence: [monthly_transaction_trend.json:22] SQL contains `WHERE as_of >= '2024-10-01'`
- Evidence: [Lib/Control/JobExecutorService.cs:100-101] Both MinDateKey and MaxDateKey set to same effDate
- Evidence: [Lib/Modules/DataSourcing.cs:74-78] Only loads data for that date range

BR-3: The SQL uses a CTE (`base`) but the outer query simply selects all columns from it without further transformation. The CTE structure is functionally equivalent to a direct query.
- Confidence: HIGH
- Evidence: [monthly_transaction_trend.json:22] `WITH base AS (...) SELECT as_of, daily_transactions, daily_amount, avg_transaction_amount FROM base ORDER BY as_of`

BR-4: Results are ordered by as_of (ascending).
- Confidence: HIGH
- Evidence: [monthly_transaction_trend.json:22] `ORDER BY as_of` in the outer query

BR-5: Output is written in Append mode — each daily run adds one row per effective date without truncating prior data.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/monthly_transaction_trend.json:28] `"writeMode": "Append"`
- Evidence: [curated.monthly_transaction_trend] Contains 31 distinct as_of dates (one row per date, all 31 days Oct 1-31)

BR-6: Since DataSourcing loads exactly one day's data per run, the GROUP BY as_of in the SQL produces exactly one output row per run.
- Confidence: HIGH
- Evidence: [Lib/Control/JobExecutorService.cs:100-101] Single date injection
- Evidence: [curated.monthly_transaction_trend] Each as_of has exactly 1 row

BR-7: All transaction types (Credit, Debit) are included in the aggregation — no txn_type filter.
- Confidence: HIGH
- Evidence: [monthly_transaction_trend.json:22] No txn_type filter in SQL
- Evidence: [curated.monthly_transaction_trend] daily_transactions counts match total transactions per as_of in datalake

BR-8: The branches DataFrame is sourced but NOT used in the Transformation SQL.
- Confidence: HIGH
- Evidence: [monthly_transaction_trend.json:13-18] Branches sourced; SQL only references `transactions` table

BR-9: The job has a SameDay dependency on DailyTransactionVolume.
- Confidence: HIGH
- Evidence: [control.job_dependencies] Query shows MonthlyTransactionTrend depends on DailyTransactionVolume with dependency_type = 'SameDay'

BR-10: Transactions data is available for all 31 days of October (including weekends), so this job produces output for every day.
- Confidence: HIGH
- Evidence: [datalake.transactions] 31 distinct as_of dates from 2024-10-01 to 2024-10-31
- Evidence: [curated.monthly_transaction_trend] 31 rows, one per day

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| as_of | transactions.as_of | GROUP BY key | [monthly_transaction_trend.json:22] |
| daily_transactions | COUNT(*) of transactions | Integer count | [monthly_transaction_trend.json:22] |
| daily_amount | SUM(amount) | Rounded to 2 decimal places | [monthly_transaction_trend.json:22] |
| avg_transaction_amount | AVG(amount) | Rounded to 2 decimal places | [monthly_transaction_trend.json:22] |

## Edge Cases
- **NULL handling**: No explicit NULL handling. If amount is NULL, SUM/AVG would ignore it per SQL semantics. COUNT(*) counts all rows regardless.
- **Weekend/date fallback**: Transactions have data on weekends (31 days), so no empty-day issue. The job produces output for every calendar day.
- **Zero-row behavior**: If no transactions exist for an effective date, the GROUP BY would produce zero rows, and no data would be appended.
- **Duplicate prevention**: Append mode means re-running the same effective date would produce duplicate rows. The framework's gap-fill logic prevents this under normal operation.
- **ROUND behavior**: SQLite's ROUND function is used (since Transformation runs in SQLite). SQLite ROUND uses banker's rounding (round half to even) which may differ from PostgreSQL's ROUND behavior in edge cases.

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [monthly_transaction_trend.json:22], [curated data verification] |
| BR-2 | [monthly_transaction_trend.json:22], [JobExecutorService.cs:100-101], [DataSourcing.cs:74-78] |
| BR-3 | [monthly_transaction_trend.json:22] |
| BR-4 | [monthly_transaction_trend.json:22] |
| BR-5 | [monthly_transaction_trend.json:28], [curated data observation] |
| BR-6 | [JobExecutorService.cs:100-101], [curated data observation] |
| BR-7 | [monthly_transaction_trend.json:22], [curated data verification] |
| BR-8 | [monthly_transaction_trend.json:13-18], [SQL analysis] |
| BR-9 | [control.job_dependencies query] |
| BR-10 | [datalake.transactions date analysis], [curated data observation] |

## Open Questions
- The branches table is sourced but unused. Confidence: MEDIUM that this is an oversight.
- The name "MonthlyTransactionTrend" suggests monthly aggregation but the job actually produces daily aggregates that accumulate over the month. The "monthly" aspect is that the Append-mode table accumulates a month's worth of daily data. Confidence: HIGH that this is the intended behavior based on code and output evidence.
- SQLite ROUND vs PostgreSQL ROUND: If the V2 implementation uses different SQL engine, rounding edge cases on .5 values could produce discrepancies. Confidence: MEDIUM that this could be an issue.
