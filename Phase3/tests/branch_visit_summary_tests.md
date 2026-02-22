# Test Plan: BranchVisitSummaryV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Visit counts grouped by (branch_id, as_of) | Correct total visit_count per branch per date |
| TC-2 | BR-2 | branch_name joined via INNER JOIN on branch_id AND as_of | Correct branch_name for each row |
| TC-3 | BR-3 | Visits to non-existent branch_ids dropped by INNER JOIN | Only branches in branches table appear |
| TC-4 | BR-4 | Append mode -- results accumulate across dates | Running for multiple dates adds rows |
| TC-5 | BR-5 | Output ordered by (as_of, branch_id) | Rows sorted correctly |
| TC-6 | BR-6 | Job processes all calendar days including weekends | Output for all 31 days in October |
| TC-7 | BR-7 | SameDay dependency on BranchDirectory | Handled in Phase C |
| TC-8 | BR-8 | Only branches with visits appear in output | No zero-count rows |
| TC-9 | BR-9 | Simpler than BranchVisitPurposeBreakdown | No visit_purpose column in output |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | No visits for a date | Zero rows output for that date |
| EC-2 | Branch in visits but not in branches table | Visit dropped by INNER JOIN |
| EC-3 | Weekend date with visit data | Output produced normally |
| EC-4 | visit_count should be sum of all purpose-specific counts | Consistent with BranchVisitPurposeBreakdown totals |
| EC-5 | Comparison: curated vs double_secret_curated | Row counts and all column values match exactly |
