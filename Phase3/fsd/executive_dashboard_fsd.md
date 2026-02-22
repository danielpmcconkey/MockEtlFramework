# FSD: ExecutiveDashboardV2

## Overview
Replaces the original ExecutiveDashboard job with a V2 implementation that writes to the `double_secret_curated` schema instead of `curated`. The V2 External module replicates the exact same business logic as the original ExecutiveDashboardBuilder (computing 9 KPI metrics from customers, accounts, transactions, loans, and branch visits) and then writes directly to `double_secret_curated.executive_dashboard` via DscWriterUtil.

## Design Decisions
- **Pattern A (External module replacement)**: The original job uses DataSourcing (x7) -> External (ExecutiveDashboardBuilder) -> DataFrameWriter. The V2 replaces both the External and DataFrameWriter steps with a single V2 External module that combines the processing logic and writing.
- The V2 module replicates the original's logic: computing 9 metrics (total_customers, total_accounts, total_balance, total_transactions, total_txn_amount, avg_txn_amount, total_loans, total_loan_balance, total_branch_visits), each rounded to 2 decimal places.
- Write mode is Overwrite (matching original), so DscWriterUtil.Write is called with overwrite=true.
- The branches and segments DataSourcing steps are retained in the config (matching the original) even though they are unused by the External module, to ensure identical shared state.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=transactions, columns=[transaction_id, account_id, txn_type, amount], resultName=transactions |
| 2 | DataSourcing | schema=datalake, table=accounts, columns=[account_id, customer_id, account_type, account_status, current_balance], resultName=accounts |
| 3 | DataSourcing | schema=datalake, table=customers, columns=[id, first_name, last_name], resultName=customers |
| 4 | DataSourcing | schema=datalake, table=loan_accounts, columns=[loan_id, customer_id, loan_type, current_balance], resultName=loan_accounts |
| 5 | DataSourcing | schema=datalake, table=branch_visits, columns=[visit_id, customer_id, branch_id, visit_purpose], resultName=branch_visits |
| 6 | DataSourcing | schema=datalake, table=branches, columns=[branch_id, branch_name, city, state_province], resultName=branches (unused) |
| 7 | DataSourcing | schema=datalake, table=segments, columns=[segment_id, segment_name], resultName=segments (unused) |
| 8 | External | ExecutiveDashboardV2Processor -- computes 9 metrics, writes to dsc |

## V2 External Module: ExecutiveDashboardV2Processor
- File: ExternalModules/ExecutiveDashboardV2Processor.cs
- Processing logic: Reads customers, accounts, transactions, loan_accounts, branch_visits from shared state. Computes 9 KPI metrics with Math.Round(..., 2). Gets as_of from first customer row (fallback to transactions). Builds metric_name/metric_value/as_of rows. Writes to double_secret_curated via DscWriterUtil with overwrite=true.
- Output columns: metric_name, metric_value, as_of
- Target table: double_secret_curated.executive_dashboard
- Write mode: Overwrite (overwrite=true)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | V2 processor builds exactly 9 metric rows |
| BR-2 | total_customers = (decimal)customers.Count |
| BR-3 | total_accounts = (decimal)accounts.Count |
| BR-4 | total_balance = sum of current_balance across accounts, rounded to 2 |
| BR-5 | total_transactions = transactions.Count |
| BR-6 | total_txn_amount = sum of amount across transactions, rounded to 2 |
| BR-7 | avg_txn_amount = total_txn_amount / total_transactions (0 if no txns), rounded to 2 |
| BR-8 | total_loans = (decimal)loanAccounts.Count |
| BR-9 | total_loan_balance = sum of current_balance across loans, rounded to 2 |
| BR-10 | total_branch_visits = branchVisits.Count |
| BR-11 | All metrics wrapped in Math.Round(..., 2) |
| BR-12 | as_of from customers.Rows[0]["as_of"], fallback to transactions |
| BR-13 | DscWriterUtil.Write with overwrite=true |
| BR-14 | Empty DataFrame guard if customers, accounts, or loan_accounts are null/empty |
| BR-15 | branches and segments sourced in config but not referenced in V2 processor |
