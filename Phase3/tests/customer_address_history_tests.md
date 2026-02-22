# CustomerAddressHistory -- Test Plan

## Test Cases

TC-1: Non-null customer_id addresses are included -- Traces to BR-1
- Input: datalake.addresses for 2024-10-01 (223 rows, all non-null customer_id)
- Expected: 223 rows in output for as_of = 2024-10-01
- Verification: Row count comparison; EXCEPT query returns zero rows

TC-2: Output columns match expected schema -- Traces to BR-2
- Input: Any date with address data
- Expected: Columns are customer_id, address_line1, city, state_province, postal_code, country, as_of
- Verification: Compare column names between curated and double_secret_curated

TC-3: Output is ordered by customer_id ascending -- Traces to BR-3
- Input: Any date with multiple addresses
- Expected: customer_id values are in ascending order
- Verification: Query output and verify monotonically increasing customer_id

TC-4: Append mode accumulates rows across dates -- Traces to BR-4
- Input: Run for Oct 1 and Oct 2
- Expected: Both dates have rows in the table
- Verification: SELECT DISTINCT as_of shows both dates

TC-5: Data values match curated output exactly -- Traces to BR-1, BR-2
- Input: All dates Oct 1-31
- Expected: Exact match between curated and double_secret_curated per date
- Verification: EXCEPT-based comparison per as_of date

TC-6: Weekend data is included -- Traces to BR-6
- Input: Oct 5-6 (weekend dates)
- Expected: Rows exist for Oct 5 and Oct 6
- Verification: Count rows for weekend dates
