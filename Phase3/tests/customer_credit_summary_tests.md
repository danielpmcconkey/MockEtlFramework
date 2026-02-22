# CustomerCreditSummary -- Test Plan

## Test Cases

TC-1: One output row per customer in customers table -- Traces to BR-1
- Input: 223 customers for 2024-10-31
- Expected: 223 rows in output
- Verification: Row count comparison; EXCEPT query returns zero rows

TC-2: avg_credit_score is average of all bureau scores -- Traces to BR-2
- Input: Customer 1001 with scores 850, 836, 850
- Expected: avg_credit_score = average of those values
- Verification: Compare specific customer's avg_credit_score

TC-3: avg_credit_score is NULL for customers with no credit scores -- Traces to BR-2
- Input: Customer with no credit_scores records (if any)
- Expected: avg_credit_score is NULL
- Verification: Check for NULL avg_credit_score values

TC-4: total_loan_balance is sum of loan balances -- Traces to BR-3
- Input: Customer with loan accounts
- Expected: total_loan_balance equals sum of all loan current_balance values
- Verification: Compare against manual calculation from datalake.loan_accounts

TC-5: loan_count defaults to 0 for customers without loans -- Traces to BR-4
- Input: Customer with no loan accounts
- Expected: loan_count = 0, total_loan_balance = 0
- Verification: Check customers with zero loans in output

TC-6: total_account_balance is sum of account balances -- Traces to BR-5
- Input: Customer with multiple accounts
- Expected: total_account_balance equals sum of all account current_balance values
- Verification: Compare against manual calculation from datalake.accounts

TC-7: account_count defaults to 0 for customers without accounts -- Traces to BR-6
- Input: Customer with no accounts (if any)
- Expected: account_count = 0, total_account_balance = 0
- Verification: Check customers with zero accounts in output

TC-8: Empty output when any input DataFrame is empty -- Traces to BR-7
- Input: Weekend date where all tables are empty
- Expected: Zero rows in output after Overwrite
- Verification: Count rows after weekend run

TC-9: Overwrite mode replaces previous data -- Traces to BR-9
- Input: Run for Oct 30, then Oct 31
- Expected: Only Oct 31 data remains
- Verification: Single distinct as_of after second run

TC-10: All accounts included regardless of type/status -- Traces to BR-10
- Input: Accounts with various types and statuses
- Expected: All contribute to balance/count
- Verification: Count matches total accounts per customer in datalake

TC-11: Data values match curated output exactly -- Traces to all BRs
- Input: All dates Oct 1-31
- Expected: Exact match between curated and double_secret_curated per date
- Verification: EXCEPT-based comparison
