# DailyTransactionVolume — Business Requirements Document

## Overview

The DailyTransactionVolume job produces a single aggregate row per effective date summarizing total transaction count, total amount, and average amount across all transactions. Output uses Append mode, accumulating daily snapshots.

## Source Tables

| Table | Alias in Config | Columns Sourced | Purpose |
|-------|----------------|-----------------|---------|
| `datalake.transactions` | transactions | transaction_id, account_id, txn_type, amount | Transaction records to aggregate per date |

- Join logic: No joins — the SQL operates solely on the `transactions` table.
- Evidence: [JobExecutor/Jobs/daily_transaction_volume.json:15] SQL references only `transactions`.

## Business Rules

BR-1: One output row is produced per effective date, summarizing all transactions for that date.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_volume.json:15] `GROUP BY as_of`
- Evidence: [curated.daily_transaction_volume] Exactly 1 row per as_of for all 31 dates.

BR-2: total_transactions is the count of all transactions for the date.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_volume.json:15] `COUNT(*) AS total_transactions`

BR-3: total_amount is the sum of all transaction amounts for the date, rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_volume.json:15] `ROUND(SUM(amount), 2) AS total_amount`

BR-4: avg_amount is the average transaction amount for the date (total_amount / total_transactions), rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_volume.json:15] `ROUND(AVG(amount), 2) AS avg_amount`
- Evidence: [curated.daily_transaction_volume] For 2024-10-01: total_amount=362968.14, total_transactions=405, avg_amount=896.22. 362968.14/405 = 896.22 (confirmed).

BR-5: Output uses Append write mode.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/daily_transaction_volume.json:22] `"writeMode": "Append"`
- Evidence: [curated.daily_transaction_volume] Has data for all 31 dates.

BR-6: Weekend dates are included because transactions have data for all 7 days.
- Confidence: HIGH
- Evidence: [curated.daily_transaction_volume] Has rows for all 31 days including weekends.

BR-7: The output values match the aggregation of DailyTransactionSummary's per-account data exactly.
- Confidence: HIGH
- Evidence: [curated.daily_transaction_summary] SUM(total_amount)=362968.14 and SUM(transaction_count)=405 for 2024-10-01, matching daily_transaction_volume output exactly.

BR-8: DailyTransactionVolume has a declared SameDay dependency on DailyTransactionSummary.
- Confidence: HIGH
- Evidence: [control.job_dependencies] Query shows `DailyTransactionVolume` depends on `DailyTransactionSummary` with type `SameDay`.

## Output Schema

| Column | Type | Source | Transformation |
|--------|------|--------|---------------|
| as_of | date | `transactions.as_of` | GROUP BY key |
| total_transactions | integer | Calculated | COUNT(*) |
| total_amount | numeric(14,2) | Calculated | ROUND(SUM(amount), 2) |
| avg_amount | numeric(14,2) | Calculated | ROUND(AVG(amount), 2) |

## Edge Cases

- **Weekend dates:** Transactions exist on all days, so this job produces output for weekends.
- **Zero transactions:** If no transactions exist for a date, no output row is produced (GROUP BY returns no rows). In practice, this doesn't occur — all dates in the data range have transactions.
- **CTE min/max discarded:** The CTE computes `MIN(amount)` and `MAX(amount)` but the outer SELECT discards them (see AP-8).

## Anti-Patterns Identified

- **AP-2: Duplicated Transformation Logic** — Despite having a declared SameDay dependency on DailyTransactionSummary, this job re-derives the aggregates from raw `datalake.transactions` instead of reading from `curated.daily_transaction_summary`. The dependency exists but is not leveraged for data reuse. The output could be computed as `SELECT as_of, SUM(transaction_count), SUM(total_amount), ROUND(SUM(total_amount)/SUM(transaction_count), 2) FROM curated.daily_transaction_summary GROUP BY as_of`. V2 approach: Read from the curated daily_transaction_summary table and aggregate, or compute directly from datalake.transactions with simplified SQL (eliminating the dependency entirely if not needed for data purposes).

- **AP-4: Unused Columns Sourced** — Columns `transaction_id`, `account_id`, and `txn_type` are sourced from transactions [JobExecutor/Jobs/daily_transaction_volume.json:10] but never used in the SQL. The SQL only references `as_of` and `amount`. V2 approach: Source only `amount` (plus `as_of` which is auto-added by DataSourcing).

- **AP-8: Overly Complex SQL** — The SQL uses an unnecessary CTE (`daily_agg`) that computes `MIN(amount)` and `MAX(amount)` columns, but the outer SELECT only takes `as_of, total_transactions, total_amount, avg_amount` — discarding min and max. The CTE wrapper is unnecessary since the outer query just selects a subset of CTE columns. The entire query could be a single `SELECT as_of, COUNT(*), ROUND(SUM(amount), 2), ROUND(AVG(amount), 2) FROM transactions GROUP BY as_of ORDER BY as_of`. V2 approach: Remove the CTE and unused min/max calculations; use a direct single-level query.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [JobExecutor/Jobs/daily_transaction_volume.json:15], curated 1 row/date |
| BR-2 | [JobExecutor/Jobs/daily_transaction_volume.json:15] COUNT(*) |
| BR-3 | [JobExecutor/Jobs/daily_transaction_volume.json:15] SUM(amount) |
| BR-4 | [JobExecutor/Jobs/daily_transaction_volume.json:15] AVG(amount), curated verification |
| BR-5 | [JobExecutor/Jobs/daily_transaction_volume.json:22], curated 31 dates |
| BR-6 | curated weekend data present |
| BR-7 | Cross-table comparison of curated.daily_transaction_summary vs curated.daily_transaction_volume |
| BR-8 | [control.job_dependencies] SameDay dependency on DailyTransactionSummary |

## Open Questions

- **Dependency design decision:** The job has a declared dependency on DailyTransactionSummary but doesn't read from its output. For V2, the architect should decide whether to (a) leverage the dependency and read from the upstream curated table (fixing AP-2), or (b) remove the dependency and compute independently (simpler, but maintains duplication). The values are provably equivalent, so either approach is correct.
  - Confidence: HIGH — both approaches produce identical output. The decision is architectural.
