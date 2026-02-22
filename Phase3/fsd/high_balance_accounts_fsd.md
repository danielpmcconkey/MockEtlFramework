# FSD: HighBalanceAccountsV2

## Overview
Replaces the original HighBalanceAccounts job with a V2 implementation that writes to the `double_secret_curated` schema instead of `curated`. The V2 External module replicates the exact same business logic as the original HighBalanceFilter (filtering accounts with balance > $10,000 and enriching with customer names) and then writes directly to `double_secret_curated.high_balance_accounts` via DscWriterUtil.

## Design Decisions
- **Pattern A (External module replacement)**: The original job uses DataSourcing (x2) -> External (HighBalanceFilter) -> DataFrameWriter. The V2 replaces both the External and DataFrameWriter steps with a single V2 External module.
- The V2 module replicates the original's filtering logic (balance > 10000) and customer name enrichment via dictionary lookup.
- Write mode is Overwrite (matching original), so DscWriterUtil.Write is called with overwrite=true.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=accounts, columns=[account_id, customer_id, account_type, account_status, current_balance], resultName=accounts |
| 2 | DataSourcing | schema=datalake, table=customers, columns=[id, first_name, last_name], resultName=customers |
| 3 | External | HighBalanceV2Processor -- filters balance > 10000, enriches with customer names, writes to dsc |

## V2 External Module: HighBalanceV2Processor
- File: ExternalModules/HighBalanceV2Processor.cs
- Processing logic: Builds customer_id -> (first_name, last_name) lookup from customers. Iterates accounts, filters balance > 10000. Enriches with customer name (default "" if not found). Writes to double_secret_curated via DscWriterUtil with overwrite=true.
- Output columns: account_id, customer_id, account_type, current_balance, first_name, last_name, as_of
- Target table: double_secret_curated.high_balance_accounts
- Write mode: Overwrite (overwrite=true)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | V2 processor filters accounts with balance > 10000 |
| BR-2 | V2 processor builds customer name lookup and enriches output |
| BR-3 | GetValueOrDefault with ("", "") for missing customers |
| BR-4 | Customer lookup keyed on custRow["id"], not customer_id |
| BR-5 | No account_type filter applied |
| BR-6 | No account_status filter applied |
| BR-7 | DscWriterUtil.Write with overwrite=true |
| BR-8 | Empty DataFrame guard if accounts or customers are null/empty |
| BR-9 | as_of from acctRow["as_of"] |
| BR-10 | Dictionary assignment overwrites duplicate customer ids |
