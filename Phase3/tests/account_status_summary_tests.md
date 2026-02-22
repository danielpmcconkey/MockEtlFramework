# Test Plan: AccountStatusSummaryV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Output groups accounts by (account_type, account_status) with count | Correct group counts matching source data |
| TC-2 | BR-2 | as_of value comes from first row of accounts DataFrame | as_of matches effective date |
| TC-3 | BR-3 | segments DataFrame in shared state but unused | Output has no segment-related columns |
| TC-4 | BR-4 | Overwrite mode -- only latest date retained | Table truncated before each write |
| TC-5 | BR-5 | All accounts counted regardless of balance | Sum of account_count equals total accounts |
| TC-6 | BR-6 | NULL account_type or account_status | Coalesced to empty string for grouping |
| TC-7 | BR-7 | Weekday-only execution | No output for weekend dates |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | Zero rows in accounts | Empty DataFrame with correct 4-column schema |
| EC-2 | All accounts same type and status | Single output row with count equal to total accounts |
| EC-3 | as_of from first row when all rows have same as_of | Correct as_of in all output rows |
| EC-4 | Comparison: curated vs double_secret_curated | Row counts and all column values match exactly |
