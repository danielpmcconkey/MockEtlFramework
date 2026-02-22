# ExecutiveDashboard — Test Plan

## Test Cases

### TC-1: Exactly 9 metric rows per date
- **Traces to:** BR-1
- **Method:** Verify `SELECT COUNT(*) FROM double_secret_curated.executive_dashboard` returns 9 for weekday dates.
- **Expected:** 9 rows

### TC-2: total_customers metric
- **Traces to:** BR-2
- **Method:** Verify metric_value for 'total_customers' matches `SELECT COUNT(*) FROM datalake.customers WHERE as_of = {date}`.
- **Expected:** 223.00 on 2024-10-31

### TC-3: total_accounts metric
- **Traces to:** BR-3
- **Method:** Verify metric_value for 'total_accounts' matches account count.
- **Expected:** 277.00 on 2024-10-31

### TC-4: total_balance metric
- **Traces to:** BR-4
- **Method:** Verify metric_value for 'total_balance' matches `SELECT SUM(current_balance) FROM datalake.accounts WHERE as_of = {date}`.
- **Expected:** 1064917.73 on 2024-10-31

### TC-5: total_transactions metric
- **Traces to:** BR-5
- **Method:** Verify metric_value for 'total_transactions' matches transaction count.
- **Expected:** 400.00 on 2024-10-31

### TC-6: total_txn_amount metric
- **Traces to:** BR-6
- **Method:** Verify metric_value for 'total_txn_amount' matches `SELECT SUM(amount) FROM datalake.transactions WHERE as_of = {date}`.
- **Expected:** 365391.00 on 2024-10-31

### TC-7: avg_txn_amount metric
- **Traces to:** BR-7
- **Method:** Verify metric_value for 'avg_txn_amount' = total_txn_amount / total_transactions.
- **Expected:** 913.48 on 2024-10-31 (365391.00 / 400 = 913.4775, rounded to 913.48)

### TC-8: total_loans metric
- **Traces to:** BR-8
- **Method:** Verify metric_value for 'total_loans' matches loan_accounts count.
- **Expected:** 90.00 on 2024-10-31

### TC-9: total_loan_balance metric
- **Traces to:** BR-9
- **Method:** Verify metric_value for 'total_loan_balance' matches `SELECT SUM(current_balance) FROM datalake.loan_accounts WHERE as_of = {date}`.
- **Expected:** 12069052.90 on 2024-10-31

### TC-10: total_branch_visits metric
- **Traces to:** BR-10
- **Method:** Verify metric_value for 'total_branch_visits' matches branch_visits count.
- **Expected:** 27.00 on 2024-10-31

### TC-11: All metrics rounded to 2 decimal places
- **Traces to:** BR-11
- **Method:** Verify all metric_value values have at most 2 decimal places.
- **Expected:** All values end in .00 or .XX

### TC-12: Weekend produces empty output
- **Traces to:** BR-13
- **Method:** Run for 2024-10-05 (Saturday). Customers/accounts/loan_accounts are empty.
- **Expected:** 0 rows

### TC-13: Overwrite mode
- **Traces to:** BR-15
- **Method:** After running for multiple dates, verify only one as_of value exists.
- **Expected:** Single as_of date

### TC-14: Full EXCEPT comparison with original
- **Traces to:** All BRs
- **Method:** For each date: `SELECT * FROM curated.executive_dashboard WHERE as_of = {date} EXCEPT SELECT * FROM double_secret_curated.executive_dashboard WHERE as_of = {date}` and vice versa.
- **Expected:** Both EXCEPT queries return 0 rows
