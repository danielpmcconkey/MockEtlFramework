# FSD: CustomerValueScoreV2

## Overview
Replaces the original CustomerValueScore job with a V2 implementation that writes to the `double_secret_curated` schema instead of `curated`. The V2 External module replicates the exact same business logic as the original CustomerValueCalculator and then writes directly to `double_secret_curated.customer_value_score` via DscWriterUtil.

## Design Decisions
- **Pattern A (External module replacement)**: The original job uses DataSourcing x4 -> External (CustomerValueCalculator) -> DataFrameWriter. The V2 replaces both the External and DataFrameWriter steps with a single V2 External module that combines the processing logic and writing.
- The V2 module replicates the original's three-factor scoring system: transaction_score (count * 10, cap 1000), balance_score (balance / 1000, cap 1000), visit_score (count * 50, cap 1000), with composite = weighted sum (0.4, 0.35, 0.25).
- Write mode is Overwrite (matching original), so DscWriterUtil.Write is called with overwrite=true.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=customers, columns=[id, first_name, last_name], resultName=customers |
| 2 | DataSourcing | schema=datalake, table=transactions, columns=[transaction_id, account_id, txn_type, amount], resultName=transactions |
| 3 | DataSourcing | schema=datalake, table=accounts, columns=[account_id, customer_id, current_balance], resultName=accounts |
| 4 | DataSourcing | schema=datalake, table=branch_visits, columns=[visit_id, customer_id, branch_id], resultName=branch_visits |
| 5 | External | CustomerValueScoreV2Processor -- replicates original logic + writes to dsc |

## V2 External Module: CustomerValueScoreV2Processor
- File: ExternalModules/CustomerValueScoreV2Processor.cs
- Processing logic: Reads customers, transactions, accounts, branch_visits from shared state. Builds account-to-customer lookup. Aggregates transaction counts per customer via account lookup. Aggregates account balances per customer. Aggregates branch visit counts per customer. Computes three sub-scores (capped at 1000), then composite weighted score. All scores rounded to 2 decimal places. Writes to double_secret_curated via DscWriterUtil with overwrite=true.
- Output columns: customer_id, first_name, last_name, transaction_score, balance_score, visit_score, composite_score, as_of
- Target table: double_secret_curated.customer_value_score
- Write mode: Overwrite (overwrite=true)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | V2 processor iterates all customer rows, producing one output row per customer |
| BR-2 | V2 processor has guard clause returning empty DataFrame when customers or accounts is null/empty |
| BR-3 | V2 processor: transactionScore = Math.Min(txnCount * 10.0m, 1000m) |
| BR-4 | V2 processor: balanceScore = Math.Min(totalBalance / 1000.0m, 1000m) |
| BR-5 | V2 processor: visitScore = Math.Min(visitCount * 50.0m, 1000m) |
| BR-6 | V2 processor: compositeScore = txnScore*0.4 + balScore*0.35 + visitScore*0.25 |
| BR-7 | V2 processor applies Math.Round(..., 2) to all four scores |
| BR-8 | V2 processor builds accountToCustomer lookup, counts transactions per customer |
| BR-9 | V2 processor skips transactions with customerId == 0 (unmatched account_id) |
| BR-10 | V2 processor does not apply floor to balance_score (can be negative) |
| BR-11 | V2 processor defaults txnCount to 0 for customers with no transactions |
| BR-12 | V2 processor defaults visitCount to 0 for customers with no visits |
| BR-13 | DscWriterUtil.Write called with overwrite=true (Overwrite) |
| BR-14 | V2 processor carries as_of from customers row |
