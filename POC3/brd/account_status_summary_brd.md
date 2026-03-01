# AccountStatusSummary — Business Requirements Document

## Overview
Produces a daily summary counting accounts grouped by account_type and account_status. Output is written to CSV with a trailer line, overwriting on each run.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/account_status_summary.csv`
- **includeHeader**: true
- **trailerFormat**: `TRAILER|{row_count}|{date}`
- **writeMode**: Overwrite
- **lineEnding**: LF

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.accounts | account_id, customer_id, account_type, account_status, current_balance | Effective date range (injected) | [account_status_summary.json:8-10] |
| datalake.segments | segment_id, segment_name | Effective date range (injected) | [account_status_summary.json:13-16] |

## Business Rules

BR-1: Accounts are grouped by the composite key (account_type, account_status), and the count of accounts in each group is computed.
- Confidence: HIGH
- Evidence: [AccountStatusCounter.cs:27-37] — Dictionary keyed by (type, status) tuple, incremented per row

BR-2: The segments DataFrame is sourced but NOT used by the External module.
- Confidence: HIGH
- Evidence: [AccountStatusCounter.cs:8-54] — no reference to "segments" in the Execute method

BR-3: The as_of value is taken from the first row of the accounts DataFrame and applied uniformly to all output rows.
- Confidence: HIGH
- Evidence: [AccountStatusCounter.cs:24] — `var asOf = accounts.Rows[0]["as_of"];`

BR-4: If accounts is null or empty, an empty DataFrame with the correct schema is produced.
- Confidence: HIGH
- Evidence: [AccountStatusCounter.cs:18-22]

BR-5: Currently, all accounts in the datalake have status "Active", so the grouping effectively produces one row per account_type. With 3 account types (Checking, Savings, Credit), expect 3 output rows per day.
- Confidence: HIGH
- Evidence: [DB query: SELECT DISTINCT account_status returns only "Active"; DISTINCT account_type returns 3 values]

BR-6: The customer_id and current_balance columns are sourced from accounts but never used in the output.
- Confidence: HIGH
- Evidence: [AccountStatusCounter.cs:10-13] — outputColumns does not include customer_id or current_balance

BR-7: The trailer line format is `TRAILER|{row_count}|{date}` where row_count is the number of data rows (excluding header/trailer) and date is the max effective date.
- Confidence: HIGH
- Evidence: [account_status_summary.json:28-29], [Architecture.md:241] — trailer token definitions

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| account_type | accounts.account_type | Group key | [AccountStatusCounter.cs:30,44] |
| account_status | accounts.account_status | Group key | [AccountStatusCounter.cs:31,45] |
| account_count | Computed | COUNT of rows per (account_type, account_status) group | [AccountStatusCounter.cs:36,46] |
| as_of | accounts.Rows[0].as_of | First row value applied to all output rows | [AccountStatusCounter.cs:24,47] |

## Non-Deterministic Fields
None identified. Output order of groups may vary since Dictionary iteration order is not guaranteed in .NET, but this is non-semantic for CSV.

## Write Mode Implications
- **Overwrite mode**: Each run replaces the entire CSV file. Only the most recent effective date's data persists.
- On multi-day gap-fill, intermediate days are overwritten by subsequent days.

## Edge Cases
- **Weekend dates**: Accounts is weekday-only. Weekend effective dates produce empty data, resulting in a CSV with only header and trailer (0 data rows).
- **Single status**: Currently only "Active" status exists, making the account_status dimension trivial. If statuses change over time, more rows would appear.
- **Output row ordering**: Dictionary iteration order is not guaranteed. Rows may appear in different order across runs.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Group by (account_type, account_status) | AccountStatusCounter.cs:27-37 |
| Segments sourced but unused | AccountStatusCounter.cs (no reference) |
| as_of from first accounts row | AccountStatusCounter.cs:24 |
| Trailer format TRAILER\|{row_count}\|{date} | account_status_summary.json:29 |
| Overwrite write mode | account_status_summary.json:30 |
| First effective date 2024-10-01 | account_status_summary.json:3 |

## Open Questions
1. Why are segments sourced if they are not used? Possible vestigial config. (Confidence: LOW)
2. Why are customer_id and current_balance sourced from accounts if they are not included in the output? (Confidence: LOW — likely over-sourcing)
