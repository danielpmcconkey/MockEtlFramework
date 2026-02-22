# TransactionCategorySummary -- Business Requirements Document

## Overview

This job computes daily transaction statistics grouped by transaction type (Debit/Credit), producing total amount, transaction count, and average amount per category per date. The output is written in Append mode, accumulating summary rows across all processed dates.

## Source Tables

### datalake.transactions
- **Columns used**: `txn_type`, `as_of`, `amount`, `transaction_id` (referenced in window function but has no effect on output)
- **Column sourced but unused**: `account_id` (see AP-4)
- **Filter**: None (all transactions included)
- **Evidence**: [JobExecutor/Jobs/transaction_category_summary.json:22] SQL references transactions table

### datalake.segments (UNUSED)
- **Columns sourced**: `segment_id`, `segment_name`, `segment_code`
- **Usage**: NONE -- the Transformation SQL does not reference the `segments` table at all
- **Evidence**: [JobExecutor/Jobs/transaction_category_summary.json:22] SQL only references `txn_stats` CTE derived from `transactions`
- See AP-1.

## Business Rules

BR-1: Transactions are grouped by txn_type and as_of to produce one summary row per transaction category per date.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/transaction_category_summary.json:22] `GROUP BY txn_type, as_of`
- Evidence: [curated.transaction_category_summary] Each as_of has exactly 2 rows (one Credit, one Debit)

BR-2: For each group, compute total_amount (SUM of amount), transaction_count (COUNT), and avg_amount (AVG of amount), all rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/transaction_category_summary.json:22] `ROUND(SUM(amount), 2) AS total_amount, COUNT(*) AS transaction_count, ROUND(AVG(amount), 2) AS avg_amount`
- Evidence: Verified for as_of = 2024-10-02: Direct query `SELECT txn_type, COUNT(*), ROUND(SUM(amount), 2), ROUND(AVG(amount), 2) FROM datalake.transactions WHERE as_of = '2024-10-02' GROUP BY txn_type` yields Credit: 146, 131704.00, 902.08 and Debit: 263, 241392.00, 917.84 -- matches curated output exactly.

BR-3: All transaction types are included -- no filtering on txn_type.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/transaction_category_summary.json:22] No WHERE clause filtering transactions
- Evidence: [curated.transaction_category_summary] Contains both Credit and Debit rows

BR-4: Results are ordered by as_of, then by txn_type.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/transaction_category_summary.json:22] `ORDER BY as_of, txn_type`

BR-5: The output uses Append mode.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/transaction_category_summary.json:28] `"writeMode": "Append"`
- Evidence: [curated.transaction_category_summary] Contains 62 rows (2 per day * 31 days)

## Output Schema

| Column | Source | Transformation |
|--------|--------|----------------|
| txn_type | datalake.transactions.txn_type | GROUP BY key |
| as_of | datalake.transactions.as_of (via DataSourcing injection) | GROUP BY key |
| total_amount | Derived: SUM(amount) | ROUND to 2 decimal places |
| transaction_count | Derived: COUNT(*) | Integer count |
| avg_amount | Derived: AVG(amount) | ROUND to 2 decimal places |

## Edge Cases

- **Zero transactions for a type on a date**: Would produce no row for that type/date combination. In practice, both Debit and Credit transactions exist for all dates.
- **Append mode**: Rows accumulate. Re-running a date would produce duplicates.
- **Rounding**: SUM and AVG are rounded to 2 decimal places in the SQL.

## Anti-Patterns Identified

- **AP-1: Redundant Data Sourcing** -- The `segments` DataSourcing module fetches `segment_id`, `segment_name`, `segment_code` from `datalake.segments`, but the Transformation SQL never references the segments table. The segments data is completely unused. V2 approach: Remove the segments DataSourcing module.

- **AP-4: Unused Columns Sourced** -- The transactions DataSourcing includes `account_id`, which is never referenced in the Transformation SQL. V2 approach: Remove `account_id` from the transactions DataSourcing columns. Note: `transaction_id` is referenced in the SQL (in the ROW_NUMBER window function), but its contribution is discarded by the outer GROUP BY -- see AP-8.

- **AP-8: Overly Complex SQL** -- The SQL uses a CTE (`WITH txn_stats AS (...)`) that computes a ROW_NUMBER window function (`ROW_NUMBER() OVER (PARTITION BY txn_type, as_of ORDER BY transaction_id) AS rn`) and a COUNT window function (`COUNT(*) OVER (PARTITION BY txn_type, as_of) AS type_count`). Neither `rn` nor `type_count` is used in the outer query. The outer query simply does `GROUP BY txn_type, as_of` with SUM, COUNT, AVG -- which could be done directly on the base `transactions` table without the CTE. The CTE and its window functions add unnecessary complexity and have no effect on the result. V2 approach: Simplify to `SELECT txn_type, as_of, ROUND(SUM(amount), 2) AS total_amount, COUNT(*) AS transaction_count, ROUND(AVG(amount), 2) AS avg_amount FROM transactions GROUP BY txn_type, as_of ORDER BY as_of, txn_type`.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [JobExecutor/Jobs/transaction_category_summary.json:22] `GROUP BY txn_type, as_of` |
| BR-2 | [JobExecutor/Jobs/transaction_category_summary.json:22] SUM, COUNT, AVG with ROUND |
| BR-3 | [JobExecutor/Jobs/transaction_category_summary.json:22] no WHERE clause on txn_type |
| BR-4 | [JobExecutor/Jobs/transaction_category_summary.json:22] `ORDER BY as_of, txn_type` |
| BR-5 | [JobExecutor/Jobs/transaction_category_summary.json:28] `"writeMode": "Append"` |

## Open Questions

- None. All business rules are directly observable with HIGH confidence.
