# Test Plan: CustomerTransactionActivityV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Verify account-to-customer mapping | Transactions correctly attributed to customers via account lookup |
| TC-2 | BR-2 | Verify orphan transaction handling | Transactions with unmatched account_id silently skipped |
| TC-3 | BR-3 | Verify aggregation fields | transaction_count, total_amount, debit_count, credit_count computed per customer |
| TC-4 | BR-4 | Verify debit/credit classification | Debit increments debit_count; Credit increments credit_count |
| TC-5 | BR-5 | Verify non-Debit/Credit handling | Non-standard types counted in transaction_count but not in debit/credit counts |
| TC-6 | BR-6 | Verify as_of from first transaction | as_of taken from transactions.Rows[0]["as_of"] |
| TC-7 | BR-7 | Verify accounts empty guard | Empty accounts -> empty output |
| TC-8 | BR-8 | Verify transactions empty guard | Empty transactions -> empty output |
| TC-9 | BR-9 | Verify Append mode | Rows accumulate across effective dates |
| TC-10 | BR-10 | Verify weekday-only output | Only 23 dates in output (accounts weekday-only triggers guard on weekends) |
| TC-11 | BR-11 | Verify total_amount includes all types | Sum of all amounts regardless of txn_type |
| TC-12 | BR-1,9 | Compare V2 output to original | EXCEPT query yields zero rows |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | Accounts empty (weekend) | Empty output DataFrame |
| EC-2 | Transactions empty | Empty output DataFrame |
| EC-3 | Transaction with unknown account_id | Silently skipped |
| EC-4 | Customer with multiple accounts | All transactions across accounts aggregated together |
| EC-5 | Non-standard txn_type (if any) | Counted in transaction_count but 0 for debit/credit |
