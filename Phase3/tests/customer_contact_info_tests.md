# CustomerContactInfo -- Test Plan

## Test Cases

TC-1: Phone and email records are combined via UNION ALL -- Traces to BR-1
- Input: 429 phone records + 321 email records for 2024-10-01
- Expected: 750 rows in output for as_of = 2024-10-01
- Verification: Row count comparison; EXCEPT query returns zero rows

TC-2: Phone records have contact_type = 'Phone' -- Traces to BR-2
- Input: Phone number records
- Expected: contact_type is 'Phone', contact_subtype is phone_type, contact_value is phone_number
- Verification: SELECT WHERE contact_type = 'Phone' and verify values match source

TC-3: Email records have contact_type = 'Email' -- Traces to BR-3
- Input: Email address records
- Expected: contact_type is 'Email', contact_subtype is email_type, contact_value is email_address
- Verification: SELECT WHERE contact_type = 'Email' and verify values match source

TC-4: Output is ordered by customer_id, contact_type, contact_subtype -- Traces to BR-4
- Input: Combined phone and email records
- Expected: Email records appear before Phone for each customer (alphabetical order of contact_type)
- Verification: Verify ordering in output

TC-5: Append mode accumulates rows across dates -- Traces to BR-5
- Input: Run for Oct 1 and Oct 2
- Expected: Both dates have rows in the table
- Verification: SELECT DISTINCT as_of shows both dates

TC-6: All records included without filtering -- Traces to BR-6
- Input: All phone and email records for a date
- Expected: No records are filtered out
- Verification: Total output rows = phone count + email count

TC-7: UNION ALL preserves duplicates -- Traces to BR-7
- Input: Records including any potential duplicates
- Expected: Duplicate rows are preserved (not deduplicated)
- Verification: UNION ALL used, not UNION

TC-8: Data values match curated output exactly -- Traces to BR-1 through BR-7
- Input: All dates Oct 1-31
- Expected: Exact match between curated and double_secret_curated per date
- Verification: EXCEPT-based comparison per as_of date
