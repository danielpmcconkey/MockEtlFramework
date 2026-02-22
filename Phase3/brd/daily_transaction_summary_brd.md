# DailyTransactionSummary — Business Requirements Document

## Overview

The DailyTransactionSummary job aggregates transaction data per account per effective date, computing total amount, transaction count, debit total, and credit total. Output uses Append mode, accumulating daily snapshots across all dates including weekends.

## Source Tables

| Table | Alias in Config | Columns Sourced | Purpose |
|-------|----------------|-----------------|---------|
| `datalake.transactions` | transactions | transaction_id, account_id, txn_timestamp, txn_type, amount, description | Transaction records to aggregate per account |
| `datalake.branches` | branches | branch_id, branch_name | **NOT USED** — sourced but never referenced in the Transformation SQL |

- Join logic: No joins — the SQL Transformation operates solely on the `transactions` table, grouping by account_id and as_of.
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json:22] SQL references only `transactions t`.

## Business Rules

BR-1: One output row is produced per account per effective date, containing aggregate transaction metrics.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json:22] `GROUP BY t.account_id, t.as_of`
- Evidence: [curated.daily_transaction_summary] Row counts vary by date (241 on 2024-10-01, 245 on 2024-10-02), matching accounts with transactions on each date.

BR-2: total_amount is the sum of debit amounts plus credit amounts (i.e., the sum of all transaction amounts regardless of type).
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json:22] `ROUND(SUM(CASE WHEN t.txn_type = 'Debit' THEN t.amount ELSE 0 END) + SUM(CASE WHEN t.txn_type = 'Credit' THEN t.amount ELSE 0 END), 2) AS total_amount`
- Note: This is functionally equivalent to `ROUND(SUM(t.amount), 2)` since all transactions are either Debit or Credit (constraint on datalake.transactions). The original SQL is unnecessarily verbose (see AP-8).

BR-3: transaction_count is the total count of all transactions for the account on that date.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json:22] `COUNT(*) AS transaction_count`

BR-4: debit_total is the sum of amounts for transactions where txn_type = 'Debit', rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json:22] `ROUND(SUM(CASE WHEN t.txn_type = 'Debit' THEN t.amount ELSE 0 END), 2) AS debit_total`

BR-5: credit_total is the sum of amounts for transactions where txn_type = 'Credit', rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json:22] `ROUND(SUM(CASE WHEN t.txn_type = 'Credit' THEN t.amount ELSE 0 END), 2) AS credit_total`

BR-6: Output is ordered by as_of ascending, then account_id ascending.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json:22] `ORDER BY sub.as_of, sub.account_id`

BR-7: Output uses Append write mode.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json:28] `"writeMode": "Append"`
- Evidence: [curated.daily_transaction_summary] Has data for all 31 dates in October.

BR-8: Weekend dates are included because the transactions table has data for all 7 days of the week.
- Confidence: HIGH
- Evidence: [datalake.transactions] Has as_of dates for 2024-10-05 (Sat) and 2024-10-06 (Sun).
- Evidence: [curated.daily_transaction_summary] Has rows for all 31 days including weekends.

BR-9: All amounts are rounded to 2 decimal places using SQL ROUND.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_summary.json:22] ROUND(..., 2) applied to total_amount, debit_total, and credit_total.

## Output Schema

| Column | Type | Source | Transformation |
|--------|------|--------|---------------|
| account_id | integer | `transactions.account_id` | GROUP BY key |
| as_of | date | `transactions.as_of` | GROUP BY key |
| total_amount | numeric(14,2) | Calculated | SUM(debit amounts) + SUM(credit amounts), ROUND to 2 |
| transaction_count | integer | Calculated | COUNT(*) |
| debit_total | numeric(14,2) | Calculated | SUM of amounts WHERE txn_type = 'Debit', ROUND to 2 |
| credit_total | numeric(14,2) | Calculated | SUM of amounts WHERE txn_type = 'Credit', ROUND to 2 |

## Edge Cases

- **Weekend dates:** Transactions have data for all 7 days, so this job produces output on weekends. Row counts vary (e.g., 231 on Sat vs. 241 on weekdays) reflecting varying transaction volumes.
- **Accounts with no transactions:** Accounts that have zero transactions on a given date are simply absent from the output (no zero-count rows).
- **All amounts positive:** The datalake.transactions table has a CHECK constraint `amount > 0`, so both debit_total and credit_total are always >= 0.
- **Subquery wrapping:** The SQL wraps the aggregation in a subquery `sub` then does `SELECT sub.* FROM (...) sub ORDER BY`. This is functionally unnecessary — the ORDER BY could be applied directly — but does not affect results (see AP-8).

## Anti-Patterns Identified

- **AP-1: Redundant Data Sourcing** — The `branches` table is sourced [JobExecutor/Jobs/daily_transaction_summary.json:14-18] with columns `branch_id, branch_name` but is never referenced in the Transformation SQL. The SQL only operates on the `transactions` table. V2 approach: Remove the branches DataSourcing module entirely.

- **AP-4: Unused Columns Sourced** — From the `transactions` table, columns `transaction_id`, `txn_timestamp`, and `description` are sourced [JobExecutor/Jobs/daily_transaction_summary.json:10] but never referenced in the Transformation SQL. The SQL only uses `account_id`, `as_of`, `txn_type`, and `amount`. V2 approach: Source only the columns actually used.

- **AP-8: Overly Complex SQL** — Two unnecessary complexities: (1) The `total_amount` is computed as `SUM(CASE WHEN Debit...) + SUM(CASE WHEN Credit...)` instead of simply `SUM(amount)`, since all transactions are either Debit or Credit (enforced by check constraint). (2) The entire aggregation is wrapped in an unnecessary subquery `sub` just to ORDER BY — the ORDER BY could be applied directly to the GROUP BY query. V2 approach: Simplify to `SUM(amount)` for total_amount and remove the unnecessary subquery wrapper.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [JobExecutor/Jobs/daily_transaction_summary.json:22], curated row counts |
| BR-2 | [JobExecutor/Jobs/daily_transaction_summary.json:22] SUM+SUM expression |
| BR-3 | [JobExecutor/Jobs/daily_transaction_summary.json:22] COUNT(*) |
| BR-4 | [JobExecutor/Jobs/daily_transaction_summary.json:22] debit CASE |
| BR-5 | [JobExecutor/Jobs/daily_transaction_summary.json:22] credit CASE |
| BR-6 | [JobExecutor/Jobs/daily_transaction_summary.json:22] ORDER BY |
| BR-7 | [JobExecutor/Jobs/daily_transaction_summary.json:28], curated 31 dates |
| BR-8 | [datalake.transactions] weekend data, curated weekend data |
| BR-9 | [JobExecutor/Jobs/daily_transaction_summary.json:22] ROUND calls |

## Open Questions

None — this job's logic is fully observable in the SQL Transformation. All business rules are HIGH confidence.
