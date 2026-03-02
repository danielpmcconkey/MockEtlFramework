# CoveredTransactions -- Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1 (corrected) | Only Checking account transactions are included (not Savings) |
| TC-02   | BR-2           | Only customers with active US address on effective date are included |
| TC-03   | BR-3           | Account data uses snapshot fallback (as_of <= @date, most recent per account_id) |
| TC-04   | BR-4           | Customer data uses snapshot fallback (as_of <= @date, most recent per id) |
| TC-05   | BR-5           | Address data uses exact date match (as_of = @date), no snapshot fallback |
| TC-06   | BR-6           | When multiple active US addresses exist, earliest start_date is selected |
| TC-07   | BR-7           | When multiple segments exist, first alphabetical segment_code is selected |
| TC-08   | BR-8           | Output is sorted by customer_id ASC, transaction_id DESC |
| TC-09   | BR-9           | record_count field is total qualifying row count, applied to every row |
| TC-10   | BR-9           | Zero qualifying rows emits single null-placeholder row with record_count = 0 |
| TC-11   | BR-10          | All string fields are trimmed in output |
| TC-12   | BR-11          | txn_timestamp formatted as yyyy-MM-dd HH:mm:ss |
| TC-13   | BR-11          | account_opened formatted as yyyy-MM-dd |
| TC-14   | BR-12          | Effective date read from __minEffectiveDate shared state key |
| TC-15   | BR-13          | Transactions use exact date; accounts/customers use snapshot fallback |
| TC-16   | Writer Config   | Parquet output uses Append mode with 4 part files |
| TC-17   | Output Schema   | Output contains exactly 24 columns in correct order |
| TC-18   | Edge Case       | Customer with no segment mapping gets null customer_segment |
| TC-19   | Edge Case       | Customer in accounts but not in customers table gets null name fields |
| TC-20   | Edge Case       | Account/customer with no snapshot on or before effective date excluded |
| TC-21   | Edge Case       | Multiple Checking accounts per customer included independently |
| TC-22   | Edge Case       | Expired address (end_date < @date) excluded |
| TC-23   | Edge Case       | Weekend date with no transactions produces null-placeholder row |
| TC-24   | Edge Case       | Multi-day Append run accumulates 4*N part files for N days |
| TC-25   | Edge Case       | Month-end and quarter-end boundary dates produce normal output |
| TC-26   | FSD Correction  | BR-1 correction: V1 filters Checking only, not Checking+Savings |
| TC-27   | FSD: Tier 3     | V2 uses Tier 3 (External -> Writer) with justified rationale |
| TC-28   | FSD: AP7        | Magic string literals replaced with named constants |
| TC-29   | Proofmark       | Proofmark comparison passes with zero exclusions and zero fuzzy columns |

## Test Cases

### TC-01: Only Checking account transactions are included
- **Traces to:** BR-1 (corrected per FSD Section 3)
- **Input conditions:** Run V2 job for a single weekday (e.g., 2024-10-01). The datalake.accounts table contains Checking, Savings, and other account types.
- **Expected output:** Every row in the output has `account_type = "Checking"`. No rows with `account_type = "Savings"`, `"CD"`, `"Money Market"`, or any other type appear. The FSD explicitly corrects the BRD: V1 filters for Checking only, not Checking + Savings [FSD Section 3, BRD Correction].
- **Verification method:** Read V2 Parquet output and run `SELECT DISTINCT account_type` over all output rows. Confirm the only value is `"Checking"`. Cross-reference with V1 output to confirm identical filtering.

### TC-02: Only customers with active US address on effective date
- **Traces to:** BR-2
- **Input conditions:** Run V2 job for a date where some customers with Checking accounts have no active US address (either no address at all, a non-US address, or an expired address).
- **Expected output:** Transactions for customers without an active US address on the effective date are excluded from output. Active means `country = 'US'` and `(end_date IS NULL OR end_date >= @date)` with `as_of = @date`.
- **Verification method:** Identify a customer with a Checking account but no active US address on the effective date via direct SQL query. Verify that customer's transactions do not appear in V2 output. Conversely, verify that all customers present in the output DO have an active US address for that date.

### TC-03: Account data uses snapshot fallback
- **Traces to:** BR-3
- **Input conditions:** Run V2 job for a date (e.g., 2024-10-15) where some accounts have `as_of` snapshots older than the effective date but no snapshot on the exact date.
- **Expected output:** The most recent account snapshot on or before the effective date is used. Accounts with `as_of = 2024-10-10` (and no newer snapshot by 2024-10-15) should still appear in the lookup dictionary.
- **Verification method:** Query `datalake.accounts` to find accounts whose latest `as_of <= '2024-10-15'` differs from `'2024-10-15'`. Verify those accounts are included in V2 output with data from their most recent snapshot. Compare against V1 output for the same date.

### TC-04: Customer data uses snapshot fallback
- **Traces to:** BR-4
- **Input conditions:** Same as TC-03 but for the customers table.
- **Expected output:** The most recent customer snapshot on or before the effective date is used. Customer name fields reflect the latest available snapshot, not necessarily the exact effective date.
- **Verification method:** Query `datalake.customers` using `DISTINCT ON (id) ... WHERE as_of <= @date ORDER BY id, as_of DESC`. Verify V2 output customer names match these results.

### TC-05: Address data uses exact date match only
- **Traces to:** BR-5
- **Input conditions:** Run V2 job for a date. Verify that addresses are queried with `as_of = @date` (no snapshot fallback).
- **Expected output:** Only addresses with `as_of` matching the exact effective date are considered. If a customer's only address has `as_of` on a prior date, they are treated as having no address for the current date and their transactions are excluded.
- **Verification method:** Identify a customer with an active US address at `as_of = '2024-10-01'` but no address row at `as_of = '2024-10-02'`. Run V2 for 2024-10-02 and verify the customer's transactions are excluded (no snapshot fallback for addresses).

### TC-06: Earliest active US address selected when multiple exist
- **Traces to:** BR-6
- **Input conditions:** Run V2 job for a date where at least one customer has multiple active US addresses (same `as_of`, same customer_id, both with `country = 'US'` and valid `end_date`).
- **Expected output:** The address with the earliest `start_date` is selected. Address fields (address_id, address_line1, city, state_province, postal_code, country) in the output row reflect the earliest-start-date address.
- **Verification method:** Query `datalake.addresses` for customers with multiple active US addresses on a given date. Verify V2 output uses the address with the smallest `start_date` per customer. Compare with V1 output.

### TC-07: First alphabetical segment selected when multiple exist
- **Traces to:** BR-7
- **Input conditions:** Run V2 job for a date where at least one customer has multiple segment mappings in `customers_segments`.
- **Expected output:** The segment with the first alphabetical `segment_code` (ASC) is selected. The `customer_segment` field reflects this selection.
- **Verification method:** Query `datalake.customers_segments cs JOIN datalake.segments s ON cs.segment_id = s.segment_id AND s.as_of = cs.as_of WHERE cs.as_of = @date` grouped by customer_id. For customers with multiple entries, verify V2 output uses the alphabetically-first segment_code.

### TC-08: Output sorted by customer_id ASC, transaction_id DESC
- **Traces to:** BR-8
- **Input conditions:** Run V2 job for a single weekday.
- **Expected output:** Output rows are ordered by `customer_id` ascending. Within the same `customer_id`, rows are ordered by `transaction_id` descending.
- **Verification method:** Read all V2 output rows and verify the sort order programmatically. Check that: (a) customer_id values are non-decreasing, and (b) within each customer_id group, transaction_id values are strictly decreasing. Compare sort order with V1 output.

### TC-09: record_count field is total qualifying row count
- **Traces to:** BR-9
- **Input conditions:** Run V2 job for a date with qualifying transactions (e.g., 2024-10-01).
- **Expected output:** Every row in the output has the same `record_count` value, and that value equals the total number of rows in the output. For example, if 500 rows qualify, every row has `record_count = 500`.
- **Verification method:** Read V2 output. Count total rows (excluding any null-placeholder row). Verify every row's `record_count` matches the total count. Compare with V1 output for the same date.

### TC-10: Zero qualifying rows emits null-placeholder row
- **Traces to:** BR-9 (zero-row case)
- **Input conditions:** Run V2 job for a date where no transactions link to Checking accounts with active US addresses (e.g., a weekend date, or a date engineered to have no qualifying data).
- **Expected output:** A single row is emitted where all fields except `as_of` and `record_count` are null. `as_of` is set to the effective date formatted as `yyyy-MM-dd`. `record_count` is 0.
- **Verification method:** Read V2 output for the date. Verify exactly 1 row exists. Verify `record_count = 0`, `as_of` matches the effective date, and all other fields (transaction_id, txn_timestamp, customer_id, etc.) are null. Compare with V1 output.

### TC-11: All string fields are trimmed
- **Traces to:** BR-10
- **Input conditions:** Run V2 job for a date where source data contains strings with leading or trailing whitespace (e.g., `" John "` in first_name or `"  Main St  "` in address_line1).
- **Expected output:** All string fields in the output are trimmed. No leading or trailing whitespace in: txn_type, description, name_prefix, first_name, last_name, sort_name, name_suffix, address_line1, city, state_province, postal_code, country, account_type, account_status.
- **Verification method:** Query source tables for rows with whitespace-padded values. Verify V2 output values are trimmed. Compare with V1 output to confirm identical trimming behavior.

### TC-12: txn_timestamp formatted as yyyy-MM-dd HH:mm:ss
- **Traces to:** BR-11
- **Input conditions:** Run V2 job for a date with transactions.
- **Expected output:** The `txn_timestamp` column in the output contains string values formatted exactly as `yyyy-MM-dd HH:mm:ss` (e.g., `"2024-10-01 14:30:00"`). No timezone suffix, no fractional seconds.
- **Verification method:** Read V2 output and verify every `txn_timestamp` value matches the regex `^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$`. Compare with V1 output for identical formatting.

### TC-13: account_opened formatted as yyyy-MM-dd
- **Traces to:** BR-11
- **Input conditions:** Run V2 job for a date with qualifying transactions.
- **Expected output:** The `account_opened` column contains string values formatted exactly as `yyyy-MM-dd` (e.g., `"2023-05-15"`). No time component.
- **Verification method:** Read V2 output and verify every `account_opened` value matches the regex `^\d{4}-\d{2}-\d{2}$`. Compare with V1 output.

### TC-14: Effective date sourced from __minEffectiveDate
- **Traces to:** BR-12
- **Input conditions:** Run V2 job for a specific effective date (e.g., 2024-10-15).
- **Expected output:** All queries use the injected effective date. The `as_of` column in the output matches the effective date. Transaction data is from that exact date. Account/customer snapshots are on or before that date.
- **Verification method:** Run V2 for 2024-10-15. Verify all output rows have `as_of = "2024-10-15"`. Verify transaction data matches `datalake.transactions WHERE as_of = '2024-10-15'`. Confirm the External module reads `DataSourcing.MinDateKey` [FSD Section 5].

### TC-15: Transactions exact date vs. accounts/customers snapshot fallback
- **Traces to:** BR-13
- **Input conditions:** Run V2 job for a date where transactions exist only for that exact date, but some accounts/customers have older snapshots.
- **Expected output:** Transactions are fetched only for `as_of = @date`. Accounts and customers use `as_of <= @date` with the most recent row per entity. This means a transaction from 2024-10-15 can be enriched with an account snapshot from 2024-10-10 if that is the most recent.
- **Verification method:** Cross-reference V2 output against direct queries: `datalake.transactions WHERE as_of = @date` for transactions, and `DISTINCT ON (account_id) FROM datalake.accounts WHERE as_of <= @date` for accounts. Confirm the different date strategies are applied correctly.

### TC-16: Writer configuration matches V1 (Append mode, 4 parts)
- **Traces to:** BRD Writer Configuration
- **Input conditions:** Run V2 job for two consecutive dates.
- **Expected output:** After the first run, the output directory contains 4 part files. After the second run, 8 part files exist (4 new ones appended). Part files are named `part-00000.parquet` through `part-00003.parquet` for the first run, then incrementing for the second. Data from the first run is preserved after the second run.
- **Verification method:** Inspect `Output/double_secret_curated/covered_transactions/` after each run. Count part files and verify Append semantics. Verify `numParts = 4` and `writeMode = "Append"` in V2 job config [FSD Section 7].

### TC-17: Output contains exactly 24 columns in correct order
- **Traces to:** BRD Output Schema
- **Input conditions:** Run V2 job for a single weekday.
- **Expected output:** Parquet output contains exactly 24 columns in this order: transaction_id, txn_timestamp, txn_type, amount, description, customer_id, name_prefix, first_name, last_name, sort_name, name_suffix, customer_segment, address_id, address_line1, city, state_province, postal_code, country, account_id, account_type, account_status, account_opened, as_of, record_count.
- **Verification method:** Read V2 Parquet output schema. Verify column names and order match the BRD output schema exactly. Compare with V1 output schema.

### TC-18: Customer with no segment mapping gets null customer_segment
- **Traces to:** Edge Case (BRD: Customer with no segment)
- **Input conditions:** Run V2 job for a date where at least one qualifying customer has no entry in `customers_segments` for that date.
- **Expected output:** The `customer_segment` field is null for that customer's transactions. The transaction rows are still included in the output (segment is optional).
- **Verification method:** Identify a customer with Checking account and active US address but no segment mapping. Verify their transactions appear in V2 output with `customer_segment = null`. Compare with V1 output.

### TC-19: Customer in accounts but not in customers table
- **Traces to:** Edge Case (BRD: Customer not in customers table)
- **Input conditions:** Run V2 job for a date where a customer_id referenced in accounts has no row in the customers table (or no snapshot on or before the effective date).
- **Expected output:** The customer's name fields (name_prefix, first_name, last_name, sort_name, name_suffix) are all null. The transaction rows are still included if the customer has an active US address.
- **Verification method:** Identify such a customer via direct queries. Verify their transactions appear with null name fields in V2 output. Compare with V1.

### TC-20: Account/customer with no snapshot on or before effective date
- **Traces to:** Edge Case (BRD: Snapshot fallback boundary)
- **Input conditions:** Run V2 job for the earliest available date (e.g., 2024-10-01, the first effective date). Verify behavior when an account has no `as_of` on or before 2024-10-01.
- **Expected output:** Accounts with no snapshot on or before the effective date are absent from the lookup dictionary. Their transactions are excluded from output (the account lookup fails, so the transaction is skipped).
- **Verification method:** Query `datalake.accounts` for accounts whose earliest `as_of` is after the effective date. Verify their transactions are absent from V2 output.

### TC-21: Multiple Checking accounts per customer included independently
- **Traces to:** Edge Case (BRD: Multiple accounts per customer)
- **Input conditions:** Run V2 job for a date where a customer has multiple Checking accounts, each with transactions.
- **Expected output:** Transactions from all Checking accounts for that customer appear in the output. Each transaction row has the correct `account_id` for its originating account. No deduplication at the customer level.
- **Verification method:** Identify a customer with multiple Checking accounts and transactions on the same date. Verify all transactions from all Checking accounts appear in V2 output. Compare with V1.

### TC-22: Expired address excluded
- **Traces to:** Edge Case (BRD: Address end_date handling)
- **Input conditions:** Run V2 job for a date where a customer's US address has `end_date < @date` (expired).
- **Expected output:** The expired address is excluded from the lookup. If the customer has no other active US address, their transactions are excluded from output entirely.
- **Verification method:** Query `datalake.addresses` for addresses with `end_date < @date` and `country = 'US'`. Verify those customers' transactions are excluded from V2 output (unless they have another active US address). Compare with V1.

### TC-23: Weekend date with no transactions produces null-placeholder row
- **Traces to:** Edge Case (weekend + zero-row behavior)
- **Input conditions:** Run V2 job for a Saturday or Sunday (e.g., 2024-10-05 or 2024-10-06) where `datalake.transactions` has no rows for that date.
- **Expected output:** Since there are no transactions, zero rows qualify. A single null-placeholder row is emitted with `as_of` set to the weekend date and `record_count = 0`. All other fields are null.
- **Verification method:** Read V2 Parquet output for the weekend date. Verify 1 row with `record_count = 0` and `as_of` matching the weekend date. Compare with V1 output.

### TC-24: Multi-day Append run accumulates 4*N part files
- **Traces to:** Edge Case (BRD: Write Mode Implications)
- **Input conditions:** Run V2 job for 5 consecutive dates using auto-advance mode.
- **Expected output:** The output directory contains 4 * 5 = 20 part files (4 per day in Append mode). Each day's 4 part files contain that day's qualifying rows. The total row count is the sum of qualifying rows across all 5 dates.
- **Verification method:** Count part files in `Output/double_secret_curated/covered_transactions/`. Verify the count equals 4 * N for N days run. Read all part files and verify row count sums correctly.

### TC-25: Month-end and quarter-end boundary dates produce normal output
- **Traces to:** Edge Case (date boundaries)
- **Input conditions:** Run V2 job for 2024-10-31 (month-end) and 2024-12-31 (quarter-end).
- **Expected output:** Normal output rows with no special summary rows, boundary markers, or altered behavior. No W3a/W3b/W3c wrinkles apply [FSD Section 3].
- **Verification method:** Verify row counts match the number of qualifying transactions for those dates. Verify no extra rows with aggregated values. Compare with V1 output.

### TC-26: FSD correction -- Checking only, not Checking+Savings
- **Traces to:** FSD Section 3 (BRD Correction)
- **Input conditions:** Run both V1 and V2 for the same date. Examine V1 output to confirm it does NOT include Savings account transactions.
- **Expected output:** Neither V1 nor V2 output contains any rows with `account_type = "Savings"`. The BRD's claim that Savings is included was incorrect. V2 correctly matches V1's Checking-only behavior.
- **Verification method:** Read V1 output from `Output/curated/covered_transactions/` and V2 output from `Output/double_secret_curated/covered_transactions/`. Run `SELECT DISTINCT account_type` over both outputs. Both should return only `"Checking"`. This validates the FSD's BRD correction.

### TC-27: V2 Tier 3 implementation produces identical output to V1
- **Traces to:** FSD Tier Justification
- **Input conditions:** Run both V1 and V2 jobs for the full date range (2024-10-01 through 2024-12-31).
- **Expected output:** V2 output in `Output/double_secret_curated/covered_transactions/` is data-identical to V1 output in `Output/curated/covered_transactions/`. The Tier 3 External module reproduces V1 behavior exactly.
- **Verification method:** Run Proofmark comparison between V1 and V2 output directories. Proofmark must report PASS with 100% threshold. This validates the Tier 3 justification: snapshot fallback queries and DISTINCT ON patterns require an External module.

### TC-28: Magic string literals replaced with named constants
- **Traces to:** FSD Section 10 (AP7 elimination)
- **Input conditions:** Inspect V2 External module source code (`CoveredTransactionsV2Processor.cs`).
- **Expected output:** The strings `"Checking"`, `"US"`, `"yyyy-MM-dd HH:mm:ss"`, and `"yyyy-MM-dd"` are defined as named constants with descriptive names (e.g., `CoveredAccountType`, `RequiredCountry`, `TimestampFormat`, `DateFormat`). No inline magic string literals for these values in the processing logic.
- **Verification method:** Read the V2 source code and search for `const` declarations. Verify the named constants exist and are used in place of inline literals throughout the code. This confirms AP7 elimination per FSD Section 10.

### TC-29: Proofmark comparison passes with zero exclusions and zero fuzzy
- **Traces to:** FSD Proofmark Config Design (Section 8)
- **Input conditions:** Run Proofmark with the designed config: `reader: parquet`, `threshold: 100.0`, no `excluded_columns`, no `fuzzy_columns`.
- **Expected output:** Proofmark exits with code 0 (PASS). All 24 columns match exactly between V1 and V2 output.
- **Verification method:** Execute Proofmark with config from FSD Section 8. Verify exit code is 0. Read the Proofmark report JSON and confirm zero mismatches across all columns. This validates the FSD's assertion that all output fields are deterministic and require no exclusions.
