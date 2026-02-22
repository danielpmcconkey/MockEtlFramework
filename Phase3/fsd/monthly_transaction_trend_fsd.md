# FSD: MonthlyTransactionTrendV2

## Overview
Replaces the original MonthlyTransactionTrend job with a V2 implementation that writes to the `double_secret_curated` schema instead of `curated`. The V2 uses the same Transformation SQL as the original but replaces the DataFrameWriter with a V2Writer External module that writes to `double_secret_curated.monthly_transaction_trend` via DscWriterUtil.

## Design Decisions
- **Pattern B (Transformation + V2Writer)**: The original job uses DataSourcing (x2) -> Transformation -> DataFrameWriter. The V2 retains the same DataSourcing and Transformation steps, and replaces DataFrameWriter with a thin External writer module.
- The Transformation SQL is kept exactly as-is (same CTE structure, same aggregations, same ordering).
- Write mode is Append (matching original), so DscWriterUtil.Write is called with overwrite=false.
- The branches DataSourcing step is retained (matching the original) even though the SQL does not reference it.
- **SameDay dependency on DailyTransactionVolume**: The V2 job inherits this dependency. The V2 must be registered with the same dependency to ensure correct execution order.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=transactions, columns=[transaction_id, account_id, txn_type, amount], resultName=transactions |
| 2 | DataSourcing | schema=datalake, table=branches, columns=[branch_id, branch_name], resultName=branches (unused by SQL) |
| 3 | Transformation | SQL aggregates transactions by as_of: COUNT, ROUND(SUM, 2), ROUND(AVG, 2); resultName=monthly_trend |
| 4 | External | MonthlyTransactionTrendV2Writer -- reads monthly_trend DataFrame, writes to dsc |

## V2 External Module: MonthlyTransactionTrendV2Writer
- File: ExternalModules/MonthlyTransactionTrendV2Writer.cs
- Processing logic: Reads "monthly_trend" DataFrame from shared state (result of Transformation). Writes to double_secret_curated via DscWriterUtil with overwrite=false (Append). Puts result in sharedState["output"].
- Output columns: as_of, daily_transactions, daily_amount, avg_transaction_amount
- Target table: double_secret_curated.monthly_transaction_trend
- Write mode: Append (overwrite=false)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | Transformation SQL computes COUNT, ROUND(SUM, 2), ROUND(AVG, 2) grouped by as_of |
| BR-2 | WHERE as_of >= '2024-10-01' retained in SQL (redundant but preserved for behavioral equivalence) |
| BR-3 | CTE structure preserved exactly as-is |
| BR-4 | ORDER BY as_of preserved in SQL |
| BR-5 | DscWriterUtil.Write with overwrite=false (Append) |
| BR-6 | Single date per run produces one output row |
| BR-7 | No txn_type filter in SQL |
| BR-8 | branches sourced but not referenced in SQL |
| BR-9 | SameDay dependency on DailyTransactionVolume must be registered |
| BR-10 | Transactions available for all 31 days |
