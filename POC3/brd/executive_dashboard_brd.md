# ExecutiveDashboard — Business Requirements Document

## Overview
Produces a set of 9 key business metrics (total customers, accounts, balances, transactions, loans, branch visits) as a vertical metric-name/metric-value table. Output is written to CSV with a SUMMARY trailer including row count, date, and timestamp.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/executive_dashboard.csv`
- **includeHeader**: true
- **trailerFormat**: `SUMMARY|{row_count}|{date}|{timestamp}`
- **writeMode**: Overwrite
- **lineEnding**: LF

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.transactions | transaction_id, account_id, txn_type, amount | Effective date range (injected) | [executive_dashboard.json:8-10] |
| datalake.accounts | account_id, customer_id, account_type, account_status, current_balance | Effective date range (injected) | [executive_dashboard.json:13-17] |
| datalake.customers | id, first_name, last_name | Effective date range (injected) | [executive_dashboard.json:20-22] |
| datalake.loan_accounts | loan_id, customer_id, loan_type, current_balance | Effective date range (injected) | [executive_dashboard.json:25-28] |
| datalake.branch_visits | visit_id, customer_id, branch_id, visit_purpose | Effective date range (injected) | [executive_dashboard.json:31-34] |
| datalake.branches | branch_id, branch_name, city, state_province | Effective date range (injected) | [executive_dashboard.json:37-40] |
| datalake.segments | segment_id, segment_name | Effective date range (injected) | [executive_dashboard.json:43-45] |

## Business Rules

BR-1: The guard clause requires customers, accounts, AND loan_accounts to all be non-null and non-empty. If any is missing, an empty DataFrame is produced. Transactions and branch_visits being empty does NOT trigger the guard.
- Confidence: HIGH
- Evidence: [ExecutiveDashboardBuilder.cs:22-28] — explicit null/empty check on customers, accounts, loanAccounts only

BR-2: The branches and segments DataFrames are sourced but NOT used by the External module.
- Confidence: HIGH
- Evidence: [ExecutiveDashboardBuilder.cs:8-109] — no reference to "branches" or "segments"

BR-3: The as_of value is taken from the first customer row. If customer as_of is null, falls back to the first transaction row's as_of.
- Confidence: HIGH
- Evidence: [ExecutiveDashboardBuilder.cs:31-35]

BR-4: Nine metrics are produced in a fixed order, each as a separate row with metric_name, metric_value, and as_of columns.
- Confidence: HIGH
- Evidence: [ExecutiveDashboardBuilder.cs:83-94] — metrics list with 9 entries

BR-5: All metric values are rounded to 2 decimal places using Math.Round (default MidpointRounding.ToEven / banker's rounding).
- Confidence: HIGH
- Evidence: [ExecutiveDashboardBuilder.cs:86-94] — `Math.Round(value, 2)` on every metric

BR-6: total_customers and total_accounts are simple row counts cast to decimal (not distinct counts).
- Confidence: HIGH
- Evidence: [ExecutiveDashboardBuilder.cs:38-40] — `(decimal)customers.Count` and `(decimal)accounts.Count`

BR-7: total_balance sums ALL account current_balance values (not filtered by status or type).
- Confidence: HIGH
- Evidence: [ExecutiveDashboardBuilder.cs:43-47] — iterates all accounts.Rows

BR-8: avg_txn_amount is total_txn_amount / total_transactions. If there are no transactions, avg_txn_amount is 0.
- Confidence: HIGH
- Evidence: [ExecutiveDashboardBuilder.cs:63] — ternary with > 0 check

BR-9: total_branch_visits counts all branch visit rows. If branchVisits is null, defaults to 0.
- Confidence: HIGH
- Evidence: [ExecutiveDashboardBuilder.cs:76-80]

BR-10: The trailer format includes a timestamp token ({timestamp}), making it non-deterministic — the trailer line will differ on every run even with the same data.
- Confidence: HIGH
- Evidence: [executive_dashboard.json:64] — `SUMMARY|{row_count}|{date}|{timestamp}`; [Architecture.md:241] — {timestamp} = UTC now

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| metric_name | Fixed strings | One of 9 metric names | [ExecutiveDashboardBuilder.cs:86-94] |
| metric_value | Computed | Rounded to 2 decimal places | [ExecutiveDashboardBuilder.cs:86-94] |
| as_of | customers.Rows[0].as_of | Fallback to transactions.Rows[0].as_of | [ExecutiveDashboardBuilder.cs:31-35] |

### Metric Names and Computations

| Metric Name | Computation | Evidence |
|-------------|-------------|----------|
| total_customers | COUNT of customer rows | [ExecutiveDashboardBuilder.cs:38] |
| total_accounts | COUNT of account rows | [ExecutiveDashboardBuilder.cs:40] |
| total_balance | SUM of accounts.current_balance | [ExecutiveDashboardBuilder.cs:43-47] |
| total_transactions | COUNT of transaction rows | [ExecutiveDashboardBuilder.cs:53] |
| total_txn_amount | SUM of transactions.amount | [ExecutiveDashboardBuilder.cs:55-58] |
| avg_txn_amount | total_txn_amount / total_transactions (0 if no txns) | [ExecutiveDashboardBuilder.cs:63] |
| total_loans | COUNT of loan_accounts rows | [ExecutiveDashboardBuilder.cs:66] |
| total_loan_balance | SUM of loan_accounts.current_balance | [ExecutiveDashboardBuilder.cs:69-73] |
| total_branch_visits | COUNT of branch_visits rows (0 if null) | [ExecutiveDashboardBuilder.cs:76-80] |

## Non-Deterministic Fields
- **Trailer line**: The `{timestamp}` token in the trailer format produces a UTC timestamp that changes with every execution, even for the same effective date.

## Write Mode Implications
- **Overwrite mode**: Each run replaces the CSV. Only the latest effective date's metrics persist.
- Multi-day gap-fill: intermediate days are lost.

## Edge Cases
- **Weekend dates**: Customers, accounts, and loan_accounts are weekday-only. On a weekend, the guard clause triggers (empty customers/accounts/loans), producing an empty output that overwrites any previous data.
- **Transactions on weekends**: Even though transactions exist on weekends, the guard clause checks customers/accounts/loans first, so weekend runs still produce empty output.
- **Branch visits on weekends**: Branch visits exist on weekends but are irrelevant if the guard clause fails.
- **All values are row counts, not distinct counts**: total_customers is customers.Count, not COUNT(DISTINCT id). In a single-day effective date range this is fine; in multi-day ranges, counts would include duplicates across dates.
- **account_type, account_status, txn_type sourced but unused**: Several columns from the config are not referenced in the metrics.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Guard clause on customers + accounts + loans | ExecutiveDashboardBuilder.cs:22-28 |
| 9 fixed metrics | ExecutiveDashboardBuilder.cs:83-94 |
| Branches and segments unused | ExecutiveDashboardBuilder.cs (no reference) |
| Banker's rounding to 2 decimals | ExecutiveDashboardBuilder.cs:86-94 |
| SUMMARY trailer with timestamp | executive_dashboard.json:64 |
| Overwrite write mode | executive_dashboard.json:65 |
| First effective date 2024-10-01 | executive_dashboard.json:3 |

## Open Questions
1. Why are branches and segments sourced if they are never used? (Confidence: LOW)
2. The {timestamp} in the trailer makes file comparison non-deterministic across runs. Is this intentional? (Confidence: MEDIUM)
3. Should counts be distinct? In a multi-day range, total_customers would count the same customer once per day. (Confidence: MEDIUM — depends on effective date range behavior)
