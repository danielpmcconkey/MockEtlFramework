# LoanPortfolioSnapshot -- Test Plan

## Test Cases

### TC-1: All loans included (BR-1)
- **Objective**: Verify all loan account records appear in output without filtering
- **Method**: Compare row count: `SELECT COUNT(*) FROM double_secret_curated.loan_portfolio_snapshot WHERE as_of = '{date}'` vs `SELECT COUNT(*) FROM datalake.loan_accounts WHERE as_of = '{date}'` -- must match

### TC-2: Excluded columns (BR-2)
- **Objective**: Verify origination_date and maturity_date are not in output
- **Method**: Check column list of double_secret_curated.loan_portfolio_snapshot -- must not contain origination_date or maturity_date

### TC-3: Pass-through values (BR-3)
- **Objective**: Verify all included columns match source values exactly
- **Method**: For sample loan_ids, compare each column value between datalake.loan_accounts and double_secret_curated.loan_portfolio_snapshot

### TC-4: Overwrite mode (BR-4)
- **Objective**: Verify only latest effective date's data exists
- **Method**: After running for date X, verify `SELECT DISTINCT as_of FROM double_secret_curated.loan_portfolio_snapshot` returns only date X

### TC-5: All loan statuses included (BR-1 edge case)
- **Objective**: Verify Active, Delinquent, and Paid Off loans are all present
- **Method**: `SELECT DISTINCT loan_status FROM double_secret_curated.loan_portfolio_snapshot` should match `SELECT DISTINCT loan_status FROM datalake.loan_accounts WHERE as_of = '{date}'`

### TC-6: Exact match with original (all dates)
- **Objective**: Verify V2 output is identical to original for every date Oct 1-31
- **Method**: EXCEPT-based comparison:
  ```sql
  SELECT * FROM curated.loan_portfolio_snapshot WHERE as_of = '{date}'
  EXCEPT
  SELECT * FROM double_secret_curated.loan_portfolio_snapshot WHERE as_of = '{date}'
  ```
  Must return 0 rows in both directions.

### TC-7: Column schema match
- **Objective**: Verify output columns match original schema exactly
- **Method**: Compare column names and types between curated and double_secret_curated tables
