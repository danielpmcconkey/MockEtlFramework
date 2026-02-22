# FSD: LargeTransactionLogV2

## Overview
Replaces the original LargeTransactionLog job with a V2 implementation that writes to the `double_secret_curated` schema instead of `curated`. The V2 External module replicates the exact same business logic as the original LargeTransactionProcessor (filtering transactions > $500 and enriching with customer identity via account-to-customer lookup) and then writes directly to `double_secret_curated.large_transaction_log` via DscWriterUtil.

## Design Decisions
- **Pattern A (External module replacement)**: The original job uses DataSourcing (x4) -> External (LargeTransactionProcessor) -> DataFrameWriter. The V2 replaces both the External and DataFrameWriter steps with a single V2 External module.
- The V2 module replicates the original's two-step lookup: account_id -> customer_id, then customer_id -> (first_name, last_name).
- Write mode is Append (matching original), so DscWriterUtil.Write is called with overwrite=false.
- The addresses DataSourcing step is retained in the config (matching the original) even though it is unused.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=transactions, columns=[transaction_id, account_id, txn_timestamp, txn_type, amount, description], resultName=transactions |
| 2 | DataSourcing | schema=datalake, table=accounts, columns=[account_id, customer_id, account_type, account_status, open_date, current_balance, interest_rate, credit_limit, apr], resultName=accounts |
| 3 | DataSourcing | schema=datalake, table=customers, columns=[id, first_name, last_name], resultName=customers |
| 4 | DataSourcing | schema=datalake, table=addresses, columns=[address_id, customer_id, address_line1, city], resultName=addresses (unused) |
| 5 | External | LargeTransactionV2Processor -- filters amount > 500, enriches with customer identity, writes to dsc |

## V2 External Module: LargeTransactionV2Processor
- File: ExternalModules/LargeTransactionV2Processor.cs
- Processing logic: Builds account_id -> customer_id lookup from accounts. Builds customer_id -> (first_name, last_name) lookup from customers. Iterates transactions, filters amount > 500, enriches with customer identity. Writes to double_secret_curated via DscWriterUtil with overwrite=false (Append).
- Output columns: transaction_id, account_id, customer_id, first_name, last_name, txn_type, amount, description, txn_timestamp, as_of
- Target table: double_secret_curated.large_transaction_log
- Write mode: Append (overwrite=false)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | V2 processor filters transactions with amount > 500 |
| BR-2 | Two-step lookup: account_id -> customer_id -> (first_name, last_name) |
| BR-3 | accountToCustomer.GetValueOrDefault(accountId, 0) for missing accounts |
| BR-4 | customerNames.GetValueOrDefault(customerId, ("", "")) for missing customers |
| BR-5 | No txn_type filter applied |
| BR-6 | DscWriterUtil.Write with overwrite=false (Append) |
| BR-7 | Empty DataFrame guard if accounts or customers are null/empty |
| BR-8 | Empty DataFrame guard if transactions are null/empty |
| BR-9 | addresses sourced in config but not referenced in V2 processor |
| BR-10 | as_of from txnRow["as_of"] |
| BR-11 | Only customer_id used from accounts table; other columns loaded but unused |
