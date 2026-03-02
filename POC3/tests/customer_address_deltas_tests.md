# CustomerAddressDeltas -- Test Plan

## 1. Overview

This test plan validates the V2 implementation of the CustomerAddressDeltas job, which detects day-over-day changes in customer address records by comparing the current effective date's address snapshot against the previous day's snapshot. It produces delta records (NEW or UPDATED) with Parquet output in Append mode.

**BRD:** `POC3/brd/customer_address_deltas_brd.md`
**FSD:** `POC3/fsd/customer_address_deltas_fsd.md`
**V2 Tier:** Tier 3 (Full External: External -> ParquetFileWriter)

---

## 2. Traceability Matrix

| Test ID | BRD Requirement | FSD Section | Description |
|---------|----------------|-------------|-------------|
| TC-01 | BR-1 | 10 (Execute Method Flow, step 1-2) | Uses __minEffectiveDate; previous date = current - 1 |
| TC-02 | BR-2 | 1 (Tier Justification), 5 (SQL Design) | Direct PostgreSQL queries, no DataSourcing |
| TC-03 | BR-3 | 10 (Execute Method Flow, step 5) | Baseline day produces null-filled sentinel row |
| TC-04 | BR-4 | 10 (Execute Method Flow, step 7) | NEW delta: address_id in current but not previous |
| TC-05 | BR-5 | 10 (Execute Method Flow, step 7) | UPDATED delta: address_id in both, field changed |
| TC-06 | BR-6 | 10 (Constants, CompareFields) | Compare fields list is correct and complete |
| TC-07 | BR-7 | 10 (Execute Method Flow, step 7) | DELETED addresses not detected |
| TC-08 | BR-8 | 5 (Query 2), 10 (FetchCustomerNames) | Customer names via DISTINCT ON, most recent as_of |
| TC-09 | BR-9 | 10 (Execute Method Flow, step 7) | Customer name formatted as "first_name last_name" |
| TC-10 | BR-10 | 4 (Output Schema, country) | Country field trimmed in output |
| TC-11 | BR-11 | 10 (FormatDate helper) | Date fields formatted as "yyyy-MM-dd" strings |
| TC-12 | BR-12 | 4 (Output Schema, as_of) | as_of stored as "yyyy-MM-dd" string |
| TC-13 | BR-13 | 10 (Execute Method Flow, step 9) | record_count stamped on every row |
| TC-14 | BR-14 | 10 (Execute Method Flow, step 8) | No deltas detected: null-filled sentinel row with record_count=0 |
| TC-15 | BR-15 | 10 (Execute Method Flow, step 7) | Delta rows ordered by address_id ascending |
| TC-16 | BR-16 | 10 (Normalize helper) | Normalize: trim strings, format dates, null->empty |
| TC-17 | Writer Config | 7 (Writer Configuration) | Parquet, 1 part, Append mode |
| TC-18 | AP7 | 3 (Anti-Pattern Analysis), 10 (Constants) | Named constants for CompareFields and DateFormat |
| TC-19 | -- | 4 (Output Schema) | Column order matches V1 exactly (13 columns) |
| TC-20 | BRD Edge Case | 10 (Execute Method Flow, step 7) | Unknown customer_id defaults to empty string name |
| TC-21 | -- | 8 (Proofmark Config) | Proofmark config is strict with zero exclusions |
| TC-22 | BR-3, BR-14 | 10 (Execute Method Flow) | Baseline row vs no-delta row: both produce sentinel |
| TC-23 | BRD Edge Case | -- | Multiple addresses per customer tracked independently |
| TC-24 | BRD Edge Case | -- | Weekend dates have no special handling |
| TC-25 | BR-11 | 10 (FormatDate helper) | Null start_date/end_date remain null in output |
| TC-26 | Write Mode | 7 (Writer Configuration) | Append mode accumulates delta sets across dates |
| TC-27 | BR-5, BR-6 | 10 (HasFieldChanged, Normalize) | Field change detection with NULL values |
| TC-28 | -- | 1 (Tier Justification) | Tier 3 justified: cross-date access + DISTINCT ON |

---

## 3. Test Cases

### TC-01: Effective Date Source and Previous Date Computation

**Requirement:** BR-1
**Description:** The job reads the current effective date from `__minEffectiveDate` (not `__maxEffectiveDate`) and computes the previous date as `currentDate.AddDays(-1)`.
**Preconditions:** Job executed by the framework with effective date injected into shared state.
**Steps:**
1. Run V2 job with effective date = 2024-10-15.
2. Verify the External module reads `sharedState[DataSourcing.MinDateKey]` to get `2024-10-15`.
3. Verify the previous date is computed as `2024-10-14`.
4. Verify addresses are fetched for both 2024-10-15 (current) and 2024-10-14 (previous).
**Expected Result:** Current date = effective date from MinDateKey; previous date = current - 1.
**Pass Criteria:** Address queries target exactly these two dates.

---

### TC-02: Direct PostgreSQL Queries (No DataSourcing)

**Requirement:** BR-2
**Description:** The V2 job config has NO DataSourcing modules. The External module queries PostgreSQL directly via NpgsqlConnection.
**Preconditions:** V2 job config exists.
**Steps:**
1. Read V2 job config JSON.
2. Verify no module has `"type": "DataSourcing"`.
3. Verify the External module is first in the module chain.
4. Verify the External module opens a NpgsqlConnection and executes SQL against `datalake.addresses` and `datalake.customers`.
**Expected Result:** No DataSourcing modules; External queries PostgreSQL directly.
**Pass Criteria:** Job config contains only External + ParquetFileWriter modules.

---

### TC-03: Baseline Day (First Run) -- Null-Filled Sentinel Row

**Requirement:** BR-3
**Description:** On the first effective date (baseline day), when no previous-day address data exists (previous addresses count = 0), the output is a single row with all fields null except `as_of` (formatted current date) and `record_count` (0).
**Preconditions:** Run for the first effective date (2024-10-01) where no data exists for 2024-09-30.
**Steps:**
1. Run V2 job with effective date = 2024-10-01.
2. Verify output has exactly 1 row.
3. Verify sentinel row: `change_type = null`, `address_id = null`, `customer_id = null`, `customer_name = null`, `address_line1 = null`, `city = null`, `state_province = null`, `postal_code = null`, `country = null`, `start_date = null`, `end_date = null`.
4. Verify sentinel row: `as_of = "2024-10-01"`, `record_count = 0`.
**Expected Result:** Single null-filled row with as_of and record_count populated.
**Pass Criteria:** Exactly one row with the described null pattern.

---

### TC-04: NEW Delta Detection

**Requirement:** BR-4
**Description:** When an `address_id` exists in the current date's snapshot but NOT in the previous date's snapshot, it is flagged as "NEW".
**Preconditions:** Address ID 500 exists on 2024-10-15 but not on 2024-10-14.
**Steps:**
1. Run V2 job with effective date = 2024-10-15.
2. Verify the output contains a row for address_id = 500 with `change_type = "NEW"`.
3. Verify the row contains all address fields from the current snapshot.
**Expected Result:** NEW delta record for the new address.
**Pass Criteria:** change_type = "NEW" and all address fields populated from current-day data.

---

### TC-05: UPDATED Delta Detection

**Requirement:** BR-5
**Description:** When an `address_id` exists in both the current and previous snapshots and at least one compare field has changed, it is flagged as "UPDATED".
**Preconditions:** Address ID 100 exists on both 2024-10-14 and 2024-10-15. The `city` field changed from "Springfield" to "Shelbyville".
**Steps:**
1. Run V2 job with effective date = 2024-10-15.
2. Verify the output contains a row for address_id = 100 with `change_type = "UPDATED"`.
3. Verify the row contains the CURRENT (new) field values.
**Expected Result:** UPDATED delta record with current-day field values.
**Pass Criteria:** change_type = "UPDATED" and fields reflect the current snapshot.

---

### TC-06: Compare Fields Completeness

**Requirement:** BR-6
**Description:** The compare fields for change detection are: customer_id, address_line1, city, state_province, postal_code, country, start_date, end_date. All eight fields are checked.
**Preconditions:** V2 External module source code exists.
**Steps:**
1. Inspect the V2 External module's `CompareFields` constant.
2. Verify it contains exactly these 8 fields: `customer_id`, `address_line1`, `city`, `state_province`, `postal_code`, `country`, `start_date`, `end_date`.
3. Verify `HasFieldChanged` iterates all fields in `CompareFields`.
**Expected Result:** All 8 compare fields present; no extras, no missing.
**Pass Criteria:** CompareFields array matches exactly.

---

### TC-07: DELETED Addresses Not Detected

**Requirement:** BR-7
**Description:** Addresses present in the previous day but absent from the current day are NOT flagged. No "DELETED" change type exists.
**Preconditions:** Address ID 200 exists on 2024-10-14 but NOT on 2024-10-15.
**Steps:**
1. Run V2 job with effective date = 2024-10-15.
2. Verify no output row exists for address_id = 200.
3. Verify no row with `change_type = "DELETED"` exists anywhere in the output.
**Expected Result:** Deleted addresses are silently ignored.
**Pass Criteria:** No output row for the deleted address; no "DELETED" change_type in output.

---

### TC-08: Customer Name Lookup (DISTINCT ON, Most Recent)

**Requirement:** BR-8
**Description:** Customer names are fetched using `DISTINCT ON (id) ... WHERE as_of <= currentDate ORDER BY id, as_of DESC`, returning the most recent name for each customer as of the current date.
**Preconditions:** Customer ID 42 has name records on as_of = 2024-10-01 ("Alice Smith") and as_of = 2024-10-10 ("Alice Jones"). Current date = 2024-10-15.
**Steps:**
1. Run V2 job with effective date = 2024-10-15.
2. Verify that any delta row for customer_id = 42 has `customer_name = "Alice Jones"` (the more recent name).
**Expected Result:** Most recent customer name used.
**Pass Criteria:** customer_name reflects the most recent as_of record <= current date.

---

### TC-09: Customer Name Formatting

**Requirement:** BR-9
**Description:** Customer name is formatted as "first_name last_name" with a single space separator.
**Preconditions:** Customer has first_name = "John" and last_name = "Doe".
**Steps:**
1. Run V2 job.
2. Verify `customer_name = "John Doe"`.
**Expected Result:** Space-separated concatenation of first_name and last_name.
**Pass Criteria:** customer_name = "John Doe" (not "Doe, John" or "JohnDoe").

---

### TC-10: Country Field Trimming

**Requirement:** BR-10
**Description:** The `country` column is trimmed (`.Trim()`) in the output. The `datalake.addresses.country` column is `character` (fixed-width) type, which may have trailing spaces.
**Preconditions:** An address has `country = "US          "` (with trailing spaces due to fixed-width character type).
**Steps:**
1. Run V2 job.
2. Verify the output row has `country = "US"` (trimmed, no trailing spaces).
**Expected Result:** Country value is trimmed.
**Pass Criteria:** No trailing spaces in the country field.

---

### TC-11: Date Field Formatting

**Requirement:** BR-11
**Description:** `start_date` and `end_date` are formatted as "yyyy-MM-dd" strings in the output.
**Preconditions:** An address has `start_date = 2024-01-15` and `end_date = 2024-12-31` as date/datetime values in the source.
**Steps:**
1. Run V2 job.
2. Verify `start_date = "2024-01-15"` (string).
3. Verify `end_date = "2024-12-31"` (string).
**Expected Result:** Dates formatted as "yyyy-MM-dd" strings.
**Pass Criteria:** String values match the expected format.

---

### TC-12: as_of Stored as String

**Requirement:** BR-12
**Description:** The `as_of` field in the output is stored as a "yyyy-MM-dd" string, not a DateOnly or date type.
**Preconditions:** Effective date = 2024-10-15.
**Steps:**
1. Run V2 job.
2. Inspect the output Parquet file schema.
3. Verify `as_of` is a string type with value `"2024-10-15"`.
**Expected Result:** as_of is a string in "yyyy-MM-dd" format.
**Pass Criteria:** Type is string; value matches the expected format.

---

### TC-13: record_count Stamped on Every Row

**Requirement:** BR-13
**Description:** The `record_count` field is set to the total number of delta rows and this value is stamped on EVERY output row.
**Preconditions:** Run produces 5 delta rows (e.g., 3 NEW + 2 UPDATED).
**Steps:**
1. Run V2 job.
2. Verify output has 5 rows.
3. Verify ALL 5 rows have `record_count = 5`.
**Expected Result:** Every row has the same record_count = total delta count.
**Pass Criteria:** record_count is identical on all rows and equals the total row count.

---

### TC-14: No Deltas Detected -- Null-Filled Sentinel Row

**Requirement:** BR-14
**Description:** When both snapshots exist but no NEW or UPDATED changes are found (all addresses identical between days), a single null-filled sentinel row is produced with `as_of` and `record_count = 0`.
**Preconditions:** Previous and current snapshots contain the same addresses with identical field values.
**Steps:**
1. Run V2 job for a date where no changes occurred.
2. Verify output has exactly 1 row.
3. Verify sentinel row structure matches TC-03: all fields null except `as_of` and `record_count = 0`.
**Expected Result:** Single sentinel row for no-change days.
**Pass Criteria:** One row, all nulls except as_of (current date string) and record_count (0).

---

### TC-15: Delta Rows Ordered by address_id Ascending

**Requirement:** BR-15
**Description:** When multiple deltas are detected, the output rows are ordered by `address_id` in ascending order.
**Preconditions:** Deltas detected for address_ids 300, 100, 500, 200.
**Steps:**
1. Run V2 job.
2. Verify output rows appear in order: address_id 100, 200, 300, 500.
**Expected Result:** Rows sorted by address_id ascending.
**Pass Criteria:** Output row order matches ascending address_id.

---

### TC-16: Normalize Function Behavior

**Requirement:** BR-16
**Description:** The `Normalize` function trims string values, converts DateTime/DateOnly to "yyyy-MM-dd", and treats null/DBNull as empty string for comparison purposes.
**Preconditions:** V2 External module source code exists.
**Steps:**
1. Inspect `Normalize` method in V2 code.
2. Verify null input returns `""`.
3. Verify DBNull input returns `""`.
4. Verify DateTime input returns date formatted as "yyyy-MM-dd".
5. Verify DateOnly input returns date formatted as "yyyy-MM-dd".
6. Verify string input is trimmed.
**Expected Result:** Normalize handles all input types as specified.
**Pass Criteria:** Method handles null, DBNull, DateTime, DateOnly, and string correctly.

---

### TC-17: Writer Configuration

**Requirement:** Writer Config
**Description:** V2 uses ParquetFileWriter with numParts=1, writeMode=Append, and output to `Output/double_secret_curated/customer_address_deltas/`.
**Preconditions:** V2 job config exists.
**Steps:**
1. Read V2 job config JSON.
2. Verify writer type = `ParquetFileWriter`.
3. Verify `source` = `output`.
4. Verify `outputDirectory` = `Output/double_secret_curated/customer_address_deltas/`.
5. Verify `numParts` = 1.
6. Verify `writeMode` = `Append`.
**Expected Result:** All writer params match the specification.
**Pass Criteria:** All five writer parameters match.

---

### TC-18: Named Constants (AP7 Elimination)

**Requirement:** AP7
**Description:** V2 uses named constants for compare fields (`CompareFields`), output columns (`OutputColumns`), and date format (`DateFormat`) instead of inline magic values.
**Preconditions:** V2 External module source code exists.
**Steps:**
1. Inspect V2 code for `CompareFields` constant with documenting comment.
2. Inspect V2 code for `OutputColumns` constant with documenting comment.
3. Inspect V2 code for `DateFormat` constant = `"yyyy-MM-dd"`.
4. Verify no inline string literals for these values appear elsewhere in the code.
**Expected Result:** All magic values replaced with named constants.
**Pass Criteria:** Constants exist with documentation; no inline duplicates.

---

### TC-19: Column Order

**Requirement:** Output Schema
**Description:** Output column order matches V1 exactly: change_type, address_id, customer_id, customer_name, address_line1, city, state_province, postal_code, country, start_date, end_date, as_of, record_count (13 columns).
**Preconditions:** V2 job produces output.
**Steps:**
1. Run V2 job and inspect the output Parquet schema.
2. Verify column order: change_type, address_id, customer_id, customer_name, address_line1, city, state_province, postal_code, country, start_date, end_date, as_of, record_count.
**Expected Result:** Column order is identical to V1.
**Pass Criteria:** All 13 columns in exact order.

---

### TC-20: Unknown Customer ID -- Empty String Name

**Requirement:** BRD Edge Case
**Description:** When a customer_id from the addresses table is not found in the customer names lookup, the customer_name defaults to empty string.
**Preconditions:** An address exists for customer_id = 9999, but no customer record exists in `datalake.customers` for that ID.
**Steps:**
1. Run V2 job for a date with a delta for customer_id = 9999.
2. Verify `customer_name = ""` (empty string, not null) for that row.
**Expected Result:** Empty string for unknown customer.
**Pass Criteria:** customer_name is empty string, not null or an error.

---

### TC-21: Proofmark Config Validation

**Requirement:** Proofmark Config
**Description:** The Proofmark config for this job should be strict with no exclusions and no fuzzy overrides.
**Preconditions:** Proofmark config YAML exists at `POC3/proofmark_configs/customer_address_deltas.yaml`.
**Steps:**
1. Read the Proofmark config.
2. Verify `reader: parquet`.
3. Verify `threshold: 100.0`.
4. Verify no `columns.excluded` entries.
5. Verify no `columns.fuzzy` entries.
**Expected Result:** Strict config with zero overrides.
**Pass Criteria:** Config matches the FSD's proposed default strict configuration.

---

### TC-22: Baseline Row vs No-Delta Row Equivalence

**Requirement:** BR-3, BR-14
**Description:** Both the baseline day (no previous data) and a no-delta day (previous data exists but is identical) produce the same sentinel row structure: all fields null except as_of and record_count=0.
**Preconditions:** Two different dates: one baseline, one with no changes.
**Steps:**
1. Run V2 job for the baseline date (e.g., 2024-10-01). Capture output row structure.
2. Run V2 job for a no-change date (e.g., 2024-10-03 where data is identical to 2024-10-02). Capture output row structure.
3. Compare the two sentinel rows: they should be structurally identical except for the `as_of` value.
**Expected Result:** Both sentinel rows have the same null pattern; only as_of differs.
**Pass Criteria:** Identical sentinel row format for both cases.

---

### TC-23: Multiple Addresses Per Customer

**Requirement:** BRD Edge Case
**Description:** Each address is tracked independently by address_id. A single customer can have multiple address records, each producing independent delta detection.
**Preconditions:** Customer ID 42 has address_ids 100 and 101. Address 100 is unchanged; address 101 has a city change.
**Steps:**
1. Run V2 job.
2. Verify address_id 100 does NOT appear in the output (no change).
3. Verify address_id 101 appears with `change_type = "UPDATED"`.
**Expected Result:** Independent delta detection per address_id.
**Pass Criteria:** Changed address detected; unchanged address omitted.

---

### TC-24: Weekend Dates -- No Special Handling

**Requirement:** BRD Edge Case
**Description:** Unlike customer_360_snapshot (which has weekend fallback), this job has NO weekend-specific logic. Saturday's run compares against Friday normally.
**Preconditions:** Effective date = Saturday 2024-10-05.
**Steps:**
1. Run V2 job with effective date = 2024-10-05 (Saturday).
2. Verify current date = 2024-10-05 and previous date = 2024-10-04 (Friday).
3. Verify delta detection proceeds normally comparing Saturday vs Friday data.
4. Verify output as_of = "2024-10-05" (Saturday, not adjusted).
**Expected Result:** No weekend fallback; dates used as-is.
**Pass Criteria:** Comparison uses exact dates with no adjustment.

---

### TC-25: Null start_date and end_date

**Requirement:** BR-11
**Description:** When `start_date` or `end_date` is null in the source data, the output preserves the null (does not convert to empty string or a default date).
**Preconditions:** An address has `start_date = 2024-01-01` and `end_date = null`.
**Steps:**
1. Run V2 job for a date where this address is a delta.
2. Verify `start_date = "2024-01-01"` (formatted string).
3. Verify `end_date = null` (not "" or "0001-01-01" or any other default).
**Expected Result:** Null dates remain null in output.
**Pass Criteria:** end_date is null in the output row.

---

### TC-26: Append Mode Accumulation

**Requirement:** Write Mode Implications
**Description:** Because the writer uses Append mode, output from each effective date is appended to the existing Parquet directory. After running multiple dates, the output contains delta sets from ALL dates.
**Preconditions:** Run V2 job for effective dates 2024-10-01 through 2024-10-03.
**Steps:**
1. Run V2 job for 2024-10-01 (baseline).
2. Run V2 job for 2024-10-02 (may have deltas).
3. Run V2 job for 2024-10-03 (may have deltas).
4. Read the output directory.
5. Verify all three dates' outputs are present (rows with as_of = "2024-10-01", "2024-10-02", "2024-10-03").
**Expected Result:** Historical delta sets preserved across runs.
**Pass Criteria:** Output contains rows from all three effective dates.

---

### TC-27: Field Change Detection with NULL Values

**Requirement:** BR-5, BR-6, BR-16
**Description:** When a compare field transitions from null to a value (or value to null), the Normalize function should detect this as a change. Null normalizes to empty string for comparison.
**Preconditions:** Address ID 300 has `postal_code = null` on 2024-10-14 and `postal_code = "90210"` on 2024-10-15.
**Steps:**
1. Run V2 job with effective date = 2024-10-15.
2. Verify address_id 300 appears in output with `change_type = "UPDATED"`.
**Expected Result:** Null-to-value change detected as UPDATED.
**Pass Criteria:** The transition is detected; change_type = "UPDATED".

---

### TC-28: Tier 3 Justification Validation

**Requirement:** Tier Selection
**Description:** The V2 design correctly uses Tier 3 (External -> Writer) because: (1) cross-date data access is incompatible with DataSourcing (requires current and previous dates), and (2) DISTINCT ON is a PostgreSQL extension not available in SQLite.
**Preconditions:** FSD exists and documents the tier justification.
**Steps:**
1. Read the FSD tier justification section.
2. Verify it cites both reasons: cross-date access and DISTINCT ON.
3. Verify the V2 job config has exactly 2 modules: External + ParquetFileWriter.
4. Verify no DataSourcing or Transformation modules are present.
**Expected Result:** Tier 3 is properly justified and implemented.
**Pass Criteria:** FSD documents both reasons; config matches Tier 3 pattern.

---

## 4. Edge Case Summary

| Edge Case | Covered By | Expected Behavior |
|-----------|-----------|-------------------|
| Baseline day (no previous data) | TC-03 | Single null-filled sentinel row with as_of + record_count=0 |
| No changes detected | TC-14 | Single null-filled sentinel row with as_of + record_count=0 |
| DELETED address (previous only) | TC-07 | Not detected; silently ignored |
| Weekend effective date | TC-24 | No special handling; dates used as-is |
| Country with trailing spaces | TC-10 | Trimmed in output |
| Null start_date or end_date | TC-25 | Preserved as null in output |
| Unknown customer_id | TC-20 | customer_name = empty string |
| Multiple addresses per customer | TC-23 | Each tracked independently by address_id |
| NULL compare field transitions | TC-27 | Detected as change (null normalizes to empty string) |
| Baseline vs no-delta sentinel structure | TC-22 | Identical structure, different as_of |
| Append mode across multiple dates | TC-26 | Historical deltas accumulated |
| record_count consistency | TC-13 | Same value stamped on every row in a run |

---

## 5. Output Format Validation

| Property | Expected Value | Test |
|----------|---------------|------|
| File format | Parquet | TC-17 |
| Number of part files | 1 | TC-17 |
| Write mode | Append | TC-17 |
| Output path | `Output/double_secret_curated/customer_address_deltas/` | TC-17 |
| Column count | 13 | TC-19 |
| Column order | change_type, address_id, customer_id, customer_name, address_line1, city, state_province, postal_code, country, start_date, end_date, as_of, record_count | TC-19 |
| as_of type | String ("yyyy-MM-dd") | TC-12 |
| start_date/end_date type | String ("yyyy-MM-dd") or null | TC-11, TC-25 |
