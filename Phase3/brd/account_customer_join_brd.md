# BRD: AccountCustomerJoin

## Overview
Produces a denormalized view joining account data with customer names, creating one row per account with the customer's first and last name attached. Uses Overwrite mode, so the output always reflects only the latest effective date's data.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| accounts | datalake | account_id, customer_id, account_type, account_status, current_balance | Filtered by effective date range. All accounts included. | [JobExecutor/Jobs/account_customer_join.json:6-11] |
| customers | datalake | id, first_name, last_name | Filtered by effective date range. Joined to accounts via customer_id = id. | [JobExecutor/Jobs/account_customer_join.json:13-18] |
| addresses | datalake | address_id, customer_id, address_line1, city, state_province | Filtered by effective date range. Sourced but NOT used in output. | [JobExecutor/Jobs/account_customer_join.json:20-25] |

## Business Rules
BR-1: Each account row is enriched with the customer's first_name and last_name by joining accounts.customer_id to customers.id.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountCustomerDenormalizer.cs:26-33] Customer lookup built from customers.Rows using `custRow["id"]` as key, mapped to `(first_name, last_name)`
- Evidence: [ExternalModules/AccountCustomerDenormalizer.cs:39] `var customerId = Convert.ToInt32(acctRow["customer_id"])` used to look up customer name

BR-2: The join is performed as a left-join-like operation from accounts to customers. If a customer_id has no matching customer, empty strings are used for first_name and last_name.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountCustomerDenormalizer.cs:40] `customerNames.GetValueOrDefault(customerId, ("", ""))` returns empty strings when no match found

BR-3: The customer lookup dictionary is built from ALL customer rows across ALL as_of dates in the DataFrame (last write wins for duplicate customer_ids). This means for multi-date runs, the latest as_of date's customer data will be used for all accounts.
- Confidence: MEDIUM
- Evidence: [ExternalModules/AccountCustomerDenormalizer.cs:27-33] Dictionary is built iterating all customer rows; duplicate keys overwrite with last value
- Note: Since the framework runs one effective date at a time (executor gap-fills day by day), both DataFrames typically contain only a single as_of date, making this a non-issue in practice.

BR-4: The addresses table is sourced but never used in the output. The AccountCustomerDenormalizer only reads "accounts" and "customers" from shared state.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountCustomerDenormalizer.cs:16-17] Only `accounts` and `customers` are read from shared state -- no reference to "addresses"

BR-5: Write mode is Overwrite -- the curated table is truncated before each write. Only the latest effective date's data is retained.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/account_customer_join.json:35] `"writeMode": "Overwrite"`
- Evidence: [curated.account_customer_join] Only 1 as_of date (2024-10-31) present with 277 rows, confirming previous dates were overwritten

BR-6: All accounts are included regardless of account_type, account_status, or balance. No filtering is applied.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountCustomerDenormalizer.cs:36-53] `foreach (var acctRow in accounts.Rows)` iterates all rows with no conditional filtering
- Evidence: [curated.account_customer_join] 277 rows matches datalake.accounts count of 277

BR-7: No transformations are applied to the data values. account_id, customer_id, account_type, account_status, current_balance, and as_of are passed through as-is from the accounts source.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountCustomerDenormalizer.cs:42-52] Direct assignment for all fields

BR-8: The job runs only on business days (weekdays) since the accounts and customers source tables only have weekday as_of dates.
- Confidence: HIGH
- Evidence: [datalake.accounts] Weekday-only as_of dates
- Evidence: [datalake.customers] Weekday-only as_of dates (Oct 4 -> Oct 7)

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| account_id | datalake.accounts.account_id | None (pass-through) | [AccountCustomerDenormalizer.cs:43] |
| customer_id | datalake.accounts.customer_id | None (pass-through) | [AccountCustomerDenormalizer.cs:44] |
| first_name | datalake.customers.first_name | Looked up via customer_id -> id join; empty string if no match | [AccountCustomerDenormalizer.cs:45] |
| last_name | datalake.customers.last_name | Looked up via customer_id -> id join; empty string if no match | [AccountCustomerDenormalizer.cs:46] |
| account_type | datalake.accounts.account_type | None (pass-through) | [AccountCustomerDenormalizer.cs:47] |
| account_status | datalake.accounts.account_status | None (pass-through) | [AccountCustomerDenormalizer.cs:48] |
| current_balance | datalake.accounts.current_balance | None (pass-through) | [AccountCustomerDenormalizer.cs:49] |
| as_of | datalake.accounts.as_of | None (pass-through) | [AccountCustomerDenormalizer.cs:50] |

## Edge Cases
- **NULL handling**: Customer first_name and last_name use `?.ToString() ?? ""` which converts NULL to empty string. [AccountCustomerDenormalizer.cs:30-31]
- **Missing customer match**: Accounts with customer_id not found in customers get empty strings for first_name and last_name (not NULL). [AccountCustomerDenormalizer.cs:40]
- **Empty accounts or customers**: If accounts is null/empty OR customers is null/empty, an empty output DataFrame with correct schema is returned. [AccountCustomerDenormalizer.cs:19-23]
- **Weekend/date fallback**: Both accounts and customers tables are weekday-only, so the job only produces data on weekdays.
- **Overwrite mode**: Since the table is truncated on each write, only one date's data exists at any time.

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [AccountCustomerDenormalizer.cs:26-33], [AccountCustomerDenormalizer.cs:39] |
| BR-2 | [AccountCustomerDenormalizer.cs:40] |
| BR-3 | [AccountCustomerDenormalizer.cs:27-33] |
| BR-4 | [AccountCustomerDenormalizer.cs:16-17], [account_customer_join.json:20-25] |
| BR-5 | [account_customer_join.json:35], [curated.account_customer_join row counts] |
| BR-6 | [AccountCustomerDenormalizer.cs:36-53], [curated.account_customer_join row counts] |
| BR-7 | [AccountCustomerDenormalizer.cs:42-52] |
| BR-8 | [datalake.accounts as_of dates], [datalake.customers as_of dates] |

## Open Questions
- **Why is addresses sourced but unused?** The addresses DataSourcing module is configured in the job but never referenced in AccountCustomerDenormalizer. This appears to be dead code in the job config, similar to the branches sourcing in AccountBalanceSnapshot. Confidence: MEDIUM that it is intentionally unused.
