# FSD: DailyTransactionVolumeV2

## Overview
Replaces the original DailyTransactionVolume job with a V2 implementation that writes to the `double_secret_curated` schema instead of `curated`. Since the original uses a Transformation (SQL) step, the V2 retains the same SQL and adds a V2 External writer module that reads the transformation result and writes it to `double_secret_curated.daily_transaction_volume` via DscWriterUtil.

## Design Decisions
- **Pattern B (Transformation + V2 Writer)**: The original job uses DataSourcing -> Transformation -> DataFrameWriter. The V2 retains the same DataSourcing and Transformation steps, then replaces DataFrameWriter with a V2 External writer module.
- The V2 writer reads the transformation result DataFrame from shared state (key "daily_vol") and writes it to double_secret_curated.
- Write mode is Append (matching original), so DscWriterUtil.Write is called with overwrite=false.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=transactions, columns=[transaction_id, account_id, txn_type, amount], resultName=transactions |
| 2 | Transformation | Same SQL as original: CTE computing COUNT(*), SUM, AVG, MIN, MAX per as_of; outer SELECT outputs as_of, total_transactions, total_amount, avg_amount; ORDER BY as_of; resultName=daily_vol |
| 3 | External | DailyTransactionVolumeV2Writer -- reads daily_vol, writes to dsc |

## V2 External Module: DailyTransactionVolumeV2Writer
- File: ExternalModules/DailyTransactionVolumeV2Writer.cs
- Processing logic: Reads the "daily_vol" DataFrame from shared state (result of the Transformation step), writes it to double_secret_curated via DscWriterUtil with overwrite=false (Append mode), then puts it in sharedState["output"].
- Output columns: as_of, total_transactions, total_amount, avg_amount (as produced by the Transformation SQL)
- Target table: double_secret_curated.daily_transaction_volume
- Write mode: Append (overwrite=false)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | Same Transformation SQL with GROUP BY as_of (one row per date) |
| BR-2 | Same COUNT(*) for total_transactions |
| BR-3 | Same ROUND(SUM(amount), 2) for total_amount |
| BR-4 | Same ROUND(AVG(amount), 2) for avg_amount |
| BR-5 | Same CTE computes MIN/MAX but outer SELECT excludes them |
| BR-6 | Same ORDER BY as_of |
| BR-7 | DscWriterUtil.Write called with overwrite=false (Append) |
| BR-8 | Same CTE-based SQL pattern |
| BR-9 | V2 job config should have SameDay dependency on DailyTransactionSummaryV2 (operational ordering) |
| BR-10 | Transactions for all 31 days produce 31 output rows |
| BR-11 | Same columns sourced from transactions |
| BR-12 | Same SUM/AVG on raw amount field (no CASE filtering by txn_type) |
