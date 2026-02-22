# Test Plan: TransactionCategorySummaryV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Verify total_amount = ROUND(SUM(amount), 2) per txn_type | Amounts match rounded sums |
| TC-2 | BR-1 | Verify transaction_count = COUNT(*) per txn_type | Counts match per-type counts |
| TC-3 | BR-1 | Verify avg_amount = ROUND(AVG(amount), 2) per txn_type | Averages match rounded averages |
| TC-4 | BR-3 | Verify ordering by as_of ASC, txn_type ASC | Rows ordered correctly |
| TC-5 | BR-4 | Verify Append mode: multiple dates accumulate | Table contains rows for multiple as_of dates |
| TC-6 | BR-5 | Verify 2 output rows per effective date (Credit and Debit) | Each as_of has exactly 2 rows |
| TC-7 | BR-6 | Verify all transactions included, no filters | Both Credit and Debit, all amounts |
| TC-8 | BR-7 | Verify segments loaded but unused | Job succeeds; output unaffected by segments data |
| TC-9 | BR-8 | Verify output for all 31 days including weekends | 62 rows total for October (31 days x 2 types) |
| TC-10 | BR-9 | Verify ROUND applied to total_amount and avg_amount | Values have at most 2 decimal places |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | No transactions for a given txn_type on a date | That txn_type row is missing (GROUP BY produces no row) |
| EC-2 | ROUND behavior on .5 values | SQLite banker's rounding matches original behavior |
| EC-3 | Dead code in CTE (ROW_NUMBER, COUNT window functions) | Does not affect output |
| EC-4 | Comparison with curated.transaction_category_summary for same date | Row-for-row match on all columns per as_of |
