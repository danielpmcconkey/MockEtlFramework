# BRD: CustomerTransactionActivity

## Overview
This job produces a per-customer transaction activity summary for each effective date, aggregating transaction counts, total amounts, and debit/credit counts by linking transactions to customers through accounts. It writes to `curated.customer_transaction_activity` using Append mode, accumulating daily snapshots.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| transactions | datalake | transaction_id, account_id, txn_type, amount | Transaction data; joined to accounts via account_id to resolve customer_id | [JobExecutor/Jobs/customer_transaction_activity.json:7-11] |
| accounts | datalake | account_id, customer_id | Lookup table for account_id -> customer_id mapping | [JobExecutor/Jobs/customer_transaction_activity.json:13-16] |

## Business Rules
BR-1: Transactions are attributed to customers by looking up the account_id in the accounts table to resolve customer_id.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:32-38] Builds `accountToCustomer` dictionary mapping account_id -> customer_id
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:44] `accountToCustomer.GetValueOrDefault(accountId, 0)`

BR-2: Transactions with an account_id not found in the accounts table are silently skipped.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:45-46] `if (customerId == 0) continue;` — GetValueOrDefault returns 0 for missing keys

BR-3: For each customer, the job computes: transaction_count, total_amount, debit_count, credit_count.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:41-57] Groups by customer_id, accumulates count, total amount, debit count, credit count

BR-4: Debit count increments by 1 for each transaction where txn_type == "Debit"; credit count increments for txn_type == "Credit".
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:55-56] `var isDebit = txnType == "Debit" ? 1 : 0; var isCredit = txnType == "Credit" ? 1 : 0;`

BR-5: Transactions with a txn_type other than "Debit" or "Credit" are counted in transaction_count and total_amount but not in debit_count or credit_count.
- Confidence: MEDIUM
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:55-57] Only "Debit" and "Credit" are checked; other types would contribute 0 to both debit and credit counts but still be counted in the overall transaction_count
- Evidence: Current data appears to only have "Debit" and "Credit" txn_types

BR-6: The as_of value for all output rows is taken from the first transaction row.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:61] `var asOf = transactions.Rows[0]["as_of"];`

BR-7: If accounts is null or empty, the output is an empty DataFrame (weekend guard).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:19-23] `if (accounts == null || accounts.Count == 0)` — comment says "Weekend guard on accounts empty"

BR-8: If transactions is null or empty, the output is an empty DataFrame.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:25-29]

BR-9: The output is written using Append mode.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_transaction_activity.json:28] `"writeMode": "Append"`
- Evidence: [curated.customer_transaction_activity] Contains 23 distinct as_of dates (weekdays only, matching accounts table availability)

BR-10: Output rows appear only for weekday dates because the accounts table is weekday-only.
- Confidence: HIGH
- Evidence: [curated.customer_transaction_activity] 23 distinct dates matching weekday-only pattern
- Evidence: The guard clause at [ExternalModules/CustomerTxnActivityBuilder.cs:19-23] returns empty when accounts is empty, which happens on weekends since accounts has no weekend data

BR-11: total_amount includes all transactions regardless of type (sum of amount field).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerTxnActivityBuilder.cs:57] `current.totalAmount + amount` — no type filtering for amount summation

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | accounts.customer_id (via account_id lookup) | Resolved from transaction's account_id | [ExternalModules/CustomerTxnActivityBuilder.cs:44-46] |
| as_of | transactions.Rows[0]["as_of"] | First transaction row's as_of, applied to all output rows | [ExternalModules/CustomerTxnActivityBuilder.cs:61] |
| transaction_count | transactions | Count of all transactions per customer | [ExternalModules/CustomerTxnActivityBuilder.cs:57] `current.count + 1` |
| total_amount | transactions.amount | Sum of all transaction amounts per customer | [ExternalModules/CustomerTxnActivityBuilder.cs:57] `current.totalAmount + amount` |
| debit_count | transactions (txn_type == "Debit") | Count of debit transactions per customer | [ExternalModules/CustomerTxnActivityBuilder.cs:55, 57] |
| credit_count | transactions (txn_type == "Credit") | Count of credit transactions per customer | [ExternalModules/CustomerTxnActivityBuilder.cs:56, 57] |

## Edge Cases
- **Weekend handling**: The accounts table has no weekend data. On weekend effective dates, accounts DataSourcing returns empty, triggering the guard clause. The job produces an empty DataFrame, which with Append mode means no rows are written. This is why only 23 dates appear in the output. [ExternalModules/CustomerTxnActivityBuilder.cs:19-23]
- **Orphan transactions**: Transactions whose account_id is not in the accounts table are silently dropped (`continue`). [ExternalModules/CustomerTxnActivityBuilder.cs:45-46]
- **Single as_of per run**: The as_of is taken from the first transaction row and applied uniformly to all output rows. Since the executor runs one effective date at a time, all transactions share the same as_of. [ExternalModules/CustomerTxnActivityBuilder.cs:61]
- **Non-Debit/Credit types**: Any txn_type other than "Debit" or "Credit" would be counted in transaction_count and total_amount but not in debit_count or credit_count.
- **Multiple accounts per customer**: If a customer has multiple accounts, all their transactions across all accounts are aggregated together. [ExternalModules/CustomerTxnActivityBuilder.cs:44] — all account_ids mapping to the same customer_id contribute to the same aggregation

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [ExternalModules/CustomerTxnActivityBuilder.cs:32-38, 44] |
| BR-2 | [ExternalModules/CustomerTxnActivityBuilder.cs:45-46] |
| BR-3 | [ExternalModules/CustomerTxnActivityBuilder.cs:41-57] |
| BR-4 | [ExternalModules/CustomerTxnActivityBuilder.cs:55-56] |
| BR-5 | [ExternalModules/CustomerTxnActivityBuilder.cs:55-57] |
| BR-6 | [ExternalModules/CustomerTxnActivityBuilder.cs:61] |
| BR-7 | [ExternalModules/CustomerTxnActivityBuilder.cs:19-23] |
| BR-8 | [ExternalModules/CustomerTxnActivityBuilder.cs:25-29] |
| BR-9 | [JobExecutor/Jobs/customer_transaction_activity.json:28] |
| BR-10 | [curated.customer_transaction_activity dates], [datalake.accounts dates] |
| BR-11 | [ExternalModules/CustomerTxnActivityBuilder.cs:57] |

## Open Questions
- **Non-standard txn_types**: The code only explicitly checks for "Debit" and "Credit". If other txn_types exist, they would still be counted and summed but not classified. Current data verification needed to confirm only these two types exist. Confidence: MEDIUM.
- **Output row ordering**: The output rows are ordered by dictionary iteration order (customerTxns dictionary), which is not guaranteed to be deterministic. However, since Append mode just inserts rows, this only affects the insertion order, not the data correctness. Confidence: HIGH that data is correct regardless of order.
