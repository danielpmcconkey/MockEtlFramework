# FSD: TransactionCategorySummaryV2

## Overview
Replaces the original TransactionCategorySummary job with a V2 implementation that writes to the `double_secret_curated` schema instead of `curated`. The V2 uses the same Transformation SQL as the original (including the dead-code CTE window functions) but replaces the DataFrameWriter with a V2Writer External module that writes to `double_secret_curated.transaction_category_summary` via DscWriterUtil.

## Design Decisions
- **Pattern B (Transformation + V2Writer)**: The original job uses DataSourcing (x2) -> Transformation -> DataFrameWriter. The V2 retains the same DataSourcing and Transformation steps, and replaces DataFrameWriter with a thin External writer module.
- The Transformation SQL is kept exactly as-is, including the unused ROW_NUMBER and COUNT window functions in the CTE. This preserves behavioral equivalence since those window functions do not affect the final output.
- Write mode is Append (matching original), so DscWriterUtil.Write is called with overwrite=false.
- The segments DataSourcing step is retained (matching the original) even though the SQL does not reference it.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=transactions, columns=[transaction_id, account_id, txn_type, amount], resultName=transactions |
| 2 | DataSourcing | schema=datalake, table=segments, columns=[segment_id, segment_name, segment_code], resultName=segments (unused by SQL) |
| 3 | Transformation | SQL: CTE with window functions (dead code), outer query aggregates by txn_type and as_of; resultName=txn_cat_summary |
| 4 | External | TransactionCategorySummaryV2Writer -- reads txn_cat_summary DataFrame, writes to dsc |

## V2 External Module: TransactionCategorySummaryV2Writer
- File: ExternalModules/TransactionCategorySummaryV2Writer.cs
- Processing logic: Reads "txn_cat_summary" DataFrame from shared state (result of Transformation). Writes to double_secret_curated via DscWriterUtil with overwrite=false (Append). Puts result in sharedState["output"].
- Output columns: txn_type, as_of, total_amount, transaction_count, avg_amount
- Target table: double_secret_curated.transaction_category_summary
- Write mode: Append (overwrite=false)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | Transformation SQL computes SUM(amount), COUNT(*), AVG(amount) grouped by txn_type, as_of with ROUND to 2 |
| BR-2 | CTE with ROW_NUMBER and COUNT window functions preserved exactly (dead code, does not affect output) |
| BR-3 | ORDER BY as_of, txn_type preserved in SQL |
| BR-4 | DscWriterUtil.Write with overwrite=false (Append) |
| BR-5 | Single date per run produces 2 rows (Credit, Debit) |
| BR-6 | No amount or txn_type filter in SQL |
| BR-7 | segments sourced but not referenced in SQL |
| BR-8 | Transactions available for all 31 days |
| BR-9 | ROUND applied to SUM and AVG |
