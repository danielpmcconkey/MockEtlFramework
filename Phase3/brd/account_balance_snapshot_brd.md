# AccountBalanceSnapshot — Business Requirements Document

## Overview

This job produces a daily snapshot of all account balances by extracting core account attributes (account_id, customer_id, account_type, account_status, current_balance) from the datalake accounts table and writing them to `curated.account_balance_snapshot` using Append mode, building up a historical record of balances over time.

## Source Tables

### datalake.accounts
- **Columns sourced:** account_id, customer_id, account_type, account_status, open_date, current_balance, interest_rate, credit_limit
- **Columns actually used by External module:** account_id, customer_id, account_type, account_status, current_balance, as_of
- **Join/filter logic:** No filtering applied. All rows for the effective date are included.
- **Evidence:** [ExternalModules/AccountSnapshotBuilder.cs:27-35] Only these 6 columns are mapped to output rows.

### datalake.branches
- **Columns sourced:** branch_id, branch_name
- **Usage:** NONE — this DataSourcing module is loaded into shared state as "branches" but the External module (AccountSnapshotBuilder) never reads from it.
- **Evidence:** [ExternalModules/AccountSnapshotBuilder.cs:16] Only `sharedState["accounts"]` is accessed; "branches" is never referenced.

## Business Rules

BR-1: All accounts from the datalake are included in the snapshot regardless of account type or status.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountSnapshotBuilder.cs:25-35] The foreach loop iterates all rows with no conditional filtering.
- Evidence: [curated.account_balance_snapshot] Row count (277) matches datalake.accounts row count (277) for each as_of date.

BR-2: The output contains exactly 6 columns: account_id, customer_id, account_type, account_status, current_balance, and as_of.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountSnapshotBuilder.cs:10-14] `outputColumns` is explicitly defined as these 6 columns.
- Evidence: [curated.account_balance_snapshot] Schema confirms these 6 columns.

BR-3: Data is written in Append mode, accumulating snapshots across effective dates.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/account_balance_snapshot.json:28] `"writeMode": "Append"`.
- Evidence: [curated.account_balance_snapshot] Contains 23 distinct as_of dates (weekdays only in Oct 2024), each with 277 rows.

BR-4: The job processes only weekday effective dates (no weekends).
- Confidence: HIGH
- Evidence: [curated.account_balance_snapshot] as_of values skip Oct 5-6, 12-13, 19-20, 26-27.
- Evidence: [datalake.accounts] Source data only exists for weekdays (Oct 5-6 are missing).

BR-5: When the accounts DataFrame is null or empty, an empty DataFrame with the correct output schema is returned.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountSnapshotBuilder.cs:18-22] Explicit null/empty guard returns empty DataFrame with correct columns.

## Output Schema

| Column | Source | Transformation |
|--------|--------|----------------|
| account_id | datalake.accounts.account_id | Direct pass-through |
| customer_id | datalake.accounts.customer_id | Direct pass-through |
| account_type | datalake.accounts.account_type | Direct pass-through |
| account_status | datalake.accounts.account_status | Direct pass-through |
| current_balance | datalake.accounts.current_balance | Direct pass-through |
| as_of | datalake.accounts.as_of (injected by framework) | Direct pass-through |

## Edge Cases

- **Empty accounts DataFrame:** Returns empty output with correct schema (BR-5).
- **Weekend dates:** No source data exists for weekends; the framework's gap-fill mechanism processes each day but DataSourcing returns zero rows for weekend dates in accounts, so no rows are written.
- **NULL handling:** All source columns in datalake.accounts are NOT NULL (per schema constraints), so no NULL handling is needed in this job. The External module does not apply any COALESCE or default values.

## Anti-Patterns Identified

- **AP-1: Redundant Data Sourcing** — The `branches` DataSourcing module fetches `branch_id` and `branch_name` from `datalake.branches`, but the External module (`AccountSnapshotBuilder`) never accesses `sharedState["branches"]`. This is completely unused data sourcing.
  - Evidence: [JobExecutor/Jobs/account_balance_snapshot.json:13-18] branches DataSourcing defined; [ExternalModules/AccountSnapshotBuilder.cs] no reference to "branches".
  - V2 approach: Remove the branches DataSourcing module entirely.

- **AP-3: Unnecessary External Module** — The `AccountSnapshotBuilder` External module performs a trivial row-by-row copy of 6 columns from accounts. This is a simple `SELECT` query that can be expressed as a SQL Transformation.
  - Evidence: [ExternalModules/AccountSnapshotBuilder.cs:25-35] The entire logic is: for each account row, copy 6 columns.
  - V2 approach: Replace with a SQL Transformation: `SELECT account_id, customer_id, account_type, account_status, current_balance, as_of FROM accounts`.

- **AP-4: Unused Columns Sourced** — The accounts DataSourcing module fetches `open_date`, `interest_rate`, and `credit_limit`, but the External module only uses account_id, customer_id, account_type, account_status, current_balance, and as_of.
  - Evidence: [JobExecutor/Jobs/account_balance_snapshot.json:10] columns include open_date, interest_rate, credit_limit; [ExternalModules/AccountSnapshotBuilder.cs:27-35] only 5 account columns + as_of are used.
  - V2 approach: Remove open_date, interest_rate, and credit_limit from the DataSourcing columns list.

- **AP-6: Row-by-Row Iteration in External Module** — The External module iterates over every account row individually to build the output, when a simple SELECT would suffice.
  - Evidence: [ExternalModules/AccountSnapshotBuilder.cs:25] `foreach (var acctRow in accounts.Rows)`
  - V2 approach: Replace with a SQL Transformation (eliminates row-by-row processing entirely).

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [ExternalModules/AccountSnapshotBuilder.cs:25-35], [curated.account_balance_snapshot] row count = 277 = datalake.accounts count |
| BR-2 | [ExternalModules/AccountSnapshotBuilder.cs:10-14], [curated.account_balance_snapshot] schema |
| BR-3 | [JobExecutor/Jobs/account_balance_snapshot.json:28], [curated.account_balance_snapshot] 23 distinct as_of dates |
| BR-4 | [curated.account_balance_snapshot] missing weekend dates, [datalake.accounts] missing weekend dates |
| BR-5 | [ExternalModules/AccountSnapshotBuilder.cs:18-22] |

## Open Questions

None. This job is straightforward — all business logic is directly observable with HIGH confidence.
