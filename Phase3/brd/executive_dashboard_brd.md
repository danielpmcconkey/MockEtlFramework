# BRD: ExecutiveDashboard

## Overview
This job produces a daily executive dashboard of 9 key performance indicator (KPI) metrics summarizing the state of customers, accounts, transactions, loans, and branch visits for a single effective date. The output is a set of metric name/value pairs written to `curated.executive_dashboard` in Overwrite mode.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| transactions | datalake | transaction_id, account_id, txn_type, amount | No filter beyond effective date; iterated for count and sum | [JobExecutor/Jobs/executive_dashboard.json:7-11] DataSourcing config; [ExternalModules/ExecutiveDashboardBuilder.cs:52-59] iteration |
| accounts | datalake | account_id, customer_id, account_type, account_status, current_balance | No filter; iterated for count and balance sum | [executive_dashboard.json:13-18] DataSourcing config; [ExecutiveDashboardBuilder.cs:42-48] iteration |
| customers | datalake | id, first_name, last_name | No filter; counted | [executive_dashboard.json:20-24] DataSourcing config; [ExecutiveDashboardBuilder.cs:38] count |
| loan_accounts | datalake | loan_id, customer_id, loan_type, current_balance | No filter; counted and balance summed | [executive_dashboard.json:26-31] DataSourcing config; [ExecutiveDashboardBuilder.cs:66-73] iteration |
| branch_visits | datalake | visit_id, customer_id, branch_id, visit_purpose | No filter; counted | [executive_dashboard.json:33-38] DataSourcing config; [ExecutiveDashboardBuilder.cs:76-80] count |
| branches | datalake | branch_id, branch_name, city, state_province | Sourced but NOT used in External module logic | [executive_dashboard.json:40-45] DataSourcing config; Not referenced in ExecutiveDashboardBuilder.cs |
| segments | datalake | segment_id, segment_name | Sourced but NOT used in External module logic | [executive_dashboard.json:47-51] DataSourcing config; Not referenced in ExecutiveDashboardBuilder.cs |

## Business Rules

BR-1: The job produces exactly 9 metrics per effective date: total_customers, total_accounts, total_balance, total_transactions, total_txn_amount, avg_txn_amount, total_loans, total_loan_balance, total_branch_visits.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:83-94] Explicit metric list construction
- Evidence: [curated.executive_dashboard] `SELECT count(*) FROM curated.executive_dashboard WHERE as_of = '2024-10-31'` yields 9

BR-2: total_customers is the count of all customer rows for the effective date.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:38] `var totalCustomers = (decimal)customers.Count;`
- Evidence: [curated.executive_dashboard] metric_value = 223.00 matches datalake.customers count of 223 for 2024-10-31

BR-3: total_accounts is the count of all account rows for the effective date.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:41] `var totalAccounts = (decimal)accounts.Count;`
- Evidence: [curated.executive_dashboard] metric_value = 277.00 matches datalake.accounts count of 277

BR-4: total_balance is the sum of current_balance across all accounts for the effective date, rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:44-48] Iterates accounts, sums current_balance via Convert.ToDecimal
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:87] `Math.Round(totalBalance, 2)`

BR-5: total_transactions is the count of all transaction rows for the effective date.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:52-55] `totalTransactions = transactions.Count`

BR-6: total_txn_amount is the sum of all transaction amounts for the effective date, rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:56-59] Iterates transactions, sums amount via Convert.ToDecimal
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:89] `Math.Round(totalTxnAmount, 2)`

BR-7: avg_txn_amount is total_txn_amount divided by total_transactions, rounded to 2 decimal places. If there are no transactions, avg_txn_amount is 0.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:63] `var avgTxnAmount = totalTransactions > 0 ? totalTxnAmount / totalTransactions : 0m;`
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:90] `Math.Round(avgTxnAmount, 2)`

BR-8: total_loans is the count of all loan_accounts rows for the effective date.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:66] `var totalLoans = (decimal)loanAccounts.Count;`

BR-9: total_loan_balance is the sum of current_balance across all loan accounts for the effective date, rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:69-73] Iterates loan_accounts, sums current_balance

BR-10: total_branch_visits is the count of all branch_visit rows for the effective date.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:76-80] `totalBranchVisits = branchVisits.Count`

BR-11: All metric values are rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:85-93] Every metric wrapped in `Math.Round(..., 2)`

BR-12: The as_of value in the output is taken from the first row of the customers DataFrame. If customers is empty or as_of is null, falls back to the first row of transactions.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:31-35] `asOf = customers.Rows[0]["as_of"]` with fallback to transactions

BR-13: Output is written in Overwrite mode, meaning each run truncates the entire target table before writing.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/executive_dashboard.json:63] `"writeMode": "Overwrite"`
- Evidence: [curated.executive_dashboard] Only one as_of (2024-10-31) present, consistent with last-run-wins Overwrite behavior

BR-14: If customers, accounts, or loan_accounts DataFrames are null or empty, the job produces an empty output DataFrame (zero rows).
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs:22-28] Null/empty check returns empty DataFrame

BR-15: The branches and segments DataFrames are sourced by the job config but are NOT used by the External module.
- Confidence: HIGH
- Evidence: [ExternalModules/ExecutiveDashboardBuilder.cs] No references to `branches` or `segments` variables; they are not retrieved from sharedState

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| metric_name | Hardcoded string | One of 9 metric names | [ExecutiveDashboardBuilder.cs:85-93] |
| metric_value | Computed decimal | Rounded to 2 decimal places | [ExecutiveDashboardBuilder.cs:85-93] |
| as_of | customers.Rows[0]["as_of"] | Pass-through from first customer row (fallback to transactions) | [ExecutiveDashboardBuilder.cs:31-35] |

## Edge Cases
- **NULL handling**: Transactions and branch_visits may be null/empty without causing failure; the corresponding metrics are set to 0. However, if customers, accounts, or loan_accounts are null/empty, the entire output is empty (zero rows).
- **Weekend/date fallback**: On weekends, accounts/customers/loan_accounts may not have data (weekday-only pattern observed in datalake), which would trigger the empty-output guard (BR-14). Transactions and branch_visits have data on weekends.
- **Zero-row behavior**: Empty output DataFrame is valid and written (no rows inserted after truncate).
- **Unused DataFrames**: branches and segments are loaded but unused — this is a potential inefficiency but does not affect correctness.

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [ExecutiveDashboardBuilder.cs:83-94], [curated.executive_dashboard row count] |
| BR-2 | [ExecutiveDashboardBuilder.cs:38], [curated data verification] |
| BR-3 | [ExecutiveDashboardBuilder.cs:41], [curated data verification] |
| BR-4 | [ExecutiveDashboardBuilder.cs:44-48, 87] |
| BR-5 | [ExecutiveDashboardBuilder.cs:52-55] |
| BR-6 | [ExecutiveDashboardBuilder.cs:56-59, 89] |
| BR-7 | [ExecutiveDashboardBuilder.cs:63, 90] |
| BR-8 | [ExecutiveDashboardBuilder.cs:66] |
| BR-9 | [ExecutiveDashboardBuilder.cs:69-73] |
| BR-10 | [ExecutiveDashboardBuilder.cs:76-80] |
| BR-11 | [ExecutiveDashboardBuilder.cs:85-93] |
| BR-12 | [ExecutiveDashboardBuilder.cs:31-35] |
| BR-13 | [executive_dashboard.json:63], [curated data observation] |
| BR-14 | [ExecutiveDashboardBuilder.cs:22-28] |
| BR-15 | [ExecutiveDashboardBuilder.cs], [executive_dashboard.json:40-51] |

## Open Questions
- The branches and segments DataFrames are loaded but not used. This could be intentional (reserved for future use) or a configuration oversight. Confidence: MEDIUM that this is an oversight — no impact on output.
- The as_of fallback from customers to transactions (BR-12) is unusual. If both are empty, as_of could be null for all metric rows. This is mitigated by BR-14 (customers empty = no output). Confidence: HIGH that this edge case is handled.
