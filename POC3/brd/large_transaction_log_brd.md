# LargeTransactionLog — Business Requirements Document

## Overview
Produces a log of large transactions (amount > 500) enriched with customer name information via account-to-customer lookup. Used for transaction monitoring and audit trails.

## Output Type
ParquetFileWriter

## Writer Configuration
- **outputDirectory**: `Output/curated/large_transaction_log/`
- **numParts**: 3
- **writeMode**: Append

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.transactions | transaction_id, account_id, txn_timestamp, txn_type, amount, description | Effective date range via executor; filtered to amount > 500 | [large_transaction_log.json:4-11] |
| datalake.accounts | account_id, customer_id, account_type, account_status, open_date, current_balance, interest_rate, credit_limit, apr | Effective date range via executor; used for account-to-customer lookup | [large_transaction_log.json:12-19] |
| datalake.customers | id, first_name, last_name | Effective date range via executor; used for customer name lookup | [large_transaction_log.json:20-27] |
| datalake.addresses | address_id, customer_id, address_line1, city | Effective date range via executor | [large_transaction_log.json:28-35] |

### Source Table Schemas (from database)

**transactions**: transaction_id (integer), account_id (integer), txn_timestamp (timestamp), txn_type (varchar), amount (numeric), description (varchar), as_of (date)

**accounts**: account_id (integer), customer_id (integer), account_type (varchar), account_status (varchar), open_date (date), current_balance (numeric), interest_rate (numeric), credit_limit (numeric), apr (numeric), as_of (date)

**customers**: id (integer), prefix (varchar), first_name (varchar), last_name (varchar), sort_name (varchar), suffix (varchar), birthdate (date), as_of (date)

**addresses**: address_id (integer), customer_id (integer), address_line1 (varchar), city (varchar), state_province (varchar), postal_code (varchar), country (char), start_date (date), end_date (date), as_of (date)

## Business Rules

BR-1: Only transactions with amount > 500 (strictly greater than, not >=) are included in the output.
- Confidence: HIGH
- Evidence: [LargeTransactionProcessor.cs:56] — `if (amount > 500)`

BR-2: Customer ID is resolved via a two-step lookup: transaction.account_id -> accounts.customer_id -> customers.id. This properly joins through the accounts table.
- Confidence: HIGH
- Evidence: [LargeTransactionProcessor.cs:33-39] — builds `accountToCustomer` dictionary; [LargeTransactionProcessor.cs:58-59] — uses it to resolve customer_id

BR-3: If no matching account is found for a transaction's account_id, customer_id defaults to 0 and name fields default to empty strings.
- Confidence: HIGH
- Evidence: [LargeTransactionProcessor.cs:59-60] — `GetValueOrDefault(accountId, 0)` and `GetValueOrDefault(customerId, ("", ""))`

BR-4: The addresses DataFrame is sourced but never used in the External module logic. It is a dead-end data source.
- Confidence: HIGH
- Evidence: [LargeTransactionProcessor.cs] — no reference to "addresses" anywhere in the Execute method; [large_transaction_log.json:28-35] — addresses DataSourcing configured

BR-5: Most columns sourced from accounts (account_type, account_status, open_date, current_balance, interest_rate, credit_limit, apr) are unused — only account_id and customer_id are used.
- Confidence: HIGH
- Evidence: [LargeTransactionProcessor.cs:36-37] — only `account_id` and `customer_id` are read from account rows

BR-6: If accounts or customers DataFrame is null/empty, empty output is produced (even if transactions exist).
- Confidence: HIGH
- Evidence: [LargeTransactionProcessor.cs:19-22] — null/empty check on accounts AND customers

BR-7: If transactions DataFrame is null/empty, empty output is produced.
- Confidence: HIGH
- Evidence: [LargeTransactionProcessor.cs:25-29]

BR-8: NULL first_name or last_name values are coalesced to empty string.
- Confidence: HIGH
- Evidence: [LargeTransactionProcessor.cs:47-48] — `?.ToString() ?? ""`

BR-9: The account-to-customer lookup uses ALL rows from accounts without date filtering. If multiple as_of dates exist, the last-seen account_id mapping wins.
- Confidence: HIGH
- Evidence: [LargeTransactionProcessor.cs:34-39] — iterates all rows, dictionary overwrites

BR-10: Current data shows about 288,340 transactions with amount > 500 (out of the full transaction set). Transaction amounts range from 20 to 4200 with mean ~910.
- Confidence: HIGH
- Evidence: [DB query: COUNT(*) WHERE amount > 500 = 288340]; [DB query: MIN=20, MAX=4200, AVG≈910]

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| transaction_id | transactions.transaction_id | Direct passthrough | [LargeTransactionProcessor.cs:64] |
| account_id | transactions.account_id | Direct passthrough | [LargeTransactionProcessor.cs:65] |
| customer_id | Computed | Resolved via accounts lookup, default 0 | [LargeTransactionProcessor.cs:58-59,66] |
| first_name | customers.first_name | Lookup by customer_id, default "" | [LargeTransactionProcessor.cs:60,67] |
| last_name | customers.last_name | Lookup by customer_id, default "" | [LargeTransactionProcessor.cs:60,68] |
| txn_type | transactions.txn_type | Direct passthrough | [LargeTransactionProcessor.cs:69] |
| amount | transactions.amount | Direct passthrough | [LargeTransactionProcessor.cs:70] |
| description | transactions.description | Direct passthrough | [LargeTransactionProcessor.cs:71] |
| txn_timestamp | transactions.txn_timestamp | Direct passthrough | [LargeTransactionProcessor.cs:72] |
| as_of | transactions.as_of | Direct passthrough | [LargeTransactionProcessor.cs:73] |

## Non-Deterministic Fields
None identified. Output row order follows the iteration order of the transactions DataFrame.

## Write Mode Implications
- **Append** mode: each run ADDS part files to the output directory without removing existing ones. Multi-day runs will accumulate data across all effective dates.
- This means the output directory grows over time and will contain duplicates if the same effective date is reprocessed.
- Evidence: [large_transaction_log.json:43] — `"writeMode": "Append"`

## Edge Cases

1. **Missing account mapping**: Transactions from accounts not in the accounts DataFrame get customer_id = 0 and empty names. The transaction is still included in output.
   - Evidence: [LargeTransactionProcessor.cs:59-60]

2. **Missing customer**: If an account's customer_id is not found in the customers DataFrame, first_name and last_name default to empty strings.
   - Evidence: [LargeTransactionProcessor.cs:60]

3. **Append mode reprocessing**: If the same effective date is run twice, transactions are duplicated in the output directory.
   - Evidence: [large_transaction_log.json:43] — Append mode

4. **Unused addresses**: The addresses DataFrame is loaded but never used, consuming memory unnecessarily.
   - Evidence: [large_transaction_log.json:28-35]; [LargeTransactionProcessor.cs] — no addresses reference

5. **Boundary value**: Transactions with amount exactly equal to 500 are EXCLUDED (strictly > 500).
   - Evidence: [LargeTransactionProcessor.cs:56] — `amount > 500`

6. **3-part split**: Output is split across 3 part files. The framework handles the splitting logic.
   - Evidence: [large_transaction_log.json:42]

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Amount > 500 filter | [LargeTransactionProcessor.cs:56] |
| BR-2: Two-step customer lookup | [LargeTransactionProcessor.cs:33-39, 58-59] |
| BR-3: Default values for missing lookups | [LargeTransactionProcessor.cs:59-60] |
| BR-4: Dead-end addresses | [LargeTransactionProcessor.cs], [large_transaction_log.json:28-35] |
| BR-5: Unused account columns | [LargeTransactionProcessor.cs:36-37] |
| BR-6: Empty output on missing accounts/customers | [LargeTransactionProcessor.cs:19-22] |
| BR-7: Empty output on missing transactions | [LargeTransactionProcessor.cs:25-29] |
| BR-8: NULL name coalescing | [LargeTransactionProcessor.cs:47-48] |
| BR-9: Unfiltered account lookup | [LargeTransactionProcessor.cs:34-39] |
| BR-10: Data volume | [DB queries] |

## Open Questions
1. Why is the addresses DataFrame sourced but never used? Possible future enrichment requirement or configuration error.
   - Confidence: LOW
2. Why are so many account columns sourced (account_type, account_status, open_date, current_balance, etc.) when only account_id and customer_id are used?
   - Confidence: LOW — may be over-sourcing for defensive purposes
