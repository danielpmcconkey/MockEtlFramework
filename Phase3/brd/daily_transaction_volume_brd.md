# BRD: DailyTransactionVolume

## Overview
This job produces a daily aggregate summary of all transactions across all accounts, computing total transaction count, total amount, and average amount per day. It writes to `curated.daily_transaction_volume` using Append mode. This job has a SameDay dependency on DailyTransactionSummary.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| transactions | datalake | transaction_id, account_id, txn_type, amount | Aggregated by as_of date to produce daily volume metrics | [JobExecutor/Jobs/daily_transaction_volume.json:7-10] |

## Business Rules
BR-1: The output produces one row per as_of date, aggregating all transactions for that date.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_volume.json:14] SQL: `GROUP BY as_of`

BR-2: total_transactions = COUNT(*) — counts all transaction rows per date.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_volume.json:14] `COUNT(*) AS total_transactions`

BR-3: total_amount = SUM(amount), rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_volume.json:14] `ROUND(SUM(amount), 2) AS total_amount`

BR-4: avg_amount = AVG(amount), rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_volume.json:14] `ROUND(AVG(amount), 2) AS avg_amount`

BR-5: The SQL computes MIN(amount) and MAX(amount) in the CTE but these are NOT included in the final SELECT output.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_volume.json:14] CTE includes `MIN(amount) AS min_amount, MAX(amount) AS max_amount` but the outer SELECT only includes `as_of, total_transactions, total_amount, avg_amount`

BR-6: Results are ordered by as_of.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_volume.json:14] `ORDER BY as_of`

BR-7: The output is written using Append mode.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_volume.json:21] `"writeMode": "Append"`
- Evidence: [curated.daily_transaction_volume] 31 rows, one per day

BR-8: This is a pure SQL Transformation job using a CTE (WITH clause).
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_volume.json:14] `WITH daily_agg AS (SELECT ...) SELECT ...`

BR-9: This job has a SameDay dependency on DailyTransactionSummary.
- Confidence: HIGH
- Evidence: [control.job_dependencies] `dependency_id=1, job_id=5 (DailyTransactionVolume), depends_on_job_id=2 (DailyTransactionSummary), dependency_type='SameDay'`
- Note: Despite the dependency, this job reads directly from datalake.transactions, not from the output of DailyTransactionSummary. The dependency may be for scheduling/ordering purposes only.

BR-10: Transactions exist for all 31 days of October, so the output has 31 rows.
- Confidence: HIGH
- Evidence: [curated.daily_transaction_volume] 31 rows
- Evidence: [datalake.transactions] 31 distinct as_of dates

BR-11: Only transaction_id, account_id, txn_type, and amount are sourced. txn_timestamp and description are NOT sourced (unlike DailyTransactionSummary).
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_volume.json:9] columns: `["transaction_id", "account_id", "txn_type", "amount"]`

BR-12: The SUM and AVG are computed on the raw `amount` field — all transaction types are included (no CASE filtering by txn_type).
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_volume.json:14] `SUM(amount)`, `AVG(amount)` — no CASE or WHERE filtering by txn_type
- Note: This contrasts with DailyTransactionSummary which uses CASE-based type filtering

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| as_of | transactions.as_of | Group key | [JobExecutor/Jobs/daily_transaction_volume.json:14] |
| total_transactions | transactions | COUNT(*) per date | [JobExecutor/Jobs/daily_transaction_volume.json:14] |
| total_amount | transactions.amount | SUM(amount), ROUND 2 | [JobExecutor/Jobs/daily_transaction_volume.json:14] |
| avg_amount | transactions.amount | AVG(amount), ROUND 2 | [JobExecutor/Jobs/daily_transaction_volume.json:14] |

## Edge Cases
- **CTE computes unused columns**: MIN and MAX amounts are computed in the CTE but excluded from the final output. This is wasted computation but has no effect on results. [JobExecutor/Jobs/daily_transaction_volume.json:14]
- **Weekend data**: Transactions exist 7 days/week, so the job produces output every day.
- **Zero transactions on a date**: If no transactions exist for a date, no output row is produced. Current data has transactions every day.
- **All txn_types included**: Unlike DailyTransactionSummary, this job does not filter by txn_type — all amounts are summed and averaged equally.
- **Append accumulation**: Running the job for the same date twice would produce duplicate rows.
- **SameDay dependency**: This job depends on DailyTransactionSummary being completed first for the same run_date. However, the job reads from datalake.transactions directly, not from DailyTransactionSummary output. The dependency appears to be for operational ordering, not data flow.

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [JobExecutor/Jobs/daily_transaction_volume.json:14] |
| BR-2 | [JobExecutor/Jobs/daily_transaction_volume.json:14] |
| BR-3 | [JobExecutor/Jobs/daily_transaction_volume.json:14] |
| BR-4 | [JobExecutor/Jobs/daily_transaction_volume.json:14] |
| BR-5 | [JobExecutor/Jobs/daily_transaction_volume.json:14] |
| BR-6 | [JobExecutor/Jobs/daily_transaction_volume.json:14] |
| BR-7 | [JobExecutor/Jobs/daily_transaction_volume.json:21] |
| BR-8 | [JobExecutor/Jobs/daily_transaction_volume.json:14] |
| BR-9 | [control.job_dependencies] |
| BR-10 | [curated.daily_transaction_volume count], [datalake.transactions dates] |
| BR-11 | [JobExecutor/Jobs/daily_transaction_volume.json:9] |
| BR-12 | [JobExecutor/Jobs/daily_transaction_volume.json:14] |

## Open Questions
- **SameDay dependency rationale**: DailyTransactionVolume has a SameDay dependency on DailyTransactionSummary, but does not read DailyTransactionSummary's output. The dependency may be intentional for operational ordering or may be a configuration artifact. Confidence: MEDIUM that the dependency is for scheduling order rather than data flow.
- **Unused CTE columns**: MIN(amount) and MAX(amount) are computed but not output. This could indicate that these columns were previously included or are planned for future use. Confidence: HIGH that they are currently unused.
