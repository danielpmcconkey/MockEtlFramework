# CoveredTransactions — Business Requirements Document

## Overview

Produces a denormalized transaction-level report of all transactions from Checking accounts where the account holder has an active US address. Each row enriches a transaction with customer demographics, address, account details, and segment information. Output is appended daily.

## Source Tables

| Table | Schema | Columns Used | Purpose |
|-------|--------|-------------|---------|
| `datalake.transactions` | datalake | transaction_id, account_id, txn_timestamp, txn_type, amount, description | Base transaction records for the effective date |
| `datalake.accounts` | datalake | account_id, customer_id, account_type, account_status, open_date | Account lookup with snapshot fallback; filtered to Checking only |
| `datalake.customers` | datalake | id, prefix, first_name, last_name, sort_name, suffix | Customer demographics with snapshot fallback |
| `datalake.addresses` | datalake | address_id, customer_id, address_line1, city, state_province, postal_code, country, start_date, end_date | Active US addresses for the effective date |
| `datalake.customers_segments` | datalake | customer_id, segment_id | Customer-to-segment mapping for the effective date |
| `datalake.segments` | datalake | segment_id, segment_code | Segment code lookup joined to customers_segments |

## Business Rules

BR-1: Only transactions associated with Checking accounts are included.
- Confidence: HIGH
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:44] `if (row["account_type"]?.ToString() == "Checking")` — only Checking accounts are added to the lookup dictionary
- Evidence: [curated.covered_transactions] `SELECT DISTINCT account_type FROM curated.covered_transactions` yields only 'Checking'

BR-2: Accounts are resolved using snapshot fallback — the most recent account snapshot on or before the effective date is used.
- Confidence: HIGH
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:36-38] `SELECT DISTINCT ON (account_id) ... FROM datalake.accounts WHERE as_of <= @date ORDER BY account_id, as_of DESC`

BR-3: Customers are resolved using snapshot fallback — the most recent customer snapshot on or before the effective date is used.
- Confidence: HIGH
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:52-55] `SELECT DISTINCT ON (id) ... FROM datalake.customers WHERE as_of <= @date ORDER BY id, as_of DESC`

BR-4: Only customers with an active US address on the effective date are included. An active address has country = 'US' and either no end_date or end_date >= effective date.
- Confidence: HIGH
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:67-69] `WHERE as_of = @date AND country = 'US' AND (end_date IS NULL OR end_date >= @date)`
- Evidence: [curated.covered_transactions] `SELECT DISTINCT country FROM curated.covered_transactions WHERE country IS NOT NULL` yields only 'US'

BR-5: When a customer has multiple active US addresses, the one with the earliest start_date is used.
- Confidence: HIGH
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:70] `ORDER BY customer_id, start_date ASC`
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:78-79] First row per customer_id wins due to `if (!activeUsAddresses.ContainsKey(customerId))`

BR-6: Customer segment is resolved by joining customers_segments to segments for the effective date. When a customer has multiple segments, the one with the alphabetically first segment_code is used.
- Confidence: HIGH
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:84-88] `SELECT DISTINCT ON (cs.customer_id) cs.customer_id, s.segment_code ... ORDER BY cs.customer_id, s.segment_code ASC`

BR-7: Output is sorted by customer_id ascending, then transaction_id descending within each customer.
- Confidence: HIGH
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:155-159] Sort comparison: `a.customerId.CompareTo(b.customerId)` then `b.transactionId.CompareTo(a.transactionId)`

BR-8: Every row includes a `record_count` field set to the total number of output rows for that effective date.
- Confidence: HIGH
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:162,197-198] `int recordCount = finalRows.Count;` then `finalRows[i]["record_count"] = recordCount;`

BR-9: When there are zero qualifying transactions for an effective date, a single null-row is emitted with only `as_of` and `record_count = 0` populated.
- Confidence: HIGH
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:164-194] Zero-row case produces a row with all fields null except as_of and record_count

BR-10: String fields are trimmed of whitespace. Timestamps are formatted as 'yyyy-MM-dd HH:mm:ss'. Dates are formatted as 'yyyy-MM-dd'.
- Confidence: HIGH
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:127-146] Multiple `.Trim()` calls; [line 226-228] FormatTimestamp formats DateTime; [line 233-237] FormatDate formats dates.

BR-11: The job writes using Append mode — each daily run appends its rows to the output table.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/covered_transactions.json:14] `"writeMode": "Append"`

## Output Schema

| Column | Source | Transformation |
|--------|--------|----------------|
| transaction_id | transactions.transaction_id | Direct |
| txn_timestamp | transactions.txn_timestamp | Formatted as 'yyyy-MM-dd HH:mm:ss' |
| txn_type | transactions.txn_type | Trimmed |
| amount | transactions.amount | Direct |
| description | transactions.description | Trimmed |
| customer_id | accounts.customer_id | Direct (from account lookup) |
| name_prefix | customers.prefix | Trimmed |
| first_name | customers.first_name | Trimmed |
| last_name | customers.last_name | Trimmed |
| sort_name | customers.sort_name | Trimmed |
| name_suffix | customers.suffix | Trimmed |
| customer_segment | segments.segment_code | Via customers_segments join; first alphabetically |
| address_id | addresses.address_id | Direct (earliest active US address) |
| address_line1 | addresses.address_line1 | Trimmed |
| city | addresses.city | Trimmed |
| state_province | addresses.state_province | Trimmed |
| postal_code | addresses.postal_code | Trimmed |
| country | addresses.country | Trimmed |
| account_id | accounts.account_id | Direct |
| account_type | accounts.account_type | Trimmed (always 'Checking') |
| account_status | accounts.account_status | Trimmed |
| account_opened | accounts.open_date | Formatted as 'yyyy-MM-dd' |
| as_of | Effective date | Formatted as 'yyyy-MM-dd' |
| record_count | Computed | Count of all output rows for the date |

## Edge Cases

- **No qualifying transactions for a date**: A single null-row is emitted with as_of and record_count = 0 (BR-9).
- **Customer not found in customers table**: Customer demographic fields are null, but the row is still emitted (transaction + account + address match is sufficient). Evidence: [ExternalModules/CoveredTransactionProcessor.cs:116] `customers.TryGetValue(customerId, out var customer)` — no continue on failure.
- **Customer has no segment**: `customer_segment` is null. Evidence: [ExternalModules/CoveredTransactionProcessor.cs:119] `segments.TryGetValue(customerId, out var segmentCode)` — no continue on failure.
- **Snapshot fallback for accounts/customers**: Uses most recent snapshot <= effective date (DISTINCT ON with DESC ordering).
- **Weekend behavior**: Transactions exist on weekends (datalake.transactions has data every day), but accounts/customers may not have same-day snapshots. Snapshot fallback handles this by picking the most recent available snapshot.

## Anti-Patterns Identified

- **AP-3: Unnecessary External Module** — The External module performs multi-query database access with snapshot fallback logic (DISTINCT ON ... ORDER BY as_of DESC) and complex multi-table joins across 5+ tables. This genuinely requires procedural code because: (a) snapshot fallback queries reference data outside the effective date range that DataSourcing would provide, (b) the join logic requires intermediate lookups (transaction -> account -> customer -> address + segment). V2 approach: This External module is justified; keep as External but clean up the code.

- **AP-5: Asymmetric NULL/Default Handling** — When customer lookup fails, demographic fields become null. When segment lookup fails, segment becomes null. But when a customer has scores from only some bureaus, that scenario doesn't apply here. The asymmetry is between `customer` fields (null on miss) and `account` fields (row is skipped on miss) — this is intentional business logic (account is required, customer demographics are optional enrichment). V2 approach: Document the intentional asymmetry; reproduce same behavior.

- **AP-7: Hardcoded Magic Values** — The string "Checking" is hardcoded at [line 44], "US" at [line 69]. These are business filter criteria with clear meaning but no explanatory comments. V2 approach: Add SQL comments explaining that "Checking" filters for covered (FDIC-insured checking) accounts and "US" filters for US-based addresses.

- **AP-10: Missing Dependency Declarations** — This job has no declared dependencies, yet it uses snapshot fallback that reads historical data across all dates. While it queries datalake directly and doesn't depend on curated tables from other jobs, the lack of any dependency declaration is technically correct in this case since all sources are raw datalake tables. V2 approach: No dependency needed since all sources are datalake.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [ExternalModules/CoveredTransactionProcessor.cs:44] |
| BR-2 | [ExternalModules/CoveredTransactionProcessor.cs:36-38] |
| BR-3 | [ExternalModules/CoveredTransactionProcessor.cs:52-55] |
| BR-4 | [ExternalModules/CoveredTransactionProcessor.cs:67-69] |
| BR-5 | [ExternalModules/CoveredTransactionProcessor.cs:70,78-79] |
| BR-6 | [ExternalModules/CoveredTransactionProcessor.cs:84-88] |
| BR-7 | [ExternalModules/CoveredTransactionProcessor.cs:155-159] |
| BR-8 | [ExternalModules/CoveredTransactionProcessor.cs:162,197-198] |
| BR-9 | [ExternalModules/CoveredTransactionProcessor.cs:164-194] |
| BR-10 | [ExternalModules/CoveredTransactionProcessor.cs:127-146,226-237] |
| BR-11 | [JobExecutor/Jobs/covered_transactions.json:14] |

## Open Questions

- **Weekend transaction coverage**: Transactions exist on weekends in datalake, but accounts/customers may lack same-day snapshots. The snapshot fallback approach handles this gracefully, but it means weekend transaction rows reference the most recent weekday account/customer state. Confidence: HIGH that this is intentional behavior.
