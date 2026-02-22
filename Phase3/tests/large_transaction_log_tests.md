# Test Plan: LargeTransactionLogV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Verify only transactions with amount > 500 are included | No rows with amount <= 500 |
| TC-2 | BR-2 | Verify customer enrichment via two-step lookup | customer_id, first_name, last_name populated correctly |
| TC-3 | BR-3 | Verify missing account defaults customer_id to 0 | Transactions with unknown account_id get customer_id=0 |
| TC-4 | BR-4 | Verify missing customer defaults names to empty | customer_id with no match gets first_name="" and last_name="" |
| TC-5 | BR-5 | Verify all transaction types included (Credit and Debit) | Both Credit and Debit txn_type values present |
| TC-6 | BR-6 | Verify Append mode: multiple dates accumulate | Table contains rows for multiple as_of dates |
| TC-7 | BR-7 | Verify empty output when accounts or customers are empty | 0 rows on weekend effective dates |
| TC-8 | BR-8 | Verify empty output when transactions are empty | 0 rows when no transactions |
| TC-9 | BR-9 | Verify addresses loaded but unused | Job succeeds; output unaffected by addresses data |
| TC-10 | BR-10 | Verify as_of comes from transaction row | as_of matches the effective date |
| TC-11 | BR-11 | Verify only customer_id used from accounts | Output does not contain account_type, account_status, etc. |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | Weekend effective date (no accounts/customers data) | Empty output (0 rows), no error |
| EC-2 | No transactions exceed $500 threshold | Empty output (0 rows appended) |
| EC-3 | All transactions exceed $500 threshold | All transactions in output |
| EC-4 | Comparison with curated.large_transaction_log for same date | Row-for-row match on all columns for each as_of |
