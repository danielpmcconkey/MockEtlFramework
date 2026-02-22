# FSD: AccountCustomerJoinV2

## Overview
Replaces the original AccountCustomerJoin job with a V2 implementation that writes to `double_secret_curated` instead of `curated`. The V2 External module replicates the exact AccountCustomerDenormalizer logic -- joining accounts with customer names -- then writes to `double_secret_curated.account_customer_join` via DscWriterUtil.

## Design Decisions
- **Pattern A (External module replacement)**: The original uses DataSourcing -> External (AccountCustomerDenormalizer) -> DataFrameWriter. The V2 replaces both External and DataFrameWriter with a single V2 External module.
- Write mode is Overwrite (matching original), so DscWriterUtil.Write is called with overwrite=true.
- The addresses DataSourcing step is retained in the config (matching the original) even though it is unused.
- The customer lookup dictionary uses `Convert.ToInt32` for customer_id matching, with `GetValueOrDefault` returning ("", "") for missing customers -- identical to the original.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=accounts, columns=[account_id, customer_id, account_type, account_status, current_balance], resultName=accounts |
| 2 | DataSourcing | schema=datalake, table=customers, columns=[id, first_name, last_name], resultName=customers |
| 3 | DataSourcing | schema=datalake, table=addresses, columns=[address_id, customer_id, address_line1, city, state_province], resultName=addresses (unused but retained) |
| 4 | External | AccountCustomerJoinV2Processor -- joins accounts with customers, writes to dsc |

## V2 External Module: AccountCustomerJoinV2Processor
- File: ExternalModules/AccountCustomerJoinV2Processor.cs
- Processing logic: Reads "accounts" and "customers" from shared state; builds customer_id -> (first_name, last_name) lookup; iterates accounts adding customer names; writes to double_secret_curated
- Output columns: account_id, customer_id, first_name, last_name, account_type, account_status, current_balance, as_of
- Target table: double_secret_curated.account_customer_join
- Write mode: Overwrite (overwrite=true)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | V2 builds customer lookup from customers.Rows keyed by id, joins to accounts via customer_id |
| BR-2 | GetValueOrDefault returns ("", "") for missing customers |
| BR-3 | Dictionary built from all customer rows; last-write-wins for duplicates (identical to original) |
| BR-4 | addresses DataSourcing retained in config but not read by V2 processor |
| BR-5 | DscWriterUtil.Write called with overwrite=true (Overwrite) |
| BR-6 | All accounts iterated without filtering |
| BR-7 | All values passed through as-is |
| BR-8 | Framework handles date filtering; empty guard returns empty DataFrame |
