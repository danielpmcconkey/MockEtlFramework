# Test Plan: ExecutiveDashboardV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Verify output contains exactly 9 metric rows per effective date | 9 rows with distinct metric_name values |
| TC-2 | BR-2 | Verify total_customers equals count of customers for the effective date | Metric value matches datalake.customers count |
| TC-3 | BR-3 | Verify total_accounts equals count of accounts for the effective date | Metric value matches datalake.accounts count |
| TC-4 | BR-4 | Verify total_balance equals rounded sum of current_balance across accounts | Metric value matches SUM(current_balance) rounded to 2 decimals |
| TC-5 | BR-5 | Verify total_transactions equals count of transactions for the effective date | Metric value matches datalake.transactions count |
| TC-6 | BR-6 | Verify total_txn_amount equals rounded sum of transaction amounts | Metric value matches ROUND(SUM(amount), 2) |
| TC-7 | BR-7 | Verify avg_txn_amount equals total_txn_amount / total_transactions, rounded | Metric value matches ROUND(SUM(amount)/COUNT(*), 2) |
| TC-8 | BR-8 | Verify total_loans equals count of loan_accounts for the effective date | Metric value matches datalake.loan_accounts count |
| TC-9 | BR-9 | Verify total_loan_balance equals rounded sum of loan current_balance | Metric value matches ROUND(SUM(current_balance), 2) |
| TC-10 | BR-10 | Verify total_branch_visits equals count of branch_visits for the effective date | Metric value matches datalake.branch_visits count |
| TC-11 | BR-11 | Verify all metric_values have at most 2 decimal places | All values rounded to 2 decimals |
| TC-12 | BR-12 | Verify as_of comes from first customer row | as_of matches effective date |
| TC-13 | BR-13 | Verify Overwrite mode: only latest effective date data present | Only one as_of in output table after run |
| TC-14 | BR-14 | Verify empty output when customers/accounts/loan_accounts are empty | 0 rows on weekend effective dates |
| TC-15 | BR-15 | Verify branches and segments are loaded but not used | Job succeeds; output unaffected by branches/segments data |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | Weekend effective date (no customers/accounts/loans data) | Empty output (0 rows), no error |
| EC-2 | Zero transactions for effective date | total_transactions=0, total_txn_amount=0, avg_txn_amount=0 |
| EC-3 | Zero branch visits for effective date | total_branch_visits=0 |
| EC-4 | Comparison with curated.executive_dashboard for same date | Row-for-row match on metric_name, metric_value, as_of |
