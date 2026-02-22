# BranchVisitLog -- Test Plan

## Test Cases

TC-1: Branch visits enriched with branch names -- Traces to BR-1
- Input conditions: Visit to branch_id 6 on 2024-10-01
- Expected output: branch_name = 'Austin TX Branch'
- Verification: Spot-check against curated.branch_visit_log

TC-2: Branch visits enriched with customer names -- Traces to BR-2
- Input conditions: Visit by customer_id 1006 on 2024-10-01
- Expected output: first_name = 'Ava', last_name = 'Garcia'
- Verification: Spot-check against curated.branch_visit_log

TC-3: Missing branch defaults to empty string -- Traces to BR-3
- Input conditions: Visit with branch_id not in branches table (theoretical)
- Expected output: branch_name = '' (empty string, not NULL)
- Verification: Check COALESCE behavior in SQL

TC-4: Missing customer defaults to NULL -- Traces to BR-4
- Input conditions: Visit with customer_id not in customers table (theoretical)
- Expected output: first_name = NULL, last_name = NULL
- Verification: Check LEFT JOIN NULL behavior

TC-5: Output schema has 9 columns -- Traces to BR-5
- Input conditions: Any weekday effective date
- Expected output: Columns: visit_id, customer_id, first_name, last_name, branch_id, branch_name, visit_timestamp, visit_purpose, as_of
- Verification: Column comparison against curated schema

TC-6: Append mode accumulates logs -- Traces to BR-6
- Input conditions: Run for multiple weekday dates
- Expected output: All processed dates present with correct row counts
- Verification: SELECT as_of, COUNT(*) GROUP BY as_of matches curated

TC-7: Weekend dates produce zero output -- Traces to BR-7, BR-8
- Input conditions: Effective date 2024-10-05 (Saturday) -- branch_visits has data but customers is empty
- Expected output: Zero rows for as_of = 2024-10-05
- Verification: COUNT(*) WHERE as_of = '2024-10-05' returns 0

TC-8: All visits included on weekdays -- Traces to BR-9
- Input conditions: 29 visits on 2024-10-01
- Expected output: 29 rows in output for as_of = 2024-10-01
- Verification: Row count matches curated.branch_visit_log for same date

TC-9: Data values match original exactly -- Traces to all BRs
- Input conditions: Run for as_of = 2024-10-01
- Expected output: Identical to curated.branch_visit_log for that date
- Verification: EXCEPT query returns zero rows both directions

TC-10: Full month comparison -- Traces to all BRs
- Input conditions: Run for all 31 days of October 2024
- Expected output: Identical data to curated.branch_visit_log (23 weekday dates, zero rows for 8 weekend dates)
- Verification: Full EXCEPT comparison across all as_of dates
