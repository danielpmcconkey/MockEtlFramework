# CustomerTransactionActivity — Business Requirements Document

## Overview

The CustomerTransactionActivity job aggregates per-customer transaction statistics (count, total amount, debit count, credit count) for each effective date by joining transactions to accounts to derive customer ownership. Output uses Append mode, accumulating daily snapshots.

## Source Tables

| Table | Alias in Config | Columns Sourced | Purpose |
|-------|----------------|-----------------|---------|
| `datalake.transactions` | transactions | transaction_id, account_id, txn_type, amount | Transaction records to aggregate |
| `datalake.accounts` | accounts | account_id, customer_id | Maps accounts to customers for attribution |

- Join logic: The External module builds a dictionary mapping `account_id -> customer_id` from accounts, then iterates transactions looking up each transaction's account_id to find the owning customer_id. Transactions whose account_id is not found in accounts (customer_id = 0) are skipped.
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:33-38,44-46]

## Business Rules

BR-1: Output contains one row per customer who has at least one transaction for the effective date.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:41-57] Builds `customerTxns` dictionary keyed by customer_id; only customers with transactions appear.
- Evidence: [curated.customer_transaction_activity] Row counts vary by date (e.g., 196 on 2024-10-01, 200 on 2024-10-02) — only customers with transactions are included.

BR-2: Transactions are attributed to customers via the accounts table (transaction.account_id -> account.customer_id).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:33-38] Builds `accountToCustomer` dictionary.
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:44-45] `var customerId = accountToCustomer.GetValueOrDefault(accountId, 0);`

BR-3: Transactions with an account_id not found in the accounts table are silently skipped.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:46] `if (customerId == 0) continue;`

BR-4: transaction_count is the total number of transactions for the customer for that date.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:54-57] `current.count + 1`

BR-5: total_amount is the sum of all transaction amounts for the customer for that date (both debits and credits).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:57] `current.totalAmount + amount`

BR-6: debit_count is the number of transactions where txn_type = "Debit".
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:55] `var isDebit = txnType == "Debit" ? 1 : 0;`

BR-7: credit_count is the number of transactions where txn_type = "Credit".
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:56] `var isCredit = txnType == "Credit" ? 1 : 0;`

BR-8: The as_of value for all output rows is taken from the first transaction row's as_of.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:61] `var asOf = transactions.Rows[0]["as_of"];`

BR-9: If accounts DataFrame is empty (e.g., weekend dates), an empty DataFrame is returned.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:19-22] Guard: `if (accounts == null || accounts.Count == 0)`
- Evidence: [curated.customer_transaction_activity] No rows for weekend dates (2024-10-05, 2024-10-06, etc.) because accounts has no weekend data.

BR-10: If transactions DataFrame is empty, an empty DataFrame is returned.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:24-28]

BR-11: Output uses Append write mode.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_transaction_activity.json:28] `"writeMode": "Append"`
- Evidence: [curated.customer_transaction_activity] Has 23 dates of data (weekdays only in October 2024).

## Output Schema

| Column | Type | Source | Transformation |
|--------|------|--------|---------------|
| customer_id | integer | `accounts.customer_id` (via account_id lookup) | Aggregation key |
| as_of | date | `transactions.Rows[0]["as_of"]` | Taken from first transaction row |
| transaction_count | integer | Calculated | Count of all transactions per customer |
| total_amount | numeric(14,2) | Calculated | Sum of all transaction amounts per customer |
| debit_count | integer | Calculated | Count of transactions where txn_type = "Debit" |
| credit_count | integer | Calculated | Count of transactions where txn_type = "Credit" |

## Edge Cases

- **Weekend dates:** The accounts table has no weekend data, so the empty guard triggers and produces zero rows for weekend dates. The transactions table DOES have weekend data, but it cannot be processed without account-to-customer mappings. This results in weekday-only output (23 dates in October).
- **Orphan transactions:** Transactions with account_ids not present in accounts are silently excluded (customerId = 0, skipped). This could happen if an account was deleted or if transactions reference accounts in a different snapshot.
- **as_of from first row:** All output rows use the as_of from `transactions.Rows[0]`, not from each individual transaction. Since DataSourcing filters to a single effective date, all rows should have the same as_of, so this is functionally correct but slightly fragile.
- **Decimal precision:** total_amount uses C# decimal arithmetic (no explicit rounding). The curated table stores it as numeric(14,2).

## Anti-Patterns Identified

- **AP-3: Unnecessary External Module** — The External module performs: (a) dictionary lookup for account-to-customer mapping, (b) aggregation of transaction counts and amounts by customer. Both operations are standard SQL: a JOIN between transactions and accounts, followed by GROUP BY customer_id with COUNT/SUM aggregations and conditional counting via CASE. V2 approach: Replace with SQL Transformation.

- **AP-6: Row-by-Row Iteration in External Module** — Two foreach loops: one over accounts to build the lookup [ExternalModules/CustomerTxnActivityBuilder.cs:33], one over transactions to aggregate [line 42]. Both are set-based operations (JOIN + GROUP BY). V2 approach: Single SQL query with JOIN and GROUP BY.

- **AP-4: Unused Columns Sourced** — The `transaction_id` column is sourced from `datalake.transactions` [JobExecutor/Jobs/customer_transaction_activity.json:10] but is never referenced in `CustomerTxnActivityBuilder.cs`. The External module only accesses `account_id`, `txn_type`, and `amount` from transactions. V2 approach: Remove `transaction_id` from the DataSourcing columns list.

- **AP-5: Asymmetric NULL/Default Handling** — The empty guard checks accounts first, then transactions separately [lines 19-28]. If accounts is empty but transactions has data, the transactions are silently dropped. If transactions is empty but accounts has data, the same empty result occurs. The asymmetry is that the code has an explicit comment "Weekend guard on accounts empty" but no corresponding comment for transactions. However, functionally both guards produce the same empty result, so this is a minor documentation issue rather than a data issue.
  - Confidence: LOW — the behavior is consistent (empty output either way), but the asymmetric commenting is notable.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [ExternalModules/CustomerTxnActivityBuilder.cs:41-57], curated row counts |
| BR-2 | [ExternalModules/CustomerTxnActivityBuilder.cs:33-38,44-45] |
| BR-3 | [ExternalModules/CustomerTxnActivityBuilder.cs:46] |
| BR-4 | [ExternalModules/CustomerTxnActivityBuilder.cs:54-57] |
| BR-5 | [ExternalModules/CustomerTxnActivityBuilder.cs:57] |
| BR-6 | [ExternalModules/CustomerTxnActivityBuilder.cs:55] |
| BR-7 | [ExternalModules/CustomerTxnActivityBuilder.cs:56] |
| BR-8 | [ExternalModules/CustomerTxnActivityBuilder.cs:61] |
| BR-9 | [ExternalModules/CustomerTxnActivityBuilder.cs:19-22], curated no weekend data |
| BR-10 | [ExternalModules/CustomerTxnActivityBuilder.cs:24-28] |
| BR-11 | [JobExecutor/Jobs/customer_transaction_activity.json:28], curated 23 dates |

## Open Questions

- **Weekend transaction data loss:** Transactions exist on weekends in the datalake (e.g., 380 on 2024-10-05, 410 on 2024-10-06) but are excluded because accounts has no weekend snapshot. This means weekend transaction activity is lost. It's unclear whether this is intentional business logic (only report on "business days") or a gap.
  - Confidence: MEDIUM — the code behavior is deterministic (driven by accounts availability), but the business intent is ambiguous.
