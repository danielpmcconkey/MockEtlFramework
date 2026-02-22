# BRD: HighBalanceAccounts

## Overview
This job identifies accounts with a current balance exceeding $10,000 and enriches them with customer name information. The output is a filtered list of high-balance accounts with customer details, written to `curated.high_balance_accounts` in Overwrite mode.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| accounts | datalake | account_id, customer_id, account_type, account_status, current_balance | Filtered: current_balance > 10000 | [JobExecutor/Jobs/high_balance_accounts.json:5-11] DataSourcing config; [ExternalModules/HighBalanceFilter.cs:39] filter condition |
| customers | datalake | id, first_name, last_name | Lookup by customer_id for name enrichment | [high_balance_accounts.json:13-17] DataSourcing config; [HighBalanceFilter.cs:27-33] dictionary build |

## Business Rules

BR-1: Only accounts with current_balance strictly greater than 10,000 are included in the output.
- Confidence: HIGH
- Evidence: [ExternalModules/HighBalanceFilter.cs:39] `if (balance > 10000)`
- Evidence: [curated.high_balance_accounts] `SELECT MIN(current_balance) FROM curated.high_balance_accounts` confirms all balances > 10000

BR-2: Each qualifying account is enriched with the customer's first_name and last_name via a lookup on customer_id.
- Confidence: HIGH
- Evidence: [ExternalModules/HighBalanceFilter.cs:26-33] Customer name dictionary built keyed by customer id
- Evidence: [ExternalModules/HighBalanceFilter.cs:41-42] Lookup using `customerNames.GetValueOrDefault(customerId, ("", ""))`

BR-3: If a qualifying account's customer_id has no matching customer record, first_name and last_name default to empty strings.
- Confidence: HIGH
- Evidence: [ExternalModules/HighBalanceFilter.cs:42] `GetValueOrDefault(customerId, ("", ""))`

BR-4: The customer lookup uses the `id` column from customers, not `customer_id`.
- Confidence: HIGH
- Evidence: [ExternalModules/HighBalanceFilter.cs:29] `var custId = Convert.ToInt32(custRow["id"]);`
- Evidence: [high_balance_accounts.json:16] customers table sources column `id`

BR-5: The output includes all account types (Checking, Savings, etc.) as long as the balance threshold is met — there is no account_type filter.
- Confidence: HIGH
- Evidence: [ExternalModules/HighBalanceFilter.cs:37-55] Only `balance > 10000` check; no account_type condition
- Evidence: [curated.high_balance_accounts] `SELECT DISTINCT account_type` shows multiple types present

BR-6: The output includes accounts regardless of account_status (Active, Closed, etc.) — there is no status filter.
- Confidence: HIGH
- Evidence: [ExternalModules/HighBalanceFilter.cs:37-55] No account_status condition
- Evidence: [high_balance_accounts.json:10] account_status is sourced but not filtered on in the External module

BR-7: Output is written in Overwrite mode — each run truncates the entire table before writing.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/high_balance_accounts.json:28] `"writeMode": "Overwrite"`
- Evidence: [curated.high_balance_accounts] Only one as_of date (2024-10-31) present

BR-8: If accounts or customers are null or empty, the job produces an empty output DataFrame.
- Confidence: HIGH
- Evidence: [ExternalModules/HighBalanceFilter.cs:19-23] Null/empty check returns empty DataFrame

BR-9: The as_of column in the output comes directly from the account row's as_of value (not from customers).
- Confidence: HIGH
- Evidence: [ExternalModules/HighBalanceFilter.cs:52] `["as_of"] = acctRow["as_of"]`

BR-10: If multiple customer rows exist for the same id (due to dictionary overwrite), the last one encountered wins.
- Confidence: MEDIUM
- Evidence: [ExternalModules/HighBalanceFilter.cs:30] `customerNames[custId] = (firstName, lastName);` — dictionary assignment overwrites
- Evidence: In practice, customer ids are unique per as_of, so this is not expected to cause issues

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| account_id | accounts.account_id | Pass-through | [HighBalanceFilter.cs:46] |
| customer_id | accounts.customer_id | Pass-through | [HighBalanceFilter.cs:47] |
| account_type | accounts.account_type | Pass-through | [HighBalanceFilter.cs:48] |
| current_balance | accounts.current_balance | Pass-through (no rounding) | [HighBalanceFilter.cs:49] |
| first_name | customers.first_name | Lookup by customer_id; default "" if not found | [HighBalanceFilter.cs:50] |
| last_name | customers.last_name | Lookup by customer_id; default "" if not found | [HighBalanceFilter.cs:51] |
| as_of | accounts.as_of | Pass-through from account row | [HighBalanceFilter.cs:52] |

## Edge Cases
- **NULL handling**: customer first_name/last_name null values are coalesced to empty string via `?.ToString() ?? ""` at [HighBalanceFilter.cs:31-32]. If customer_id not found in lookup, defaults to ("", "").
- **Weekend/date fallback**: Accounts table has weekday-only data. On weekend effective dates, accounts DataFrame would be empty, triggering the empty-output guard (BR-8).
- **Zero-row behavior**: If no accounts exceed $10,000 threshold, output is a valid empty DataFrame.
- **Balance precision**: The balance comparison uses `Convert.ToDecimal` but output passes through the raw value — no rounding applied to the balance itself.

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [HighBalanceFilter.cs:39], [curated data verification] |
| BR-2 | [HighBalanceFilter.cs:26-33, 41-42] |
| BR-3 | [HighBalanceFilter.cs:42] |
| BR-4 | [HighBalanceFilter.cs:29], [high_balance_accounts.json:16] |
| BR-5 | [HighBalanceFilter.cs:37-55], [curated data verification] |
| BR-6 | [HighBalanceFilter.cs:37-55], [high_balance_accounts.json:10] |
| BR-7 | [high_balance_accounts.json:28], [curated data observation] |
| BR-8 | [HighBalanceFilter.cs:19-23] |
| BR-9 | [HighBalanceFilter.cs:52] |
| BR-10 | [HighBalanceFilter.cs:30] |

## Open Questions
- None. The logic is straightforward with no ambiguity.
