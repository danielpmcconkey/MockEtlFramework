# Test Plan: AccountTypeDistributionV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Output groups accounts by account_type with count | One row per account_type with correct account_count |
| TC-2 | BR-2 | total_accounts equals total number of accounts | total_accounts = 277 (or whatever the date's total is) for all rows |
| TC-3 | BR-3 | percentage = (account_count / total_accounts) * 100.0 | Percentage values match expected floating-point computation |
| TC-4 | BR-4 | percentage is a double/floating-point value | Values like 34.657039711..., not rounded integers |
| TC-5 | BR-5 | as_of from first row of accounts DataFrame | as_of matches effective date |
| TC-6 | BR-6 | branches DataFrame in shared state but unused | Output has no branch-related columns |
| TC-7 | BR-7 | Overwrite mode -- only latest date retained | Table truncated before each write |
| TC-8 | BR-8 | NULL account_type | Coalesced to empty string for grouping |
| TC-9 | BR-9 | Weekday-only execution | No output for weekend dates |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | Zero rows in accounts | Empty DataFrame with correct 5-column schema |
| EC-2 | All accounts same type | Single output row with account_count = total_accounts, percentage = 100.0 |
| EC-3 | Floating-point precision | Percentage values match original double computation exactly |
| EC-4 | Comparison: curated vs double_secret_curated | Row counts and all column values match exactly |
