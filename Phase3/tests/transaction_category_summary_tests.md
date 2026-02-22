# TransactionCategorySummary -- Test Plan

## Test Cases

### TC-1: Grouping by txn_type and as_of (BR-1)
- **Objective**: Verify output has one row per txn_type per as_of
- **Method**: `SELECT as_of, COUNT(*) FROM double_secret_curated.transaction_category_summary GROUP BY as_of` -- each date should have exactly 2 rows (Credit, Debit)

### TC-2: Aggregate computation (BR-2)
- **Objective**: Verify total_amount, transaction_count, and avg_amount are correct
- **Method**: For sample dates, compute directly from datalake.transactions and compare:
  ```sql
  SELECT txn_type, ROUND(SUM(amount), 2), COUNT(*), ROUND(AVG(amount), 2)
  FROM datalake.transactions WHERE as_of = '{date}' GROUP BY txn_type
  ```

### TC-3: All transaction types included (BR-3)
- **Objective**: Verify both Credit and Debit types appear
- **Method**: `SELECT DISTINCT txn_type FROM double_secret_curated.transaction_category_summary` should return Credit and Debit

### TC-4: Ordering (BR-4)
- **Objective**: Verify rows are ordered by as_of, then txn_type
- **Method**: Visual inspection of output ordering

### TC-5: Append mode accumulation (BR-5)
- **Objective**: Verify rows accumulate across dates
- **Method**: After running for multiple dates, verify all dates are present in output

### TC-6: Rounding precision (BR-2 detail)
- **Objective**: Verify ROUND to 2 decimal places matches original
- **Method**: Compare total_amount and avg_amount values between curated and double_secret_curated

### TC-7: Exact match with original (all dates)
- **Objective**: Verify V2 output is identical to original for every date Oct 1-31
- **Method**: EXCEPT-based comparison per date:
  ```sql
  SELECT * FROM curated.transaction_category_summary WHERE as_of = '{date}'
  EXCEPT
  SELECT * FROM double_secret_curated.transaction_category_summary WHERE as_of = '{date}'
  ```
  Must return 0 rows in both directions.

### TC-8: Column schema match
- **Objective**: Verify output columns match original schema exactly
- **Method**: Compare column names and types between curated and double_secret_curated tables

### TC-9: Simplified SQL produces identical results
- **Objective**: Verify removal of CTE/window functions does not change output
- **Method**: The EXCEPT comparison (TC-7) validates this; the simplified SQL must produce byte-identical results
