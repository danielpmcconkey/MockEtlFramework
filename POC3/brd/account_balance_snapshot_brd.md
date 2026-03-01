# AccountBalanceSnapshot — Business Requirements Document

## Overview
Produces a daily snapshot of account balances by selecting key account fields from the datalake and writing them to Parquet. Each effective date appends a new set of rows, building a historical time series of account states.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `output`
- **outputDirectory**: `Output/curated/account_balance_snapshot/`
- **numParts**: 2
- **writeMode**: Append

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.accounts | account_id, customer_id, account_type, account_status, open_date, current_balance, interest_rate, credit_limit | Effective date range (injected by executor) | [account_balance_snapshot.json:8-10] |
| datalake.branches | branch_id, branch_name | Effective date range (injected by executor) | [account_balance_snapshot.json:13-17] |

## Business Rules

BR-1: The External module selects only a subset of account columns for output: account_id, customer_id, account_type, account_status, current_balance, and as_of. The open_date, interest_rate, and credit_limit columns sourced in the config are NOT included in the output.
- Confidence: HIGH
- Evidence: [AccountSnapshotBuilder.cs:10-14] — outputColumns list explicitly defines 6 columns, excluding open_date, interest_rate, credit_limit

BR-2: The branches DataFrame is sourced but NOT used by the External module. It is loaded into shared state but the AccountSnapshotBuilder code never references it.
- Confidence: HIGH
- Evidence: [AccountSnapshotBuilder.cs:8-39] — no reference to "branches" in the Execute method

BR-3: If the accounts DataFrame is null or empty, an empty DataFrame with the correct output schema is produced (no error thrown).
- Confidence: HIGH
- Evidence: [AccountSnapshotBuilder.cs:18-22] — explicit null/empty check returns empty DataFrame

BR-4: Each output row is a direct passthrough of account fields with no transformation applied. Values are copied verbatim from the source accounts DataFrame.
- Confidence: HIGH
- Evidence: [AccountSnapshotBuilder.cs:27-35] — row-by-row copy with no calculations

BR-5: The as_of column is carried through from the accounts DataFrame, reflecting the effective date of the source snapshot.
- Confidence: HIGH
- Evidence: [AccountSnapshotBuilder.cs:34] — `["as_of"] = acctRow["as_of"]`

BR-6: Data is only available on weekdays (no Saturday/Sunday as_of dates for accounts). The snapshot count is consistent at 2,869 accounts per day.
- Confidence: HIGH
- Evidence: [DB query: accounts GROUP BY as_of shows weekday-only dates, 2869 rows each]

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| account_id | accounts.account_id | None (passthrough) | [AccountSnapshotBuilder.cs:29] |
| customer_id | accounts.customer_id | None (passthrough) | [AccountSnapshotBuilder.cs:30] |
| account_type | accounts.account_type | None (passthrough) | [AccountSnapshotBuilder.cs:31] |
| account_status | accounts.account_status | None (passthrough) | [AccountSnapshotBuilder.cs:32] |
| current_balance | accounts.current_balance | None (passthrough) | [AccountSnapshotBuilder.cs:33] |
| as_of | accounts.as_of | None (passthrough) | [AccountSnapshotBuilder.cs:34] |

## Non-Deterministic Fields
None identified. All output fields are deterministic passthroughs from the source data.

## Write Mode Implications
- **Append mode**: Each effective date run appends rows to the existing Parquet directory. Over time this builds a historical archive of all account states across all processed dates.
- On multi-day gap-fill runs, each day appends its own partition of 2,869 rows.
- No deduplication is performed. If a date is re-run, duplicate rows for that date will accumulate.

## Edge Cases
- **Weekend dates**: Accounts table has no weekend data. If the executor advances to a weekend date, DataSourcing will return zero rows, resulting in an empty append (header-only partition).
- **Null handling**: The code uses null-coalescing only for the accounts DataFrame check, not for individual field values. Null column values will be written as Parquet nulls.
- **Unused source data**: branches is sourced but never used — this is a wasted query with no impact on output.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Output columns are 6 fields from accounts | AccountSnapshotBuilder.cs:10-14 |
| Branches sourced but unused | AccountSnapshotBuilder.cs:8-39 (no reference) |
| Empty input produces empty output | AccountSnapshotBuilder.cs:18-22 |
| Append write mode | account_balance_snapshot.json:29 |
| 2 Parquet part files | account_balance_snapshot.json:28 |
| First effective date is 2024-10-01 | account_balance_snapshot.json:3 |
| Weekday-only account data | DB query: accounts GROUP BY as_of |

## Open Questions
1. Why are branches sourced if they are never used? Possible vestigial config from an earlier design that intended to include branch info. (Confidence: LOW — no code evidence of intent)
2. Why are open_date, interest_rate, and credit_limit sourced from accounts if they are dropped by the External module? Similar vestigial sourcing. (Confidence: LOW)
