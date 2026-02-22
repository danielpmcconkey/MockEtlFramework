# ExecutiveDashboard — Business Requirements Document

## Overview

The ExecutiveDashboard job produces a set of 9 high-level aggregate metric rows (key business KPIs) for each effective date, including total customers, accounts, balances, transactions, loans, and branch visits. Output uses Overwrite mode, retaining only the most recent effective date's data.

## Source Tables

| Table | Alias in Config | Columns Sourced | Purpose |
|-------|----------------|-----------------|---------|
| `datalake.customers` | customers | id, first_name, last_name | Customer count |
| `datalake.accounts` | accounts | account_id, customer_id, account_type, account_status, current_balance | Account count and total balance |
| `datalake.transactions` | transactions | transaction_id, account_id, txn_type, amount | Transaction count and total/average amount |
| `datalake.loan_accounts` | loan_accounts | loan_id, customer_id, loan_type, current_balance | Loan count and total loan balance |
| `datalake.branch_visits` | branch_visits | visit_id, customer_id, branch_id, visit_purpose | Branch visit count |
| `datalake.branches` | branches | branch_id, branch_name, city, state_province | **NOT USED** — sourced but never referenced |
| `datalake.segments` | segments | segment_id, segment_name | **NOT USED** — sourced but never referenced |

- Join logic: No joins — the External module counts/sums each table independently.
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:15-19] Each DataFrame is accessed independently.

## Business Rules

BR-1: The output contains exactly 9 metric rows per effective date, each with a metric_name and metric_value.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:83-94] Builds exactly 9 metric tuples.
- Evidence: [curated.executive_dashboard] 9 rows for as_of = 2024-10-31.

BR-2: total_customers = count of rows in the customers DataFrame.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:38] `var totalCustomers = (decimal)customers.Count;`

BR-3: total_accounts = count of rows in the accounts DataFrame.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:41] `var totalAccounts = (decimal)accounts.Count;`

BR-4: total_balance = sum of all current_balance values from accounts.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:44-48] Iterates accounts, summing current_balance.
- Evidence: [curated.executive_dashboard] total_balance = 1064917.73 for 2024-10-31.

BR-5: total_transactions = count of rows in the transactions DataFrame.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:51-52] `totalTransactions = transactions.Count`

BR-6: total_txn_amount = sum of all transaction amounts.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:53-57] Iterates transactions, summing amount.

BR-7: avg_txn_amount = total_txn_amount / total_transactions (0 if no transactions).
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:63] `totalTransactions > 0 ? totalTxnAmount / totalTransactions : 0m`

BR-8: total_loans = count of rows in the loan_accounts DataFrame.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:66] `var totalLoans = (decimal)loanAccounts.Count;`

BR-9: total_loan_balance = sum of all current_balance values from loan_accounts.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:69-73] Iterates loan_accounts, summing current_balance.

BR-10: total_branch_visits = count of rows in the branch_visits DataFrame.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:76-80] `totalBranchVisits = branchVisits.Count`

BR-11: All metric values are rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:85-93] `Math.Round(..., 2)` applied to every metric value.

BR-12: The as_of value for all metric rows is taken from the first customer row, with fallback to the first transaction row if customer as_of is null.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:31-35] `asOf = customers.Rows[0]["as_of"]; if (asOf == null ... asOf = transactions.Rows[0]["as_of"])`

BR-13: If customers, accounts, OR loan_accounts are empty, an empty DataFrame is returned.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:22-28] Triple-condition empty guard checks all three.

BR-14: If transactions or branch_visits are empty/null, their metrics default to 0.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:50-53] `if (transactions != null)` guard with default 0m.
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:76-79] `if (branchVisits != null)` guard with default 0m.

BR-15: Output uses Overwrite write mode.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/executive_dashboard.json:63] `"writeMode": "Overwrite"`
- Evidence: [curated.executive_dashboard] Only has data for as_of = 2024-10-31.

## Output Schema

| Column | Type | Source | Transformation |
|--------|------|--------|---------------|
| metric_name | varchar(100) | Hardcoded strings | One of 9 predefined metric names |
| metric_value | numeric(14,2) | Calculated | Count or sum from source table, rounded to 2 dp |
| as_of | date | `customers.Rows[0]["as_of"]` | Passthrough from first customer row |

The 9 metrics in order:
1. total_customers
2. total_accounts
3. total_balance
4. total_transactions
5. total_txn_amount
6. avg_txn_amount
7. total_loans
8. total_loan_balance
9. total_branch_visits

## Edge Cases

- **Weekend dates:** The customers, accounts, and loan_accounts tables have no weekend data. The empty guard checks all three — if any is empty, the output is empty. So weekend dates produce no output. However, Overwrite mode means the previous day's data gets cleared. This means on a Monday, only Monday's data is visible (Saturday/Sunday runs would have cleared Friday's data if they ran).
- **Transactions on weekends:** Transactions and branch_visits have weekend data, but they can't be reported without customers/accounts/loan_accounts.
- **avg_txn_amount division by zero:** Protected by `totalTransactions > 0` guard.
- **Null as_of fallback:** The as_of fallback to transactions is defensive but unlikely to trigger (customers always has as_of when rows exist).
- **Metric precision:** Count metrics (total_customers, total_accounts, etc.) are stored as decimal with Round(2) but will always have .00 since they are counts.

## Anti-Patterns Identified

- **AP-1: Redundant Data Sourcing** — Two tables are sourced but never used: (1) `branches` [JobExecutor/Jobs/executive_dashboard.json:41-46] with columns branch_id, branch_name, city, state_province; (2) `segments` [JobExecutor/Jobs/executive_dashboard.json:48-52] with columns segment_id, segment_name. The External module never accesses `sharedState["branches"]` or `sharedState["segments"]`. V2 approach: Remove both DataSourcing modules.

- **AP-3: Unnecessary External Module** — The logic is entirely simple counting and summing: COUNT(*) and SUM(column) across separate tables. SQL can express this via UNION ALL of individual aggregate queries, each producing a (metric_name, metric_value, as_of) row. V2 approach: Replace with a SQL Transformation that UNIONs individual aggregate queries.

- **AP-4: Unused Columns Sourced** — Multiple sourced columns are never used: from customers: `first_name`, `last_name` (only count matters); from accounts: `customer_id`, `account_type`, `account_status` (only count and balance used); from transactions: `transaction_id`, `account_id`, `txn_type` (only count and amount used); from loan_accounts: `customer_id`, `loan_type` (only count and balance used); from branch_visits: `visit_id`, `customer_id`, `branch_id`, `visit_purpose` (only count used). V2 approach: Source only columns actually needed.

- **AP-6: Row-by-Row Iteration in External Module** — Three foreach loops iterate over accounts [line 46], transactions [line 55], and loan_accounts [line 71] to sum values. These are simple SUM aggregations that SQL handles natively. V2 approach: SQL SUM/COUNT aggregations.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [ExternalModules/ExecutiveDashboardBuilder.cs:83-94], curated 9 rows |
| BR-2 | [ExternalModules/ExecutiveDashboardBuilder.cs:38] |
| BR-3 | [ExternalModules/ExecutiveDashboardBuilder.cs:41] |
| BR-4 | [ExternalModules/ExecutiveDashboardBuilder.cs:44-48], curated total_balance |
| BR-5 | [ExternalModules/ExecutiveDashboardBuilder.cs:51-52] |
| BR-6 | [ExternalModules/ExecutiveDashboardBuilder.cs:53-57] |
| BR-7 | [ExternalModules/ExecutiveDashboardBuilder.cs:63] |
| BR-8 | [ExternalModules/ExecutiveDashboardBuilder.cs:66] |
| BR-9 | [ExternalModules/ExecutiveDashboardBuilder.cs:69-73] |
| BR-10 | [ExternalModules/ExecutiveDashboardBuilder.cs:76-80] |
| BR-11 | [ExternalModules/ExecutiveDashboardBuilder.cs:85-93] |
| BR-12 | [ExternalModules/ExecutiveDashboardBuilder.cs:31-35] |
| BR-13 | [ExternalModules/ExecutiveDashboardBuilder.cs:22-28] |
| BR-14 | [ExternalModules/ExecutiveDashboardBuilder.cs:50-53,76-79] |
| BR-15 | [JobExecutor/Jobs/executive_dashboard.json:63], curated single date |

## Open Questions

- **Overwrite + weekend behavior:** If the framework runs on a Saturday and Sunday with Overwrite mode, it would first clear Friday's data (Sat run writes empty), then clear Saturday's empty result (Sun run writes empty). By Monday, there would be no data. The framework's gap-fill mechanism would run all dates sequentially, so the last run (Oct 31) is what persists. This is correct for Overwrite mode but means intermediate dates are not preserved.
  - Confidence: HIGH — this is understood framework behavior, not an ambiguity.

- **Metric name as identifier:** The metric_name strings are the primary identifiers in this table. If downstream consumers query by metric_name, the exact spelling matters. The 9 names are hardcoded in the External module.
  - Confidence: HIGH — the names are deterministic.
