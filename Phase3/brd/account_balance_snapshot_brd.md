# BRD: AccountBalanceSnapshot

## Overview
Produces a daily snapshot of all account balances by extracting key account attributes from the datalake and appending them to a curated table, one row per account per business day. This provides a historical record of account balance positions over time.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| accounts | datalake | account_id, customer_id, account_type, account_status, open_date, current_balance, interest_rate, credit_limit | Filtered by effective date range (as_of between min and max effective date). No additional filters. | [JobExecutor/Jobs/account_balance_snapshot.json:6-11] DataSourcing module config |
| branches | datalake | branch_id, branch_name | Filtered by effective date range. Sourced but NOT used in output. | [JobExecutor/Jobs/account_balance_snapshot.json:13-18] DataSourcing module config; [ExternalModules/AccountSnapshotBuilder.cs:10-14] output columns do not reference branch data |

## Business Rules
BR-1: The job sources all accounts from the datalake for the effective date and outputs a subset of columns: account_id, customer_id, account_type, account_status, current_balance, and as_of.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountSnapshotBuilder.cs:10-14] Output columns defined as `"account_id", "customer_id", "account_type", "account_status", "current_balance", "as_of"`
- Evidence: [ExternalModules/AccountSnapshotBuilder.cs:26-35] Each account row maps directly to output without transformation

BR-2: The branches table is sourced but never used in the output. The AccountSnapshotBuilder only reads the "accounts" DataFrame from shared state.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountSnapshotBuilder.cs:16] `var accounts = sharedState.ContainsKey("accounts") ? sharedState["accounts"] as DataFrame : null;` -- no reference to "branches"
- Evidence: [JobExecutor/Jobs/account_balance_snapshot.json:13-18] branches DataSourcing module exists in config

BR-3: The job drops columns open_date, interest_rate, and credit_limit that were sourced from the accounts table. Only 5 account columns plus as_of are retained.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/account_balance_snapshot.json:10] Sources 8 columns: account_id, customer_id, account_type, account_status, open_date, current_balance, interest_rate, credit_limit
- Evidence: [ExternalModules/AccountSnapshotBuilder.cs:10-14] Output only includes 6 columns (5 account columns + as_of)

BR-4: Write mode is Append -- each effective date run adds rows to the existing table without truncating previous data. This builds a historical time series.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/account_balance_snapshot.json:28] `"writeMode": "Append"`

BR-5: No filtering is applied -- all accounts regardless of type or status are included in the output.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountSnapshotBuilder.cs:25-35] Iterates all account rows with `foreach (var acctRow in accounts.Rows)` -- no conditional filtering
- Evidence: [curated.account_balance_snapshot] Row count of 277 per date matches datalake.accounts row count of 277 per date

BR-6: No transformations or calculations are applied to any column values. Values are passed through as-is from the source.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountSnapshotBuilder.cs:27-35] Direct assignment: `["account_id"] = acctRow["account_id"]`, etc.

BR-7: The job runs only on business days (weekdays) since the accounts source table only has weekday as_of dates.
- Confidence: HIGH
- Evidence: [datalake.accounts] `SELECT as_of FROM datalake.accounts GROUP BY as_of ORDER BY as_of` shows dates skip weekends (Oct 4 -> Oct 7, Oct 11 -> Oct 14)
- Evidence: [curated.account_balance_snapshot] Output has exactly 23 dates matching the 23 weekday dates in October 2024

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| account_id | datalake.accounts.account_id | None (pass-through) | [AccountSnapshotBuilder.cs:29] |
| customer_id | datalake.accounts.customer_id | None (pass-through) | [AccountSnapshotBuilder.cs:30] |
| account_type | datalake.accounts.account_type | None (pass-through) | [AccountSnapshotBuilder.cs:31] |
| account_status | datalake.accounts.account_status | None (pass-through) | [AccountSnapshotBuilder.cs:32] |
| current_balance | datalake.accounts.current_balance | None (pass-through) | [AccountSnapshotBuilder.cs:33] |
| as_of | datalake.accounts.as_of | None (pass-through) | [AccountSnapshotBuilder.cs:34] |

## Edge Cases
- **NULL handling**: No explicit NULL handling in AccountSnapshotBuilder. Values are passed through as-is. If a source column is NULL, it will be NULL in the output.
- **Empty accounts DataFrame**: If accounts is null or has 0 rows, an empty DataFrame with the correct columns is returned. [AccountSnapshotBuilder.cs:18-22]
- **Weekend/date fallback**: Accounts table has no weekend data (weekdays only). The framework's DataSourcing module returns empty results for dates with no data, which would trigger the empty DataFrame guard.
- **Zero-row behavior**: Handled gracefully -- produces empty output DataFrame with correct column schema. [AccountSnapshotBuilder.cs:20]

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [AccountSnapshotBuilder.cs:10-14], [AccountSnapshotBuilder.cs:26-35] |
| BR-2 | [AccountSnapshotBuilder.cs:16], [account_balance_snapshot.json:13-18] |
| BR-3 | [account_balance_snapshot.json:10], [AccountSnapshotBuilder.cs:10-14] |
| BR-4 | [account_balance_snapshot.json:28] |
| BR-5 | [AccountSnapshotBuilder.cs:25-35], [curated.account_balance_snapshot row counts] |
| BR-6 | [AccountSnapshotBuilder.cs:27-35] |
| BR-7 | [datalake.accounts as_of dates], [curated.account_balance_snapshot dates] |

## Open Questions
- **Why is branches sourced but unused?** The branches DataSourcing module is configured in the job but the AccountSnapshotBuilder never references "branches" from shared state. This appears to be dead code in the job config. Confidence: MEDIUM that it is intentionally unused (it may have been removed from the processor logic but left in the config).
