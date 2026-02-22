# Test Plan: DailyTransactionSummaryV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Verify grouping by account_id and as_of | One row per account per date |
| TC-2 | BR-2 | Verify total_amount computation | SUM(debit amounts) + SUM(credit amounts), rounded to 2 dp |
| TC-3 | BR-3 | Verify transaction_count | COUNT(*) of all transaction rows per group |
| TC-4 | BR-4 | Verify debit_total | SUM of amounts where txn_type='Debit', ROUND(2) |
| TC-5 | BR-5 | Verify credit_total | SUM of amounts where txn_type='Credit', ROUND(2) |
| TC-6 | BR-6 | Verify total_amount rounding | Rounded to 2 decimal places |
| TC-7 | BR-7 | Verify ordering | Results ordered by as_of, then account_id |
| TC-8 | BR-8 | Verify subquery pattern | SQL uses subquery (inner computes, outer selects) |
| TC-9 | BR-9 | Verify Append mode | Rows accumulate across effective dates |
| TC-10 | BR-10 | Verify branches unused | No branch-related columns in output |
| TC-11 | BR-11 | Verify unused columns sourced | txn_timestamp and description sourced but not in output |
| TC-12 | BR-12 | Verify pure SQL Transformation | No External module processing |
| TC-13 | BR-13 | Verify 31 days of output | Transactions exist every day, 31 distinct dates in output |
| TC-14 | BR-14 | Verify non-Debit/Credit handling | Other types contribute 0 to debit/credit totals but counted in transaction_count |
| TC-15 | BR-1,9 | Compare V2 output to original | EXCEPT query yields zero rows |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | Account with no transactions on a date | No output row for that account/date |
| EC-2 | Weekend data | Transactions exist on weekends, so output produced |
| EC-3 | All transactions for an account are Debit | credit_total = 0 |
| EC-4 | All transactions for an account are Credit | debit_total = 0 |
| EC-5 | Non-standard txn_type (if any) | 0 in both debit_total and credit_total, counted in transaction_count |
