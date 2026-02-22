# AccountBalanceSnapshot -- Test Plan

## Test Cases

TC-1: All accounts included in output -- Traces to BR-1
- Input conditions: datalake.accounts has 277 rows for effective date 2024-10-01
- Expected output: 277 rows in double_secret_curated.account_balance_snapshot for as_of = 2024-10-01
- Verification: Compare row count between curated and double_secret_curated for same as_of date

TC-2: Output schema matches 6 columns -- Traces to BR-2
- Input conditions: Any effective date with account data
- Expected output: Columns are exactly account_id, customer_id, account_type, account_status, current_balance, as_of
- Verification: EXCEPT-based comparison between curated and double_secret_curated tables

TC-3: Append mode accumulates snapshots -- Traces to BR-3
- Input conditions: Run job for multiple effective dates (2024-10-01 through 2024-10-04)
- Expected output: All 4 dates present in output with correct row counts per date
- Verification: SELECT DISTINCT as_of, COUNT(*) GROUP BY as_of should show all processed dates

TC-4: Weekend dates produce no output -- Traces to BR-4
- Input conditions: Effective date 2024-10-05 (Saturday)
- Expected output: Zero rows for as_of = 2024-10-05
- Verification: COUNT(*) WHERE as_of = '2024-10-05' returns 0

TC-5: Empty accounts produces zero rows -- Traces to BR-5
- Input conditions: Effective date with no accounts data (e.g., weekend)
- Expected output: Zero rows written
- Verification: No rows in output for that date

TC-6: Data values match original exactly -- Traces to BR-1, BR-2
- Input conditions: Run for as_of = 2024-10-01
- Expected output: Every row matches curated.account_balance_snapshot for same date
- Verification: EXCEPT query between curated and double_secret_curated returns zero rows in both directions

TC-7: Full month comparison -- Traces to all BRs
- Input conditions: Run for all 31 days of October 2024
- Expected output: Identical data to curated.account_balance_snapshot across all dates
- Verification: Full EXCEPT comparison across all as_of dates
