# Test Plan: MonthlyTransactionTrendV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Verify aggregation: daily_transactions = COUNT(*) | Count matches datalake.transactions count for date |
| TC-2 | BR-1 | Verify aggregation: daily_amount = ROUND(SUM(amount), 2) | Amount matches rounded sum |
| TC-3 | BR-1 | Verify aggregation: avg_transaction_amount = ROUND(AVG(amount), 2) | Average matches rounded average |
| TC-4 | BR-4 | Verify ordering by as_of ascending | Rows ordered by date |
| TC-5 | BR-5 | Verify Append mode: multiple dates accumulate | Table contains rows for multiple as_of dates after multi-day runs |
| TC-6 | BR-6 | Verify one output row per effective date | Each as_of has exactly 1 row |
| TC-7 | BR-7 | Verify all transaction types included | Both Credit and Debit counted |
| TC-8 | BR-8 | Verify branches loaded but unused | Job succeeds; output unaffected by branches data |
| TC-9 | BR-9 | Verify SameDay dependency on DailyTransactionVolume | Job only runs after DailyTransactionVolume succeeds |
| TC-10 | BR-10 | Verify output for all 31 days including weekends | 31 rows total for October |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | No transactions for effective date | Zero rows produced, nothing appended |
| EC-2 | ROUND behavior on .5 values | SQLite banker's rounding matches original behavior |
| EC-3 | Comparison with curated.monthly_transaction_trend for same date | Row-for-row match on all columns per as_of |
