# FSD: CustomerCreditSummaryV2

## Overview
Replaces the original CustomerCreditSummary job with a V2 implementation that writes to the `double_secret_curated` schema instead of `curated`. The V2 External module replicates the exact same business logic as the original CustomerCreditSummaryBuilder and then writes directly to `double_secret_curated.customer_credit_summary` via DscWriterUtil.

## Design Decisions
- **Pattern A (External module replacement)**: The original job uses DataSourcing x5 -> External (CustomerCreditSummaryBuilder) -> DataFrameWriter. The V2 replaces both the External and DataFrameWriter steps with a single V2 External module that combines the processing logic and writing.
- The V2 module replicates the original's aggregation logic: grouping credit scores by customer for averaging, grouping loan balances and account balances by customer for summing, then producing one output row per customer.
- Write mode is Overwrite (matching original), so DscWriterUtil.Write is called with overwrite=true.
- The segments DataSourcing step is retained in the config (matching the original) even though it is unused by the External module, to ensure identical shared state.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=customers, columns=[id, first_name, last_name], resultName=customers |
| 2 | DataSourcing | schema=datalake, table=accounts, columns=[account_id, customer_id, account_type, account_status, current_balance], resultName=accounts |
| 3 | DataSourcing | schema=datalake, table=credit_scores, columns=[credit_score_id, customer_id, bureau, score], resultName=credit_scores |
| 4 | DataSourcing | schema=datalake, table=loan_accounts, columns=[loan_id, customer_id, loan_type, current_balance], resultName=loan_accounts |
| 5 | DataSourcing | schema=datalake, table=segments, columns=[segment_id, segment_name], resultName=segments (unused but retained) |
| 6 | External | CustomerCreditSummaryV2Processor -- replicates original logic + writes to dsc |

## V2 External Module: CustomerCreditSummaryV2Processor
- File: ExternalModules/CustomerCreditSummaryV2Processor.cs
- Processing logic: Reads customers, accounts, credit_scores, loan_accounts from shared state. Groups credit scores by customer_id and computes average. Groups loans by customer_id and sums balances/counts. Groups accounts by customer_id and sums balances/counts. Iterates all customers and produces one row per customer with aggregated values. Writes to double_secret_curated via DscWriterUtil with overwrite=true (Overwrite mode).
- Output columns: customer_id, first_name, last_name, avg_credit_score, total_loan_balance, total_account_balance, loan_count, account_count, as_of
- Target table: double_secret_curated.customer_credit_summary
- Write mode: Overwrite (overwrite=true)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | V2 processor iterates all customer rows, producing one output row per customer |
| BR-2 | V2 processor groups credit_scores by customer_id and calls Average() |
| BR-3 | V2 processor sets avg_credit_score to DBNull.Value when no scores exist |
| BR-4 | V2 processor groups loan_accounts by customer_id and sums current_balance |
| BR-5 | V2 processor defaults totalLoanBalance=0, loanCount=0 when customer has no loans |
| BR-6 | V2 processor groups accounts by customer_id and sums current_balance |
| BR-7 | V2 processor defaults totalAccountBalance=0, accountCount=0 when customer has no accounts |
| BR-8 | segments DataSourcing retained in config but not read by V2 processor |
| BR-9 | V2 processor has guard clause returning empty DataFrame when any required input is null/empty |
| BR-10 | DscWriterUtil.Write called with overwrite=true (Overwrite) |
| BR-11 | V2 processor carries as_of from customers row |
| BR-12 | V2 processor does not filter account balances by sign |
| BR-13 | V2 processor iterates all account rows without filtering by type or status |
