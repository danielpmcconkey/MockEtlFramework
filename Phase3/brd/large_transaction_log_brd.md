# BRD: LargeTransactionLog

## Overview
This job identifies transactions with an amount exceeding $500, enriches them with customer identity information (via account-to-customer lookup), and appends the results to a running log. The output is written to `curated.large_transaction_log` in Append mode, producing a cumulative historical record of large transactions.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| transactions | datalake | transaction_id, account_id, txn_timestamp, txn_type, amount, description | Filtered: amount > 500 | [JobExecutor/Jobs/large_transaction_log.json:5-11] DataSourcing config; [ExternalModules/LargeTransactionProcessor.cs:55-56] filter condition |
| accounts | datalake | account_id, customer_id, account_type, account_status, open_date, current_balance, interest_rate, credit_limit, apr | Used only for account_id -> customer_id mapping | [large_transaction_log.json:13-18] DataSourcing config; [LargeTransactionProcessor.cs:33-39] lookup build |
| customers | datalake | id, first_name, last_name | Used for customer_id -> name lookup | [large_transaction_log.json:20-24] DataSourcing config; [LargeTransactionProcessor.cs:42-49] lookup build |
| addresses | datalake | address_id, customer_id, address_line1, city | Sourced but NOT used in External module logic | [large_transaction_log.json:26-30] DataSourcing config; Not referenced in LargeTransactionProcessor.cs |

## Business Rules

BR-1: Only transactions with amount strictly greater than 500 are included in the output.
- Confidence: HIGH
- Evidence: [ExternalModules/LargeTransactionProcessor.cs:55-56] `if (amount > 500)`
- Evidence: [curated.large_transaction_log] `SELECT MIN(amount) FROM curated.large_transaction_log` would confirm all > 500

BR-2: Each qualifying transaction is enriched with customer_id, first_name, and last_name via a two-step lookup: first account_id -> customer_id (from accounts), then customer_id -> (first_name, last_name) (from customers).
- Confidence: HIGH
- Evidence: [LargeTransactionProcessor.cs:33-39] account-to-customer dictionary
- Evidence: [LargeTransactionProcessor.cs:42-49] customer name dictionary
- Evidence: [LargeTransactionProcessor.cs:58-60] lookup chain

BR-3: If a transaction's account_id has no matching account record, customer_id defaults to 0.
- Confidence: HIGH
- Evidence: [LargeTransactionProcessor.cs:59] `accountToCustomer.GetValueOrDefault(accountId, 0)`

BR-4: If the derived customer_id has no matching customer record, first_name and last_name default to empty strings.
- Confidence: HIGH
- Evidence: [LargeTransactionProcessor.cs:60] `customerNames.GetValueOrDefault(customerId, ("", ""))`

BR-5: The amount threshold filter applies to all transaction types (Credit and Debit) — there is no txn_type filter.
- Confidence: HIGH
- Evidence: [LargeTransactionProcessor.cs:53-76] Only `amount > 500` check; no txn_type condition

BR-6: Output is written in Append mode — each daily run adds new rows without truncating prior data.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/large_transaction_log.json:42] `"writeMode": "Append"`
- Evidence: [curated.large_transaction_log] Contains rows for 23 distinct as_of dates (weekdays only)

BR-7: If accounts or customers DataFrames are null or empty, the job produces an empty output DataFrame (no rows appended).
- Confidence: HIGH
- Evidence: [LargeTransactionProcessor.cs:17-23] First null/empty check

BR-8: If transactions DataFrame is null or empty, the job produces an empty output DataFrame (no rows appended).
- Confidence: HIGH
- Evidence: [LargeTransactionProcessor.cs:25-30] Second null/empty check

BR-9: The addresses DataFrame is sourced by the job config but is NOT used by the External module.
- Confidence: HIGH
- Evidence: [large_transaction_log.json:26-30] Addresses sourced; [LargeTransactionProcessor.cs] No reference to "addresses" key in sharedState

BR-10: The as_of column in the output comes from the transaction row.
- Confidence: HIGH
- Evidence: [LargeTransactionProcessor.cs:73] `["as_of"] = txnRow["as_of"]`

BR-11: Many accounts columns are sourced (account_type, account_status, open_date, current_balance, interest_rate, credit_limit, apr) but only customer_id is used from the accounts table.
- Confidence: HIGH
- Evidence: [large_transaction_log.json:17] All columns listed; [LargeTransactionProcessor.cs:36-37] Only account_id and customer_id extracted

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| transaction_id | transactions.transaction_id | Pass-through | [LargeTransactionProcessor.cs:63] |
| account_id | transactions.account_id | Pass-through | [LargeTransactionProcessor.cs:64] |
| customer_id | accounts (via lookup) | Derived from account_id -> customer_id lookup; default 0 | [LargeTransactionProcessor.cs:58-59] |
| first_name | customers (via lookup) | Derived from customer_id -> first_name; default "" | [LargeTransactionProcessor.cs:60, 66] |
| last_name | customers (via lookup) | Derived from customer_id -> last_name; default "" | [LargeTransactionProcessor.cs:60, 67] |
| txn_type | transactions.txn_type | Pass-through | [LargeTransactionProcessor.cs:68] |
| amount | transactions.amount | Pass-through (no rounding) | [LargeTransactionProcessor.cs:69] |
| description | transactions.description | Pass-through | [LargeTransactionProcessor.cs:70] |
| txn_timestamp | transactions.txn_timestamp | Pass-through | [LargeTransactionProcessor.cs:71] |
| as_of | transactions.as_of | Pass-through | [LargeTransactionProcessor.cs:73] |

## Edge Cases
- **NULL handling**: Customer first_name/last_name null values are coalesced to empty string via `?.ToString() ?? ""` at [LargeTransactionProcessor.cs:45-46]. Missing account or customer lookups use GetValueOrDefault with safe defaults (0 and ("","")).
- **Weekend/date fallback**: Transactions have data for all 31 days including weekends, but accounts and customers are weekday-only. On weekends, accounts/customers DataFrames would be empty, triggering the empty-output guard (BR-7). Observed output confirms only weekday dates (23 dates).
- **Zero-row behavior**: If no transactions exceed $500, an empty DataFrame is produced and no rows are appended.
- **Append accumulation**: Since writeMode is Append, the table grows over time. Re-running the same effective date would produce duplicate rows. The framework prevents this through gap-fill logic.
- **Unused data**: addresses table and most accounts columns are loaded but unused — potential inefficiency.

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [LargeTransactionProcessor.cs:55-56], [curated data verification] |
| BR-2 | [LargeTransactionProcessor.cs:33-39, 42-49, 58-60] |
| BR-3 | [LargeTransactionProcessor.cs:59] |
| BR-4 | [LargeTransactionProcessor.cs:60] |
| BR-5 | [LargeTransactionProcessor.cs:53-76] |
| BR-6 | [large_transaction_log.json:42], [curated data observation] |
| BR-7 | [LargeTransactionProcessor.cs:17-23] |
| BR-8 | [LargeTransactionProcessor.cs:25-30] |
| BR-9 | [large_transaction_log.json:26-30], [LargeTransactionProcessor.cs] |
| BR-10 | [LargeTransactionProcessor.cs:73] |
| BR-11 | [large_transaction_log.json:17], [LargeTransactionProcessor.cs:36-37] |

## Open Questions
- The addresses table is sourced but unused. This could be intended for future enrichment or is a configuration oversight. Confidence: MEDIUM that this is an oversight — no impact on output.
- Similarly, many accounts columns beyond customer_id are sourced but unused. Confidence: HIGH this is just over-fetching with no logic impact.
