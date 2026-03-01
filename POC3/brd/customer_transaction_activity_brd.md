# CustomerTransactionActivity — Business Requirements Document

## Overview
Produces a per-customer summary of transaction activity, aggregating transaction counts, total amounts, and debit/credit breakdowns by joining transactions to accounts via an External module (CustomerTxnActivityBuilder).

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/customer_transaction_activity.csv`
- **includeHeader**: true
- **writeMode**: Append
- **lineEnding**: LF
- **trailerFormat**: not specified (no trailer)

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.transactions | transaction_id, account_id, txn_type, amount | Effective date range (injected by executor) | [customer_transaction_activity.json:8-10] |
| datalake.accounts | account_id, customer_id | Effective date range (injected by executor) | [customer_transaction_activity.json:14-16] |

### Schema Details

**transactions**: transaction_id (integer), account_id (integer), txn_timestamp (timestamp), txn_type (varchar), amount (numeric), description (varchar), as_of (date)

**accounts**: account_id (integer), customer_id (integer), account_type (varchar), account_status (varchar), open_date (date), current_balance (numeric), interest_rate (numeric), credit_limit (numeric), apr (numeric), as_of (date)

## Business Rules

BR-1: The External module (CustomerTxnActivityBuilder) maps transactions to customers via an account_id-to-customer_id lookup built from the accounts DataFrame.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:32-38] builds accountToCustomer dictionary

BR-2: Transactions whose account_id maps to customer_id 0 (no match in the accounts lookup) are silently skipped.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:45-46] `GetValueOrDefault(accountId, 0); if (customerId == 0) continue;`

BR-3: Transaction types are classified as "Debit" or "Credit" for counting. Any txn_type that is not exactly "Debit" counts as 0 debits, and any that is not exactly "Credit" counts as 0 credits.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:55-56] `isDebit = txnType == "Debit" ? 1 : 0; isCredit = txnType == "Credit" ? 1 : 0`

BR-4: All amounts are summed as-is (no rounding). The total_amount is a raw decimal sum.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:57] `current.totalAmount + amount` — no ROUND applied

BR-5: If accounts DataFrame is null or empty, an empty output is returned (weekend guard).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:19-23]

BR-6: If transactions DataFrame is null or empty, an empty output is returned.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:25-29]

BR-7: The as_of value for ALL output rows is taken from the FIRST transaction row, not per-customer.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:61] `var asOf = transactions.Rows[0]["as_of"]`

BR-8: Aggregation is across ALL as_of dates in the effective range — not per-date. A customer with 5 debits on day 1 and 3 credits on day 2 gets transaction_count=8 in a single row.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:41-58] no date filtering in the aggregation loop

BR-9: The account_id-to-customer_id mapping uses last-write-wins when multiple as_of dates exist (accounts from later dates overwrite earlier ones in the dictionary).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:34-37] dictionary keyed by accountId, iterated in DataFrame order

BR-10: Output row order follows dictionary enumeration order (insertion order of first encounter of each customer_id).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:65-78] iterates customerTxns dictionary

BR-11: The `transaction_id` column is sourced but not used in the External module logic.
- Confidence: HIGH
- Evidence: [customer_transaction_activity.json:10] sourced; [CustomerTxnActivityBuilder.cs] not referenced

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | accounts.customer_id | Grouped key via account lookup | [CustomerTxnActivityBuilder.cs:69] |
| as_of | transactions.Rows[0]["as_of"] | First row's as_of for all output | [CustomerTxnActivityBuilder.cs:61] |
| transaction_count | transactions | Count of all transactions per customer | [CustomerTxnActivityBuilder.cs:70] |
| total_amount | transactions.amount | Sum of all amounts per customer (no rounding) | [CustomerTxnActivityBuilder.cs:71] |
| debit_count | transactions.txn_type | Count where txn_type == "Debit" | [CustomerTxnActivityBuilder.cs:72] |
| credit_count | transactions.txn_type | Count where txn_type == "Credit" | [CustomerTxnActivityBuilder.cs:73] |

## Non-Deterministic Fields
None identified.

## Write Mode Implications
**Append mode**: Each execution appends to `Output/curated/customer_transaction_activity.csv`. Re-running the same date produces duplicate rows. No trailer means no delimiter between runs.

## Edge Cases

- **Weekend guard**: Empty accounts or empty transactions returns empty output with correct schema.
- **Unmatched transactions**: Transactions whose account_id has no matching account are silently dropped (customer_id would be 0).
- **Multi-day effective range**: Aggregation crosses all dates. The single as_of from the first row may not represent the full range processed.
- **Debit+Credit != transaction_count**: If a txn_type value exists that is neither "Debit" nor "Credit", it would be counted in transaction_count but not in either debit_count or credit_count. Current data only has Debit and Credit.
- **LF line endings**: Unix-style.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Account-to-customer mapping | [CustomerTxnActivityBuilder.cs:32-38] |
| BR-2: Skip unmatched transactions | [CustomerTxnActivityBuilder.cs:45-46] |
| BR-3: Debit/Credit classification | [CustomerTxnActivityBuilder.cs:55-56] |
| BR-4: Raw sum, no rounding | [CustomerTxnActivityBuilder.cs:57] |
| BR-5: Weekend guard on accounts | [CustomerTxnActivityBuilder.cs:19-23] |
| BR-6: Empty transactions guard | [CustomerTxnActivityBuilder.cs:25-29] |
| BR-7: Single as_of from first row | [CustomerTxnActivityBuilder.cs:61] |
| BR-8: Cross-date aggregation | [CustomerTxnActivityBuilder.cs:41-58] |
| BR-9: Account lookup last-write-wins | [CustomerTxnActivityBuilder.cs:34-37] |
| BR-10: Dictionary insertion order | [CustomerTxnActivityBuilder.cs:65-78] |
| BR-11: transaction_id unused | [customer_transaction_activity.json:10] |

## Open Questions

OQ-1: Is the cross-date aggregation (all dates into one row per customer) intentional? The as_of on output uses the first row's date, which seems like a simplification.
- Confidence: MEDIUM — consistent pattern with CustomerBranchActivity
