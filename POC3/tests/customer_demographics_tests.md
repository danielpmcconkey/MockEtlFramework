# CustomerDemographics -- Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | Age calculated as year difference with birthday adjustment |
| TC-02   | BR-2           | Age bracket assigned correctly for each range boundary |
| TC-03   | BR-3           | Primary phone is the first phone encountered per customer |
| TC-04   | BR-4           | Primary email is the first email encountered per customer |
| TC-05   | BR-5           | Customer with no phone/email gets empty string defaults |
| TC-06   | BR-6           | Empty customers DataFrame produces empty output (header only) |
| TC-07   | BR-7           | as_of column is pass-through from customer row |
| TC-08   | BR-8           | birthdate column is pass-through (raw value, not reformatted) |
| TC-09   | BR-9 (AP4)     | Unused columns (prefix, sort_name, suffix, phone_type, email_type) not sourced in V2 |
| TC-10   | BR-10 (AP1)    | Segments table is NOT sourced in V2 config |
| TC-11   | BR-11          | Birthdate-to-date conversion handles different source formats |
| TC-12   | Writer Config  | CSV output uses CRLF line endings, header, Overwrite mode, no trailer |
| TC-13   | Edge Case      | Customer under age 18 gets "18-25" bracket (no lower bound check) |
| TC-14   | Edge Case      | Customer exactly age 25 gets "18-25" bracket |
| TC-15   | Edge Case      | Customer exactly age 26 gets "26-35" bracket |
| TC-16   | Edge Case      | Customer exactly age 65 gets "56-65" bracket |
| TC-17   | Edge Case      | Customer age 66 or older gets "65+" bracket |
| TC-18   | Edge Case      | Birthday on as_of date: birthday HAS occurred (no subtract 1) |
| TC-19   | Edge Case      | Birthday day after as_of date: birthday has NOT occurred (subtract 1) |
| TC-20   | Edge Case      | Weekend date (no data) produces empty output |
| TC-21   | Edge Case      | Month-end boundary (2024-10-31) produces normal output |
| TC-22   | Edge Case      | Quarter-end boundary (2024-12-31) produces normal output |
| TC-23   | Edge Case      | Multi-day Overwrite run: only last effective date's output survives (W9) |
| TC-24   | Edge Case      | Customer with multiple phones -- only first is used |
| TC-25   | Edge Case      | Customer with multiple emails -- only first is used |
| TC-26   | Edge Case      | NULL first_name/last_name coalesce to empty string |
| TC-27   | FSD: Output    | Output column order matches spec exactly (9 columns) |
| TC-28   | FSD: Tier 1    | V2 uses Tier 1 chain (DataSourcing -> Transformation -> CsvFileWriter), no External module |
| TC-29   | Proofmark      | Proofmark comparison passes with zero exclusions and zero fuzzy columns |

## Test Cases

### TC-01: Age calculated with birthday adjustment
- **Traces to:** BR-1
- **Input conditions:** Run V2 job for a specific effective date (e.g., 2024-10-15). Select a customer whose birthdate is known from `datalake.customers`.
- **Expected output:** The `age` column equals `year(as_of) - year(birthdate)`, minus 1 if the customer's birthday has not yet occurred in the as_of year. For example, a customer born 1990-12-01 with as_of 2024-10-15 has age = 2024 - 1990 - 1 = 33 (birthday Dec 1 hasn't happened by Oct 15). A customer born 1990-03-01 with as_of 2024-10-15 has age = 2024 - 1990 = 34 (birthday already passed).
- **Verification method:** Query `datalake.customers` for specific birthdates on the effective date. Manually compute expected ages. Compare against V2 CSV output. The FSD implements this via SQLite `strftime('%Y')` year difference with a `strftime('%m-%d')` lexicographic comparison for the birthday adjustment [FSD Section 5].

### TC-02: Age bracket ranges assigned correctly
- **Traces to:** BR-2
- **Input conditions:** Run V2 job for a date and identify customers falling into each age bracket from the data.
- **Expected output:** Age brackets assigned per these rules:
  - age < 26: "18-25"
  - 26 <= age <= 35: "26-35"
  - 36 <= age <= 45: "36-45"
  - 46 <= age <= 55: "46-55"
  - 56 <= age <= 65: "56-65"
  - age > 65: "65+"
- **Verification method:** Compute expected ages from birthdate and as_of for a sample of customers. Verify each customer's `age_bracket` field in the V2 CSV matches the expected range. Validates the CASE/WHEN expression in FSD Section 5.

### TC-03: Primary phone is first encountered per customer
- **Traces to:** BR-3
- **Input conditions:** Run V2 job for a date where at least one customer has multiple phone numbers in `datalake.phone_numbers`.
- **Expected output:** The `primary_phone` field contains the phone number from the row with the smallest `phone_id` for that customer (since V2 uses `MIN(rowid)` in SQLite, and DataSourcing insertion order determines rowid, which correlates with database natural order).
- **Verification method:** Query `datalake.phone_numbers` for a customer with multiple phone records: `SELECT phone_id, phone_number FROM datalake.phone_numbers WHERE customer_id = <id> AND as_of = '<date>' ORDER BY phone_id`. The first row's `phone_number` should match the V2 CSV output. Validates FSD Section 5 `MIN(rowid)` subquery approach [FSD SQL Design Notes #1].

### TC-04: Primary email is first encountered per customer
- **Traces to:** BR-4
- **Input conditions:** Run V2 job for a date where at least one customer has multiple email addresses in `datalake.email_addresses`.
- **Expected output:** The `primary_email` field contains the email address from the row with the smallest `email_id` for that customer.
- **Verification method:** Query `datalake.email_addresses` for a customer with multiple email records: `SELECT email_id, email_address FROM datalake.email_addresses WHERE customer_id = <id> AND as_of = '<date>' ORDER BY email_id`. The first row's `email_address` should match the V2 CSV. Validates the same `MIN(rowid)` approach as TC-03 but for emails.

### TC-05: Missing phone/email defaults to empty string
- **Traces to:** BR-5
- **Input conditions:** Run V2 job for a date where at least one customer has no phone numbers and/or no email addresses.
- **Expected output:** The `primary_phone` and/or `primary_email` field is an empty string (empty CSV field -- two consecutive commas or trailing comma) rather than NULL, "NULL", or any other placeholder.
- **Verification method:** Identify a customer with no phone records: `SELECT c.id FROM datalake.customers c WHERE c.as_of = '<date>' AND c.id NOT IN (SELECT DISTINCT customer_id FROM datalake.phone_numbers WHERE as_of = '<date>')`. Verify the V2 CSV output shows an empty field. The FSD uses `COALESCE(pp.phone_number, '')` via LEFT JOIN [FSD Section 5, SQL Design Note #2].

### TC-06: Empty customers table produces header-only output
- **Traces to:** BR-6
- **Input conditions:** Run V2 job for a date where `datalake.customers` has zero rows (e.g., a weekend date if no weekend data exists).
- **Expected output:** The CSV output contains only the header row and zero data rows. The file exists and is not empty -- it has the header line.
- **Verification method:** Verify `datalake.customers` has no rows for the date. Run the V2 job. Confirm the CSV has exactly 1 line (the header). The FSD notes that SQL naturally returns 0 rows when the customers table is empty, and the Transformation module returns an empty DataFrame [FSD Section 9, BR-6 traceability].

### TC-07: as_of column is pass-through
- **Traces to:** BR-7
- **Input conditions:** Run V2 job for a specific effective date (e.g., 2024-10-01).
- **Expected output:** Every row's `as_of` value matches the effective date. Since the executor runs one effective date at a time with Overwrite mode, all rows share the same `as_of`.
- **Verification method:** Read the V2 CSV and verify all `as_of` values equal the effective date. Cross-reference with `SELECT DISTINCT as_of FROM datalake.customers WHERE as_of = '2024-10-01'`. Validates FSD Section 4: `c.as_of` in the SELECT.

### TC-08: birthdate column is raw pass-through
- **Traces to:** BR-8
- **Input conditions:** Run V2 job for a date and pick several customers.
- **Expected output:** The `birthdate` column contains the exact same value as stored in `datalake.customers.birthdate` -- no reformatting, no conversion to a different date format, no truncation.
- **Verification method:** Query `datalake.customers` for specific customer birthdates. Compare the raw string representation in the V2 CSV against the source value. The FSD uses `c.birthdate` as a direct passthrough in the SQL SELECT [FSD Section 4].

### TC-09: Unused columns not sourced in V2
- **Traces to:** BR-9 (AP4 elimination)
- **Input conditions:** Inspect the V2 job config JSON (`customer_demographics_v2.json`).
- **Expected output:** Column lists in V2 DataSourcing entries:
  - `customers`: `["id", "first_name", "last_name", "birthdate"]` (removed: prefix, sort_name, suffix)
  - `phone_numbers`: `["phone_id", "customer_id", "phone_number"]` (removed: phone_type; retained phone_id for ordering)
  - `email_addresses`: `["email_id", "customer_id", "email_address"]` (removed: email_type; retained email_id for ordering)
- **Verification method:** Read the V2 config and compare column arrays against V1. Verify `prefix`, `sort_name`, `suffix`, `phone_type`, `email_type` are absent. Note that `phone_id` and `email_id` are retained for deterministic first-row selection via `MIN(rowid)` [FSD Section 2, DataSourcing #2 and #3].

### TC-10: Segments table not sourced in V2
- **Traces to:** BR-10 (AP1 elimination)
- **Input conditions:** Inspect the V2 job config JSON.
- **Expected output:** The config contains exactly three DataSourcing entries: `customers`, `phone_numbers`, `email_addresses`. There is NO DataSourcing entry for `segments`.
- **Verification method:** Read the V2 config and count DataSourcing entries. Confirm none references `table: "segments"`. Validates FSD Section 3, AP1 elimination.

### TC-11: Birthdate type handling
- **Traces to:** BR-11
- **Input conditions:** Run V2 job for a date. Verify that birthdate values from the source are handled correctly regardless of whether PostgreSQL returns them as DateOnly, DateTime, or string.
- **Expected output:** All birthdates produce valid age calculations. No exceptions thrown during processing.
- **Verification method:** The FSD notes that V2's SQL approach operates on date strings natively (SQLite `strftime`), so the `ToDateOnly` helper from V1 is not needed [FSD Section 9, BR-11 traceability]. Verify by running the job and confirming no errors. Spot-check a few customers' ages against manual calculation.

### TC-12: Writer configuration matches V1 -- CRLF line endings
- **Traces to:** BRD Writer Configuration
- **Input conditions:** Run V2 job for a single date. Inspect the output file.
- **Expected output:** CSV file with:
  - Header row present (first line is column names)
  - CRLF line endings (0x0D 0x0A at end of each line)
  - No trailer line
  - File at `Output/double_secret_curated/customer_demographics.csv`
  - Overwrite mode
- **Verification method:** Read the V2 config and verify: `includeHeader: true`, `writeMode: "Overwrite"`, `lineEnding: "CRLF"`, no `trailerFormat`. Inspect the output file bytes to confirm CRLF endings (0x0D 0x0A, not just 0x0A). This is a critical distinction from many other jobs that use LF. Validates FSD Section 7 writer config.

### TC-13: Customer under age 18 gets "18-25" bracket
- **Traces to:** Edge Case (BRD: Customer under 18)
- **Input conditions:** If any customer in the data has a birthdate that makes them under 18 relative to the as_of date, run the V2 job for that date.
- **Expected output:** The customer's `age_bracket` is "18-25" because the CASE expression's first branch is `age < 26`, which has no lower bound at 18. An age of, say, 15 satisfies `< 26`.
- **Verification method:** Query `datalake.customers` for very recent birthdates. If a customer under 18 exists, verify their bracket in the V2 CSV. If no such customer exists in the data, this test is documented as not exercisable with current data but the SQL logic is verified by code inspection of the CASE expression [FSD Section 5].

### TC-14: Age exactly 25 -- upper bound of "18-25" bracket
- **Traces to:** Edge Case (BR-2 boundary)
- **Input conditions:** Identify a customer who is exactly 25 years old on the as_of date.
- **Expected output:** `age_bracket` = "18-25" because 25 < 26.
- **Verification method:** Compute expected age from birthdate and as_of. Verify the bracket in V2 CSV. This tests the boundary between "18-25" and "26-35".

### TC-15: Age exactly 26 -- lower bound of "26-35" bracket
- **Traces to:** Edge Case (BR-2 boundary)
- **Input conditions:** Identify a customer who is exactly 26 years old on the as_of date.
- **Expected output:** `age_bracket` = "26-35" because 26 is NOT < 26, and 26 <= 35.
- **Verification method:** Compute expected age and verify bracket in V2 CSV. This tests the transition from the first CASE branch to the second.

### TC-16: Age exactly 65 -- upper bound of "56-65" bracket
- **Traces to:** Edge Case (BR-2 boundary)
- **Input conditions:** Identify a customer who is exactly 65 years old on the as_of date.
- **Expected output:** `age_bracket` = "56-65" because 65 <= 65.
- **Verification method:** Compute expected age and verify bracket. This tests the boundary between "56-65" and "65+".

### TC-17: Age 66 or older gets "65+" bracket
- **Traces to:** Edge Case (BR-2 boundary)
- **Input conditions:** Identify a customer who is 66+ years old on the as_of date.
- **Expected output:** `age_bracket` = "65+" (the ELSE clause of the CASE expression).
- **Verification method:** Verify bracket in V2 CSV for an elderly customer. Validates the ELSE branch of the SQL CASE.

### TC-18: Birthday ON as_of date -- no subtraction
- **Traces to:** Edge Case (BR-1 birthday adjustment)
- **Input conditions:** Find or note a customer whose birthday month-day matches the as_of date's month-day (e.g., birthdate 1990-10-15 with as_of 2024-10-15).
- **Expected output:** Age = `year(as_of) - year(birthdate)` with NO subtraction of 1. The birthday HAS occurred on or before the as_of date. In this example: age = 2024 - 1990 = 34.
- **Verification method:** The FSD SQL uses `strftime('%m-%d', birthdate) > strftime('%m-%d', as_of)`. When they are equal, the condition is FALSE, so 0 is subtracted. Verify the computed age in the V2 CSV. This edge case confirms the `>` comparison (not `>=`) in the birthday adjustment.

### TC-19: Birthday one day after as_of -- subtract 1
- **Traces to:** Edge Case (BR-1 birthday adjustment)
- **Input conditions:** Find a customer whose birthday is one day after the as_of month-day (e.g., birthdate 1990-10-16 with as_of 2024-10-15).
- **Expected output:** Age = `year(as_of) - year(birthdate) - 1`. The birthday has NOT occurred yet. In this example: age = 2024 - 1990 - 1 = 33.
- **Verification method:** Verify the computed age in the V2 CSV. The `strftime('%m-%d')` comparison yields `'10-16' > '10-15'` which is TRUE, so 1 is subtracted.

### TC-20: Weekend date produces empty output
- **Traces to:** Edge Case (no weekend data)
- **Input conditions:** Run V2 job for a weekend date (e.g., 2024-10-05 Saturday).
- **Expected output:** DataSourcing returns zero rows. The Transformation SQL produces zero rows from an empty customers table. The CSV contains only the header row.
- **Verification method:** Verify no data exists for the weekend date. Run V2 and confirm header-only CSV output.

### TC-21: Month-end boundary produces normal output
- **Traces to:** Edge Case (boundary dates)
- **Input conditions:** Run V2 job for 2024-10-31 (last day of October, a weekday).
- **Expected output:** Normal output with the expected number of customer rows. No summary rows, boundary markers, or special behavior. No W3a/W3b/W3c wrinkles apply [FSD Section 3].
- **Verification method:** Verify row count matches `SELECT COUNT(*) FROM datalake.customers WHERE as_of = '2024-10-31'`. Inspect output for any unexpected extra rows.

### TC-22: Quarter-end boundary produces normal output
- **Traces to:** Edge Case (boundary dates)
- **Input conditions:** Run V2 job for 2024-12-31 (last day of Q4).
- **Expected output:** Normal output. No quarterly summary behavior. No W-codes apply.
- **Verification method:** Same as TC-21 but for the quarter-end date.

### TC-23: Overwrite mode -- multi-day run keeps only last day (W9)
- **Traces to:** Edge Case (W9, Write Mode Implications)
- **Input conditions:** Run V2 job for two consecutive weekdays (e.g., 2024-10-01 then 2024-10-02) via auto-advance.
- **Expected output:** After both runs complete, the CSV file contains ONLY data from the second day (2024-10-02). The first day's data is gone because Overwrite mode replaces the file each run.
- **Verification method:** Run the job for two dates. Open the CSV and verify all `as_of` values are the second date. Verify the row count matches the customer count for the second date only. Validates the Overwrite write mode documented in FSD Section 7.

### TC-24: Customer with multiple phones -- only first used
- **Traces to:** Edge Case (BR-3)
- **Input conditions:** Identify a customer with 3+ phone numbers in `datalake.phone_numbers` for a given date.
- **Expected output:** The `primary_phone` field contains only ONE phone number -- the one from the row with the smallest `phone_id`. The other phone numbers are discarded.
- **Verification method:** Query `datalake.phone_numbers` for the customer and order by phone_id. Verify the first row's phone_number matches the V2 CSV. Verify the CSV does NOT contain the other phone numbers.

### TC-25: Customer with multiple emails -- only first used
- **Traces to:** Edge Case (BR-4)
- **Input conditions:** Identify a customer with 3+ email addresses in `datalake.email_addresses` for a given date.
- **Expected output:** The `primary_email` field contains only ONE email address -- the one from the row with the smallest `email_id`.
- **Verification method:** Same approach as TC-24 but for email_addresses.

### TC-26: NULL first_name/last_name coalesce to empty string
- **Traces to:** Edge Case (Output Schema)
- **Input conditions:** Run V2 job for a date where a customer has NULL `first_name` or `last_name`.
- **Expected output:** The CSV shows an empty string (empty field) rather than "NULL" or any placeholder.
- **Verification method:** Query `datalake.customers` for NULL name fields. If found, verify the V2 CSV output field is empty. The FSD SQL uses `COALESCE(c.first_name, '')` [FSD Section 5].

### TC-27: Output column order matches specification
- **Traces to:** FSD Output Schema
- **Input conditions:** Run V2 job for any valid date.
- **Expected output:** The CSV header row contains exactly 9 columns in this order: `customer_id,first_name,last_name,birthdate,age,age_bracket,primary_phone,primary_email,as_of`.
- **Verification method:** Read the first line of the V2 CSV output. Compare against the expected order from FSD Section 4. The SQL SELECT clause defines this order.

### TC-28: V2 uses Tier 1 architecture -- no External module
- **Traces to:** FSD Tier Selection (AP3 elimination)
- **Input conditions:** Inspect the V2 job config JSON (`customer_demographics_v2.json`).
- **Expected output:** The V2 config contains: 3 DataSourcing modules (customers, phone_numbers, email_addresses), 1 Transformation module (SQL), 1 CsvFileWriter. There is NO External module. This confirms AP3 (unnecessary External module) is eliminated.
- **Verification method:** Read the V2 config and verify the module chain. Count module types. Confirm no `type: "External"` entry exists. The FSD justifies Tier 1 by showing all V1 logic is expressible in SQL: age via `strftime`, brackets via CASE, first phone/email via `MIN(rowid)` [FSD Section 1].

### TC-29: Proofmark comparison passes with strict config
- **Traces to:** FSD Proofmark Config Design
- **Input conditions:** Run both V1 and V2 jobs for the full date range (2024-10-01 through 2024-12-31). Run Proofmark with config: `reader: csv`, `header_rows: 1`, `trailer_rows: 0`, `threshold: 100.0`, no exclusions, no fuzzy columns.
- **Expected output:** Proofmark exits with code 0 (PASS). All 9 columns match exactly between V1 (`Output/curated/customer_demographics.csv`) and V2 (`Output/double_secret_curated/customer_demographics.csv`).
- **Verification method:** Execute Proofmark with the config from FSD Section 8. Verify exit code 0. Read the Proofmark report JSON and confirm zero mismatches. This validates the Tier 1 SQL implementation produces byte-identical output to V1's External module. If this fails due to row ordering alone, the FSD documents a mitigation: add `ORDER BY c.rowid` to the SQL [FSD Appendix, Risk 1].
