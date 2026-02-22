# FSD: CustomerAccountSummaryV2V2

## Overview
CustomerAccountSummaryV2V2 replicates the exact business logic of CustomerAccountSummaryV2, producing per-customer account summaries with account count, total balance, and active balance. The V2V2 uses DscWriterUtil to write to `double_secret_curated.customer_account_summary_v2`.

## Design Decisions
- **Pattern A (External module)**: Original uses DataSourcing + External + DataFrameWriter. V2V2 keeps DataSourcing steps identical and replaces External+DataFrameWriter with a single V2 External.
- **Write mode**: Overwrite (overwrite=true) to match original.
- **Branches DataSourcing retained**: Original sources branches but never uses them. V2V2 retains this.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | customers: id, first_name, last_name |
| 2 | DataSourcing | accounts: account_id, customer_id, account_type, account_status, current_balance |
| 3 | DataSourcing | branches: branch_id, branch_name, city |
| 4 | External | CustomerAccountSummaryV2V2Processor |

## V2 External Module: CustomerAccountSummaryV2V2Processor
- File: ExternalModules/CustomerAccountSummaryV2V2Processor.cs
- Processing logic: Groups accounts by customer_id, computes count/total_balance/active_balance, joins with customer names
- Output columns: customer_id, first_name, last_name, account_count, total_balance, active_balance, as_of
- Target table: double_secret_curated.customer_account_summary_v2
- Write mode: Overwrite (overwrite=true)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 (All customers included) | Loop iterates all customers; GetValueOrDefault for zero accounts |
| BR-2 (Account count) | count + 1 for every account row |
| BR-3 (Total balance) | Sum of current_balance across all accounts |
| BR-4 (Active balance) | Sum only where account_status = "Active" |
| BR-5 (Overwrite mode) | DscWriterUtil.Write with overwrite=true |
| BR-6 (Empty guard) | Early return if customers or accounts empty |
| BR-7 (as_of from customers) | custRow["as_of"] passed through |
| BR-8 (Names default to "") | Null coalesce to "" |
| BR-9 (Branches unused) | Branches DataSourcing retained but not referenced |
| BR-10 (total_balance added) | Includes total_balance column |
