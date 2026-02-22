# BranchVisitPurposeBreakdown -- Test Plan

## Test Cases

TC-1: Visits grouped by branch, purpose, and date -- Traces to BR-1
- Input conditions: Branch 7 (Denver CO Branch) has visits with 4 different purposes on 2024-10-01
- Expected output: 4 rows for branch_id 7 on 2024-10-01, each with visit_count = 1
- Verification: Row-by-row comparison with curated.branch_visit_purpose_breakdown

TC-2: Branch names included via join -- Traces to BR-2
- Input conditions: Branch_id 7 on 2024-10-01
- Expected output: branch_name = 'Denver CO Branch'
- Verification: Spot-check against curated output

TC-3: Unused total_branch_visits not in output -- Traces to BR-3
- Input conditions: Any effective date
- Expected output: No total_branch_visits column in output
- Verification: Column list check against curated schema

TC-4: Output schema has 5 columns -- Traces to BR-4
- Input conditions: Any effective date
- Expected output: Columns: branch_id, branch_name, visit_purpose, as_of, visit_count
- Verification: Column comparison against curated schema

TC-5: Results ordered correctly -- Traces to BR-5
- Input conditions: Any effective date
- Expected output: Rows ordered by as_of, branch_id, visit_purpose
- Verification: Verify ordering matches curated output

TC-6: Append mode accumulates data -- Traces to BR-6
- Input conditions: Run for multiple dates
- Expected output: All dates present with correct row counts
- Verification: SELECT as_of, COUNT(*) GROUP BY as_of matches curated

TC-7: Only branches with visits appear -- Traces to BR-7
- Input conditions: Some branches have no visits on a given date
- Expected output: Those branches absent from output
- Verification: INNER JOIN behavior confirmed by comparing branch_ids in output vs all branch_ids

TC-8: Weekend dates produce output -- Edge case
- Input conditions: Effective date 2024-10-05 (Saturday) -- both branch_visits and branches have weekend data
- Expected output: Rows present for as_of = 2024-10-05
- Verification: COUNT(*) WHERE as_of = '2024-10-05' > 0

TC-9: Data values match original exactly -- Traces to all BRs
- Input conditions: Run for as_of = 2024-10-01
- Expected output: Identical to curated.branch_visit_purpose_breakdown for that date
- Verification: EXCEPT query returns zero rows both directions

TC-10: Full month comparison -- Traces to all BRs
- Input conditions: Run for all 31 days of October 2024
- Expected output: Identical data to curated.branch_visit_purpose_breakdown across all dates
- Verification: Full EXCEPT comparison across all as_of dates
