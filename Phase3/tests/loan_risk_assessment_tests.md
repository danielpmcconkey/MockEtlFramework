# LoanRiskAssessment -- Test Plan

## Test Cases

### TC-1: Average credit score computation (BR-1)
- **Objective**: Verify avg_credit_score equals the average of all bureau scores for each customer
- **Method**: For sample loan_ids, compute AVG(score) from datalake.credit_scores and compare with double_secret_curated.loan_risk_assessment.avg_credit_score

### TC-2: Risk tier assignment (BR-2)
- **Objective**: Verify risk tiers are assigned correctly based on thresholds
- **Method**: Check that all rows with avg_credit_score >= 750 have risk_tier = 'Low Risk', >= 650 have 'Medium Risk', >= 550 have 'High Risk', < 550 have 'Very High Risk'
- **SQL**: `SELECT COUNT(*) FROM double_secret_curated.loan_risk_assessment WHERE avg_credit_score >= 750 AND risk_tier <> 'Low Risk'` -- must be 0 (repeat for each tier)

### TC-3: Missing credit scores handling (BR-3)
- **Objective**: Verify customers with no credit scores get NULL avg_credit_score and 'Unknown' risk_tier
- **Method**: Check for any Unknown-tier loans and verify their customer_id has no credit_scores rows
- **Note**: In current data all loan customers have credit scores, so 0 Unknown rows expected

### TC-4: All loans included (BR-4)
- **Objective**: Verify all loan accounts appear in output without filtering
- **Method**: Compare row count per date between double_secret_curated.loan_risk_assessment and datalake.loan_accounts

### TC-5: Overwrite mode (BR-5)
- **Objective**: Verify only latest effective date's data exists
- **Method**: After running for date X, verify `SELECT DISTINCT as_of FROM double_secret_curated.loan_risk_assessment` returns only date X

### TC-6: Exact match with original (all dates)
- **Objective**: Verify V2 output is identical to original for every date Oct 1-31
- **Method**: EXCEPT-based comparison:
  ```sql
  SELECT * FROM curated.loan_risk_assessment WHERE as_of = '{date}'
  EXCEPT
  SELECT * FROM double_secret_curated.loan_risk_assessment WHERE as_of = '{date}'
  ```
  Must return 0 rows in both directions.

### TC-7: Column schema match
- **Objective**: Verify output columns match original schema exactly
- **Method**: Compare column names and types between curated and double_secret_curated tables

### TC-8: Boundary value test for risk tiers (BR-2 edge cases)
- **Objective**: Verify threshold boundaries are inclusive on the correct side
- **Method**: Verify a customer with avg_credit_score of exactly 750.00 is 'Low Risk', 649.99 is 'High Risk', etc.
- **Note**: Depends on actual data values; verify with curated output

### TC-9: Rounding precision (BR-1 detail)
- **Objective**: Verify ROUND(AVG(score), 2) produces same precision as C# decimal.Average()
- **Method**: Compare avg_credit_score values between curated and double_secret_curated for all loans
