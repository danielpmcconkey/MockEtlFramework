# CustomerContactInfo -- Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | UNION ALL combines phone and email records into single result set |
| TC-02   | BR-1           | contact_type is 'Phone' for phone records and 'Email' for email records |
| TC-03   | BR-2           | phone_type maps to contact_subtype for phone records |
| TC-04   | BR-2           | email_type maps to contact_subtype for email records |
| TC-05   | BR-3           | phone_number maps to contact_value for phone records |
| TC-06   | BR-3           | email_address maps to contact_value for email records |
| TC-07   | BR-4           | Output ordered by customer_id, contact_type, contact_subtype |
| TC-08   | BR-5, AP1      | Dead-end segments table eliminated from V2 DataSourcing |
| TC-09   | BR-6           | as_of column carried through from source tables |
| TC-10   | Writer Config  | Output format: Parquet, numParts=2, Append mode |
| TC-11   | Writer Config  | Append mode accumulates data across effective dates |
| TC-12   | Edge Case      | Empty phone_numbers AND empty email_addresses yields zero rows |
| TC-13   | Edge Case      | Customer with only phone records (no email) appears in output |
| TC-14   | Edge Case      | Customer with only email records (no phone) appears in output |
| TC-15   | Edge Case      | Customer with multiple contact methods produces multiple rows |
| TC-16   | FSD Proofmark  | Proofmark comparison config correctness |
| TC-17   | FSD Schema     | Output column order matches V1 schema exactly |
| TC-18   | BR-2           | Distinct phone_type values are Mobile, Home, Work |
| TC-19   | BR-2           | Distinct email_type values are Personal, Work |
| TC-20   | Edge Case      | UNION ALL preserves duplicates (not UNION) |
| TC-21   | Writer Config  | Re-run for same date range produces duplicate data in Append mode |
| TC-22   | Edge Case      | NULL contact_subtype or contact_value handling |
| TC-23   | FSD SQL        | V2 SQL is functionally identical to V1 SQL |

## Test Cases

### TC-01: UNION ALL Combines Phone and Email Records
- **Traces to:** BR-1
- **Input conditions:** datalake.phone_numbers contains phone records and datalake.email_addresses contains email records for the same effective date range.
- **Expected output:** The output contains rows from BOTH tables combined into a single result set. The total row count equals the sum of phone rows and email rows from the source tables (within the effective date range). UNION ALL is used (not UNION), so no deduplication occurs.
- **Verification method:** Count the rows in V2 output. Query `SELECT COUNT(*) FROM datalake.phone_numbers WHERE as_of BETWEEN min AND max` plus `SELECT COUNT(*) FROM datalake.email_addresses WHERE as_of BETWEEN min AND max`. The output row count should equal the sum. Compare against V1 output via Proofmark.

### TC-02: contact_type Literals
- **Traces to:** BR-1
- **Input conditions:** Standard job run with both phone and email data.
- **Expected output:** Every row in the output has contact_type = 'Phone' or contact_type = 'Email'. No other values exist. Phone-sourced rows have exactly 'Phone'. Email-sourced rows have exactly 'Email'. The values are case-sensitive string literals.
- **Verification method:** Query the output for DISTINCT contact_type values. Verify only 'Phone' and 'Email' appear. Verify the count of 'Phone' rows matches the phone_numbers source count and the count of 'Email' rows matches the email_addresses source count for the date range.

### TC-03: phone_type Maps to contact_subtype (Phone Records)
- **Traces to:** BR-2
- **Input conditions:** datalake.phone_numbers contains rows with phone_type values: Mobile, Home, Work.
- **Expected output:** For every output row where contact_type = 'Phone', the contact_subtype value equals the original phone_type from the source. No transformation is applied -- it is a direct alias.
- **Verification method:** Join V2 output (contact_type = 'Phone') back to datalake.phone_numbers on customer_id, phone_number, as_of. Verify contact_subtype matches phone_type for every matched row. Query DISTINCT contact_subtype WHERE contact_type = 'Phone' and confirm the set is {Mobile, Home, Work}.

### TC-04: email_type Maps to contact_subtype (Email Records)
- **Traces to:** BR-2
- **Input conditions:** datalake.email_addresses contains rows with email_type values: Personal, Work.
- **Expected output:** For every output row where contact_type = 'Email', the contact_subtype value equals the original email_type from the source.
- **Verification method:** Same approach as TC-03 but for email records. Query DISTINCT contact_subtype WHERE contact_type = 'Email' and confirm the set is {Personal, Work}.

### TC-05: phone_number Maps to contact_value (Phone Records)
- **Traces to:** BR-3
- **Input conditions:** Standard phone_numbers data.
- **Expected output:** For every output row where contact_type = 'Phone', the contact_value equals the original phone_number from the source table.
- **Verification method:** Join V2 output (contact_type = 'Phone') back to datalake.phone_numbers on customer_id, as_of. Verify contact_value matches phone_number for every matched row.

### TC-06: email_address Maps to contact_value (Email Records)
- **Traces to:** BR-3
- **Input conditions:** Standard email_addresses data.
- **Expected output:** For every output row where contact_type = 'Email', the contact_value equals the original email_address from the source table.
- **Verification method:** Join V2 output (contact_type = 'Email') back to datalake.email_addresses on customer_id, as_of. Verify contact_value matches email_address for every matched row.

### TC-07: Output Ordering
- **Traces to:** BR-4
- **Input conditions:** Output contains multiple customers with multiple contact types and subtypes.
- **Expected output:** Rows are ordered by customer_id (ascending), then contact_type (ascending -- 'Email' before 'Phone' alphabetically), then contact_subtype (ascending). Within tied sort keys, order is undefined (Parquet is unordered by nature, but within a single part file the ORDER BY should be preserved).
- **Verification method:** Read the output Parquet file and verify the rows are sorted by (customer_id, contact_type, contact_subtype). For a given customer, 'Email' rows should appear before 'Phone' rows. Within 'Email', 'Personal' before 'Work'. Within 'Phone', 'Home' before 'Mobile' before 'Work'. Compare against V1 output.

### TC-08: Dead-End segments Table Eliminated
- **Traces to:** BR-5, AP1
- **Input conditions:** V2 job config module list.
- **Expected output:** The V2 config does NOT include a DataSourcing entry for datalake.segments. V1 sources it but never references it in the SQL transformation. AP1 is eliminated.
- **Verification method:** Read the V2 job config JSON at `JobExecutor/Jobs/customer_contact_info_v2.json`. Verify no module references the segments table. The module list should contain exactly: DataSourcing (phone_numbers), DataSourcing (email_addresses), Transformation, ParquetFileWriter. Verify output still matches V1 via Proofmark.

### TC-09: as_of Column Carried Through
- **Traces to:** BR-6
- **Input conditions:** Source tables have rows across multiple as_of dates in the effective date range.
- **Expected output:** The as_of column appears in the output and reflects the original as_of value from each source row. Phone rows carry their phone_numbers.as_of. Email rows carry their email_addresses.as_of. The as_of is not modified or replaced.
- **Verification method:** Run the job for a multi-day effective date range. Verify the output contains as_of values spanning the full range. Verify each output row's as_of matches its source record's as_of. Compare against V1 output.

### TC-10: Output Format -- Parquet, numParts=2, Append
- **Traces to:** Writer Configuration (BRD)
- **Input conditions:** Standard job run producing output.
- **Expected output:**
  - Output directory: `Output/double_secret_curated/customer_contact_info/`
  - Format: Parquet
  - Part files per execution: 2 (part-00000.parquet, part-00001.parquet)
  - Write mode: Append -- each execution adds new part files without removing prior ones
- **Verification method:**
  - Verify the output directory exists
  - After a single run, verify 2 new .parquet files are created
  - Verify the files are valid Parquet files with the expected schema
  - Verify numParts and writeMode in the V2 job config JSON match V1 (numParts=2, writeMode=Append)

### TC-11: Append Mode Accumulates Across Dates
- **Traces to:** Writer Configuration (BRD), Write Mode Implications
- **Input conditions:** Run the job for effective date 2024-10-01, then run again for 2024-10-02.
- **Expected output:** After both runs, the output directory contains 4 part files (2 from the first run + 2 from the second run). Data from both dates is present in the combined output. This builds a cumulative historical record.
- **Verification method:** Run for two consecutive dates. Count the part files in the output directory after each run. After the first run: 2 files. After the second run: 4 files. Read all files and verify as_of values from both dates are present.

### TC-12: Empty Input -- No Phone or Email Data
- **Traces to:** Edge Case 1 (BRD)
- **Input conditions:** Both datalake.phone_numbers and datalake.email_addresses return zero rows for the given effective date range.
- **Expected output:** The UNION ALL produces zero rows. The ParquetFileWriter writes an empty Parquet file (or a Parquet file with zero data rows but valid schema).
- **Verification method:** If such a date range exists in the test data, run for it and verify output is empty. Otherwise, verify by code inspection that an empty UNION ALL produces zero rows and the writer handles this gracefully. Compare against V1 behavior.

### TC-13: Customer With Only Phone Records
- **Traces to:** Edge Case 2 (BRD)
- **Input conditions:** A customer who has entries in datalake.phone_numbers but no entries in datalake.email_addresses.
- **Expected output:** The customer appears in the output with contact_type = 'Phone' rows only. The UNION ALL includes partial records -- having only phone records does not exclude a customer.
- **Verification method:** Identify customers present in phone_numbers but absent from email_addresses for the test date range. Verify they appear in the output with 'Phone' rows only and zero 'Email' rows.

### TC-14: Customer With Only Email Records
- **Traces to:** Edge Case 2 (BRD)
- **Input conditions:** A customer who has entries in datalake.email_addresses but no entries in datalake.phone_numbers.
- **Expected output:** The customer appears in the output with contact_type = 'Email' rows only.
- **Verification method:** Same approach as TC-13 but reversed. Verify the customer has 'Email' rows only in the output.

### TC-15: Customer With Multiple Contact Methods
- **Traces to:** Edge Case 3 (BRD)
- **Input conditions:** A customer with 3 phone numbers (Mobile, Home, Work) and 2 email addresses (Personal, Work) for a given as_of date.
- **Expected output:** That customer produces 5 rows in the output: 3 with contact_type = 'Phone' (one per phone_type) and 2 with contact_type = 'Email' (one per email_type). Each row has its own contact_subtype and contact_value.
- **Verification method:** Identify a customer with multiple contact records. Count their output rows. Verify the count equals the sum of their phone and email entries. Verify each row has the correct contact_type, contact_subtype, and contact_value.

### TC-16: Proofmark Configuration Correctness
- **Traces to:** FSD Section 8
- **Input conditions:** Proofmark config for this job.
- **Expected output:** Config specifies:
  - `comparison_target: "customer_contact_info"`
  - `reader: parquet`
  - `threshold: 100.0`
  - No EXCLUDED columns
  - No FUZZY columns
- **Verification method:** Read the Proofmark YAML config at `POC3/proofmark_configs/customer_contact_info.yaml` and verify all fields match the FSD's Proofmark config design. All output fields are deterministic (no timestamps, UUIDs, or runtime-generated values), so strict comparison with zero overrides is correct.

### TC-17: Output Column Order
- **Traces to:** FSD Section 4 (Output Schema)
- **Input conditions:** Standard job run.
- **Expected output:** Output Parquet columns appear in this exact order:
  1. customer_id
  2. contact_type
  3. contact_subtype
  4. contact_value
  5. as_of
- **Verification method:** Read the output Parquet file's schema. Verify column names and their order match the BRD/FSD output schema exactly. Compare V2 schema against V1 schema.

### TC-18: Phone contact_subtype Values
- **Traces to:** BR-2
- **Input conditions:** Full run against datalake.phone_numbers data.
- **Expected output:** The DISTINCT set of contact_subtype values where contact_type = 'Phone' is exactly {Mobile, Home, Work}. No other phone_type values exist in the source data per BRD evidence.
- **Verification method:** Query the output for DISTINCT contact_subtype WHERE contact_type = 'Phone'. Cross-reference with `SELECT DISTINCT phone_type FROM datalake.phone_numbers`. Confirm the sets are identical.

### TC-19: Email contact_subtype Values
- **Traces to:** BR-2
- **Input conditions:** Full run against datalake.email_addresses data.
- **Expected output:** The DISTINCT set of contact_subtype values where contact_type = 'Email' is exactly {Personal, Work}. No other email_type values exist in the source data per BRD evidence.
- **Verification method:** Query the output for DISTINCT contact_subtype WHERE contact_type = 'Email'. Cross-reference with `SELECT DISTINCT email_type FROM datalake.email_addresses`. Confirm the sets are identical.

### TC-20: UNION ALL Preserves Duplicates
- **Traces to:** BR-1 (UNION ALL, not UNION)
- **Input conditions:** If a customer has identical contact entries across multiple as_of dates (same phone number or email on different dates), both appear in the source data.
- **Expected output:** UNION ALL preserves all rows, including potential duplicates. If a customer has the same phone number on 2024-10-01 and 2024-10-02, both rows appear in the output (with different as_of values). UNION (without ALL) would deduplicate, which is NOT the intended behavior.
- **Verification method:** Query for a customer who has the same phone_number across multiple as_of dates. Verify both rows appear in the output with their respective as_of values. Count total output rows and confirm it equals the sum of source rows (phone + email) without deduplication.

### TC-21: Re-Run Same Date Range Produces Duplicates (Append)
- **Traces to:** Writer Configuration, Write Mode Implications (BRD)
- **Input conditions:** Run the job for effective date 2024-10-01. Then run the job again for the same effective date 2024-10-01.
- **Expected output:** After the second run, the output directory contains 4 part files (2 from first run + 2 from second run), and the data from 2024-10-01 is duplicated. This is expected Append mode behavior -- the framework does not check for or prevent duplicate data.
- **Verification method:** Run the job twice for the same date. Count part files after each run (2, then 4). Read all data and verify the 2024-10-01 rows appear twice (once per run's output). This confirms the Append write mode does not deduplicate.

### TC-22: NULL contact_subtype or contact_value
- **Traces to:** Edge Case (NULL handling)
- **Input conditions:** A row in datalake.phone_numbers where phone_type is NULL, or a row in datalake.email_addresses where email_type is NULL.
- **Expected output:** The NULL value passes through to the output as-is (NULL contact_subtype or NULL contact_value). The SQL does not apply any COALESCE or default value substitution. The row is still included in the output.
- **Verification method:** Query source tables for rows with NULL phone_type, email_type, phone_number, or email_address. If any exist, verify they appear in the output with NULL values in the corresponding columns. If no NULL values exist in the test data, this is a defensive edge case verified by code inspection of the SQL (no COALESCE or WHERE IS NOT NULL filters applied).

### TC-23: V2 SQL Functional Equivalence
- **Traces to:** FSD Section 5 (SQL Design)
- **Input conditions:** V2 Transformation SQL and V1 Transformation SQL.
- **Expected output:** The V2 SQL produces identical output to V1 for the same input data. The CTE wrapper (`WITH all_contacts AS (...)`) is retained for readability. The SELECT columns, UNION ALL structure, and ORDER BY clause are functionally identical.
- **Verification method:** Compare the V2 SQL from the job config against the V1 SQL from the original job config. Verify structural equivalence: same columns selected, same UNION ALL, same ORDER BY. Run both and compare output via Proofmark. The only difference should be the removal of the segments DataSourcing (AP1), which does not affect the SQL since segments was never referenced in it.
