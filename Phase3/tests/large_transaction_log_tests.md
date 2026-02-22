# LargeTransactionLog -- Test Plan

## Test Cases

### TC-1: Large transaction filter threshold (BR-1)
- **Objective**: Verify only transactions with amount > 500 appear in output
- **Method**: `SELECT COUNT(*) FROM double_secret_curated.large_transaction_log WHERE amount <= 500` -- must be 0
- **Method**: Compare row count per date with direct datalake query `SELECT COUNT(*) FROM datalake.transactions WHERE amount > 500 AND as_of = '{date}'`

### TC-2: Customer name enrichment via two-step join (BR-2)
- **Objective**: Verify customer names are correct via account_id -> customer_id -> customer lookup
- **Method**: For sample transaction_ids, verify first_name/last_name match the expected customer via the account chain

### TC-3: Missing account default (BR-3)
- **Objective**: Verify transactions with no matching account get customer_id = 0
- **Method**: Check that any transaction with no account match has customer_id = 0 (not NULL)
- **Note**: In current data all transactions have matching accounts

### TC-4: Missing customer default (BR-4)
- **Objective**: Verify transactions with no matching customer get empty string names
- **Method**: `SELECT COUNT(*) FROM double_secret_curated.large_transaction_log WHERE first_name IS NULL OR last_name IS NULL` -- must be 0

### TC-5: Append mode accumulation (BR-5)
- **Objective**: Verify rows accumulate across effective dates
- **Method**: After running for dates Oct 1-2, verify both dates exist in output: `SELECT DISTINCT as_of FROM double_secret_curated.large_transaction_log` should include both

### TC-6: Exact match with original (all dates)
- **Objective**: Verify V2 output is identical to original for every date Oct 1-31
- **Method**: EXCEPT-based comparison per date:
  ```sql
  SELECT * FROM curated.large_transaction_log WHERE as_of = '{date}'
  EXCEPT
  SELECT * FROM double_secret_curated.large_transaction_log WHERE as_of = '{date}'
  ```
  Must return 0 rows in both directions.

### TC-7: Column schema match
- **Objective**: Verify output columns match original schema exactly
- **Method**: Compare column names and types between curated and double_secret_curated tables

### TC-8: NULL description pass-through
- **Objective**: Verify NULL description values are preserved (not converted to empty string)
- **Method**: Check that description NULL count matches between original and V2

### TC-9: Row count match per date
- **Objective**: Verify row count matches original for each effective date
- **Method**: Compare COUNT(*) grouped by as_of between curated and double_secret_curated
