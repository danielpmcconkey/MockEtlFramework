# Test Plan: CoveredTransactionsV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Only Checking account transactions appear in output | No non-Checking account_type values in output |
| TC-2 | BR-2 | Only customers with active US addresses included | All output rows have country='US'; customers without US addresses excluded |
| TC-3 | BR-3 | Accounts use snapshot fallback (as_of <= date) | On weekends (no account snapshot), most recent weekday snapshot used |
| TC-4 | BR-4 | Customers use snapshot fallback (as_of <= date) | On weekends (no customer snapshot), most recent weekday snapshot used |
| TC-5 | BR-5 | Earliest active US address selected per customer | When customer has multiple active US addresses, earliest start_date wins |
| TC-6 | BR-6 | First segment alphabetically per customer | When customer has multiple segments, lowest segment_code used |
| TC-7 | BR-7 | Output sorted customer_id ASC, transaction_id DESC | Verify sort order in output DataFrame |
| TC-8 | BR-8 | record_count equals total row count | Every row's record_count matches DataFrame.Count |
| TC-9 | BR-9 | Zero-row sentinel when no qualifying transactions | Single row with nulls except as_of and record_count=0 |
| TC-10 | BR-10 | String fields are trimmed | No leading/trailing whitespace in string columns |
| TC-11 | BR-11 | Timestamps formatted yyyy-MM-dd HH:mm:ss, dates yyyy-MM-dd | Verify format of txn_timestamp, account_opened, as_of |
| TC-12 | BR-12 | Append write mode | Data accumulates across dates (not truncated) |
| TC-13 | BR-14 | Transactions sourced for exact date only | Transactions as_of matches effective date exactly |
| TC-14 | BR-1,2 | V2 output matches original for each date Oct 1-31 | EXCEPT query returns zero rows |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | Zero qualifying transactions for a date | Single sentinel null row with as_of and record_count=0 |
| EC-2 | Customer exists but no segment mapping | customer_segment is null |
| EC-3 | Customer has no customer record in snapshot | Customer name fields are null |
| EC-4 | Weekend date (no account/customer snapshot) | Falls back to most recent weekday snapshot |
| EC-5 | Address end_date equals effective date | Address is still active (end_date >= @date) |
| EC-6 | Multiple Checking accounts for same customer | Each transaction on each account produces separate output rows |
