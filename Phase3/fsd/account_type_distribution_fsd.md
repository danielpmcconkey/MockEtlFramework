# FSD: AccountTypeDistributionV2

## Overview
Replaces the original AccountTypeDistribution job with a V2 implementation that writes to `double_secret_curated` instead of `curated`. The V2 External module replicates the exact AccountDistributionCalculator logic -- grouping accounts by account_type, computing counts, total, and percentage -- then writes to `double_secret_curated.account_type_distribution` via DscWriterUtil.

## Design Decisions
- **Pattern A (External module replacement)**: The original uses DataSourcing -> External (AccountDistributionCalculator) -> DataFrameWriter. The V2 replaces both with a single V2 External module.
- Write mode is Overwrite (matching original), so DscWriterUtil.Write is called with overwrite=true.
- The branches DataSourcing step is retained in the config (matching the original) even though it is unused.
- The percentage calculation uses `(double)typeCount / totalAccounts * 100.0` -- identical to the original, preserving floating-point behavior.
- The as_of value is taken from accounts.Rows[0]["as_of"], identical to the original.
- NULL account_type is coalesced to empty string via `?.ToString() ?? ""`, identical to the original.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=accounts, columns=[account_id, customer_id, account_type, account_status, current_balance], resultName=accounts |
| 2 | DataSourcing | schema=datalake, table=branches, columns=[branch_id, branch_name, city], resultName=branches (unused but retained) |
| 3 | External | AccountTypeDistributionV2Processor -- computes distribution, writes to dsc |

## V2 External Module: AccountTypeDistributionV2Processor
- File: ExternalModules/AccountTypeDistributionV2Processor.cs
- Processing logic: Reads "accounts" from shared state; gets as_of from first row; computes total_accounts = accounts.Count; groups by account_type and counts; computes percentage = (double)typeCount / totalAccounts * 100.0; writes to double_secret_curated
- Output columns: account_type, account_count, total_accounts, percentage, as_of
- Target table: double_secret_curated.account_type_distribution
- Write mode: Overwrite (overwrite=true)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | V2 groups by account_type and counts occurrences |
| BR-2 | total_accounts = accounts.Count |
| BR-3 | percentage = (double)typeCount / totalAccounts * 100.0 |
| BR-4 | percentage stored as double (C# double type) |
| BR-5 | as_of taken from accounts.Rows[0]["as_of"] |
| BR-6 | branches DataSourcing retained in config but not read by V2 processor |
| BR-7 | DscWriterUtil.Write called with overwrite=true (Overwrite) |
| BR-8 | NULL account_type coalesced to "" via ?.ToString() ?? "" |
| BR-9 | Framework handles date filtering; empty guard returns empty DataFrame |
