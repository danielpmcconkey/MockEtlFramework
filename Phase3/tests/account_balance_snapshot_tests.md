# Test Plan: AccountBalanceSnapshotV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Output contains exactly 6 columns: account_id, customer_id, account_type, account_status, current_balance, as_of | Output schema matches expected columns |
| TC-2 | BR-2 | branches DataFrame is available in shared state but not referenced by processor | Output contains no branch-related columns |
| TC-3 | BR-3 | Sourced columns open_date, interest_rate, credit_limit are not in output | Output has only 6 columns, excluding the 3 dropped columns |
| TC-4 | BR-4 | Write mode is Append -- DscWriterUtil called with overwrite=false | Running for multiple dates accumulates rows (no truncation) |
| TC-5 | BR-5 | All accounts are included regardless of type or status | Row count in output matches row count in source accounts table for the date |
| TC-6 | BR-6 | All column values are pass-through with no transformation | Values in double_secret_curated match values in datalake.accounts for each column |
| TC-7 | BR-7 | Job produces output only on weekdays (business days) | No output rows for weekend dates |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | Zero rows in accounts for effective date | Empty DataFrame with correct 6-column schema returned; no rows written to dsc |
| EC-2 | NULL value in a source column (e.g., current_balance is NULL) | NULL passes through to output as-is |
| EC-3 | Weekend date with no account data | Empty DataFrame returned; no rows written |
| EC-4 | Comparison: curated vs double_secret_curated for same date | Row counts and all column values match exactly |
