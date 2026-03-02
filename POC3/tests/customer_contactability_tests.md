# CustomerContactability -- Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | Only customers with MARKETING_EMAIL opt-in are included |
| TC-02   | BR-2           | Customer must have email AND phone on file to be included |
| TC-03   | BR-3           | Saturday execution uses Friday's preference data (maxDate - 1) |
| TC-04   | BR-3           | Sunday execution uses Friday's preference data (maxDate - 2) |
| TC-05   | BR-4           | Weekend: only preference rows matching fallback Friday date are considered |
| TC-06   | BR-4           | Weekday: ALL preference rows in the effective date range are processed |
| TC-07   | BR-5           | as_of in output is set to targetDate (Friday on weekends, actual date on weekdays) |
| TC-08   | BR-6, AP4      | Unused prefix/suffix columns eliminated from V2 DataSourcing |
| TC-09   | BR-7, AP1      | Dead-end segments table eliminated from V2 DataSourcing |
| TC-10   | BR-8           | Multiple email addresses per customer: last-wins dictionary overwrite |
| TC-11   | BR-8           | Multiple phone numbers per customer: last-wins dictionary overwrite |
| TC-12   | BR-9           | Null or empty customer_preferences yields empty output DataFrame |
| TC-13   | BR-9           | Null or empty customers table yields empty output DataFrame |
| TC-14   | Writer Config  | Output format: Parquet, numParts=1, Overwrite mode |
| TC-15   | W2             | Weekend fallback logic produces clean, documented code (not V1 copy) |
| TC-16   | W9             | Overwrite mode means only last effective date's output persists |
| TC-17   | BR-1           | Customer opted in to MARKETING_SMS but NOT MARKETING_EMAIL is excluded |
| TC-18   | BR-2           | Customer opted in but missing email (has phone) is excluded |
| TC-19   | BR-2           | Customer opted in but missing phone (has email) is excluded |
| TC-20   | FSD Proofmark  | Proofmark comparison config correctness |
| TC-21   | FSD Schema     | Output column order matches V1 schema exactly |
| TC-22   | Edge Case      | Zero-row output when no customers meet all criteria |
| TC-23   | BR-9           | Null first_name or last_name coalesced to empty string |
| TC-24   | Edge Case      | customers table has no weekend data (consistent with fallback logic) |

## Test Cases

### TC-01: MARKETING_EMAIL Opt-In Filter
- **Traces to:** BR-1
- **Input conditions:** customer_preferences data containing rows with various preference_type values (MARKETING_EMAIL, MARKETING_SMS, ACCOUNT_ALERTS, etc.) and opted_in = true/false.
- **Expected output:** Only customers who have preference_type = 'MARKETING_EMAIL' AND opted_in = true appear in the output. Customers with other preference types (even if opted_in = true) are excluded. Customers with MARKETING_EMAIL but opted_in = false are excluded.
- **Verification method:** Query the test data for all distinct (customer_id, preference_type, opted_in) combinations. Verify that every customer_id in the V2 output has at least one MARKETING_EMAIL opted_in = true row in the source. Verify no output customer_id lacks this. Compare against V1 output via Proofmark.

### TC-02: Must Have Email AND Phone
- **Traces to:** BR-2
- **Input conditions:** Customers who have opted in to MARKETING_EMAIL but have varying contact info availability:
  - Customer A: has email and phone -- should be included
  - Customer B: has email but no phone -- should be excluded
  - Customer C: has phone but no email -- should be excluded
  - Customer D: has neither email nor phone -- should be excluded
- **Expected output:** Only Customer A appears in the output. All three conditions must be satisfied: opted in, has email, has phone.
- **Verification method:** Cross-reference the output customer_ids against datalake.email_addresses and datalake.phone_numbers. Confirm every output customer has entries in both tables. Query for opted-in customers missing from one or both contact tables and verify they are absent from output.

### TC-03: Saturday Execution Uses Friday Data
- **Traces to:** BR-3
- **Input conditions:** Effective date is a Saturday (e.g., 2024-10-05 is a Saturday).
- **Expected output:** The processor computes targetDate = maxDate - 1 day (2024-10-04, a Friday). Preference rows are filtered to only include those with as_of matching the Friday date. The as_of column in the output is the Friday date, not the Saturday date.
- **Verification method:** Run the job for a Saturday effective date. Verify the output as_of column contains the prior Friday's date. Verify the preference data used corresponds to Friday's snapshot. Compare against V1 output.

### TC-04: Sunday Execution Uses Friday Data
- **Traces to:** BR-3
- **Input conditions:** Effective date is a Sunday (e.g., 2024-10-06 is a Sunday).
- **Expected output:** The processor computes targetDate = maxDate - 2 days (2024-10-04, a Friday). Same behavior as Saturday but with a 2-day subtraction. Output as_of is the Friday date.
- **Verification method:** Run the job for a Sunday effective date. Verify the output as_of column contains the prior Friday's date (2 days back). Verify preference data is filtered to that Friday. Compare against V1 output.

### TC-05: Weekend Date Filtering on Preferences
- **Traces to:** BR-4
- **Input conditions:** Effective date is a Saturday or Sunday. customer_preferences has rows across multiple as_of dates within the effective date range.
- **Expected output:** Only preference rows where as_of matches the computed Friday targetDate are processed. Preference rows from other dates are ignored. A customer who opted in on a non-Friday date but not on the Friday date will NOT appear in the output.
- **Verification method:** Identify customers whose MARKETING_EMAIL opt-in status differs between the Friday date and other dates in the range. Confirm weekend output only reflects Friday's opt-in state. Compare against V1 output.

### TC-06: Weekday Processes All Dates in Range
- **Traces to:** BR-4
- **Input conditions:** Effective date is a weekday (e.g., Monday through Friday). customer_preferences has rows across multiple as_of dates in the effective date range.
- **Expected output:** When targetDate == maxDate (weekday), no date filter is applied to preferences. ALL preference rows in the entire effective date range are processed. A customer who opted in on ANY date in the range is included.
- **Verification method:** Run the job for a weekday date that is part of a multi-day range. Verify that customers who have MARKETING_EMAIL opted_in = true on any date within the range appear in the output, even if their most recent date shows opted_in = false. Compare against V1 output. Note: this may be a V1 quirk (Open Question 1 in BRD) -- the non-filtered weekday behavior means opt-in state is additive across dates.

### TC-07: as_of Column Value
- **Traces to:** BR-5
- **Input conditions:** Run the job for both a weekday and a weekend date.
- **Expected output:**
  - Weekday: as_of in every output row equals the actual effective date (maxEffectiveDate)
  - Saturday: as_of equals maxEffectiveDate - 1 (Friday)
  - Sunday: as_of equals maxEffectiveDate - 2 (Friday)
- **Verification method:** Inspect the as_of column in the output for each run. Verify it matches the expected targetDate, not the raw effective date. Compare against V1 output.

### TC-08: Unused prefix/suffix Columns Eliminated
- **Traces to:** BR-6, AP4
- **Input conditions:** V2 job config's DataSourcing module for the customers table.
- **Expected output:** The V2 config requests only columns: id, first_name, last_name. The prefix and suffix columns are NOT requested. This eliminates AP4 (unused columns).
- **Verification method:** Read the V2 job config JSON. Verify the customers DataSourcing entry has columns = ["id", "first_name", "last_name"]. Confirm prefix and suffix are absent. Verify the output schema does not include these columns. Confirm output still matches V1 via Proofmark (since V1 never used these columns in output either).

### TC-09: Dead-End segments Table Eliminated
- **Traces to:** BR-7, AP1
- **Input conditions:** V2 job config module list.
- **Expected output:** The V2 config does NOT include a DataSourcing entry for datalake.segments. This eliminates AP1 (dead-end sourcing).
- **Verification method:** Read the V2 job config JSON. Verify no module references the segments table. Verify output still matches V1 via Proofmark (since V1 never used segments data in output).

### TC-10: Multiple Emails -- Last-Wins
- **Traces to:** BR-8
- **Input conditions:** A customer who has multiple rows in datalake.email_addresses (e.g., customer_id 1763, 2455, or 3129 per FSD analysis).
- **Expected output:** The email_address in the output for that customer is the last one encountered during dictionary iteration (last-wins overwrite). Since V1 and V2 both use DataSourcing with the same query ordering (ORDER BY as_of), the last-wins value should be deterministic between V1 and V2 -- the email from the latest as_of date wins.
- **Verification method:** Query datalake.email_addresses for customers with multiple entries. Compare the email_address in V2 output against V1 output. If they match, the ordering is consistent. If they differ, this is a non-deterministic field that may need EXCLUDED or FUZZY treatment in Proofmark.

### TC-11: Multiple Phones -- Last-Wins
- **Traces to:** BR-8
- **Input conditions:** A customer with multiple rows in datalake.phone_numbers (e.g., customer_id 2455, 2041 per FSD analysis).
- **Expected output:** Same last-wins behavior as TC-10 but for phone_number.
- **Verification method:** Same approach as TC-10 but for phone_numbers table. Compare V2 phone_number values against V1 for customers with multiple phone entries.

### TC-12: Empty customer_preferences Yields Empty Output
- **Traces to:** BR-9
- **Input conditions:** customer_preferences DataFrame is null or has zero rows for the given effective date range.
- **Expected output:** The External module returns an empty DataFrame with the correct column schema: [customer_id, first_name, last_name, email_address, phone_number, as_of]. The Parquet output contains zero data rows.
- **Verification method:** If a date range exists where no customer_preferences data is available, run for that range and verify output is empty. Otherwise, verify by code inspection that the null/empty guard in the External module correctly returns an empty DataFrame with the right columns.

### TC-13: Empty customers Table Yields Empty Output
- **Traces to:** BR-9
- **Input conditions:** customers DataFrame is null or has zero rows for the given effective date range.
- **Expected output:** Same as TC-12 -- empty DataFrame output with correct schema.
- **Verification method:** Same approach as TC-12 but for the customers table. Code inspection of the External module's empty guard covering both prefs and customers.

### TC-14: Output Format -- Parquet, numParts=1, Overwrite
- **Traces to:** Writer Configuration (BRD)
- **Input conditions:** Standard job run producing output.
- **Expected output:**
  - Output directory: `Output/double_secret_curated/customer_contactability/`
  - Format: Parquet
  - Part files: exactly 1 (part-00000.parquet)
  - Write mode: Overwrite -- running the job erases prior output
- **Verification method:**
  - Verify the output directory exists and contains exactly one .parquet file
  - Verify the file is a valid Parquet file
  - Verify numParts and writeMode in the V2 job config JSON match V1 (numParts=1, writeMode=Overwrite)

### TC-15: Weekend Fallback Clean Implementation (W2)
- **Traces to:** W2
- **Input conditions:** V2 External module source code.
- **Expected output:** The weekend fallback logic is implemented with a clear guard clause (if/else or switch on DayOfWeek), not magic number subtraction. Comments reference W2 and explain the Friday fallback behavior. The code is clean and intentional, not a copy of V1.
- **Verification method:** Code review of ExternalModules/CustomerContactabilityV2Processor.cs. Verify the targetDate computation uses DayOfWeek enum comparisons. Verify comments explain W2. Verify output matches V1 via Proofmark.

### TC-16: Overwrite Mode Loses Prior Days (W9)
- **Traces to:** W9
- **Input conditions:** Run the job for effective date 2024-10-01, then run again for 2024-10-02.
- **Expected output:** After the second run, the output directory contains ONLY the data from 2024-10-02. The 2024-10-01 data is gone because writeMode = Overwrite replaces the entire directory on each execution.
- **Verification method:** Run the job for two consecutive dates. After the second run, read the Parquet output and verify all as_of values correspond to the second date only. No rows from the first date should remain. This confirms the Overwrite write mode behavior documented as W9.

### TC-17: MARKETING_SMS Opt-In Does Not Qualify
- **Traces to:** BR-1
- **Input conditions:** A customer who has opted_in = true for preference_type = 'MARKETING_SMS' but NOT for 'MARKETING_EMAIL'.
- **Expected output:** The customer does NOT appear in the output. Only MARKETING_EMAIL opt-in qualifies.
- **Verification method:** Query customer_preferences for customers with MARKETING_SMS opted_in = true but no MARKETING_EMAIL opt-in. Verify none of these customer_ids appear in the output.

### TC-18: Opted In But Missing Email
- **Traces to:** BR-2
- **Input conditions:** A customer who has MARKETING_EMAIL opted_in = true and has a phone number on file, but has NO entry in datalake.email_addresses.
- **Expected output:** The customer is excluded from output.
- **Verification method:** Identify such customers by cross-referencing opted-in customers against the email_addresses table. Verify they are absent from V2 output.

### TC-19: Opted In But Missing Phone
- **Traces to:** BR-2
- **Input conditions:** A customer who has MARKETING_EMAIL opted_in = true and has an email address on file, but has NO entry in datalake.phone_numbers.
- **Expected output:** The customer is excluded from output.
- **Verification method:** Identify such customers by cross-referencing opted-in customers against the phone_numbers table. Verify they are absent from V2 output.

### TC-20: Proofmark Configuration Correctness
- **Traces to:** FSD Section 8
- **Input conditions:** Proofmark config for this job.
- **Expected output:** Config specifies:
  - `comparison_target: "customer_contactability"`
  - `reader: parquet`
  - `threshold: 100.0`
  - No EXCLUDED columns
  - No FUZZY columns
- **Verification method:** Read the Proofmark YAML config at `POC3/proofmark_configs/customer_contactability.yaml` and verify all fields match the FSD's Proofmark config design. If Proofmark comparison fails on email_address or phone_number due to non-deterministic row ordering in multi-email/phone customers, update this test to expect EXCLUDED or FUZZY overrides with evidence.

### TC-21: Output Column Order
- **Traces to:** FSD Section 4 (Output Schema)
- **Input conditions:** Standard job run.
- **Expected output:** Output Parquet columns appear in this exact order:
  1. customer_id
  2. first_name
  3. last_name
  4. email_address
  5. phone_number
  6. as_of
- **Verification method:** Read the output Parquet file's schema. Verify column names and their order match the BRD/FSD output schema exactly. Compare V2 schema against V1 schema.

### TC-22: Zero-Row Output When No Customers Meet Criteria
- **Traces to:** Edge Case
- **Input conditions:** A scenario where customers have opted in but none meet ALL three criteria simultaneously (opted in, has email, has phone). For example, all opted-in customers are missing either an email or a phone entry.
- **Expected output:** Empty output DataFrame with zero rows but correct column schema.
- **Verification method:** This may be difficult to produce with real test data if most customers have contact info. Verify by code inspection that the algorithm correctly handles the case where the marketingOptIn set is non-empty but every customer_id fails the three-way lookup check. Compare against V1 behavior.

### TC-23: NULL Name Coalescing
- **Traces to:** BR-9
- **Input conditions:** A customer in datalake.customers where first_name or last_name is NULL.
- **Expected output:** The output row for that customer has first_name = "" (empty string) and/or last_name = "" (empty string), not NULL. The null coalescing uses `?.ToString() ?? ""`.
- **Verification method:** Query datalake.customers for rows with NULL first_name or last_name. If any such customers appear in the output (after meeting opt-in and contact info criteria), verify their name fields are empty strings, not NULL values. Compare against V1 output.

### TC-24: No Weekend Data in customers Table
- **Traces to:** BRD Edge Case 6
- **Input conditions:** The datalake.customers table does not contain rows with as_of values that fall on Saturday or Sunday.
- **Expected output:** Weekend fallback logic still works correctly because the customer lookup dictionary is built from ALL customers rows across the effective date range (no date filtering on customers -- only on preferences). The absence of weekend customer data does not affect output since customer lookups use the full range.
- **Verification method:** Query `SELECT DISTINCT as_of, EXTRACT(DOW FROM as_of) FROM datalake.customers WHERE EXTRACT(DOW FROM as_of) IN (0, 6)` to confirm no weekend data exists. Run the job for a weekend effective date and verify output is produced correctly using Friday's preference data joined against all available customer data. Compare against V1 output.
