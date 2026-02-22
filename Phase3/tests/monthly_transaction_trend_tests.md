# MonthlyTransactionTrend -- Test Plan

## Test Cases

### TC-1: Column mapping from upstream (BR-1, AP-2 fix)
- **Objective**: Verify daily_transactions = total_transactions, daily_amount = total_amount, avg_transaction_amount = avg_amount from curated.daily_transaction_volume
- **Method**: Join double_secret_curated.monthly_transaction_trend with curated.daily_transaction_volume on as_of and verify values match

### TC-2: One row per date (BR-2)
- **Objective**: Verify output has exactly one row per effective date
- **Method**: `SELECT as_of, COUNT(*) FROM double_secret_curated.monthly_transaction_trend GROUP BY as_of HAVING COUNT(*) > 1` -- must return 0 rows

### TC-3: Rounding precision (BR-3)
- **Objective**: Verify daily_amount and avg_transaction_amount are rounded to 2 decimal places
- **Method**: Compare values with curated output; upstream already handles rounding

### TC-4: All transaction types included (BR-4)
- **Objective**: Verify totals include both Credit and Debit transactions
- **Method**: For sample dates, verify daily_transactions count matches total from datalake.transactions (all types)

### TC-5: Append mode (BR-5)
- **Objective**: Verify rows accumulate across dates
- **Method**: After running for multiple dates, verify all dates exist in output

### TC-6: Ordering (BR-6)
- **Objective**: Verify results are ordered by as_of
- **Method**: Visual inspection of output ordering

### TC-7: Exact match with original (all dates)
- **Objective**: Verify V2 output is identical to original for every date Oct 1-31
- **Method**: EXCEPT-based comparison per date:
  ```sql
  SELECT * FROM curated.monthly_transaction_trend WHERE as_of = '{date}'
  EXCEPT
  SELECT * FROM double_secret_curated.monthly_transaction_trend WHERE as_of = '{date}'
  ```
  Must return 0 rows in both directions.

### TC-8: Column schema match
- **Objective**: Verify output columns match original schema exactly
- **Method**: Compare column names and types between curated and double_secret_curated tables

### TC-9: AP-2 validation -- no raw datalake access
- **Objective**: Verify V2 reads from curated.daily_transaction_volume, not datalake.transactions
- **Method**: Inspect V2 job config to confirm DataSourcing uses schema "curated" and table "daily_transaction_volume"

### TC-10: Upstream dependency
- **Objective**: Verify DailyTransactionVolume runs before MonthlyTransactionTrendV2
- **Method**: Confirm SameDay dependency is declared in control.job_dependencies
