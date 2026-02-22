# FSD: LoanPortfolioSnapshotV2

## Overview
Replaces the original LoanPortfolioSnapshot job with a V2 implementation that writes to the `double_secret_curated` schema instead of `curated`. The V2 External module replicates the exact same business logic as the original LoanSnapshotBuilder (pass-through of loan_accounts columns, excluding origination_date and maturity_date) and then writes directly to `double_secret_curated.loan_portfolio_snapshot` via DscWriterUtil.

## Design Decisions
- **Pattern A (External module replacement)**: The original job uses DataSourcing (x2) -> External (LoanSnapshotBuilder) -> DataFrameWriter. The V2 replaces both the External and DataFrameWriter steps with a single V2 External module.
- The V2 module is a pass-through: it copies all loan_accounts columns except origination_date and maturity_date.
- Write mode is Overwrite (matching original), so DscWriterUtil.Write is called with overwrite=true.
- The branches DataSourcing step is retained in the config (matching the original) even though it is unused.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=loan_accounts, columns=[loan_id, customer_id, loan_type, original_amount, current_balance, interest_rate, origination_date, maturity_date, loan_status], resultName=loan_accounts |
| 2 | DataSourcing | schema=datalake, table=branches, columns=[branch_id, branch_name], resultName=branches (unused) |
| 3 | External | LoanPortfolioSnapshotV2Processor -- pass-through with column exclusion, writes to dsc |

## V2 External Module: LoanPortfolioSnapshotV2Processor
- File: ExternalModules/LoanPortfolioSnapshotV2Processor.cs
- Processing logic: Reads loan_accounts from shared state. Copies all rows, selecting only loan_id, customer_id, loan_type, original_amount, current_balance, interest_rate, loan_status, as_of (excluding origination_date and maturity_date). Writes to double_secret_curated via DscWriterUtil with overwrite=true.
- Output columns: loan_id, customer_id, loan_type, original_amount, current_balance, interest_rate, loan_status, as_of
- Target table: double_secret_curated.loan_portfolio_snapshot
- Write mode: Overwrite (overwrite=true)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | V2 processor iterates all loan_accounts rows without filtering |
| BR-2 | V2 processor selects 7 columns + as_of, excluding origination_date and maturity_date |
| BR-3 | Direct assignment from row values, no transformation |
| BR-4 | DscWriterUtil.Write with overwrite=true |
| BR-5 | Empty DataFrame guard if loan_accounts is null/empty |
| BR-6 | branches sourced in config but not referenced in V2 processor |
| BR-7 | as_of from each loan_accounts row |
