# LargeTransactionLog -- Business Requirements Document

## Overview

This job filters transactions to those with an amount exceeding $500, enriches each transaction with the account holder's name (via accounts-to-customers lookup), and writes the results in Append mode. This produces a cumulative log of large transactions across all processed effective dates.

## Source Tables

### datalake.transactions
- **Columns used**: `transaction_id`, `account_id`, `txn_type`, `amount`, `description`, `txn_timestamp`, `as_of`
- **Filter**: `amount > 500` applied in External module
- **Evidence**: [ExternalModules/LargeTransactionProcessor.cs:55] `if (amount > 500)`

### datalake.accounts
- **Columns used**: `account_id`, `customer_id`
- **Columns sourced but unused**: `account_type`, `account_status`, `open_date`, `current_balance`, `interest_rate`, `credit_limit`, `apr` (see AP-4)
- **Join logic**: Used to build `account_id -> customer_id` lookup dictionary
- **Evidence**: [ExternalModules/LargeTransactionProcessor.cs:33-39] builds `accountToCustomer` dictionary

### datalake.customers
- **Columns used**: `id`, `first_name`, `last_name`
- **Join logic**: Used to build `customer_id -> (first_name, last_name)` lookup dictionary
- **Evidence**: [ExternalModules/LargeTransactionProcessor.cs:42-49] builds `customerNames` dictionary

### datalake.addresses (UNUSED)
- **Columns sourced**: `address_id`, `customer_id`, `address_line1`, `city`
- **Usage**: NONE -- the External module never references the `addresses` DataFrame
- **Evidence**: [ExternalModules/LargeTransactionProcessor.cs] No reference to `addresses` key in sharedState. The module only accesses `transactions`, `accounts`, and `customers`.
- See AP-1.

## Business Rules

BR-1: Only transactions with `amount > 500` are included in the output.
- Confidence: HIGH
- Evidence: [ExternalModules/LargeTransactionProcessor.cs:55] `if (amount > 500)`
- Evidence: [curated.large_transaction_log] `SELECT MIN(amount)` yields 501.00; `SELECT COUNT(*) WHERE amount <= 500` yields 0

BR-2: Each transaction row is enriched with the customer's first_name and last_name via a two-step lookup: transaction.account_id -> accounts.customer_id -> customers.(first_name, last_name).
- Confidence: HIGH
- Evidence: [ExternalModules/LargeTransactionProcessor.cs:58-60] `accountToCustomer.GetValueOrDefault(accountId, 0)` then `customerNames.GetValueOrDefault(customerId, ("", ""))`

BR-3: If no matching account is found for a transaction's account_id, customer_id defaults to 0 and names default to empty strings.
- Confidence: HIGH
- Evidence: [ExternalModules/LargeTransactionProcessor.cs:59] `accountToCustomer.GetValueOrDefault(accountId, 0)` -- missing account yields customer_id = 0
- Evidence: [ExternalModules/LargeTransactionProcessor.cs:60] `customerNames.GetValueOrDefault(customerId, ("", ""))` -- customer_id = 0 likely has no match, yielding empty names

BR-4: If no matching customer is found for a customer_id, first_name and last_name default to empty strings.
- Confidence: HIGH
- Evidence: [ExternalModules/LargeTransactionProcessor.cs:60] `GetValueOrDefault(customerId, ("", ""))`

BR-5: The output uses Append mode -- each effective date's results accumulate in the target table.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/large_transaction_log.json:42] `"writeMode": "Append"`
- Evidence: [curated.large_transaction_log] Contains 23 distinct as_of dates with varying row counts

BR-6: If accounts, customers, or transactions DataFrames are null or empty, an empty output DataFrame is returned.
- Confidence: HIGH
- Evidence: [ExternalModules/LargeTransactionProcessor.cs:16-29] null/empty checks for accounts, customers, and transactions

## Output Schema

| Column | Source | Transformation |
|--------|--------|----------------|
| transaction_id | datalake.transactions.transaction_id | Pass-through |
| account_id | datalake.transactions.account_id | Pass-through |
| customer_id | Derived: datalake.accounts.customer_id via account_id lookup | Defaults to 0 if account not found |
| first_name | Derived: datalake.customers.first_name via customer_id lookup | Defaults to "" if customer not found |
| last_name | Derived: datalake.customers.last_name via customer_id lookup | Defaults to "" if customer not found |
| txn_type | datalake.transactions.txn_type | Pass-through |
| amount | datalake.transactions.amount | Pass-through (only rows where > 500) |
| description | datalake.transactions.description | Pass-through |
| txn_timestamp | datalake.transactions.txn_timestamp | Pass-through |
| as_of | datalake.transactions.as_of | Pass-through |

## Edge Cases

- **No matching account for a transaction**: customer_id = 0, names = ""
- **No matching customer for a customer_id**: names = ""
- **NULL description**: Passed through as-is (description is nullable in source schema)
- **Empty DataFrames**: Returns empty output for accounts, customers, or transactions being null/empty
- **Append mode**: Rows accumulate across runs. No deduplication mechanism exists; re-running a date would produce duplicates.

## Anti-Patterns Identified

- **AP-1: Redundant Data Sourcing** -- The `addresses` DataSourcing module fetches address data that is never referenced by the External module. The `addresses` key is never accessed in `LargeTransactionProcessor.cs`. V2 approach: Remove the addresses DataSourcing module entirely.

- **AP-3: Unnecessary External Module** -- The LargeTransactionProcessor performs a filter (amount > 500) and two-step JOIN (transactions -> accounts -> customers). This is standard SQL: `SELECT t.transaction_id, t.account_id, a.customer_id, c.first_name, c.last_name, t.txn_type, t.amount, t.description, t.txn_timestamp, t.as_of FROM transactions t LEFT JOIN accounts a ON t.account_id = a.account_id LEFT JOIN customers c ON a.customer_id = c.id WHERE t.amount > 500`. V2 approach: Replace with a SQL Transformation.

- **AP-4: Unused Columns Sourced** -- The accounts DataSourcing includes `account_type`, `account_status`, `open_date`, `current_balance`, `interest_rate`, `credit_limit`, `apr` -- 7 columns that the External module never references. Only `account_id` and `customer_id` are used. V2 approach: Source only `account_id` and `customer_id` from accounts.

- **AP-6: Row-by-Row Iteration in External Module** -- The External module uses three separate `foreach` loops: one to build account lookup, one to build customer lookup, and one to iterate transactions and apply the filter. All are set-based operations. V2 approach: Replace with SQL Transformation (see AP-3).

- **AP-7: Hardcoded Magic Values** -- The threshold `500` appears as a literal without explanation of its business meaning. V2 approach: Document that 500 is the large-transaction threshold in the FSD; add SQL comment.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [ExternalModules/LargeTransactionProcessor.cs:55] `if (amount > 500)` |
| BR-2 | [ExternalModules/LargeTransactionProcessor.cs:58-60] two-step lookup |
| BR-3 | [ExternalModules/LargeTransactionProcessor.cs:59] `GetValueOrDefault(accountId, 0)` |
| BR-4 | [ExternalModules/LargeTransactionProcessor.cs:60] `GetValueOrDefault(customerId, ("", ""))` |
| BR-5 | [JobExecutor/Jobs/large_transaction_log.json:42] `"writeMode": "Append"` |
| BR-6 | [ExternalModules/LargeTransactionProcessor.cs:16-29] null/empty guards |

## Open Questions

- **Missing account default**: customer_id defaults to integer 0 when no account is found. This is a sentinel value, not NULL. Whether downstream consumers interpret 0 correctly is unknown but the V2 must reproduce this behavior.
  - Confidence: MEDIUM (the default of 0 is clearly coded, but its downstream implications are uncertain)
