# DailyTransactionSummary — Test Plan

## Test Cases

### TC-1: Row count matches account-date combinations
- **Traces to:** BR-1
- **Method:** Compare `SELECT COUNT(*) FROM double_secret_curated.daily_transaction_summary WHERE as_of = {date}` with `SELECT COUNT(DISTINCT account_id) FROM datalake.transactions WHERE as_of = {date}`
- **Expected:** Counts are equal

### TC-2: total_amount equals sum of all amounts per account
- **Traces to:** BR-2, BR-9
- **Method:** For account 3001 on 2024-10-01, verify total_amount = 642.50 (142.50 debit + 500.00 credit).
- **Expected:** total_amount = 642.50

### TC-3: transaction_count is correct
- **Traces to:** BR-3
- **Method:** For account 3001 on 2024-10-01, verify transaction_count = 2.
- **Expected:** transaction_count = 2

### TC-4: debit_total is correct
- **Traces to:** BR-4, BR-9
- **Method:** For account 3001 on 2024-10-01, verify debit_total = 142.50.
- **Expected:** debit_total = 142.50

### TC-5: credit_total is correct
- **Traces to:** BR-5, BR-9
- **Method:** For account 3001 on 2024-10-01, verify credit_total = 500.00.
- **Expected:** credit_total = 500.00

### TC-6: total_amount = debit_total + credit_total
- **Traces to:** BR-2, BR-4, BR-5
- **Method:** For all rows, verify `total_amount = debit_total + credit_total`.
- **Expected:** No exceptions (may have minor rounding differences of 0.01 due to ROUND)

### TC-7: Ordering correct
- **Traces to:** BR-6
- **Method:** Verify output is ordered by as_of ASC, account_id ASC.
- **Expected:** Ordered correctly

### TC-8: Append mode — all dates present
- **Traces to:** BR-7
- **Method:** After running for Oct 1-31, verify 31 distinct as_of values exist.
- **Expected:** 31 dates

### TC-9: Weekend dates have data
- **Traces to:** BR-8
- **Method:** Verify rows exist for as_of = 2024-10-05 and 2024-10-06.
- **Expected:** Rows present for weekend dates

### TC-10: Full EXCEPT comparison with original
- **Traces to:** All BRs
- **Method:** For each date: `SELECT * FROM curated.daily_transaction_summary WHERE as_of = {date} EXCEPT SELECT * FROM double_secret_curated.daily_transaction_summary WHERE as_of = {date}` and vice versa.
- **Expected:** Both EXCEPT queries return 0 rows
