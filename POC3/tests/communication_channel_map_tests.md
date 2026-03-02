# CommunicationChannelMap -- Test Plan

## 1. Traceability Matrix

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01 | BR-1 | One output row per customer |
| TC-02 | BR-2 | Preferred channel priority: Email > SMS > Push > None |
| TC-03 | BR-3 | Only opted_in = true preferences considered |
| TC-04 | BR-4 | Asymmetric NULL handling: missing email -> "N/A", missing phone -> "" |
| TC-05 | BR-5 | Multiple emails/phones: last-wins dictionary overwrite semantics |
| TC-06 | BR-6 | as_of from first row of customers DataFrame |
| TC-07 | BR-7 | Empty customers DataFrame produces empty output with correct schema |
| TC-08 | Writer Config | CSV with header, Overwrite, LF line endings, no trailer |
| TC-09 | Edge Case 1 | Customer with no preferences -> preferred_channel = "None" |
| TC-10 | Edge Case 2 | Customer with multiple opted-in channels -> highest priority wins |
| TC-11 | Edge Case 3 | NULL handling asymmetry verified in output |
| TC-12 | Edge Case 4 | Empty customers table -> empty output |
| TC-13 | Edge Case 5 | Cross-date preference accumulation -- opts persist across snapshots |
| TC-14 | Output Schema | Column order matches V1: customer_id, first_name, last_name, preferred_channel, email, phone, as_of |
| TC-15 | Anti-Patterns | V2 eliminates AP3, AP4, AP6; reproduces AP5 in output |
| TC-16 | Non-Deterministic | email and phone columns may be non-deterministic for multi-entry customers |
| TC-17 | FSD Risk | Row ordering: V2 ORDER BY c.id vs V1 DataSourcing iteration order |
| TC-18 | Proofmark | End-to-end comparison across full date range |

---

## 2. Test Cases

### TC-01: One row per customer (BR-1)

**Objective:** Verify that the output contains exactly one row per customer_id.

**Preconditions:** Run V2 for a single weekday effective date (e.g., 2024-10-01).

**Steps:**
1. Execute CommunicationChannelMapV2 for effective date 2024-10-01.
2. Read the output CSV at `Output/double_secret_curated/communication_channel_map.csv`.
3. Exclude the header row.
4. Extract all customer_id values.
5. Check for duplicates.

**Expected Result:** Every `customer_id` appears exactly once. The total number of data rows matches the count of distinct customers in the datalake for that date:
```sql
SELECT COUNT(DISTINCT id) FROM datalake.customers WHERE as_of = '2024-10-01';
```

---

### TC-02: Preferred channel priority hierarchy (BR-2)

**Objective:** Verify the priority hierarchy: MARKETING_EMAIL -> "Email" > MARKETING_SMS -> "SMS" > PUSH_NOTIFICATIONS -> "Push" > "None".

**Preconditions:** Run V2 for effective date 2024-10-01.

**Steps:**
1. Identify a customer who has opted in to multiple channels:
   ```sql
   SELECT customer_id, preference_type, opted_in
   FROM datalake.customer_preferences
   WHERE as_of = '2024-10-01'
     AND opted_in = true
   GROUP BY customer_id, preference_type, opted_in
   HAVING COUNT(*) >= 1
   ORDER BY customer_id
   LIMIT 10;
   ```
2. Find a customer with both MARKETING_EMAIL and MARKETING_SMS opted in.
3. Execute CommunicationChannelMapV2 for 2024-10-01.
4. Look up that customer in the output CSV.

**Expected Result:** The `preferred_channel` for a customer with both email and SMS opted in is `"Email"` (highest priority). A customer with only SMS and Push opted in gets `"SMS"`. A customer with only Push gets `"Push"`.

---

### TC-03: Only opted_in = true preferences counted (BR-3)

**Objective:** Verify that opted_out (opted_in = false) preferences do not influence preferred_channel.

**Preconditions:** Run V2 for effective date 2024-10-01.

**Steps:**
1. Find a customer who has MARKETING_EMAIL with opted_in = false and MARKETING_SMS with opted_in = true:
   ```sql
   SELECT cp.customer_id, cp.preference_type, cp.opted_in
   FROM datalake.customer_preferences cp
   WHERE cp.as_of = '2024-10-01'
     AND cp.customer_id IN (
       SELECT customer_id FROM datalake.customer_preferences
       WHERE as_of = '2024-10-01' AND preference_type = 'MARKETING_EMAIL' AND opted_in = false
     )
     AND cp.customer_id IN (
       SELECT customer_id FROM datalake.customer_preferences
       WHERE as_of = '2024-10-01' AND preference_type = 'MARKETING_SMS' AND opted_in = true
     )
   ORDER BY cp.customer_id, cp.preference_type
   LIMIT 10;
   ```
2. Execute CommunicationChannelMapV2 for 2024-10-01.
3. Look up the customer in the output.

**Expected Result:** The customer's `preferred_channel` is `"SMS"`, not `"Email"`, because the email preference is opted out.

---

### TC-04: Asymmetric NULL handling (BR-4, AP5)

**Objective:** Verify that missing email defaults to "N/A" and missing phone defaults to "" (empty string).

**Preconditions:** Run V2 for effective date 2024-10-01.

**Steps:**
1. Find a customer with no email address entry:
   ```sql
   SELECT c.id FROM datalake.customers c
   WHERE c.as_of = '2024-10-01'
     AND c.id NOT IN (
       SELECT customer_id FROM datalake.email_addresses WHERE as_of = '2024-10-01'
     )
   LIMIT 5;
   ```
2. Find a customer with no phone number entry:
   ```sql
   SELECT c.id FROM datalake.customers c
   WHERE c.as_of = '2024-10-01'
     AND c.id NOT IN (
       SELECT customer_id FROM datalake.phone_numbers WHERE as_of = '2024-10-01'
     )
   LIMIT 5;
   ```
3. Execute CommunicationChannelMapV2 for 2024-10-01.
4. Look up those customers in the output.

**Expected Result:**
- Customer with no email: `email` column = `"N/A"`
- Customer with no phone: `phone` column = `""` (empty string)
- This asymmetry is intentional V1 behavior (AP5) reproduced in V2.

---

### TC-05: Last-wins for duplicate contacts (BR-5)

**Objective:** Verify that when a customer has multiple email addresses or phone numbers, the last one encountered wins.

**Preconditions:** Run V2 for effective date 2024-10-01.

**Steps:**
1. Find customers with multiple email addresses:
   ```sql
   SELECT customer_id, COUNT(*) AS cnt
   FROM datalake.email_addresses
   WHERE as_of = '2024-10-01'
   GROUP BY customer_id
   HAVING COUNT(*) > 1
   LIMIT 5;
   ```
2. For those customers, determine which email is "last" based on row ordering in the table.
3. Execute CommunicationChannelMapV2 for 2024-10-01.
4. Check which email appears in the output.

**Expected Result:** The email in the output matches the last-wins semantics. V2 uses `GROUP BY customer_id HAVING rowid = MAX(rowid)`, which picks the last-inserted row per customer in SQLite -- matching V1's dictionary overwrite behavior on the same DataFrame row iteration order.

**Note:** This is flagged as non-deterministic in the BRD because the "last" row depends on database row ordering. If V1 and V2 disagree on which row is "last", this column may need to be excluded from Proofmark comparison.

---

### TC-06: as_of from first customer row (BR-6)

**Objective:** Verify that the `as_of` column reflects the first row's as_of value from the customers DataFrame.

**Preconditions:** Run V2 for a single effective date.

**Steps:**
1. Execute CommunicationChannelMapV2 for effective date 2024-10-01.
2. Parse the output CSV.
3. Check the `as_of` value in all rows.

**Expected Result:** All rows have the same `as_of` value matching the effective date (2024-10-01). For single-day auto-advance runs, the customers DataFrame has one as_of value, so V2's `c.as_of` matches V1's `customers.Rows[0]["as_of"]` uniformly.

---

### TC-07: Empty customers produces empty output (BR-7)

**Objective:** Verify behavior when the customers DataFrame is empty.

**Preconditions:** Identify a date with no customer data (e.g., a weekend date).

**Steps:**
1. Confirm no data:
   ```sql
   SELECT COUNT(*) FROM datalake.customers WHERE as_of = '2024-10-05';
   ```
2. Execute CommunicationChannelMapV2 for 2024-10-05 (if Saturday).
3. Read the output.

**Expected Result:**
- If empty input is handled gracefully: output is a CSV with only the header row (no trailer, since no trailerFormat is configured). Zero data rows.
- If SQLite error occurs (table not registered): error is logged. Under Overwrite mode, the final output from the last weekday's run is the one that persists on disk.

**Risk Note (from FSD):** V2 Tier 1 may throw a SQLite error when customers is empty because Transformation.RegisterTable skips zero-row DataFrames.

---

### TC-08: Writer configuration -- CSV format verification (Writer Config)

**Objective:** Verify the output file matches all writer configuration parameters.

**Preconditions:** Run V2 for effective date 2024-12-31.

**Steps:**
1. Execute CommunicationChannelMapV2 for 2024-12-31.
2. Read the raw output file.
3. Check:
   - File exists at `Output/double_secret_curated/communication_channel_map.csv`.
   - First line is a header row with column names: `customer_id,first_name,last_name,preferred_channel,email,phone,as_of`.
   - No trailer row (trailerFormat is not configured).
   - Line endings are LF (`\n`), not CRLF (`\r\n`).
   - Encoding is UTF-8 (no BOM).
   - `includeHeader` is true.
   - `writeMode` is Overwrite.

**Expected Result:**
- Header row present with correct column names in correct order.
- No trailer line at end of file.
- All line endings are LF.
- File is valid UTF-8 without BOM.

---

### TC-09: Customer with no preferences -- "None" channel (Edge Case 1)

**Objective:** Verify that customers with no opted-in preferences get `preferred_channel = "None"`.

**Preconditions:** Run V2 for effective date 2024-10-01.

**Steps:**
1. Find a customer with no preferences at all:
   ```sql
   SELECT c.id FROM datalake.customers c
   WHERE c.as_of = '2024-10-01'
     AND c.id NOT IN (
       SELECT customer_id FROM datalake.customer_preferences
       WHERE as_of = '2024-10-01' AND opted_in = true
     )
   LIMIT 5;
   ```
2. Execute CommunicationChannelMapV2 for 2024-10-01.
3. Look up the customer in the output.

**Expected Result:** The `preferred_channel` column is `"None"`.

---

### TC-10: Multiple opted-in channels -- highest priority wins (Edge Case 2)

**Objective:** Verify that when a customer has opted into multiple channels, only the highest-priority one appears.

**Preconditions:** Run V2 for effective date 2024-10-01.

**Steps:**
1. Find a customer opted into all three channels:
   ```sql
   SELECT customer_id FROM datalake.customer_preferences
   WHERE as_of = '2024-10-01' AND opted_in = true
   GROUP BY customer_id
   HAVING COUNT(DISTINCT preference_type) = 3
   LIMIT 5;
   ```
2. Execute CommunicationChannelMapV2 for 2024-10-01.
3. Look up the customer in the output.

**Expected Result:** `preferred_channel` is `"Email"` (highest priority in the hierarchy).

---

### TC-11: NULL handling asymmetry confirmed in output (Edge Case 3)

**Objective:** Confirm that the asymmetric NULL defaults ("N/A" for email, "" for phone) appear in the actual output, not just the code.

**Preconditions:** Run V2 for effective date 2024-10-01.

**Steps:**
1. Find a customer who has neither email nor phone:
   ```sql
   SELECT c.id FROM datalake.customers c
   WHERE c.as_of = '2024-10-01'
     AND c.id NOT IN (SELECT customer_id FROM datalake.email_addresses WHERE as_of = '2024-10-01')
     AND c.id NOT IN (SELECT customer_id FROM datalake.phone_numbers WHERE as_of = '2024-10-01')
   LIMIT 5;
   ```
2. Execute CommunicationChannelMapV2 for 2024-10-01.
3. Parse the output CSV for that customer's row.

**Expected Result:** The row contains `email = "N/A"` and `phone = ""` (empty string). In CSV format this appears as: `...,N/A,,2024-10-01` (the phone field is an empty field between two commas).

---

### TC-12: Empty customers table (Edge Case 4)

**Objective:** Same as TC-07 -- dedicated edge case test for zero-row customers input.

See TC-07 for full procedure and expected results.

---

### TC-13: Cross-date preference accumulation (Edge Case 5)

**Objective:** Verify that preferences are accumulated across all as_of dates in the effective date range, not filtered to a single date.

**Preconditions:** Run V2 for a multi-day effective date range.

**Steps:**
1. Find a customer whose opt-in status changes across dates. For example, opted_in = false for MARKETING_EMAIL on day 1, opted_in = true on day 2:
   ```sql
   SELECT customer_id, as_of, preference_type, opted_in
   FROM datalake.customer_preferences
   WHERE preference_type = 'MARKETING_EMAIL'
     AND as_of BETWEEN '2024-10-01' AND '2024-10-03'
   ORDER BY customer_id, as_of
   LIMIT 20;
   ```
2. Execute CommunicationChannelMapV2 for the same date range.
3. Check that customer's preferred_channel in the output.

**Expected Result:** If a customer has `opted_in = true` for MARKETING_EMAIL on ANY date in the range, the SQL `MAX(CASE ... THEN 1 ELSE 0 END)` returns 1, and the `preferred_channel` is `"Email"`. This matches V1's behavior where `HashSet.Add` only adds (never removes), so any opted-in row sets the flag permanently for that run.

---

### TC-14: Output column order (Output Schema)

**Objective:** Verify the output columns appear in the exact order specified by V1.

**Preconditions:** Run V2 for effective date 2024-10-01.

**Steps:**
1. Execute CommunicationChannelMapV2 for 2024-10-01.
2. Read the header row from the output CSV.

**Expected Result:** Header row is exactly: `customer_id,first_name,last_name,preferred_channel,email,phone,as_of`

Column order matches V1's `outputColumns` list at `CommunicationChannelMapper.cs:10-14`.

---

### TC-15: Anti-pattern elimination verification (AP3, AP4, AP6)

**Objective:** Verify that V2 eliminates identified code-quality anti-patterns.

**Preconditions:** Read the V2 job config.

**Steps:**
1. Read `JobExecutor/Jobs/communication_channel_map_v2.json`.
2. Verify:
   - **AP3 eliminated:** No `External` module in the config. Module chain is `DataSourcing (x4) -> Transformation -> CsvFileWriter`.
   - **AP4 eliminated:** DataSourcing for customer_preferences drops `preference_id`; email_addresses drops `email_id`; phone_numbers drops `phone_id`.
   - **AP6 eliminated:** Business logic is in SQL (Transformation module), not row-by-row C# iteration.
3. Verify V1 output-affecting behavior preserved:
   - **AP5 reproduced:** Asymmetric NULL handling is in the SQL via `COALESCE(em.email_address, 'N/A')` and `COALESCE(ph.phone_number, '')`.

**Expected Result:** V2 config uses Tier 1 (Framework Only), sources only necessary columns, uses SQL for all business logic, and preserves the asymmetric NULL behavior documented in AP5.

---

### TC-16: Non-deterministic fields: email and phone (Non-Deterministic)

**Objective:** Assess whether the email and phone columns produce deterministic results for the test data.

**Preconditions:** Run both V1 and V2 for the same effective date.

**Steps:**
1. Check how many customers have multiple emails or phones:
   ```sql
   SELECT COUNT(*) FROM (
     SELECT customer_id FROM datalake.email_addresses
     WHERE as_of = '2024-10-01'
     GROUP BY customer_id HAVING COUNT(*) > 1
   ) t;
   ```
2. If count is 0, email is deterministic for this date.
3. Repeat for phone_numbers.
4. Run both V1 and V2 and compare the email/phone columns.

**Expected Result:**
- If no customers have multiple emails/phones: columns are deterministic, strict comparison should pass.
- If some customers do: values may differ between V1 and V2, and Proofmark may need email/phone excluded.

---

### TC-17: Row ordering: V2 ORDER BY c.id vs V1 iteration order (FSD Risk)

**Objective:** Verify that V2's `ORDER BY c.id` produces the same row order as V1.

**Preconditions:** Run both V1 and V2 for the same effective date.

**Steps:**
1. Execute both jobs for 2024-12-31.
2. Compare the sequence of customer_id values in the output.

**Expected Result:** Row ordering matches. V2 orders by `c.id` (integer sort). V1 iterates `customers.Rows` in DataSourcing order (ORDER BY as_of, then natural PG row order). For a single-day run, PG typically returns rows in primary key order (id), which should match `ORDER BY c.id`.

**Resolution Path (if mismatch):** Adjust V2's ORDER BY to match V1's actual row order. May need `ORDER BY c.as_of, c.id` or removal of ORDER BY if V1's order is truly non-deterministic.

---

### TC-18: End-to-end Proofmark comparison (Full Date Range)

**Objective:** Verify V2 output matches V1 across the full date range.

**Preconditions:** Both V1 and V2 have been run for 2024-10-01 through 2024-12-31.

**Steps:**
1. Run Proofmark comparison:
   ```bash
   python3 -m proofmark compare \
     --config POC3/proofmark_configs/communication_channel_map.yaml \
     --left Output/curated/communication_channel_map.csv \
     --right Output/double_secret_curated/communication_channel_map.csv \
     --output POC3/logs/proofmark_reports/communication_channel_map.json
   ```
2. Check exit code: 0 = PASS, 1 = FAIL, 2 = CONFIG ERROR.

**Expected Result:** Exit code 0 (PASS). Proofmark config uses `reader: csv`, `header_rows: 1`, `trailer_rows: 0`, threshold 100.0, no exclusions, no fuzzy overrides.

**If FAIL -- likely causes and resolution:**
- **Row ordering mismatch (TC-17):** Adjust ORDER BY in V2 SQL.
- **email/phone non-deterministic (TC-16):** Add exclusions for email and phone columns in Proofmark config.
- **as_of difference:** Investigate DataSourcing row ordering for customers table.
- **NULL handling mismatch:** Verify COALESCE defaults match V1's "N/A" / "" asymmetry.

---

## 3. Boundary Date Tests

### TC-19: First effective date (2024-10-01)

**Objective:** Verify correct output for the earliest date in the range.

**Steps:** Execute V2 for 2024-10-01, compare with V1 output.

**Expected Result:** Outputs match.

### TC-20: Last effective date (2024-12-31)

**Objective:** Verify correct output for the latest date (final Overwrite).

**Steps:** Execute V2 for 2024-12-31, compare with V1 output.

**Expected Result:** Outputs match. This is the file that persists on disk after a full auto-advance run.

### TC-21: Month boundary (2024-10-31 / 2024-11-01)

**Objective:** Verify no anomalies at month boundaries.

**Steps:** Execute V2 for 2024-10-31 and 2024-11-01 separately, compare with V1.

**Expected Result:** Outputs match for both dates. No special month-boundary behavior expected for this job.

### TC-22: Weekend date (2024-10-05 / 2024-10-06)

**Objective:** Verify V2 handles weekend dates where source tables may have no data.

**Steps:**
1. Confirm no data for the weekend date in all four source tables.
2. Execute V2 for the weekend date.
3. Compare behavior with V1.

**Expected Result:** Both V1 and V2 produce the same result -- either an empty output file or an error that does not affect the final file under Overwrite mode.

---

## 4. Data Integrity Checks

### TC-23: All customers accounted for

**Objective:** Verify that every customer in the datalake for the effective date appears in the output.

**Steps:**
1. Query:
   ```sql
   SELECT COUNT(DISTINCT id) FROM datalake.customers WHERE as_of = '2024-12-31';
   ```
2. Execute V2 for 2024-12-31.
3. Count data rows in the output CSV.

**Expected Result:** Data row count equals the customer count from the database query.

### TC-24: No phantom customers

**Objective:** Verify that the output does not contain customers that don't exist in the customers table.

**Steps:**
1. Execute V2 for 2024-12-31.
2. Extract all customer_id values from the output.
3. Query:
   ```sql
   SELECT id FROM datalake.customers WHERE as_of = '2024-12-31';
   ```
4. Verify every output customer_id exists in the database result.

**Expected Result:** No output customer_id is missing from the customers table. The output is a subset (or full set) of customers.id.

### TC-25: first_name and last_name NULL coalescing

**Objective:** Verify that NULL first_name or last_name values are coalesced to empty string.

**Steps:**
1. Check for NULL names:
   ```sql
   SELECT id, first_name, last_name FROM datalake.customers
   WHERE as_of = '2024-10-01'
     AND (first_name IS NULL OR last_name IS NULL)
   LIMIT 5;
   ```
2. If any exist, execute V2 and check those customers' output rows.

**Expected Result:** NULL first_name or last_name values appear as empty strings in the CSV output, matching V1's `?.ToString() ?? ""` behavior.
