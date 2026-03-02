# CustomerCreditSummary -- Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | Compound empty guard: any of 4 sources null/empty produces empty output |
| TC-02   | BR-2           | Average credit score computed with decimal precision per customer |
| TC-03   | BR-2           | Customer with no credit scores gets DBNull.Value (empty field in CSV) |
| TC-04   | BR-3           | Total loan balance and loan count aggregated per customer |
| TC-05   | BR-4           | Total account balance and account count aggregated per customer (no filtering) |
| TC-06   | BR-5           | Customer with no loans gets total_loan_balance=0 and loan_count=0 |
| TC-07   | BR-6           | Customer with no accounts gets total_account_balance=0 and account_count=0 |
| TC-08   | BR-7           | as_of column taken from the customer row's as_of value |
| TC-09   | BR-8           | Customer-driven iteration: every customer produces exactly one output row |
| TC-10   | BR-9 (AP1)     | Segments table is NOT sourced in V2 config |
| TC-11   | BR-10 (AP4)    | Unused columns removed from accounts, credit_scores, loan_accounts |
| TC-12   | Writer Config  | CSV output uses Overwrite mode, LF line endings, header, no trailer |
| TC-13   | Edge Case      | All four sources empty simultaneously produces zero-row output |
| TC-14   | Edge Case      | Only one source empty (e.g., credit_scores empty, others non-empty) produces empty output |
| TC-15   | Edge Case      | NULL values in first_name/last_name coalesce to empty string |
| TC-16   | Edge Case      | Weekend date (no data in datalake) produces empty output |
| TC-17   | Edge Case      | Boundary date: month-end (2024-10-31) produces normal output |
| TC-18   | Edge Case      | Boundary date: quarter-end (2024-12-31) produces normal output |
| TC-19   | Edge Case      | Multi-day Overwrite run: only last effective date's output survives (W9) |
| TC-20   | Edge Case      | No rounding applied to balances or avg_credit_score |
| TC-21   | FSD: Output    | Output column order matches spec exactly (9 columns) |
| TC-22   | FSD: Tier 2    | V2 uses Tier 2 chain (DataSourcing -> External -> CsvFileWriter) |
| TC-23   | Proofmark      | Proofmark comparison passes with zero exclusions and zero fuzzy columns |

## Test Cases

### TC-01: Compound empty guard -- any source null/empty produces empty output
- **Traces to:** BR-1
- **Input conditions:** Simulate a scenario where one of the four required DataFrames (customers, accounts, credit_scores, loan_accounts) is empty while the others contain data. Repeat for each of the four sources individually being empty.
- **Expected output:** In every case, the output CSV contains only the header row and zero data rows. The file is not absent -- it exists with the header `customer_id,first_name,last_name,avg_credit_score,total_loan_balance,total_account_balance,loan_count,account_count,as_of`.
- **Verification method:** Run the V2 job for a date where one source has zero rows. Verify the CSV output contains exactly 1 line (the header). This validates the compound empty guard documented in BRD BR-1 and FSD Section 2, Module 5 (responsibility #2).

### TC-02: Average credit score computed with decimal precision
- **Traces to:** BR-2
- **Input conditions:** Run V2 job for a date where customers have credit scores from multiple bureaus. For example, a customer with scores [750, 680, 710] should average to 713.33333333333333333333333333 (decimal precision, not double).
- **Expected output:** The `avg_credit_score` column contains the full decimal-precision average, not a truncated double-precision value. The CSV string representation matches what C#'s `List<decimal>.Average()` produces.
- **Verification method:** Query `datalake.credit_scores` for a customer with known multiple scores. Compute the expected average using `decimal` arithmetic. Compare against the V2 CSV output for that customer. This is the core justification for Tier 2 -- SQLite's `AVG()` returns double (~15-16 significant digits) while V1 uses C# decimal (~28-29 significant digits) [FSD Section 1, Tier Justification].

### TC-03: Customer with no credit scores gets empty field in CSV
- **Traces to:** BR-2
- **Input conditions:** Run V2 job for a date where at least one customer exists in `datalake.customers` but has no corresponding rows in `datalake.credit_scores`.
- **Expected output:** That customer's row has an empty `avg_credit_score` field in the CSV (two consecutive commas or trailing comma, depending on column position). The value is `DBNull.Value`, which `CsvFileWriter` renders as an empty string.
- **Verification method:** Identify a customer with no credit scores by querying `datalake.credit_scores`. Locate that customer's row in the V2 CSV output and verify the `avg_credit_score` field is empty. Validates BR-2's DBNull.Value handling and FSD Section 10, Key Design Decision #3.

### TC-04: Loan balance and count aggregation per customer
- **Traces to:** BR-3
- **Input conditions:** Run V2 job for a date where customers have multiple loan accounts. Pick a customer with known loan data.
- **Expected output:** `total_loan_balance` equals the sum of all `current_balance` values from `datalake.loan_accounts` for that customer and date. `loan_count` equals the number of loan rows for that customer and date.
- **Verification method:** Query `datalake.loan_accounts` for a specific customer_id and as_of date: `SELECT SUM(current_balance), COUNT(*) FROM datalake.loan_accounts WHERE customer_id = <id> AND as_of = '<date>'`. Compare results against the V2 CSV output for that customer. Validates BR-3 and FSD Section 2, Module 5 (responsibility #4).

### TC-05: Account balance and count aggregation per customer (no filtering)
- **Traces to:** BR-4
- **Input conditions:** Run V2 job for a date where customers have multiple accounts of varying types and statuses.
- **Expected output:** `total_account_balance` equals the sum of ALL `current_balance` values from `datalake.accounts` for the customer -- regardless of `account_type` or `account_status`. `account_count` equals the total number of account rows. No filtering is applied.
- **Verification method:** Query `datalake.accounts` for a specific customer: `SELECT SUM(current_balance), COUNT(*) FROM datalake.accounts WHERE customer_id = <id> AND as_of = '<date>'`. Compare against the V2 CSV. Critically, verify that inactive/closed accounts are included (no status filter). Validates BR-4 and FSD Section 2, Module 5 (responsibility #5).

### TC-06: Customer with no loans gets zero defaults
- **Traces to:** BR-5
- **Input conditions:** Run V2 job for a date where at least one customer exists in `datalake.customers` but has no corresponding rows in `datalake.loan_accounts`.
- **Expected output:** That customer's row shows `total_loan_balance=0` and `loan_count=0` in the CSV output.
- **Verification method:** Identify a customer with no loans via: `SELECT c.id FROM datalake.customers c WHERE c.as_of = '<date>' AND c.id NOT IN (SELECT DISTINCT customer_id FROM datalake.loan_accounts WHERE as_of = '<date>')`. Verify the V2 CSV output for that customer shows 0 for both fields. Validates BR-5 and FSD Section 10 pseudocode default values.

### TC-07: Customer with no accounts gets zero defaults
- **Traces to:** BR-6
- **Input conditions:** Run V2 job for a date where at least one customer has no rows in `datalake.accounts`. Note: the BRD observes this is unlikely given the compound empty guard requires accounts to be non-empty, but a customer could individually have no accounts while other customers do.
- **Expected output:** That customer's row shows `total_account_balance=0` and `account_count=0`.
- **Verification method:** Identify a customer with no accounts via: `SELECT c.id FROM datalake.customers c WHERE c.as_of = '<date>' AND c.id NOT IN (SELECT DISTINCT customer_id FROM datalake.accounts WHERE as_of = '<date>')`. Verify the V2 CSV output. Validates BR-6 and FSD Section 10.

### TC-08: as_of column from customer row
- **Traces to:** BR-7
- **Input conditions:** Run V2 job for a specific effective date (e.g., 2024-10-01).
- **Expected output:** Every row's `as_of` value matches the effective date from the customer row. Since the executor runs one effective date at a time with Overwrite mode, all rows in the output share the same `as_of`.
- **Verification method:** Read the V2 CSV output and verify all `as_of` values equal the effective date. Cross-reference with `SELECT DISTINCT as_of FROM datalake.customers WHERE as_of = '2024-10-01'`. Validates BR-7 and FSD Section 4.

### TC-09: Customer-driven iteration -- one output row per customer
- **Traces to:** BR-8
- **Input conditions:** Run V2 job for a single effective date (e.g., 2024-10-01).
- **Expected output:** The number of data rows in the CSV equals the number of customers in `datalake.customers` for that effective date. Each customer_id appears exactly once.
- **Verification method:** Count data rows in the V2 CSV (excluding header). Compare to `SELECT COUNT(*) FROM datalake.customers WHERE as_of = '2024-10-01'`. Also verify `SELECT COUNT(DISTINCT customer_id)` in the CSV output equals the total row count (no duplicate customer_ids). Validates BR-8 and FSD Section 2, Module 5 (responsibility #6).

### TC-10: Segments table not sourced in V2
- **Traces to:** BR-9 (AP1 elimination)
- **Input conditions:** Inspect the V2 job config JSON (`customer_credit_summary_v2.json`).
- **Expected output:** The config contains exactly four DataSourcing entries: `customers`, `accounts`, `credit_scores`, `loan_accounts`. There is NO DataSourcing entry for `segments`.
- **Verification method:** Read the V2 config file and verify the modules array. Count DataSourcing entries and confirm none references `table: "segments"`. Validates BR-9 and FSD Section 3, AP1 elimination.

### TC-11: Unused columns removed from V2 DataSourcing
- **Traces to:** BR-10 (AP4 elimination)
- **Input conditions:** Inspect the V2 job config JSON.
- **Expected output:** Column lists in V2 DataSourcing entries:
  - `accounts`: `["customer_id", "current_balance"]` (removed: account_id, account_type, account_status)
  - `credit_scores`: `["customer_id", "score"]` (removed: credit_score_id, bureau)
  - `loan_accounts`: `["customer_id", "current_balance"]` (removed: loan_id, loan_type)
- **Verification method:** Read the V2 config and compare column arrays against the V1 config. Verify removed columns are those documented as unused in BRD BR-10 and FSD Section 2, Modules 2-4 (AP4 fixes).

### TC-12: Writer configuration matches V1
- **Traces to:** BRD Writer Configuration
- **Input conditions:** Inspect the V2 job config JSON and run the job for a single date.
- **Expected output:** CSV file with:
  - Header row present (first line is column names)
  - LF line endings (not CRLF)
  - No trailer line at end of file
  - File at `Output/double_secret_curated/customer_credit_summary.csv`
  - Overwrite mode (file replaced each run)
- **Verification method:** Read the V2 config and verify: `includeHeader: true`, `writeMode: "Overwrite"`, `lineEnding: "LF"`, no `trailerFormat`. Inspect the output file bytes to confirm LF endings (0x0A, not 0x0D 0x0A). Validates FSD Section 7 writer config matching.

### TC-13: All four sources empty simultaneously
- **Traces to:** Edge Case (BR-1 compound guard)
- **Input conditions:** Run V2 job for a date where all four tables (customers, accounts, credit_scores, loan_accounts) have zero rows.
- **Expected output:** The CSV output contains only the header row. No data rows. No error or exception.
- **Verification method:** Run for a date known to have no data (e.g., a weekend date if weekend data is absent). Verify the CSV has exactly 1 line (the header). This is a special case of TC-01 where all sources are simultaneously empty.

### TC-14: One source empty triggers compound guard
- **Traces to:** Edge Case (BR-1)
- **Input conditions:** Scenario where credit_scores has zero rows for a date but customers, accounts, and loan_accounts have data. (This may need to be validated against actual data availability.)
- **Expected output:** Empty output -- header only, zero data rows. The compound guard treats any single empty source as a reason to produce no output.
- **Verification method:** This is the key behavioral distinction of BR-1. Most jobs only guard on the primary table (customers). This job guards on ALL four sources. If all four always have data on the same dates, this test may only be verifiable through code review of the External module.

### TC-15: NULL first_name/last_name coalesce to empty string
- **Traces to:** Edge Case (Output Schema)
- **Input conditions:** Run V2 job for a date where a customer has NULL `first_name` or `last_name` in `datalake.customers`.
- **Expected output:** The output CSV shows an empty string (not the literal "NULL" or any other placeholder) for the null name field.
- **Verification method:** Query `datalake.customers` for rows with NULL first_name or last_name. If found, verify the corresponding V2 CSV output field is empty. Validates FSD Section 4 output schema (`ToString()` with null coalesce to `""`).

### TC-16: Weekend date produces empty output
- **Traces to:** Edge Case (no weekend data in datalake)
- **Input conditions:** Run V2 job for a weekend date (e.g., 2024-10-05 Saturday or 2024-10-06 Sunday).
- **Expected output:** DataSourcing returns zero rows for all four tables (assuming no weekend data exists). The compound empty guard (BR-1) triggers, producing a CSV with header only.
- **Verification method:** Verify `datalake.customers` has no rows for the weekend date. Run the V2 job and confirm the CSV output is header-only.

### TC-17: Month-end boundary produces normal output
- **Traces to:** Edge Case (boundary dates)
- **Input conditions:** Run V2 job for 2024-10-31 (last day of October, a weekday).
- **Expected output:** Normal output with the expected number of customer rows. No summary rows, boundary markers, or special behavior. No W3a/W3b/W3c wrinkles apply [FSD Section 3].
- **Verification method:** Verify row count matches `SELECT COUNT(*) FROM datalake.customers WHERE as_of = '2024-10-31'`. Inspect output for any unexpected extra rows.

### TC-18: Quarter-end boundary produces normal output
- **Traces to:** Edge Case (boundary dates)
- **Input conditions:** Run V2 job for 2024-12-31 (last day of Q4).
- **Expected output:** Normal output. No quarterly summary behavior. No W-codes apply.
- **Verification method:** Same as TC-17 but for quarter-end date.

### TC-19: Overwrite mode -- multi-day run keeps only last day (W9)
- **Traces to:** Edge Case (W9, Write Mode Implications)
- **Input conditions:** Run V2 job for two consecutive weekdays (e.g., 2024-10-01 then 2024-10-02) via auto-advance.
- **Expected output:** After both runs complete, the CSV file contains ONLY data from the second day (2024-10-02). The first day's data is gone because Overwrite mode replaces the file each run.
- **Verification method:** Run the job for two dates. Open the CSV and verify all `as_of` values are the second date. Verify the row count matches the customer count for the second date only. Validates W9 wrinkle documentation in FSD Section 3.

### TC-20: No rounding on computed values
- **Traces to:** Edge Case (BRD OQ-2)
- **Input conditions:** Run V2 job for a date where computed values (avg_credit_score, total_loan_balance, total_account_balance) produce non-round decimals.
- **Expected output:** Values are written to CSV with full decimal precision. No `ROUND()` or `Math.Round()` is applied. For example, if a customer's credit scores average to 713.33333333333333333333333333 (decimal), that exact string appears in the CSV.
- **Verification method:** Identify a customer with non-round aggregates. Compare the V2 CSV field against the manually computed decimal value. Verify no truncation or rounding. Validates BRD OQ-2 and FSD Section 10, Key Design Decision #5.

### TC-21: Output column order matches specification
- **Traces to:** FSD Output Schema
- **Input conditions:** Run V2 job for any valid date.
- **Expected output:** The CSV header row contains exactly 9 columns in this order: `customer_id,first_name,last_name,avg_credit_score,total_loan_balance,total_account_balance,loan_count,account_count,as_of`.
- **Verification method:** Read the first line of the V2 CSV output and compare against the expected column order from FSD Section 4. Validates that the External module's `outputColumns` list matches the BRD output schema.

### TC-22: V2 uses Tier 2 architecture
- **Traces to:** FSD Tier Selection
- **Input conditions:** Inspect the V2 job config JSON and the V2 External module source file.
- **Expected output:** The V2 config contains: 4 DataSourcing modules, 1 External module (`CustomerCreditSummaryV2Processor`), 1 CsvFileWriter. No Transformation (SQL) module. The External module handles only aggregation logic -- it does not perform data sourcing or file writing.
- **Verification method:** Read the V2 config and verify the module chain. Read the External module source to confirm it only aggregates data from shared state and places a result DataFrame into shared state. Validates FSD Section 1 Tier 2 justification (SQLite double vs C# decimal precision).

### TC-23: Proofmark comparison passes with strict config
- **Traces to:** FSD Proofmark Config Design
- **Input conditions:** Run both V1 and V2 jobs for the full date range (2024-10-01 through 2024-12-31). Run Proofmark with config: `reader: csv`, `header_rows: 1`, `trailer_rows: 0`, `threshold: 100.0`, no exclusions, no fuzzy columns.
- **Expected output:** Proofmark exits with code 0 (PASS). All 9 columns match exactly between V1 (`Output/curated/customer_credit_summary.csv`) and V2 (`Output/double_secret_curated/customer_credit_summary.csv`).
- **Verification method:** Execute Proofmark with the config from FSD Section 8. Verify exit code 0. Read the Proofmark report JSON and confirm zero mismatches. This validates the entire V2 implementation chain produces byte-identical output to V1.
