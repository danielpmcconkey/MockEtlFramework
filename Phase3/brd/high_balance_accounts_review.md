# Review: HighBalanceAccounts BRD

## Status: PASS

## Checklist
- [x] All evidence citations verified
- [x] No unsupported claims (one minor DB evidence correction noted below)
- [x] No impossible knowledge
- [x] Full traceability
- [x] Format complete

## Verification Details

### Evidence Citation Checks
| Claim | Citation | Verified |
|-------|----------|----------|
| BR-1: balance > 10000 filter | HighBalanceFilter.cs:39, DB | YES - `if (balance > 10000)`; DB min=10270.00; source count 54 = output 54 |
| BR-2: Customer name enrichment via lookup | HighBalanceFilter.cs:26-33, 41-42 | YES - Dictionary keyed by custRow["id"], lookup via GetValueOrDefault |
| BR-3: Empty string defaults for missing customers | HighBalanceFilter.cs:42 | YES - `GetValueOrDefault(customerId, ("", ""))` |
| BR-4: Customer uses `id` column | HighBalanceFilter.cs:29, JSON:16 | YES - `custRow["id"]`; JSON columns include "id" |
| BR-5: All account types included (no type filter) | HighBalanceFilter.cs:37-55 | CODE LOGIC CORRECT, but DB evidence inaccurate (see note) |
| BR-6: No account_status filter | HighBalanceFilter.cs:37-55, JSON:10 | YES - No status condition in code |
| BR-7: Overwrite mode | JSON:28 | YES - `"writeMode": "Overwrite"`; only 2024-10-31 in output |
| BR-8: Empty guard on accounts/customers | HighBalanceFilter.cs:19-23 | YES - OR null/empty check returns empty DF |
| BR-9: as_of from account row | HighBalanceFilter.cs:52 | YES - `acctRow["as_of"]` |
| BR-10: Dictionary last-write-wins | HighBalanceFilter.cs:32 | YES - dictionary assignment overwrites; MEDIUM confidence appropriate |

### Database Cross-Verification
- curated.high_balance_accounts: 54 rows, as_of=2024-10-31, min_balance=10270.00, max_balance=19909.00
- datalake.accounts WHERE current_balance > 10000 AND as_of='2024-10-31': COUNT = 54 — matches output exactly
- Schema: account_id (int), customer_id (int), account_type (varchar), current_balance (numeric), first_name (varchar), last_name (varchar), as_of (date) — matches BRD

### Line Number Accuracy
All HighBalanceFilter.cs line numbers verified as accurate. JSON line references acceptable.

## Issue Found (minor, not blocking)
**BR-5 DB evidence inaccuracy**: The BRD states "SELECT DISTINCT account_type shows multiple types present" but the actual query returns only **Savings**. In the current data, only Savings accounts exceed the $10,000 threshold. The business rule itself is correct — the code has no account_type filter — but the supporting DB evidence claim is factually incorrect. This does not affect the validity of the rule, just the accuracy of one evidence citation.

## Notes
- Straightforward filter + enrichment pattern, similar to AccountCustomerJoin but with the balance threshold.
- All 54 output rows cross-verified against source data count.
- The only-Savings observation is interesting — suggests Checking and Credit accounts in this dataset don't exceed $10,000.
