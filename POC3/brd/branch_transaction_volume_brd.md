# BranchTransactionVolume — Business Requirements Document

## Overview
Produces a per-account, per-date transaction volume summary by joining transactions to accounts and aggregating transaction counts and amounts. Despite the job name suggesting branch-level data, the output is at the account/customer level with no branch dimension.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `branch_txn_vol`
- **outputDirectory**: `Output/curated/branch_transaction_volume/`
- **numParts**: 1
- **writeMode**: Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.transactions | transaction_id, account_id, txn_type, amount, description | Effective date range (injected by executor) | [branch_transaction_volume.json:8-10] |
| datalake.accounts | account_id, customer_id, interest_rate | Effective date range (injected by executor) | [branch_transaction_volume.json:14-16] |
| datalake.branches | branch_id, branch_name, city, state_province | Effective date range (injected by executor) | [branch_transaction_volume.json:20-22] |
| datalake.customers | id, first_name, last_name, prefix | Effective date range (injected by executor) | [branch_transaction_volume.json:26-28] |

### Schema Details

**transactions**: transaction_id (integer), account_id (integer), txn_timestamp (timestamp), txn_type (varchar), amount (numeric), description (varchar), as_of (date)

**accounts**: account_id (integer), customer_id (integer), account_type (varchar), account_status (varchar), open_date (date), current_balance (numeric), interest_rate (numeric), credit_limit (numeric), apr (numeric), as_of (date)

## Business Rules

BR-1: Transactions are joined to accounts on both account_id AND as_of date, ensuring date-aligned snapshots.
- Confidence: HIGH
- Evidence: [branch_transaction_volume.json:36] `JOIN accounts a ON t.account_id = a.account_id AND t.as_of = a.as_of`

BR-2: Output is grouped by account_id, customer_id, and as_of — producing one row per account per date.
- Confidence: HIGH
- Evidence: [branch_transaction_volume.json:36] `GROUP BY t.account_id, a.customer_id, t.as_of`

BR-3: Transaction amounts are rounded to 2 decimal places in the total.
- Confidence: HIGH
- Evidence: [branch_transaction_volume.json:36] `ROUND(SUM(t.amount), 2)`

BR-4: Output is ordered by as_of then account_id.
- Confidence: HIGH
- Evidence: [branch_transaction_volume.json:36] `ORDER BY t.as_of, t.account_id`

BR-5: The `branches` table is sourced but never referenced in the transformation SQL.
- Confidence: HIGH
- Evidence: [branch_transaction_volume.json:20-22] DataSourcing; [branch_transaction_volume.json:36] SQL does not mention branches

BR-6: The `customers` table is sourced but never referenced in the transformation SQL.
- Confidence: HIGH
- Evidence: [branch_transaction_volume.json:26-28] DataSourcing; [branch_transaction_volume.json:36] SQL does not mention customers

BR-7: The `description` column from transactions and `interest_rate` from accounts are sourced but not used.
- Confidence: HIGH
- Evidence: [branch_transaction_volume.json:10, 16] sourced columns; [branch_transaction_volume.json:36] not in SQL

BR-8: All transaction types (Debit and Credit) are included — no type filtering.
- Confidence: HIGH
- Evidence: [branch_transaction_volume.json:36] no WHERE clause on txn_type

BR-9: `txn_count` counts all transactions (COUNT(*)), not COUNT of a specific column.
- Confidence: HIGH
- Evidence: [branch_transaction_volume.json:36] `COUNT(*) AS txn_count`

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| account_id | transactions.account_id | Direct, grouped | [branch_transaction_volume.json:36] |
| customer_id | accounts.customer_id | Direct via JOIN | [branch_transaction_volume.json:36] |
| txn_count | transactions | COUNT(*) per group | [branch_transaction_volume.json:36] |
| total_amount | transactions.amount | ROUND(SUM(amount), 2) per group | [branch_transaction_volume.json:36] |
| as_of | transactions.as_of | Passthrough | [branch_transaction_volume.json:36] |

## Non-Deterministic Fields
None identified.

## Write Mode Implications
Overwrite mode: Each execution replaces the entire `Output/curated/branch_transaction_volume/` directory. Multi-day ranges produce multiple rows per account (one per as_of date), all written in a single pass.

## Edge Cases

- **Accounts with no transactions**: Inner join means accounts without any transactions on a given date do not appear in output.
- **Transactions with no matching account**: Inner join filters out transactions whose account_id does not match in the accounts table for that as_of date.
- **Single part file**: numParts=1 means all output goes to part-00000.parquet.
- **Naming mismatch**: Job name implies branch-level data but output has no branch dimension.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Date-aligned join | [branch_transaction_volume.json:36] |
| BR-2: Group by account+date | [branch_transaction_volume.json:36] |
| BR-3: ROUND to 2 decimals | [branch_transaction_volume.json:36] |
| BR-4: ORDER BY as_of, account_id | [branch_transaction_volume.json:36] |
| BR-5: branches unused | [branch_transaction_volume.json:20-22, 36] |
| BR-6: customers unused | [branch_transaction_volume.json:26-28, 36] |
| BR-7: description/interest_rate unused | [branch_transaction_volume.json:10, 16, 36] |
| BR-8: All txn types included | [branch_transaction_volume.json:36] |
| BR-9: COUNT(*) | [branch_transaction_volume.json:36] |

## Open Questions

OQ-1: Why is this job named "BranchTransactionVolume" when the output contains no branch data? The branches and customers tables are sourced but unused. Possible design drift or placeholder for future enrichment.
- Confidence: MEDIUM — clear mismatch between name and implementation
