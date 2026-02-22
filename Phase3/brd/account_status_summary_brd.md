# AccountStatusSummary — Business Requirements Document

## Overview

This job produces a summary count of accounts grouped by account_type and account_status, showing how many accounts exist in each type/status combination for a given effective date. The result is written to `curated.account_status_summary` using Overwrite mode.

## Source Tables

### datalake.accounts
- **Columns sourced:** account_id, customer_id, account_type, account_status, current_balance
- **Columns actually used:** account_type, account_status (for grouping), as_of (from first row)
- **Join/filter logic:** No filtering. All rows for the effective date are processed.
- **Evidence:** [ExternalModules/AccountStatusCounter.cs:29-36] Only account_type and account_status are read from each row.

### datalake.segments
- **Columns sourced:** segment_id, segment_name
- **Usage:** NONE — this DataSourcing module is loaded into shared state as "segments" but the External module never accesses it.
- **Evidence:** [ExternalModules/AccountStatusCounter.cs] No reference to `sharedState["segments"]` anywhere.

## Business Rules

BR-1: Accounts are grouped by the combination of account_type and account_status, and the count of accounts in each group is computed.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountStatusCounter.cs:28-37] Dictionary keyed by `(accountType, accountStatus)` tuple, counting occurrences.
- Evidence: [curated.account_status_summary] 3 rows for 2024-10-31: Checking/Active=96, Savings/Active=94, Credit/Active=87 (sums to 277 = total accounts).

BR-2: The as_of value is taken from the first account row and applied uniformly to all output rows.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountStatusCounter.cs:24] `var asOf = accounts.Rows[0]["as_of"];`

BR-3: All accounts are included regardless of any attributes (no filtering).
- Confidence: HIGH
- Evidence: [ExternalModules/AccountStatusCounter.cs:28] `foreach (var acctRow in accounts.Rows)` — iterates all rows without conditions.

BR-4: The output contains 4 columns: account_type, account_status, account_count, as_of.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountStatusCounter.cs:10-13] `outputColumns` explicitly lists these 4 columns.
- Evidence: [curated.account_status_summary] Schema confirms these 4 columns.

BR-5: Data is written in Overwrite mode — only the most recently processed date is retained.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/account_status_summary.json:28] `"writeMode": "Overwrite"`.
- Evidence: [curated.account_status_summary] Only 1 as_of date (2024-10-31) present with 3 rows.

BR-6: When the accounts DataFrame is null or empty, an empty DataFrame with the correct schema is returned.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountStatusCounter.cs:17-21] Explicit null/empty guard.

BR-7: NULL account_type or account_status values are coalesced to empty string for grouping purposes.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountStatusCounter.cs:30-31] `?.ToString() ?? ""` applied to both fields.
- Note: In current data, all accounts have NOT NULL type and status per schema constraints, so this is defensive only.

## Output Schema

| Column | Source | Transformation |
|--------|--------|----------------|
| account_type | datalake.accounts.account_type | Group key; COALESCE to "" if null |
| account_status | datalake.accounts.account_status | Group key; COALESCE to "" if null |
| account_count | Computed | COUNT of accounts per (type, status) group |
| as_of | datalake.accounts.as_of (first row) | Taken from first account row |

## Edge Cases

- **Empty accounts:** Returns empty output with correct schema (BR-6).
- **NULL type/status:** Coalesced to empty string for grouping. However, schema constraints prevent NULLs in these columns.
- **Single status present:** Current data shows only "Active" accounts, producing 3 output rows (one per account_type). If additional statuses existed, more rows would appear.
- **Weekend dates:** No source account data for weekends; returns empty output.

## Anti-Patterns Identified

- **AP-1: Redundant Data Sourcing** — The `segments` DataSourcing module fetches segment_id and segment_name from `datalake.segments`, but the External module (`AccountStatusCounter`) never accesses `sharedState["segments"]`. This is completely unused.
  - Evidence: [JobExecutor/Jobs/account_status_summary.json:13-18] segments DataSourcing defined; [ExternalModules/AccountStatusCounter.cs] no reference to "segments".
  - V2 approach: Remove the segments DataSourcing module entirely.

- **AP-3: Unnecessary External Module** — The `AccountStatusCounter` performs a GROUP BY count that is trivially expressible in SQL: `SELECT account_type, account_status, COUNT(*) AS account_count, as_of FROM accounts GROUP BY account_type, account_status, as_of`.
  - Evidence: [ExternalModules/AccountStatusCounter.cs] The entire logic is: group by (type, status), count rows.
  - V2 approach: Replace with a SQL Transformation.

- **AP-4: Unused Columns Sourced** — The accounts DataSourcing fetches account_id, customer_id, and current_balance, but the External module only uses account_type, account_status, and as_of.
  - Evidence: [JobExecutor/Jobs/account_status_summary.json:10] columns include account_id, customer_id, current_balance; [ExternalModules/AccountStatusCounter.cs:29-31] only account_type and account_status are read.
  - V2 approach: Remove unused columns from the DataSourcing columns list. Only source account_type and account_status.

- **AP-6: Row-by-Row Iteration in External Module** — The External module iterates accounts row-by-row to build a count dictionary, when SQL GROUP BY would handle this directly.
  - Evidence: [ExternalModules/AccountStatusCounter.cs:28] `foreach (var acctRow in accounts.Rows)`
  - V2 approach: Replace with SQL GROUP BY aggregation.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [ExternalModules/AccountStatusCounter.cs:28-37], [curated.account_status_summary] 3 rows summing to 277 |
| BR-2 | [ExternalModules/AccountStatusCounter.cs:24] |
| BR-3 | [ExternalModules/AccountStatusCounter.cs:28] no conditional in loop |
| BR-4 | [ExternalModules/AccountStatusCounter.cs:10-13], [curated.account_status_summary] schema |
| BR-5 | [JobExecutor/Jobs/account_status_summary.json:28], [curated.account_status_summary] 1 date |
| BR-6 | [ExternalModules/AccountStatusCounter.cs:17-21] |
| BR-7 | [ExternalModules/AccountStatusCounter.cs:30-31] |

## Open Questions

None. The business logic is straightforward and fully observable.
