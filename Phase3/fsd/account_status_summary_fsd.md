# FSD: AccountStatusSummaryV2

## Overview
Replaces the original AccountStatusSummary job with a V2 implementation that writes to `double_secret_curated` instead of `curated`. The V2 External module replicates the exact AccountStatusCounter logic -- grouping accounts by (account_type, account_status) and counting -- then writes to `double_secret_curated.account_status_summary` via DscWriterUtil.

## Design Decisions
- **Pattern A (External module replacement)**: The original uses DataSourcing -> External (AccountStatusCounter) -> DataFrameWriter. The V2 replaces both with a single V2 External module.
- Write mode is Overwrite (matching original), so DscWriterUtil.Write is called with overwrite=true.
- The segments DataSourcing step is retained in the config (matching the original) even though it is unused.
- The as_of value is taken from accounts.Rows[0]["as_of"], identical to the original.
- NULL account_type and account_status are coalesced to empty string via `?.ToString() ?? ""`, identical to the original.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=accounts, columns=[account_id, customer_id, account_type, account_status, current_balance], resultName=accounts |
| 2 | DataSourcing | schema=datalake, table=segments, columns=[segment_id, segment_name], resultName=segments (unused but retained) |
| 3 | External | AccountStatusSummaryV2Processor -- groups and counts by (type, status), writes to dsc |

## V2 External Module: AccountStatusSummaryV2Processor
- File: ExternalModules/AccountStatusSummaryV2Processor.cs
- Processing logic: Reads "accounts" from shared state; gets as_of from first row; builds (account_type, account_status) -> count dictionary; produces output rows; writes to double_secret_curated
- Output columns: account_type, account_status, account_count, as_of
- Target table: double_secret_curated.account_status_summary
- Write mode: Overwrite (overwrite=true)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | V2 groups by (account_type, account_status) and counts occurrences |
| BR-2 | as_of taken from accounts.Rows[0]["as_of"] |
| BR-3 | segments DataSourcing retained in config but not read by V2 processor |
| BR-4 | DscWriterUtil.Write called with overwrite=true (Overwrite) |
| BR-5 | All accounts counted; only grouping dimensions are type and status |
| BR-6 | NULL account_type and account_status coalesced to "" via ?.ToString() ?? "" |
| BR-7 | Framework handles date filtering; empty guard returns empty DataFrame |
