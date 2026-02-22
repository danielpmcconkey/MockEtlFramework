# Test Plan: BranchVisitLogV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Each visit has branch_name from branches lookup | Correct branch_name for each branch_id |
| TC-2 | BR-2 | Each visit has customer first_name and last_name | Correct names for each customer_id |
| TC-3 | BR-3 | Visit with branch_id not in branches table | branch_name is empty string "" |
| TC-4 | BR-4 | Visit with customer_id not in customers table | first_name and last_name are null (not empty string) |
| TC-5 | BR-5 | addresses DataFrame in shared state but unused | Output has no address columns |
| TC-6 | BR-6 | Append mode -- historical log accumulates | Running for multiple dates adds rows without truncation |
| TC-7 | BR-7 | Weekend date: customers empty | Empty output with correct 9-column schema |
| TC-8 | BR-8 | Effective date with no branch visits | Empty output with correct 9-column schema |
| TC-9 | BR-9 | visit_timestamp preserved as-is | Timestamp values match source exactly |
| TC-10 | BR-10 | Only weekday dates produce output | No output rows for weekend dates |
| TC-11 | BR-11 | All visits for the date included | Row count matches source branch_visits count |
| TC-12 | BR-12 | Duplicate branch_id or customer_id in lookups | Last-write-wins consistent with original |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | Empty branch_visits DataFrame | Empty output with correct schema |
| EC-2 | Empty customers DataFrame (weekend) | Empty output with correct schema |
| EC-3 | Branches is null | branch_name defaults to "" for all visits |
| EC-4 | NULL customer first_name in source | Stored as "" in lookup dictionary (coalesced) |
| EC-5 | Asymmetric null handling: missing branch="" vs missing customer=null | Verified that behavior matches original exactly |
| EC-6 | Comparison: curated vs double_secret_curated | Row counts and all column values match exactly |
