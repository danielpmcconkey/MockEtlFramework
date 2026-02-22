# FSD: AccountBalanceSnapshotV2

## Overview
Replaces the original AccountBalanceSnapshot job with a V2 implementation that writes to the `double_secret_curated` schema instead of `curated`. The V2 External module replicates the exact same business logic as the original AccountSnapshotBuilder and then writes directly to `double_secret_curated.account_balance_snapshot` via DscWriterUtil.

## Design Decisions
- **Pattern A (External module replacement)**: The original job uses DataSourcing -> External (AccountSnapshotBuilder) -> DataFrameWriter. The V2 replaces both the External and DataFrameWriter steps with a single V2 External module that combines the processing logic and writing.
- The V2 module replicates the original's column selection (account_id, customer_id, account_type, account_status, current_balance, as_of) from the accounts DataFrame.
- Write mode is Append (matching original), so DscWriterUtil.Write is called with overwrite=false.
- The branches DataSourcing step is retained in the config (matching the original) even though it is unused, to ensure identical shared state.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=accounts, columns=[account_id, customer_id, account_type, account_status, open_date, current_balance, interest_rate, credit_limit], resultName=accounts |
| 2 | DataSourcing | schema=datalake, table=branches, columns=[branch_id, branch_name], resultName=branches (unused but retained) |
| 3 | External | AccountBalanceSnapshotV2Processor -- selects output columns, writes to dsc |

## V2 External Module: AccountBalanceSnapshotV2Processor
- File: ExternalModules/AccountBalanceSnapshotV2Processor.cs
- Processing logic: Reads "accounts" from shared state, selects 6 columns (account_id, customer_id, account_type, account_status, current_balance, as_of), writes to double_secret_curated via DscWriterUtil with overwrite=false (Append mode)
- Output columns: account_id, customer_id, account_type, account_status, current_balance, as_of
- Target table: double_secret_curated.account_balance_snapshot
- Write mode: Append (overwrite=false)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | V2 processor selects same 6 columns from accounts DataFrame |
| BR-2 | branches DataSourcing retained in config but not read by V2 processor |
| BR-3 | V2 processor drops open_date, interest_rate, credit_limit by not including them in output |
| BR-4 | DscWriterUtil.Write called with overwrite=false (Append) |
| BR-5 | V2 processor iterates all account rows without filtering |
| BR-6 | V2 processor passes through values as-is with no transformations |
| BR-7 | Framework's DataSourcing handles date filtering; V2 processor has empty DataFrame guard |
