# CoveredTransactions -- Test Plan

## Test Cases

TC-1: Only Checking account transactions included -- Traces to BR-1
- Input: Transactions linked to various account types
- Expected: Output only contains rows where account_type = 'Checking'
- Verification: SELECT DISTINCT account_type FROM double_secret_curated.covered_transactions yields only 'Checking'

TC-2: Accounts resolved via snapshot fallback -- Traces to BR-2
- Input: Effective date with account data
- Expected: Account details match the most recent snapshot <= effective date
- Verification: Compare account details against DISTINCT ON query on datalake.accounts

TC-3: Customers resolved via snapshot fallback -- Traces to BR-3
- Input: Effective date with customer data
- Expected: Customer demographics match the most recent snapshot <= effective date
- Verification: Compare customer fields against DISTINCT ON query on datalake.customers

TC-4: Only active US addresses included -- Traces to BR-4
- Input: Addresses with various countries and end_dates
- Expected: Only country = 'US' with no end_date or end_date >= effective date
- Verification: SELECT DISTINCT country FROM output yields only 'US'

TC-5: Earliest active US address per customer -- Traces to BR-5
- Input: Customer with multiple active US addresses
- Expected: Address with earliest start_date is used
- Verification: Compare address_id against expected earliest address

TC-6: Customer segment is first alphabetically -- Traces to BR-6
- Input: Customer with multiple segments
- Expected: Segment with alphabetically first segment_code
- Verification: Compare customer_segment values against sorted segment query

TC-7: Output sorted by customer_id ASC, transaction_id DESC -- Traces to BR-7
- Input: Multiple transactions for multiple customers
- Expected: Rows ordered by customer_id ascending, then transaction_id descending within each customer
- Verification: Check ordering in output

TC-8: record_count reflects total output rows -- Traces to BR-8
- Input: 85 qualifying transactions for 2024-10-01
- Expected: record_count = 85 on all rows
- Verification: All rows have same record_count matching total row count

TC-9: Zero-row case emits null-row sentinel -- Traces to BR-9
- Input: A date with no qualifying transactions
- Expected: Single row with NULLs except as_of and record_count = 0
- Verification: Check for null-row sentinel on dates with no output

TC-10: String fields trimmed, dates formatted -- Traces to BR-10
- Input: Data with various string and date fields
- Expected: No leading/trailing whitespace; dates formatted correctly
- Verification: Check for trimmed strings and formatted dates in output

TC-11: Append mode accumulates daily rows -- Traces to BR-11
- Input: Run for Oct 1 through Oct 5
- Expected: All dates have rows in the table
- Verification: SELECT DISTINCT as_of shows all dates with data

TC-12: Data values match curated output exactly -- Traces to all BRs
- Input: All dates Oct 1-31
- Expected: Exact match between curated and double_secret_curated per date
- Verification: EXCEPT-based comparison per as_of date

TC-13: Customer not found produces NULL demographics -- Traces to edge case
- Input: Transaction linked to account whose customer has no customer record
- Expected: name_prefix, first_name, last_name, sort_name, name_suffix are NULL
- Verification: Check for NULL demographic fields

TC-14: Customer without segment produces NULL segment -- Traces to edge case
- Input: Customer with no segment mapping
- Expected: customer_segment is NULL
- Verification: Check for NULL customer_segment values
