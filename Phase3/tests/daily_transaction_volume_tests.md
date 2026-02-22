# Test Plan: DailyTransactionVolumeV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Verify one row per as_of date | Each date has exactly one output row |
| TC-2 | BR-2 | Verify total_transactions | COUNT(*) of all transactions per date |
| TC-3 | BR-3 | Verify total_amount | SUM(amount) rounded to 2 decimal places |
| TC-4 | BR-4 | Verify avg_amount | AVG(amount) rounded to 2 decimal places |
| TC-5 | BR-5 | Verify MIN/MAX computed but excluded | Output has no min_amount or max_amount columns |
| TC-6 | BR-6 | Verify ordering by as_of | Output rows ordered chronologically |
| TC-7 | BR-7 | Verify Append mode | Rows accumulate across effective dates |
| TC-8 | BR-8 | Verify CTE-based SQL | WITH clause used |
| TC-9 | BR-9 | Verify SameDay dependency exists | DailyTransactionSummaryV2 must succeed first |
| TC-10 | BR-10 | Verify 31 output rows | One per day of October |
| TC-11 | BR-11 | Verify columns sourced | transaction_id, account_id, txn_type, amount |
| TC-12 | BR-12 | Verify all txn_types included in SUM/AVG | No CASE filtering, all amounts equally treated |
| TC-13 | BR-1,7 | Compare V2 output to original | EXCEPT query yields zero rows |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | No transactions on a date | No output row for that date |
| EC-2 | Weekend data | Transactions exist 7 days/week, so output every day |
| EC-3 | Single transaction on a date | total_transactions=1, total_amount=amount, avg_amount=amount |
| EC-4 | All same-amount transactions on a date | avg_amount = that amount |
