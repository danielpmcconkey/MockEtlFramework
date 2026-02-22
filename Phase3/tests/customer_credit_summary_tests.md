# Test Plan: CustomerCreditSummaryV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Verify one output row per customer | Row count in double_secret_curated.customer_credit_summary matches customer count for the effective date |
| TC-2 | BR-2 | Verify avg_credit_score computation | For a sample customer with known scores (e.g., 843, 836, 850), avg should be 843.0 |
| TC-3 | BR-3 | Verify NULL avg_credit_score for customer with no scores | If any customer has no credit_scores rows, their avg_credit_score should be NULL |
| TC-4 | BR-4 | Verify total_loan_balance computation | Sum of loan_accounts.current_balance for a customer matches output total_loan_balance |
| TC-5 | BR-5 | Verify zero loan defaults | Customers with no loans show total_loan_balance=0, loan_count=0 |
| TC-6 | BR-6 | Verify total_account_balance computation | Sum of accounts.current_balance for a customer matches output total_account_balance |
| TC-7 | BR-7 | Verify zero account defaults | Customers with no accounts show total_account_balance=0, account_count=0 |
| TC-8 | BR-8 | Verify segments not used | Output does not contain segment-related columns |
| TC-9 | BR-9 | Verify empty input guard | When any required DataFrame is null/empty, output is empty DataFrame |
| TC-10 | BR-10 | Verify Overwrite mode | Only latest effective date's data exists after job run |
| TC-11 | BR-11 | Verify as_of carried from customers | as_of in output matches the effective date |
| TC-12 | BR-12 | Verify negative balances included | Customers with negative account balances appear correctly in output |
| TC-13 | BR-13 | Verify all account types included | No filtering on account_type or account_status |
| TC-14 | BR-1,10 | Compare V2 output to original | EXCEPT query between curated and double_secret_curated tables yields zero rows |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | Zero rows in any required input | Empty output DataFrame with correct columns |
| EC-2 | Customer with negative account balance | total_account_balance reflects negative value |
| EC-3 | Customer with no credit scores | avg_credit_score is NULL |
| EC-4 | Customer with no loans | total_loan_balance=0, loan_count=0 |
| EC-5 | Customer with no accounts | total_account_balance=0, account_count=0 |
| EC-6 | NULL first_name or last_name | Coalesced to empty string |
| EC-7 | Weekend effective date | Empty output (customers table has no weekend data) |
