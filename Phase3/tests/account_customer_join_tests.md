# AccountCustomerJoin -- Test Plan

## Test Cases

TC-1: All accounts joined with customer names -- Traces to BR-1, BR-3
- Input conditions: 277 accounts and 200 customers for effective date 2024-10-31
- Expected output: 277 rows with customer first_name and last_name populated
- Verification: Row count matches curated.account_customer_join; EXCEPT comparison returns zero rows

TC-2: Missing customer defaults to empty strings -- Traces to BR-2
- Input conditions: If any account has a customer_id not in customers table
- Expected output: first_name and last_name are empty strings (not NULL)
- Verification: Check that no NULL first_name/last_name exist in output; verify with COALESCE behavior

TC-3: Output schema has 8 columns -- Traces to BR-4
- Input conditions: Any effective date
- Expected output: Columns: account_id, customer_id, first_name, last_name, account_type, account_status, current_balance, as_of
- Verification: Column comparison against curated schema

TC-4: Overwrite mode retains only latest date -- Traces to BR-5
- Input conditions: Run for multiple dates
- Expected output: Only the last processed date's data remains
- Verification: SELECT DISTINCT as_of returns single date

TC-5: Weekend dates produce empty output -- Traces to BR-6
- Input conditions: Effective date 2024-10-05 (Saturday)
- Expected output: Zero rows (accounts empty on weekends)
- Verification: COUNT(*) WHERE as_of = '2024-10-05' returns 0

TC-6: Data values match original exactly -- Traces to BR-1 through BR-5
- Input conditions: Run for as_of = 2024-10-31
- Expected output: Every row matches curated.account_customer_join
- Verification: EXCEPT query between curated and double_secret_curated returns zero rows both directions

TC-7: Customer name values correct -- Traces to BR-1
- Input conditions: Run for as_of = 2024-10-31
- Expected output: account_id 3024 has first_name = 'Ellie', last_name = 'Nguyen' (customer_id 1024)
- Verification: Spot-check specific rows against curated output
