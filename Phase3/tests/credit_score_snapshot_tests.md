# CreditScoreSnapshot -- Test Plan

## Test Cases

TC-1: All credit score records for a given date are passed through -- Traces to BR-1
- Input: datalake.credit_scores has 669 rows for 2024-10-31
- Expected: double_secret_curated.credit_score_snapshot has 669 rows for as_of = 2024-10-31
- Verification: Row count comparison per date; EXCEPT query returns zero rows

TC-2: Output columns match expected schema -- Traces to BR-2
- Input: Any date with credit score data
- Expected: Output columns are credit_score_id, customer_id, bureau, score, as_of
- Verification: Compare column names and types between curated and double_secret_curated

TC-3: Empty input produces empty output -- Traces to BR-3
- Input: A date with no credit score data (e.g., weekend dates if applicable)
- Expected: Zero rows in output for that date
- Verification: Count rows WHERE as_of = weekend_date

TC-4: Overwrite mode replaces previous data -- Traces to BR-4
- Input: Run for date A, then run for date B
- Expected: Only date B data remains in the table
- Verification: After second run, only one distinct as_of value exists

TC-5: Data values are identical to source -- Traces to BR-1, BR-2
- Input: Any date with credit score data
- Expected: Every column value matches the source exactly
- Verification: EXCEPT-based comparison between curated.credit_score_snapshot and double_secret_curated.credit_score_snapshot
