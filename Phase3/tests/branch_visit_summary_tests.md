# BranchVisitSummary -- Test Plan

## Test Cases

TC-1: Visits counted per branch per day -- Traces to BR-1
- Input conditions: Branch 7 (Denver CO Branch) has 4 visits on 2024-10-01
- Expected output: visit_count = 4 for branch_id 7 on 2024-10-01
- Verification: Spot-check against curated.branch_visit_summary

TC-2: Branch names included via join -- Traces to BR-2
- Input conditions: Branch_id 7 on 2024-10-01
- Expected output: branch_name = 'Denver CO Branch'
- Verification: Spot-check against curated output

TC-3: Output schema has 4 columns -- Traces to BR-3
- Input conditions: Any effective date
- Expected output: Columns: branch_id, branch_name, as_of, visit_count
- Verification: Column comparison against curated schema

TC-4: Results ordered correctly -- Traces to BR-4
- Input conditions: Any effective date
- Expected output: Rows ordered by as_of, then branch_id
- Verification: Verify ordering matches curated output

TC-5: Append mode accumulates data -- Traces to BR-5
- Input conditions: Run for multiple dates
- Expected output: All dates present with correct row counts
- Verification: SELECT as_of, COUNT(*) GROUP BY as_of matches curated

TC-6: Only branches with visits appear -- Traces to BR-6
- Input conditions: Some branches have no visits on a given date
- Expected output: Those branches absent from output
- Verification: INNER JOIN behavior confirmed

TC-7: Weekend dates produce output -- Edge case
- Input conditions: Effective date 2024-10-05 (Saturday) -- both branch_visits and branches have weekend data
- Expected output: Rows present for as_of = 2024-10-05
- Verification: COUNT(*) WHERE as_of = '2024-10-05' > 0

TC-8: Visit counts sum correctly -- Traces to BR-1
- Input conditions: 2024-10-01 has 29 total branch visits
- Expected output: SUM(visit_count) = 29 across all branches for that date
- Verification: SUM(visit_count) matches total visit rows in datalake.branch_visits for same date

TC-9: Data values match original exactly -- Traces to all BRs
- Input conditions: Run for as_of = 2024-10-01
- Expected output: Identical to curated.branch_visit_summary for that date
- Verification: EXCEPT query returns zero rows both directions

TC-10: Full month comparison -- Traces to all BRs
- Input conditions: Run for all 31 days of October 2024
- Expected output: Identical data to curated.branch_visit_summary across all 31 dates
- Verification: Full EXCEPT comparison across all as_of dates
