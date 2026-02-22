# CustomerValueScore — Test Plan

## Test Cases

### TC-1: Row count matches customer count
- **Traces to:** BR-1
- **Method:** Compare `SELECT COUNT(*) FROM double_secret_curated.customer_value_score WHERE as_of = {date}` with `SELECT COUNT(*) FROM datalake.customers WHERE as_of = {date}`
- **Expected:** 223 on weekdays, 0 on weekends

### TC-2: transaction_score calculation
- **Traces to:** BR-2
- **Method:** For customer 1001 (1 transaction on 2024-10-31), verify transaction_score = MIN(1*10, 1000) = 10.00.
- **Expected:** transaction_score = 10.00

### TC-3: balance_score calculation
- **Traces to:** BR-3
- **Method:** For customer 1001 (total_balance = 2362.32), verify balance_score = MIN(2362.32/1000, 1000) = ROUND(2.36232, 2) = 2.36.
- **Expected:** balance_score = 2.36

### TC-4: visit_score calculation
- **Traces to:** BR-4
- **Method:** For customer 1001 (1 visit), verify visit_score = MIN(1*50, 1000) = 50.00.
- **Expected:** visit_score = 50.00

### TC-5: composite_score weighted sum
- **Traces to:** BR-5
- **Method:** For customer 1001: composite = 10*0.4 + 2.36*0.35 + 50*0.25 = 4 + 0.826 + 12.5 = 17.326 -> ROUND to 17.33.
- **Expected:** composite_score = 17.33

### TC-6: Score rounding
- **Traces to:** BR-6
- **Method:** Verify all score columns have at most 2 decimal places.
- **Expected:** No values with more than 2 decimal places

### TC-7: Customer with no transactions or visits
- **Traces to:** BR-7
- **Method:** Find a customer with no transactions and no visits. Verify transaction_score=0, visit_score=0.
- **Expected:** Zero scores for components with no data

### TC-8: Negative balance_score
- **Traces to:** BR-10
- **Method:** Verify customer 1003 (negative total balance of -758.40) has balance_score = ROUND(-758.40/1000, 2) = -0.76.
- **Expected:** balance_score = -0.76

### TC-9: Score cap at 1000
- **Traces to:** BR-2, BR-3, BR-4
- **Method:** Verify no score exceeds 1000 (theoretical max composite = 1000).
- **Expected:** All component scores <= 1000

### TC-10: Weekend produces empty output
- **Traces to:** BR-8
- **Method:** Verify 0 rows for weekend dates.
- **Expected:** 0 rows

### TC-11: Overwrite mode
- **Traces to:** BR-9
- **Method:** After running for multiple dates, verify only one as_of value exists.
- **Expected:** Single as_of date

### TC-12: Full EXCEPT comparison with original
- **Traces to:** All BRs
- **Method:** For each date: `SELECT * FROM curated.customer_value_score WHERE as_of = {date} EXCEPT SELECT * FROM double_secret_curated.customer_value_score WHERE as_of = {date}` and vice versa.
- **Expected:** Both EXCEPT queries return 0 rows
