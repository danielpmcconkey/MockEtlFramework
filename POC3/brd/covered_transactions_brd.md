# CoveredTransactions — Business Requirements Document

## Overview
Produces a denormalized view of transactions for Checking and Savings account holders who have an active US address, enriched with customer demographics, address, account, and segment information. This supports downstream regulatory or compliance reporting on "covered" transactions.

## Output Type
ParquetFileWriter (via External module for data assembly, then framework ParquetFileWriter for output)

## Writer Configuration
- **source**: `output`
- **outputDirectory**: `Output/curated/covered_transactions/`
- **numParts**: 4
- **writeMode**: Append

## Source Tables

| Table | Columns | Filters | Evidence |
|-------|---------|---------|----------|
| datalake.transactions | transaction_id, account_id, txn_timestamp, txn_type, amount, description | `as_of = @date` (effective date) | [CoveredTransactionProcessor.cs:30-32] |
| datalake.accounts | account_id, customer_id, account_type, account_status, open_date | `as_of <= @date` with DISTINCT ON (account_id) snapshot fallback; then filtered to `account_type IN ('Checking', 'Savings')` in code | [CoveredTransactionProcessor.cs:35-39, 44] |
| datalake.customers | id, prefix, first_name, last_name, sort_name, suffix | `as_of <= @date` with DISTINCT ON (id) snapshot fallback | [CoveredTransactionProcessor.cs:52-56] |
| datalake.addresses | address_id, customer_id, address_line1, city, state_province, postal_code, country, start_date | `as_of = @date AND country = 'US' AND (end_date IS NULL OR end_date >= @date)` | [CoveredTransactionProcessor.cs:66-70] |
| datalake.customers_segments + datalake.segments | cs.customer_id, s.segment_code | `cs.as_of = @date`; joined on `cs.segment_id = s.segment_id AND s.as_of = cs.as_of`; DISTINCT ON (cs.customer_id) ordered by segment_code ASC | [CoveredTransactionProcessor.cs:84-89] |

## Business Rules

BR-1: Only transactions linked to **Checking** or **Savings** accounts are included.
- Confidence: HIGH
- Evidence: [CoveredTransactionProcessor.cs:44] `if (row["account_type"]?.ToString() == "Checking" || row["account_type"]?.ToString() == "Savings")`

BR-2: Only customers with an **active US address** on the effective date are included. Active means `country = 'US'` and `(end_date IS NULL OR end_date >= @date)`.
- Confidence: HIGH
- Evidence: [CoveredTransactionProcessor.cs:69] SQL WHERE clause; [CoveredTransactionProcessor.cs:112-113] code checks `activeUsAddresses.TryGetValue`

BR-3: Account data uses **snapshot fallback** — the most recent snapshot on or before the effective date is used (`as_of <= @date`, `DISTINCT ON (account_id) ... ORDER BY account_id, as_of DESC`).
- Confidence: HIGH
- Evidence: [CoveredTransactionProcessor.cs:36-39]

BR-4: Customer data uses **snapshot fallback** — same pattern as accounts (`as_of <= @date`, `DISTINCT ON (id) ... ORDER BY id, as_of DESC`).
- Confidence: HIGH
- Evidence: [CoveredTransactionProcessor.cs:53-55]

BR-5: Address data does NOT use snapshot fallback — it uses exact date match (`as_of = @date`).
- Confidence: HIGH
- Evidence: [CoveredTransactionProcessor.cs:69] `WHERE as_of = @date`

BR-6: When multiple active US addresses exist for a customer, the one with the **earliest start_date** is selected.
- Confidence: HIGH
- Evidence: [CoveredTransactionProcessor.cs:71] `ORDER BY customer_id, start_date ASC`; [CoveredTransactionProcessor.cs:78-79] first row per customer_id wins

BR-7: When multiple segments exist for a customer, the one with the **first alphabetical segment_code** is selected.
- Confidence: HIGH
- Evidence: [CoveredTransactionProcessor.cs:88] `ORDER BY cs.customer_id, s.segment_code ASC`

BR-8: Output is sorted by **customer_id ASC, transaction_id DESC**.
- Confidence: HIGH
- Evidence: [CoveredTransactionProcessor.cs:155-159]

BR-9: The `record_count` field is set to the total number of output rows (after filtering), applied to every row. If zero rows qualify, a single null-placeholder row is emitted with `record_count = 0`.
- Confidence: HIGH
- Evidence: [CoveredTransactionProcessor.cs:162-198]

BR-10: String fields are trimmed (`.Trim()`) on output.
- Confidence: HIGH
- Evidence: [CoveredTransactionProcessor.cs:127-142] multiple `.Trim()` calls on string fields

BR-11: `txn_timestamp` is formatted as `yyyy-MM-dd HH:mm:ss`. `account_opened` is formatted as `yyyy-MM-dd`.
- Confidence: HIGH
- Evidence: [CoveredTransactionProcessor.cs:225-237] `FormatTimestamp` and `FormatDate` helper methods

BR-12: The job processes one effective date at a time. The effective date is read from `DataSourcing.MinDateKey` (`__minEffectiveDate`).
- Confidence: HIGH
- Evidence: [CoveredTransactionProcessor.cs:22] `var effectiveDate = (DateOnly)sharedState[DataSourcing.MinDateKey];`

BR-13: Transactions are fetched for exact effective date only (`as_of = @date`), while accounts and customers use snapshot fallback.
- Confidence: HIGH
- Evidence: [CoveredTransactionProcessor.cs:31] vs [CoveredTransactionProcessor.cs:37]

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| transaction_id | transactions.transaction_id | Direct | [CoveredTransactionProcessor.cs:125] |
| txn_timestamp | transactions.txn_timestamp | Formatted as `yyyy-MM-dd HH:mm:ss` | [CoveredTransactionProcessor.cs:126, 225-228] |
| txn_type | transactions.txn_type | Trimmed | [CoveredTransactionProcessor.cs:127] |
| amount | transactions.amount | Direct | [CoveredTransactionProcessor.cs:128] |
| description | transactions.description | Trimmed | [CoveredTransactionProcessor.cs:129] |
| customer_id | accounts.customer_id | Direct | [CoveredTransactionProcessor.cs:130] |
| name_prefix | customers.prefix | Trimmed | [CoveredTransactionProcessor.cs:131] |
| first_name | customers.first_name | Trimmed | [CoveredTransactionProcessor.cs:132] |
| last_name | customers.last_name | Trimmed | [CoveredTransactionProcessor.cs:133] |
| sort_name | customers.sort_name | Trimmed | [CoveredTransactionProcessor.cs:134] |
| name_suffix | customers.suffix | Trimmed | [CoveredTransactionProcessor.cs:135] |
| customer_segment | segments.segment_code (via customers_segments) | First alphabetically per customer | [CoveredTransactionProcessor.cs:136] |
| address_id | addresses.address_id | Direct | [CoveredTransactionProcessor.cs:137] |
| address_line1 | addresses.address_line1 | Trimmed | [CoveredTransactionProcessor.cs:138] |
| city | addresses.city | Trimmed | [CoveredTransactionProcessor.cs:139] |
| state_province | addresses.state_province | Trimmed | [CoveredTransactionProcessor.cs:140] |
| postal_code | addresses.postal_code | Trimmed | [CoveredTransactionProcessor.cs:141] |
| country | addresses.country | Trimmed | [CoveredTransactionProcessor.cs:142] |
| account_id | accounts.account_id | Direct | [CoveredTransactionProcessor.cs:143] |
| account_type | accounts.account_type | Trimmed (always "Checking" or "Savings") | [CoveredTransactionProcessor.cs:144] |
| account_status | accounts.account_status | Trimmed | [CoveredTransactionProcessor.cs:145] |
| account_opened | accounts.open_date | Formatted as `yyyy-MM-dd` | [CoveredTransactionProcessor.cs:146, 232-236] |
| as_of | Effective date | Formatted as `yyyy-MM-dd` string | [CoveredTransactionProcessor.cs:147] |
| record_count | Computed | Count of total output rows; 0 if no qualifying rows | [CoveredTransactionProcessor.cs:162-198] |

## Non-Deterministic Fields
None identified. All fields are derived deterministically from source data and the effective date.

## Write Mode Implications
- **Append** mode: Each effective date run appends new Parquet part files to the output directory. Over multiple days, the directory accumulates data from all processed dates.
- The ParquetFileWriter with `numParts: 4` splits the output across 4 part files per run.
- On multi-day auto-advance, each day's run appends independently, so the directory will contain `4 * N` part files for N days of data.

## Edge Cases

1. **Zero qualifying rows**: If no transactions match (no Checking accounts with active US addresses), a single null-placeholder row is emitted with `as_of` set and `record_count = 0`. All other fields are null. [CoveredTransactionProcessor.cs:166-194]

2. **Customer with no segment mapping**: `segments.TryGetValue` returns false; `customer_segment` is set to null. [CoveredTransactionProcessor.cs:119]

3. **Customer exists in account but not in customers table**: `customers.TryGetValue` returns false; all name fields are null. [CoveredTransactionProcessor.cs:116-117]

4. **Snapshot fallback for accounts/customers**: Uses most recent `as_of <= @date`. If an account or customer has no snapshot on or before the effective date, they will not appear in the lookup dictionaries and associated transactions will be excluded.

5. **Multiple Checking/Savings accounts per customer**: Each account's transactions are included independently; there is no deduplication at the customer level.

6. **Address end_date handling**: Addresses with `end_date IS NULL` (open-ended) or `end_date >= @date` (still active) qualify. Expired addresses are excluded.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Checking+Savings filter | [CoveredTransactionProcessor.cs:44] |
| Active US address filter | [CoveredTransactionProcessor.cs:69, 112-113] |
| Account snapshot fallback | [CoveredTransactionProcessor.cs:36-39] |
| Customer snapshot fallback | [CoveredTransactionProcessor.cs:53-55] |
| Address exact date match | [CoveredTransactionProcessor.cs:69] |
| Earliest address selection | [CoveredTransactionProcessor.cs:71, 78-79] |
| First alphabetical segment | [CoveredTransactionProcessor.cs:88] |
| Sort order (customer ASC, txn DESC) | [CoveredTransactionProcessor.cs:155-159] |
| Zero-row null placeholder | [CoveredTransactionProcessor.cs:166-194] |
| record_count population | [CoveredTransactionProcessor.cs:162, 197-198] |
| String trimming | [CoveredTransactionProcessor.cs:127-142] |
| Timestamp formatting | [CoveredTransactionProcessor.cs:225-228] |
| Date formatting | [CoveredTransactionProcessor.cs:232-236] |
| Append write mode | [covered_transactions.json:14] |
| 4 Parquet parts | [covered_transactions.json:13] |
| firstEffectiveDate = 2024-10-01 | [covered_transactions.json:3] |

## Open Questions
None.
