# Test Plan: AccountCustomerJoinV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Output contains customer first_name and last_name joined from customers table | Each account row has correct customer name from customer_id -> id lookup |
| TC-2 | BR-2 | Account with customer_id not in customers table | first_name and last_name are empty strings (""), not NULL |
| TC-3 | BR-3 | Customer lookup uses last-write-wins for duplicate customer_ids | Consistent with original behavior |
| TC-4 | BR-4 | addresses DataFrame in shared state but unused | Output has no address columns |
| TC-5 | BR-5 | Overwrite mode -- only latest date data retained | After multiple runs, only the most recent as_of date's rows exist |
| TC-6 | BR-6 | All accounts included regardless of type/status/balance | Output row count matches source accounts row count |
| TC-7 | BR-7 | All account column values are pass-through | account_id, customer_id, account_type, account_status, current_balance, as_of match source exactly |
| TC-8 | BR-8 | Weekday-only execution | No output for weekend dates |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | Empty accounts DataFrame | Empty output with correct 8-column schema |
| EC-2 | Empty customers DataFrame | Empty output with correct 8-column schema |
| EC-3 | Both accounts and customers empty | Empty output with correct 8-column schema |
| EC-4 | NULL customer first_name or last_name in source | Coerced to empty string in customer lookup |
| EC-5 | Comparison: curated vs double_secret_curated | Row counts and all column values match exactly |
