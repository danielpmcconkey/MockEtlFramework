# BRD: CustomerAccountSummaryV2

## Overview
This job produces a per-customer account summary containing the customer's name, total account count, total balance across all accounts, and the balance of only active accounts. It is an enhanced version of the original CustomerAccountSummary job (which uses SQL Transformation), using an External module for processing.

## Source Tables

| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| customers | datalake | id, first_name, last_name | Sourced via DataSourcing for effective date range | [customer_account_summary_v2.json:7-11] |
| accounts | datalake | account_id, customer_id, account_type, account_status, current_balance | Sourced via DataSourcing for effective date range | [customer_account_summary_v2.json:13-18] |
| branches | datalake | branch_id, branch_name, city | Sourced via DataSourcing but NOT USED in the External module | [customer_account_summary_v2.json:20-24] |

## Business Rules

BR-1: Every customer in the customers DataFrame is included in the output, even if they have no accounts (LEFT JOIN equivalent).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAccountSummaryBuilder.cs:44-61] The loop iterates all customers; `accountsByCustomer.GetValueOrDefault(customerId, (0, 0m, 0m))` provides zero defaults for customers with no accounts

BR-2: Account count is the total number of account rows per customer (all account types, all statuses).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAccountSummaryBuilder.cs:39] `current.count + 1` increments for every account row

BR-3: Total balance is the sum of current_balance across ALL accounts for the customer (regardless of status).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAccountSummaryBuilder.cs:39] `current.totalBalance + balance` adds every account's balance

BR-4: Active balance is the sum of current_balance only for accounts with account_status = "Active".
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAccountSummaryBuilder.cs:38] `var activeAdd = status == "Active" ? balance : 0m;`
- Evidence: [ExternalModules/CustomerAccountSummaryBuilder.cs:39] `current.activeBalance + activeAdd`

BR-5: Data is written in Overwrite mode -- only the most recent effective date's data persists.
- Confidence: HIGH
- Evidence: [customer_account_summary_v2.json:35] `"writeMode": "Overwrite"`
- Evidence: [curated.customer_account_summary_v2] Only 1 as_of value (2024-10-31) with 223 rows

BR-6: If either customers or accounts DataFrame is empty, an empty DataFrame is returned.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAccountSummaryBuilder.cs:20-24] `if (customers == null || customers.Count == 0 || accounts == null || accounts.Count == 0)` returns empty DataFrame

BR-7: The as_of value comes from the customers DataFrame row.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAccountSummaryBuilder.cs:60] `["as_of"] = custRow["as_of"]`

BR-8: Customer first_name and last_name default to empty string if null.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAccountSummaryBuilder.cs:47-48] `custRow["first_name"]?.ToString() ?? ""`

BR-9: The branches DataSourcing module is declared in the job config but NOT used by the External module.
- Confidence: HIGH
- Evidence: [customer_account_summary_v2.json:20-24] branches is sourced but [CustomerAccountSummaryBuilder.cs] never references `sharedState["branches"]`

BR-10: Compared to the original CustomerAccountSummary job, this V2 adds total_balance (all accounts) and active_balance (Active-status accounts only), whereas the original only had account_count and active_balance.
- Confidence: HIGH
- Evidence: [CustomerAccountSummaryBuilder.cs:11-14] output columns include `total_balance` and `active_balance`; compare with [customer_account_summary.json] in Strategy.md which shows the original uses SQL with `SUM(CASE WHEN a.account_status = 'Active' THEN a.current_balance ELSE 0 END)` as active_balance

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | customers.id | Cast to int | [CustomerAccountSummaryBuilder.cs:46,53] |
| first_name | customers.first_name | ToString, null coalesced to "" | [CustomerAccountSummaryBuilder.cs:47,55] |
| last_name | customers.last_name | ToString, null coalesced to "" | [CustomerAccountSummaryBuilder.cs:48,56] |
| account_count | Computed | Count of all account rows per customer | [CustomerAccountSummaryBuilder.cs:39,57] |
| total_balance | Computed | Sum of current_balance for all accounts | [CustomerAccountSummaryBuilder.cs:39,58] |
| active_balance | Computed | Sum of current_balance where account_status = 'Active' | [CustomerAccountSummaryBuilder.cs:38-39,59] |
| as_of | customers.as_of | Pass-through | [CustomerAccountSummaryBuilder.cs:60] |

## Edge Cases

- **NULL handling**: Customer names default to "". For customers with no accounts, account_count=0, total_balance=0, active_balance=0 (from GetValueOrDefault).
  - Evidence: [CustomerAccountSummaryBuilder.cs:50] `accountsByCustomer.GetValueOrDefault(customerId, (0, 0m, 0m))`
- **Weekend/date fallback**: Since customers and accounts both only have weekday data (23 dates), weekend runs produce empty DataFrames, returning an empty output. With Overwrite mode, the table is truncated and empty on weekends.
  - Evidence: [datalake.customers, datalake.accounts] Both have 23 distinct as_of dates (weekdays only)
- **Zero-row behavior**: Empty input (no customers OR no accounts) produces an empty DataFrame. Table is truncated via Overwrite.
  - Evidence: [CustomerAccountSummaryBuilder.cs:20-24] Early return on empty; note that BOTH must be empty for this path

## Traceability Matrix

| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [CustomerAccountSummaryBuilder.cs:44-61] |
| BR-2 | [CustomerAccountSummaryBuilder.cs:39] |
| BR-3 | [CustomerAccountSummaryBuilder.cs:39] |
| BR-4 | [CustomerAccountSummaryBuilder.cs:38-39] |
| BR-5 | [customer_account_summary_v2.json:35], [curated.customer_account_summary_v2 row counts] |
| BR-6 | [CustomerAccountSummaryBuilder.cs:20-24] |
| BR-7 | [CustomerAccountSummaryBuilder.cs:60] |
| BR-8 | [CustomerAccountSummaryBuilder.cs:47-48] |
| BR-9 | [customer_account_summary_v2.json:20-24], [CustomerAccountSummaryBuilder.cs full source] |
| BR-10 | [CustomerAccountSummaryBuilder.cs:11-14], [customer_account_summary.json in Strategy.md] |

## Open Questions

- **Branches sourced but unused**: Same pattern as CreditScoreAverage and CreditScoreSnapshot -- branches are sourced but never used. Confidence: HIGH that unused.
- **Empty accounts guard**: The guard `accounts.Count == 0` returns empty output even if customers exist. This means on a date with customers but no accounts, no output is produced (rather than showing customers with 0 accounts). This may be a design choice vs. a bug. Confidence: MEDIUM.
- **Difference from original CustomerAccountSummary**: The V2 adds total_balance and uses External module processing instead of SQL Transformation. The original also does not have the "empty accounts returns empty" guard -- it uses LEFT JOIN which would include customers with no accounts. Confidence: HIGH.
