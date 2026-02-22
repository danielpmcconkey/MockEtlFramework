# Test Plan: BranchDirectoryV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Deduplication via ROW_NUMBER produces one row per branch_id | Exactly 40 rows (one per branch) per effective date |
| TC-2 | BR-2 | ROW_NUMBER ordering preserves consistent row selection | Same branch data as original for each branch_id |
| TC-3 | BR-3 | Output has 8 columns: branch_id, branch_name, address_line1, city, state_province, postal_code, country, as_of | Schema matches expected columns |
| TC-4 | BR-4 | Overwrite mode -- only latest date retained | Table truncated before each write |
| TC-5 | BR-5 | Output ordered by branch_id | Rows sorted ascending by branch_id |
| TC-6 | BR-6 | Job processes all calendar days including weekends | Output for weekend dates when branches have data |
| TC-7 | BR-7 | Job has downstream dependents (BranchVisitPurposeBreakdown, BranchVisitSummary) | Dependency registered in Phase C |
| TC-8 | BR-8 | All branches included -- no filtering | 40 rows matches source branch count |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | No branch data for a date | Zero rows output, no error |
| EC-2 | Duplicate branch_ids in source (unlikely but handled) | ROW_NUMBER ensures exactly one row per branch_id |
| EC-3 | NULL values in address columns | Pass through as-is |
| EC-4 | Comparison: curated vs double_secret_curated | Row counts and all column values match exactly |
