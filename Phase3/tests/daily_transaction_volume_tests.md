# DailyTransactionVolume — Test Plan

## Test Cases

### TC-1: One row per date
- **Traces to:** BR-1
- **Method:** Verify `SELECT COUNT(*) FROM double_secret_curated.daily_transaction_volume WHERE as_of = {date}` returns 1 for each date.
- **Expected:** Exactly 1 row per date

### TC-2: total_transactions matches sum of account-level counts
- **Traces to:** BR-2
- **Method:** For 2024-10-01, verify total_transactions = 405 (sum of all account transaction_count values from daily_transaction_summary).
- **Expected:** total_transactions = 405

### TC-3: total_amount matches sum of account-level amounts
- **Traces to:** BR-3
- **Method:** For 2024-10-01, verify total_amount = 362968.14.
- **Expected:** total_amount = 362968.14

### TC-4: avg_amount = total_amount / total_transactions
- **Traces to:** BR-4
- **Method:** For all dates, verify `ROUND(total_amount / total_transactions, 2) = avg_amount`.
- **Expected:** Match for all dates (verified: 362968.14/405 = 896.22)

### TC-5: Append mode — all dates present
- **Traces to:** BR-5
- **Method:** After running Oct 1-31, verify 31 distinct as_of values.
- **Expected:** 31 dates

### TC-6: Weekend dates have data
- **Traces to:** BR-6
- **Method:** Verify rows exist for 2024-10-05 and 2024-10-06.
- **Expected:** Rows present

### TC-7: Values match raw transaction aggregation
- **Traces to:** BR-7
- **Method:** For each date, compare with `SELECT COUNT(*), ROUND(SUM(amount),2), ROUND(AVG(amount),2) FROM datalake.transactions WHERE as_of={date}`.
- **Expected:** All three metrics match

### TC-8: Full EXCEPT comparison with original
- **Traces to:** All BRs
- **Method:** For each date: `SELECT * FROM curated.daily_transaction_volume WHERE as_of = {date} EXCEPT SELECT * FROM double_secret_curated.daily_transaction_volume WHERE as_of = {date}` and vice versa.
- **Expected:** Both EXCEPT queries return 0 rows
