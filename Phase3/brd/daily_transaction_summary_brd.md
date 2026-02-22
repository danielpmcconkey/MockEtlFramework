# BRD: DailyTransactionSummary

## Overview
This job produces a per-account daily transaction summary that aggregates transaction amounts (total, debit, credit) and counts by account_id for each effective date. It writes to `curated.daily_transaction_summary` using Append mode, accumulating daily snapshots. Note: the branches table is sourced but not used in the transformation.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| transactions | datalake | transaction_id, account_id, txn_timestamp, txn_type, amount, description | Primary source; grouped by account_id and as_of. txn_timestamp and description are sourced but not used in the SQL. | [JobExecutor/Jobs/daily_transaction_summary.json:7-11] |
| branches | datalake | branch_id, branch_name | Sourced into shared state but NOT used in the transformation SQL | [JobExecutor/Jobs/daily_transaction_summary.json:13-16] |

## Business Rules
BR-1: Output is grouped by account_id and as_of, producing one row per account per date.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json:21] SQL: `GROUP BY t.account_id, t.as_of`

BR-2: total_amount = SUM of debit amounts + SUM of credit amounts (i.e., sum of all amounts regardless of type).
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json:21] `ROUND(SUM(CASE WHEN t.txn_type = 'Debit' THEN t.amount ELSE 0 END) + SUM(CASE WHEN t.txn_type = 'Credit' THEN t.amount ELSE 0 END), 2) AS total_amount`
- Note: This is equivalent to SUM(amount) for rows where txn_type is either 'Debit' or 'Credit'. Rows with other txn_types would contribute 0 to total_amount.

BR-3: transaction_count = COUNT(*) — counts all transaction rows per account per date.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json:21] `COUNT(*) AS transaction_count`

BR-4: debit_total = SUM of amounts where txn_type = 'Debit', rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json:21] `ROUND(SUM(CASE WHEN t.txn_type = 'Debit' THEN t.amount ELSE 0 END), 2) AS debit_total`

BR-5: credit_total = SUM of amounts where txn_type = 'Credit', rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json:21] `ROUND(SUM(CASE WHEN t.txn_type = 'Credit' THEN t.amount ELSE 0 END), 2) AS credit_total`

BR-6: total_amount is also rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json:21] `ROUND(..., 2) AS total_amount`

BR-7: Results are ordered by as_of, then account_id.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json:21] `ORDER BY sub.as_of, sub.account_id`

BR-8: The SQL uses a subquery pattern: inner query computes aggregates, outer query selects the results.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json:21] `SELECT sub.account_id, sub.as_of, ... FROM (SELECT ...) sub ORDER BY ...`

BR-9: The output is written using Append mode, accumulating daily records.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json:28] `"writeMode": "Append"`
- Evidence: [curated.daily_transaction_summary] Contains 31 distinct as_of dates (all days of October including weekends)

BR-10: The branches table is sourced but not used (dead data sourcing).
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json:13-16] DataSourcing for branches
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json:21] SQL only references `transactions t`, not `branches`

BR-11: txn_timestamp and description columns are sourced from the transactions table but not used in the transformation SQL.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json:10] columns include `txn_timestamp`, `description`
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json:21] SQL does not reference these columns

BR-12: This is a pure SQL Transformation job — no External module is used.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json] Module pipeline: DataSourcing x2 -> Transformation -> DataFrameWriter

BR-13: Transactions exist for all 31 days of October (including weekends), so the output has 31 dates.
- Confidence: HIGH
- Evidence: [datalake.transactions] 31 distinct as_of dates
- Evidence: [curated.daily_transaction_summary] 31 distinct as_of dates

BR-14: For non-Debit/non-Credit transaction types, the CASE expressions contribute 0 to both debit_total and credit_total, but they are still counted in transaction_count (COUNT(*)).
- Confidence: MEDIUM
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json:21] CASE ELSE 0 for both debit and credit; COUNT(*) counts all rows
- Evidence: Need to verify whether any non-Debit/non-Credit txn_types exist in the data

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| account_id | transactions.account_id | Group key | [JobExecutor/Jobs/daily_transaction_summary.json:21] |
| as_of | transactions.as_of | Group key | [JobExecutor/Jobs/daily_transaction_summary.json:21] |
| total_amount | transactions.amount | SUM(debit amounts) + SUM(credit amounts), ROUND 2 | [JobExecutor/Jobs/daily_transaction_summary.json:21] |
| transaction_count | transactions | COUNT(*) per group | [JobExecutor/Jobs/daily_transaction_summary.json:21] |
| debit_total | transactions.amount (where txn_type='Debit') | SUM with CASE, ROUND 2 | [JobExecutor/Jobs/daily_transaction_summary.json:21] |
| credit_total | transactions.amount (where txn_type='Credit') | SUM with CASE, ROUND 2 | [JobExecutor/Jobs/daily_transaction_summary.json:21] |

## Edge Cases
- **Non-standard txn_types**: Any txn_type other than 'Debit' or 'Credit' would contribute 0 to total_amount (since total is debit+credit sums, not SUM(amount)), but would be counted in transaction_count. This means transaction_count could be higher than debit+credit implied counts.
- **Zero transactions for an account on a date**: Such accounts would not appear in the output (no row generated by GROUP BY).
- **Weekend data**: Transactions exist 7 days/week, so the job produces output every day.
- **ROUND precision**: All monetary amounts are rounded to 2 decimal places.
- **Unused branches**: The branches table is loaded but never referenced in the SQL.
- **Unused columns**: txn_timestamp and description are fetched from PostgreSQL but never used in the transformation.
- **Append accumulation**: Running the job for the same date twice would produce duplicate rows. The executor's gap-fill mechanism prevents this.

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [JobExecutor/Jobs/daily_transaction_summary.json:21] |
| BR-2 | [JobExecutor/Jobs/daily_transaction_summary.json:21] |
| BR-3 | [JobExecutor/Jobs/daily_transaction_summary.json:21] |
| BR-4 | [JobExecutor/Jobs/daily_transaction_summary.json:21] |
| BR-5 | [JobExecutor/Jobs/daily_transaction_summary.json:21] |
| BR-6 | [JobExecutor/Jobs/daily_transaction_summary.json:21] |
| BR-7 | [JobExecutor/Jobs/daily_transaction_summary.json:21] |
| BR-8 | [JobExecutor/Jobs/daily_transaction_summary.json:21] |
| BR-9 | [JobExecutor/Jobs/daily_transaction_summary.json:28], [curated.daily_transaction_summary dates] |
| BR-10 | [JobExecutor/Jobs/daily_transaction_summary.json:13-16, 21] |
| BR-11 | [JobExecutor/Jobs/daily_transaction_summary.json:10, 21] |
| BR-12 | [JobExecutor/Jobs/daily_transaction_summary.json] |
| BR-13 | [datalake.transactions dates], [curated.daily_transaction_summary dates] |
| BR-14 | [JobExecutor/Jobs/daily_transaction_summary.json:21] |

## Open Questions
- **total_amount vs SUM(amount)**: The total_amount is computed as SUM(debit) + SUM(credit), not as SUM(amount). If any txn_type other than 'Debit'/'Credit' exists, total_amount would exclude those amounts but transaction_count would include them. This could be a subtle bug or intentional. Confidence: MEDIUM that only Debit/Credit types exist in current data.
- **Downstream dependency**: DailyTransactionVolume has a SameDay dependency on this job. Confidence: HIGH (verified in control.job_dependencies).
