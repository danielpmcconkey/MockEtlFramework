# CustomerTransactionActivity — Test Plan

## Test Cases

### TC-1: Row count — only customers with transactions
- **Traces to:** BR-1
- **Method:** Compare V2 row count with original for each date. For 2024-10-01, expect 196 rows.
- **Expected:** Row counts match original

### TC-2: Customer attribution via account mapping
- **Traces to:** BR-2
- **Method:** For customer 1001 on 2024-10-01, verify transaction_count=2, total_amount=642.50. Customer 1001 owns account 3001 which has 2 transactions (142.50 + 500.00).
- **Expected:** Matches original output

### TC-3: Orphan transactions excluded
- **Traces to:** BR-3
- **Method:** Verify no output rows reference customer_ids derived from account_ids not present in the accounts table.
- **Expected:** All customer_ids in output correspond to valid account ownership

### TC-4: Debit and credit counts correct
- **Traces to:** BR-6, BR-7
- **Method:** For customer 1001 on 2024-10-01, verify debit_count=1, credit_count=1.
- **Expected:** Counts match original

### TC-5: Weekend dates produce no output
- **Traces to:** BR-9
- **Method:** Verify 0 rows for as_of = 2024-10-05 (Saturday). Accounts has no weekend data so JOIN returns nothing.
- **Expected:** 0 rows for weekend dates

### TC-6: Append mode — weekday dates present
- **Traces to:** BR-11
- **Method:** After running Oct 1-31, verify 23 distinct as_of values (weekdays only).
- **Expected:** 23 dates

### TC-7: total_amount matches sum of individual transaction amounts
- **Traces to:** BR-5
- **Method:** For a sample customer/date, verify total_amount equals the sum of all transaction amounts attributed to that customer.
- **Expected:** Exact match

### TC-8: transaction_count = debit_count + credit_count
- **Traces to:** BR-4, BR-6, BR-7
- **Method:** For all rows, verify `transaction_count = debit_count + credit_count`.
- **Expected:** True for all rows (all transactions are either Debit or Credit)

### TC-9: Full EXCEPT comparison with original
- **Traces to:** All BRs
- **Method:** For each date: `SELECT * FROM curated.customer_transaction_activity WHERE as_of = {date} EXCEPT SELECT * FROM double_secret_curated.customer_transaction_activity WHERE as_of = {date}` and vice versa.
- **Expected:** Both EXCEPT queries return 0 rows
