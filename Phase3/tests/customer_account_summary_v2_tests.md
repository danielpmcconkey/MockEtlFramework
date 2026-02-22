# Test Plan: CustomerAccountSummaryV2V2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | All customers included even with no accounts | Customers with 0 accounts have count=0, balances=0 |
| TC-2 | BR-2 | Account count is total accounts per customer | Count matches number of account rows |
| TC-3 | BR-3 | Total balance sums all accounts regardless of status | Sum of current_balance for all accounts |
| TC-4 | BR-4 | Active balance only sums Active accounts | Sum of current_balance where status='Active' |
| TC-5 | BR-5 | Overwrite mode | Only most recent date persists |
| TC-6 | BR-6 | Empty guard triggers | Empty DataFrame when customers or accounts empty |
| TC-7 | BR-7 | as_of from customers row | Output as_of matches customer as_of |
| TC-8 | BR-8 | Null names coalesce to "" | No null first_name or last_name |
| TC-9 | BR-1-10 | V2V2 output matches original V2 for each date Oct 1-31 | EXCEPT query returns zero rows |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | Weekend (no customer/account data) | Empty DataFrame, table truncated |
| EC-2 | Customer with no accounts | account_count=0, total_balance=0, active_balance=0 |
| EC-3 | All accounts Closed/Inactive | active_balance=0, total_balance=sum of all |
| EC-4 | Null first_name or last_name | Defaults to "" |
