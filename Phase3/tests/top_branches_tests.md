# Test Plan: TopBranchesV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Verify total_visits = COUNT(*) of branch_visits per branch | Counts match per-branch visit counts |
| TC-2 | BR-3 | Verify RANK() ranking by total_visits DESC | Correct ranking with gaps for ties |
| TC-3 | BR-4 | Verify only branches with visits are included (INNER JOIN) | No branches with 0 visits in output |
| TC-4 | BR-5 | Verify as_of comes from branches table | as_of matches the effective date |
| TC-5 | BR-6 | Verify ordering by rank ASC, then branch_id ASC | Rows ordered correctly |
| TC-6 | BR-7 | Verify Overwrite mode: only latest effective date data present | Only one as_of in output table after run |
| TC-7 | BR-8 | Verify SameDay dependency on BranchVisitSummary | Job only runs after BranchVisitSummary succeeds |
| TC-8 | BR-9 | Verify output possible for all 31 days | Both source tables have data every day |
| TC-9 | BR-10 | Verify no duplicate branch_ids in output | Each branch appears once |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | No branch visits for effective date | Empty output (0 rows), table truncated |
| EC-2 | Multiple branches tied on visits | Same rank assigned, gap in subsequent rank (RANK behavior) |
| EC-3 | Single branch with visits | One row with rank=1 |
| EC-4 | Comparison with curated.top_branches for same date | Row-for-row match on all columns |
