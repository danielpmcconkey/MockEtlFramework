# HighBalanceAccounts -- Test Plan

## Test Cases

### TC-1: High-balance filter threshold (BR-1)
- **Objective**: Verify only accounts with current_balance > 10000 appear in output
- **Method**: Compare `SELECT COUNT(*) FROM double_secret_curated.high_balance_accounts WHERE current_balance <= 10000` -- must be 0
- **Method**: Compare row count with `SELECT COUNT(*) FROM datalake.accounts WHERE current_balance > 10000 AND as_of = '{date}'`

### TC-2: Customer name enrichment (BR-2)
- **Objective**: Verify each row has correct first_name and last_name from customers table
- **Method**: For a sample account, verify `first_name` and `last_name` match `datalake.customers` for the same customer_id and as_of

### TC-3: Missing customer defaults (BR-3)
- **Objective**: Verify accounts with no matching customer get empty strings for names
- **Method**: Check `SELECT COUNT(*) FROM double_secret_curated.high_balance_accounts WHERE first_name IS NULL OR last_name IS NULL` -- must be 0
- **Note**: In current data all accounts have customers, so this tests the COALESCE safety net

### TC-4: Overwrite mode (BR-4)
- **Objective**: Verify only the latest effective date's data exists after a run
- **Method**: After running for date X, verify `SELECT DISTINCT as_of FROM double_secret_curated.high_balance_accounts` returns only date X

### TC-5: No account type filter (BR-5)
- **Objective**: Verify all account types with high balance are included
- **Method**: Compare `SELECT DISTINCT account_type FROM double_secret_curated.high_balance_accounts` with direct datalake query for high-balance accounts

### TC-6: Exact match with original (all dates)
- **Objective**: Verify V2 output is identical to original for every date Oct 1-31
- **Method**: EXCEPT-based comparison:
  ```sql
  SELECT * FROM curated.high_balance_accounts WHERE as_of = '{date}'
  EXCEPT
  SELECT * FROM double_secret_curated.high_balance_accounts WHERE as_of = '{date}'
  ```
  Must return 0 rows in both directions.

### TC-7: Column schema match
- **Objective**: Verify output columns match original schema exactly
- **Method**: Compare column names and types between curated and double_secret_curated tables

### TC-8: Row count match per date
- **Objective**: Verify row count matches original for each effective date
- **Method**: Compare COUNT(*) between curated and double_secret_curated for each as_of date
