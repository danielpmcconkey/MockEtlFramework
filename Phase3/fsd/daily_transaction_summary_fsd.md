# FSD: DailyTransactionSummaryV2

## Overview
Replaces the original DailyTransactionSummary job with a V2 implementation that writes to the `double_secret_curated` schema instead of `curated`. Since the original uses a Transformation (SQL) step, the V2 retains the same SQL and adds a V2 External writer module that reads the transformation result and writes it to `double_secret_curated.daily_transaction_summary` via DscWriterUtil.

## Design Decisions
- **Pattern B (Transformation + V2 Writer)**: The original job uses DataSourcing x2 -> Transformation -> DataFrameWriter. The V2 retains the same DataSourcing and Transformation steps, then replaces DataFrameWriter with a V2 External writer module.
- The V2 writer reads the transformation result DataFrame from shared state (key "daily_txn_summary") and writes it to double_secret_curated.
- Write mode is Append (matching original), so DscWriterUtil.Write is called with overwrite=false.
- The branches DataSourcing step is retained (matching original) even though it is unused by the SQL.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=transactions, columns=[transaction_id, account_id, txn_timestamp, txn_type, amount, description], resultName=transactions |
| 2 | DataSourcing | schema=datalake, table=branches, columns=[branch_id, branch_name], resultName=branches (unused but retained) |
| 3 | Transformation | Same SQL as original: subquery pattern aggregating per account_id/as_of with CASE-based debit/credit/total, COUNT(*), ORDER BY as_of, account_id; resultName=daily_txn_summary |
| 4 | External | DailyTransactionSummaryV2Writer -- reads daily_txn_summary, writes to dsc |

## V2 External Module: DailyTransactionSummaryV2Writer
- File: ExternalModules/DailyTransactionSummaryV2Writer.cs
- Processing logic: Reads the "daily_txn_summary" DataFrame from shared state (result of the Transformation step), writes it to double_secret_curated via DscWriterUtil with overwrite=false (Append mode), then puts it in sharedState["output"].
- Output columns: account_id, as_of, total_amount, transaction_count, debit_total, credit_total (as produced by the Transformation SQL)
- Target table: double_secret_curated.daily_transaction_summary
- Write mode: Append (overwrite=false)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | Same Transformation SQL with GROUP BY account_id, as_of |
| BR-2 | Same CASE-based total_amount computation (SUM debit + SUM credit) |
| BR-3 | Same COUNT(*) for transaction_count |
| BR-4 | Same CASE-based debit_total with ROUND(2) |
| BR-5 | Same CASE-based credit_total with ROUND(2) |
| BR-6 | Same ROUND(2) on total_amount |
| BR-7 | Same ORDER BY as_of, account_id |
| BR-8 | Same subquery pattern preserved |
| BR-9 | DscWriterUtil.Write called with overwrite=false (Append) |
| BR-10 | branches DataSourcing retained in config but unused |
| BR-11 | txn_timestamp and description sourced but unused (matching original) |
| BR-12 | Same Transformation module type (pure SQL) |
| BR-13 | Transactions exist for all 31 days, producing 31 days of output |
| BR-14 | CASE ELSE 0 behavior preserved for non-Debit/Credit types |
