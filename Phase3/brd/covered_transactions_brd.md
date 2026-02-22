# BRD: CoveredTransactions

## Overview
This job produces a denormalized transaction report for "covered" transactions -- transactions on Checking accounts held by customers with an active US address. It enriches each transaction with customer demographics, account details, address, and segment information, producing a comprehensive record suitable for regulatory or reporting downstream consumption.

## Source Tables

| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| transactions | datalake | transaction_id, account_id, txn_timestamp, txn_type, amount, description | Filtered by `as_of = effective_date` | [ExternalModules/CoveredTransactionProcessor.cs:31] `WHERE as_of = @date` |
| accounts | datalake | account_id, customer_id, account_type, account_status, open_date | Snapshot fallback: `DISTINCT ON (account_id) WHERE as_of <= @date ORDER BY account_id, as_of DESC`; then filtered to Checking only | [ExternalModules/CoveredTransactionProcessor.cs:36-38] |
| customers | datalake | id, prefix, first_name, last_name, sort_name, suffix | Snapshot fallback: `DISTINCT ON (id) WHERE as_of <= @date ORDER BY id, as_of DESC` | [ExternalModules/CoveredTransactionProcessor.cs:52-55] |
| addresses | datalake | address_id, customer_id, address_line1, city, state_province, postal_code, country, start_date | Filtered: `as_of = @date AND country = 'US' AND (end_date IS NULL OR end_date >= @date)`; first per customer by earliest start_date | [ExternalModules/CoveredTransactionProcessor.cs:66-70] |
| customers_segments | datalake | customer_id, segment_id | Joined with segments; `DISTINCT ON (cs.customer_id) ... WHERE cs.as_of = @date ORDER BY cs.customer_id, s.segment_code ASC` | [ExternalModules/CoveredTransactionProcessor.cs:84-88] |
| segments | datalake | segment_id, segment_code | Joined to customers_segments on segment_id and matching as_of | [ExternalModules/CoveredTransactionProcessor.cs:86] |

## Business Rules

BR-1: Only transactions associated with Checking accounts are included.
- Confidence: HIGH
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:44] `if (row["account_type"]?.ToString() == "Checking")`
- Evidence: [curated.covered_transactions] `SELECT DISTINCT account_type` yields only 'Checking' (plus null for zero-row sentinel)

BR-2: Only customers with an active US address are included.
- Confidence: HIGH
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:69] `WHERE as_of = @date AND country = 'US' AND (end_date IS NULL OR end_date >= @date)`
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:112] `if (!activeUsAddresses.TryGetValue(customerId, out var address)) continue;`
- Evidence: [curated.covered_transactions] `SELECT DISTINCT country WHERE country IS NOT NULL` yields only 'US'

BR-3: Accounts use a snapshot fallback strategy -- the most recent account snapshot on or before the effective date is used (not just the exact date).
- Confidence: HIGH
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:37] `WHERE as_of <= @date ORDER BY account_id, as_of DESC`

BR-4: Customers use a snapshot fallback strategy -- the most recent customer snapshot on or before the effective date is used.
- Confidence: HIGH
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:54] `WHERE as_of <= @date ORDER BY id, as_of DESC`

BR-5: For each customer, the earliest active US address (by start_date) is selected when multiple active addresses exist.
- Confidence: HIGH
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:70] `ORDER BY customer_id, start_date ASC`
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:78] `if (!activeUsAddresses.ContainsKey(customerId)) activeUsAddresses[customerId] = row;` (first row wins)

BR-6: Segment assignment uses the first segment alphabetically by segment_code when a customer belongs to multiple segments.
- Confidence: HIGH
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:88] `ORDER BY cs.customer_id, s.segment_code ASC` with `DISTINCT ON (cs.customer_id)`

BR-7: Output is sorted by customer_id ascending, then transaction_id descending.
- Confidence: HIGH
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:155-158] Sort comparison: `a.customerId.CompareTo(b.customerId)` then `b.transactionId.CompareTo(a.transactionId)`

BR-8: Every output row carries a `record_count` field equal to the total number of output rows for that effective date.
- Confidence: HIGH
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:162,197-198] `int recordCount = finalRows.Count;` then `finalRows[i]["record_count"] = recordCount;`

BR-9: When no qualifying transactions exist for a date, a single sentinel row is emitted with all fields null except `as_of` (set to effective date) and `record_count` (set to 0).
- Confidence: HIGH
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:164-194] Zero-row case block creates a single null row with `as_of` and `record_count = 0`

BR-10: String fields are trimmed (trailing/leading whitespace removed) on output.
- Confidence: HIGH
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:127-142] Multiple `.Trim()` calls on txn_type, description, first_name, last_name, sort_name, etc.

BR-11: Timestamps are formatted as "yyyy-MM-dd HH:mm:ss" strings and dates as "yyyy-MM-dd" strings.
- Confidence: HIGH
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:226-237] `FormatTimestamp` returns `dt.ToString("yyyy-MM-dd HH:mm:ss")`; `FormatDate` returns `d.ToString("yyyy-MM-dd")`

BR-12: Data is written in Append mode -- each effective date's output is added to the table without truncating prior dates.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/covered_transactions.json:14] `"writeMode": "Append"`

BR-13: The job is an External-only pipeline with no DataSourcing modules; it manages its own database queries directly.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/covered_transactions.json:6-9] Only module is type "External" with CoveredTransactionProcessor, followed by DataFrameWriter

BR-14: Transactions are sourced for exact effective date only (no snapshot fallback), while accounts and customers use snapshot fallback.
- Confidence: HIGH
- Evidence: [ExternalModules/CoveredTransactionProcessor.cs:31] transactions: `WHERE as_of = @date`; accounts: `WHERE as_of <= @date` (line 37); customers: `WHERE as_of <= @date` (line 54)

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| transaction_id | transactions.transaction_id | Direct pass-through | [CoveredTransactionProcessor.cs:125] |
| txn_timestamp | transactions.txn_timestamp | Formatted as "yyyy-MM-dd HH:mm:ss" string | [CoveredTransactionProcessor.cs:126,226-228] |
| txn_type | transactions.txn_type | Trimmed | [CoveredTransactionProcessor.cs:127] |
| amount | transactions.amount | Direct pass-through | [CoveredTransactionProcessor.cs:128] |
| description | transactions.description | Trimmed | [CoveredTransactionProcessor.cs:129] |
| customer_id | accounts.customer_id | Direct pass-through | [CoveredTransactionProcessor.cs:130] |
| name_prefix | customers.prefix | Trimmed | [CoveredTransactionProcessor.cs:131] |
| first_name | customers.first_name | Trimmed | [CoveredTransactionProcessor.cs:132] |
| last_name | customers.last_name | Trimmed | [CoveredTransactionProcessor.cs:133] |
| sort_name | customers.sort_name | Trimmed | [CoveredTransactionProcessor.cs:134] |
| name_suffix | customers.suffix | Trimmed | [CoveredTransactionProcessor.cs:135] |
| customer_segment | segments.segment_code | First alphabetically per customer | [CoveredTransactionProcessor.cs:136] |
| address_id | addresses.address_id | Direct pass-through | [CoveredTransactionProcessor.cs:137] |
| address_line1 | addresses.address_line1 | Trimmed | [CoveredTransactionProcessor.cs:138] |
| city | addresses.city | Trimmed | [CoveredTransactionProcessor.cs:139] |
| state_province | addresses.state_province | Trimmed | [CoveredTransactionProcessor.cs:140] |
| postal_code | addresses.postal_code | Trimmed | [CoveredTransactionProcessor.cs:141] |
| country | addresses.country | Trimmed | [CoveredTransactionProcessor.cs:142] |
| account_id | accounts.account_id | Direct pass-through | [CoveredTransactionProcessor.cs:143] |
| account_type | accounts.account_type | Trimmed (always "Checking") | [CoveredTransactionProcessor.cs:144] |
| account_status | accounts.account_status | Trimmed | [CoveredTransactionProcessor.cs:145] |
| account_opened | accounts.open_date | Formatted as "yyyy-MM-dd" string | [CoveredTransactionProcessor.cs:146,232-236] |
| as_of | effective_date | Formatted as "yyyy-MM-dd" string | [CoveredTransactionProcessor.cs:147] |
| record_count | computed | Total count of output rows for the date | [CoveredTransactionProcessor.cs:162,197-198] |

## Edge Cases

- **NULL handling**: If customer lookup fails (no customer row), customer fields are null (customer? is null, so null-conditional operators return null). If segment lookup fails, customer_segment is null. Address is guaranteed non-null for included rows (required by BR-2).
  - Evidence: [CoveredTransactionProcessor.cs:116-119] `customers.TryGetValue(customerId, out var customer)` -- customer may be null; `segments.TryGetValue(customerId, out var segmentCode)` -- segmentCode defaults to null.
- **Weekend/date fallback**: Accounts and customers use snapshot fallback (`as_of <= @date`), so weekends (where no account/customer snapshot exists) will use the most recent weekday snapshot. Transactions and addresses are exact-date only; if no transactions exist for a weekend, the zero-row sentinel is emitted.
  - Evidence: [datalake.accounts] Only 23 distinct as_of dates (weekdays only); [datalake.transactions] All 31 dates present.
- **Zero-row behavior**: When no qualifying transactions exist (no Checking account transactions, no active US addresses, or no transactions at all), a single sentinel row with all-null fields except as_of and record_count=0 is written.
  - Evidence: [CoveredTransactionProcessor.cs:164-194]

## Traceability Matrix

| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [CoveredTransactionProcessor.cs:44], [curated.covered_transactions DISTINCT account_type] |
| BR-2 | [CoveredTransactionProcessor.cs:69,112], [curated.covered_transactions DISTINCT country] |
| BR-3 | [CoveredTransactionProcessor.cs:37] |
| BR-4 | [CoveredTransactionProcessor.cs:54] |
| BR-5 | [CoveredTransactionProcessor.cs:70,78] |
| BR-6 | [CoveredTransactionProcessor.cs:88] |
| BR-7 | [CoveredTransactionProcessor.cs:155-158] |
| BR-8 | [CoveredTransactionProcessor.cs:162,197-198] |
| BR-9 | [CoveredTransactionProcessor.cs:164-194] |
| BR-10 | [CoveredTransactionProcessor.cs:127-142] |
| BR-11 | [CoveredTransactionProcessor.cs:226-237] |
| BR-12 | [covered_transactions.json:14] |
| BR-13 | [covered_transactions.json:6-9] |
| BR-14 | [CoveredTransactionProcessor.cs:31,37,54] |

## Open Questions

- **Segment behavior when customer has no segment**: The code outputs null for customer_segment when no segment mapping exists. This appears intentional but could be a data quality gap. Confidence: MEDIUM (behavior is clear from code; business intent is unclear).
- **Addresses exact date vs snapshot**: Addresses use exact-date matching (`as_of = @date`) unlike accounts/customers which use snapshot fallback. If no address snapshot exists for a date, the customer will be excluded even if they had an address on a prior date. This may be intentional (address currency requirement) or an oversight. Confidence: MEDIUM.
