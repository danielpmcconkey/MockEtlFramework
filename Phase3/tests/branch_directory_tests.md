# BranchDirectory -- Test Plan

## Test Cases

TC-1: All branches included in output -- Traces to BR-1
- Input conditions: 40 branches in datalake.branches for effective date 2024-10-31
- Expected output: 40 rows in double_secret_curated.branch_directory
- Verification: Row count matches curated.branch_directory

TC-2: No duplicate branch_ids in output -- Traces to BR-2
- Input conditions: 40 unique branch_ids
- Expected output: 40 unique branch_ids (no duplicates)
- Verification: SELECT COUNT(DISTINCT branch_id) = COUNT(*)

TC-3: Output schema has 8 columns -- Traces to BR-3
- Input conditions: Any effective date
- Expected output: Columns: branch_id, branch_name, address_line1, city, state_province, postal_code, country, as_of
- Verification: Column comparison against curated schema

TC-4: Results ordered by branch_id ascending -- Traces to BR-4
- Input conditions: Any effective date
- Expected output: branch_id values are in ascending order
- Verification: Verify ordering in output matches curated

TC-5: Overwrite mode retains only latest date -- Traces to BR-5
- Input conditions: Run for multiple dates
- Expected output: Only last date remains
- Verification: SELECT DISTINCT as_of returns single date

TC-6: Weekend dates produce output -- Edge case
- Input conditions: Effective date 2024-10-05 (Saturday) -- branches data exists on weekends
- Expected output: 40 rows (branches has weekend data)
- Verification: COUNT(*) WHERE as_of = '2024-10-05' returns 40

TC-7: Data values match original exactly -- Traces to all BRs
- Input conditions: Run for as_of = 2024-10-31
- Expected output: Identical to curated.branch_directory
- Verification: EXCEPT query returns zero rows both directions
