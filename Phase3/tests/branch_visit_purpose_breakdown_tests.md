# Test Plan: BranchVisitPurposeBreakdownV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Visit counts grouped by (branch_id, visit_purpose, as_of) | Correct visit_count per group |
| TC-2 | BR-2 | total_branch_visits computed in CTE but not in output | Output has 5 columns, no total_branch_visits |
| TC-3 | BR-3 | branch_name joined via INNER JOIN on branch_id AND as_of | Correct branch_name for each row |
| TC-4 | BR-4 | Visits to non-existent branch_ids dropped by INNER JOIN | Only branches in branches table appear in output |
| TC-5 | BR-5 | Append mode -- results accumulate across dates | Running for multiple dates adds rows without truncation |
| TC-6 | BR-6 | Output ordered by (as_of, branch_id, visit_purpose) | Rows sorted correctly |
| TC-7 | BR-7 | segments DataFrame in shared state but unused | Output has no segment-related columns |
| TC-8 | BR-8 | Job processes all calendar days including weekends | Output for all 31 days in October |
| TC-9 | BR-9 | SameDay dependency on BranchDirectory | Handled in Phase C |
| TC-10 | BR-10 | Only branches with visits appear in output | No zero-count rows for branches without visits |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | No visits for a date | Zero rows output for that date |
| EC-2 | Branch in visits but not in branches table | Visit dropped by INNER JOIN |
| EC-3 | NULL visit_purpose | Grouped as its own category (NULL) |
| EC-4 | Weekend date with visit data | Output produced normally |
| EC-5 | Comparison: curated vs double_secret_curated | Row counts and all column values match exactly |
