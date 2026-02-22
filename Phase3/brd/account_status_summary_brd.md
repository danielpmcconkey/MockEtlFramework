# BRD: AccountStatusSummary

## Overview
Produces a summary count of accounts grouped by account_type and account_status for the current effective date. Uses Overwrite mode, so only the latest date's summary is retained in the curated table.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| accounts | datalake | account_id, customer_id, account_type, account_status, current_balance | Filtered by effective date range. All accounts included. | [JobExecutor/Jobs/account_status_summary.json:6-11] |
| segments | datalake | segment_id, segment_name | Filtered by effective date range. Sourced but NOT used in output. | [JobExecutor/Jobs/account_status_summary.json:13-18] |

## Business Rules
BR-1: Accounts are grouped by the combination of (account_type, account_status) and the count of accounts in each group is computed.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountStatusCounter.cs:27-37] Groups by `(accountType, accountStatus)` key and increments count
- Evidence: [curated.account_status_summary] Output shows 3 rows: (Checking, Active, 96), (Savings, Active, 94), (Credit, Active, 87)

BR-2: The as_of value in the output is taken from the first row of the accounts DataFrame, not computed or aggregated.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountStatusCounter.cs:24] `var asOf = accounts.Rows[0]["as_of"];`

BR-3: The segments table is sourced but never used in the output. The AccountStatusCounter only reads "accounts" from shared state.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountStatusCounter.cs:15] Only `accounts` is read from shared state -- no reference to "segments"

BR-4: Write mode is Overwrite -- the curated table is truncated before each write. Only the latest effective date's summary is retained.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/account_status_summary.json:28] `"writeMode": "Overwrite"`
- Evidence: [curated.account_status_summary] Only 1 as_of date (2024-10-31) with 3 rows

BR-5: All accounts are counted regardless of balance or other attributes. The only grouping dimensions are account_type and account_status.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountStatusCounter.cs:29-36] Iterates all rows, groups only by type and status

BR-6: NULL or empty account_type and account_status values are handled by coalescing to empty string via `?.ToString() ?? ""`.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountStatusCounter.cs:30-31] `acctRow["account_type"]?.ToString() ?? ""` and `acctRow["account_status"]?.ToString() ?? ""`

BR-7: The job runs only on business days (weekdays) since the accounts source table only has weekday as_of dates.
- Confidence: HIGH
- Evidence: [datalake.accounts] Weekday-only as_of dates

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| account_type | datalake.accounts.account_type | Grouping key (coalesced to "" if null) | [AccountStatusCounter.cs:30, 44] |
| account_status | datalake.accounts.account_status | Grouping key (coalesced to "" if null) | [AccountStatusCounter.cs:31, 45] |
| account_count | Computed | COUNT of accounts per (account_type, account_status) group | [AccountStatusCounter.cs:46] |
| as_of | datalake.accounts.as_of (first row) | Taken from first account row | [AccountStatusCounter.cs:24, 47] |

## Edge Cases
- **NULL handling**: account_type and account_status are coalesced to empty string (`?.ToString() ?? ""`). This means NULL values would be grouped as empty strings. [AccountStatusCounter.cs:30-31]
- **Empty accounts DataFrame**: If accounts is null or has 0 rows, an empty DataFrame with correct schema is returned. [AccountStatusCounter.cs:15-21]
- **Single account_status in data**: Currently all accounts have status "Active", resulting in only 3 output rows (one per account_type). If other statuses existed, more rows would be produced.
- **Weekend/date fallback**: Accounts table has no weekend data, so no output is produced on weekends.
- **as_of from first row**: If the DataFrame spans multiple as_of dates (which shouldn't happen in single-date execution mode), only the first row's as_of is used for all output rows.

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [AccountStatusCounter.cs:27-37], [curated.account_status_summary data] |
| BR-2 | [AccountStatusCounter.cs:24] |
| BR-3 | [AccountStatusCounter.cs:15], [account_status_summary.json:13-18] |
| BR-4 | [account_status_summary.json:28], [curated.account_status_summary row counts] |
| BR-5 | [AccountStatusCounter.cs:29-36] |
| BR-6 | [AccountStatusCounter.cs:30-31] |
| BR-7 | [datalake.accounts as_of dates] |

## Open Questions
- **Why is segments sourced but unused?** The segments DataSourcing module is configured in the job but never referenced in AccountStatusCounter. This is consistent with a pattern across multiple jobs of sourcing unused tables. Confidence: MEDIUM that it is intentionally unused.
- **Only one account_status exists**: All accounts currently have status "Active". The grouping by (account_type, account_status) suggests the design anticipates multiple statuses, but the current data only has one. This does not affect the logic but limits validation of multi-status behavior.
