# Test Plan: CustomerValueScoreV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Verify one output row per customer | Row count matches customer count |
| TC-2 | BR-2 | Verify empty guard | When customers or accounts null/empty, empty output |
| TC-3 | BR-3 | Verify transaction_score formula | min(txn_count * 10, 1000), capped at 1000 |
| TC-4 | BR-4 | Verify balance_score formula | min(total_balance / 1000, 1000), capped at 1000 |
| TC-5 | BR-5 | Verify visit_score formula | min(visit_count * 50, 1000), capped at 1000 |
| TC-6 | BR-6 | Verify composite_score formula | txn_score*0.4 + bal_score*0.35 + visit_score*0.25 |
| TC-7 | BR-7 | Verify rounding | All scores rounded to 2 decimal places |
| TC-8 | BR-8 | Verify transaction counting via account lookup | Transactions mapped to customers through accounts |
| TC-9 | BR-9 | Verify orphan transaction skipping | Unmatched account_ids skipped |
| TC-10 | BR-10 | Verify negative balance_score allowed | Customer with negative balance gets negative balance_score |
| TC-11 | BR-11 | Verify zero transaction default | Customers with no transactions get transaction_score = 0 |
| TC-12 | BR-12 | Verify zero visit default | Customers with no visits get visit_score = 0 |
| TC-13 | BR-13 | Verify Overwrite mode | Only latest effective date's data |
| TC-14 | BR-14 | Verify as_of from customers row | as_of matches effective date |
| TC-15 | BR-1,13 | Compare V2 output to original | EXCEPT query yields zero rows |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | Customers empty | Empty output DataFrame |
| EC-2 | Accounts empty | Empty output DataFrame |
| EC-3 | Transactions null | transaction_score = 0 for all customers |
| EC-4 | Branch visits null | visit_score = 0 for all customers |
| EC-5 | Customer with negative total balance | balance_score negative (no floor) |
| EC-6 | Customer with >100 transactions | transaction_score capped at 1000 |
| EC-7 | Customer with >20 branch visits | visit_score capped at 1000 |
| EC-8 | Weekend effective date | Empty output (customers/accounts weekday-only) |
