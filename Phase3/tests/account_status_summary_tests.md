# AccountStatusSummary -- Test Plan

## Test Cases

TC-1: Accounts grouped by type and status with correct counts -- Traces to BR-1
- Input conditions: 277 accounts for effective date 2024-10-31 (96 Checking/Active, 94 Savings/Active, 87 Credit/Active)
- Expected output: 3 rows with account_count summing to 277
- Verification: Row-by-row comparison with curated.account_status_summary

TC-2: as_of value matches effective date -- Traces to BR-2
- Input conditions: Effective date 2024-10-31
- Expected output: All rows have as_of = 2024-10-31
- Verification: SELECT DISTINCT as_of returns single value matching effective date

TC-3: All accounts included in count -- Traces to BR-3
- Input conditions: 277 accounts
- Expected output: SUM(account_count) = 277
- Verification: SELECT SUM(account_count) FROM output

TC-4: Output schema has 4 columns -- Traces to BR-4
- Input conditions: Any effective date
- Expected output: Columns: account_type, account_status, account_count, as_of
- Verification: Column comparison against curated schema

TC-5: Overwrite mode retains only latest date -- Traces to BR-5
- Input conditions: Run for multiple dates
- Expected output: Only last date remains
- Verification: SELECT DISTINCT as_of returns single date

TC-6: Weekend dates produce no output -- Traces to BR-6
- Input conditions: Effective date 2024-10-05 (Saturday)
- Expected output: Zero rows
- Verification: COUNT(*) returns 0

TC-7: Data values match original exactly -- Traces to BR-1 through BR-5
- Input conditions: Run for as_of = 2024-10-31
- Expected output: Identical to curated.account_status_summary
- Verification: EXCEPT query returns zero rows both directions
