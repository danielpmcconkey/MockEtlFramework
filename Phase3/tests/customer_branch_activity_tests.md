# Test Plan: CustomerBranchActivityV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Visit count per customer | Correct count of branch_visits per customer_id |
| TC-2 | BR-2 | Only customers with visits included | No customers with visit_count=0 |
| TC-3 | BR-3 | Customer names joined; null if not found | Names from customers DataFrame, null if missing |
| TC-4 | BR-4 | as_of from first visit row | All output rows share same as_of |
| TC-5 | BR-5 | Append mode | Data accumulates across dates |
| TC-6 | BR-6 | Empty guard | No output when customers or visits empty |
| TC-7 | BR-10 | Weekday-only output | Only 23 dates have output |
| TC-8 | BR-1-10 | V2 output matches original for each date Oct 1-31 | EXCEPT query returns zero rows |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | Weekend (customers empty) | Empty DataFrame, no rows written |
| EC-2 | Customer with visits but no customer record | first_name=null, last_name=null |
| EC-3 | No branch visits for a date | Empty DataFrame, no rows written |
| EC-4 | Single visit per customer | visit_count=1 |
