# Test Plan: HighBalanceAccountsV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Verify only accounts with balance > 10000 are included | No rows with current_balance <= 10000 |
| TC-2 | BR-2 | Verify customer name enrichment is correct | first_name and last_name match customer lookup |
| TC-3 | BR-3 | Verify missing customer defaults to empty strings | Accounts with unknown customer_id have first_name="" and last_name="" |
| TC-4 | BR-4 | Verify customer lookup uses customers.id column | Join logic matches on id, not customer_id |
| TC-5 | BR-5 | Verify all account types are included | Multiple account_type values present in output |
| TC-6 | BR-6 | Verify all account statuses are included | No status-based filtering |
| TC-7 | BR-7 | Verify Overwrite mode: only latest effective date data present | Only one as_of in output table after run |
| TC-8 | BR-8 | Verify empty output when accounts or customers are empty | 0 rows on weekend effective dates |
| TC-9 | BR-9 | Verify as_of comes from account row | as_of matches the effective date of the account |
| TC-10 | BR-10 | Verify last customer row wins on duplicate ids | Consistent with original behavior |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | Weekend effective date (no accounts/customers data) | Empty output (0 rows), no error |
| EC-2 | No accounts exceed $10,000 threshold | Empty output (0 rows) |
| EC-3 | All accounts exceed $10,000 threshold | All accounts in output |
| EC-4 | Comparison with curated.high_balance_accounts for same date | Row-for-row match on all columns |
