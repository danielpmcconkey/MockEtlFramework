# AccountTypeDistribution -- Test Plan

## Test Cases

TC-1: Accounts grouped by type with correct counts -- Traces to BR-1
- Input conditions: 277 accounts for effective date 2024-10-31 (96 Checking, 94 Savings, 87 Credit)
- Expected output: 3 rows with correct account_count values
- Verification: Row-by-row comparison with curated.account_type_distribution

TC-2: Total accounts computed correctly -- Traces to BR-2
- Input conditions: 277 accounts
- Expected output: total_accounts = 277 for all rows
- Verification: SELECT DISTINCT total_accounts returns 277

TC-3: Percentage calculation matches original -- Traces to BR-3, BR-9
- Input conditions: Checking = 96 of 277 total
- Expected output: percentage = 34.66 (96/277*100 rounded to 2 decimal places)
- Verification: Compare percentage values against curated output; EXCEPT query returns zero rows

TC-4: as_of value matches effective date -- Traces to BR-4
- Input conditions: Effective date 2024-10-31
- Expected output: All rows have as_of = 2024-10-31
- Verification: SELECT DISTINCT as_of

TC-5: Output schema has 5 columns -- Traces to BR-5
- Input conditions: Any effective date
- Expected output: Columns: account_type, account_count, total_accounts, percentage, as_of
- Verification: Column comparison against curated schema

TC-6: Overwrite mode retains only latest date -- Traces to BR-6
- Input conditions: Run for multiple dates
- Expected output: Only last date remains
- Verification: SELECT DISTINCT as_of returns single date

TC-7: Weekend dates produce no output -- Traces to BR-7
- Input conditions: Effective date 2024-10-05 (Saturday)
- Expected output: Zero rows
- Verification: COUNT(*) returns 0

TC-8: All percentages sum to approximately 100 -- Traces to BR-3
- Input conditions: Any effective date with data
- Expected output: SUM(percentage) is approximately 100 (may differ slightly due to rounding)
- Verification: SUM(percentage) between 99.9 and 100.1

TC-9: Data values match original exactly -- Traces to all BRs
- Input conditions: Run for as_of = 2024-10-31
- Expected output: Identical to curated.account_type_distribution
- Verification: EXCEPT query returns zero rows both directions
