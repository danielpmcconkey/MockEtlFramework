# CustomerAddressHistory -- Test Plan

## Overview

This test plan validates the V2 implementation of `CustomerAddressHistoryV2`, a Tier 1 (framework-only) job that produces a historical record of customer addresses. The job sources the `addresses` table, filters out NULL `customer_id` rows, orders by `customer_id`, and writes to Parquet in Append mode with 2 part files.

**Source Documents:**
- BRD: `POC3/brd/customer_address_history_brd.md`
- FSD: `POC3/fsd/customer_address_history_fsd.md`

---

## Traceability Matrix

| Test ID | BRD Requirement | FSD Section | Description |
|---------|----------------|-------------|-------------|
| TC-CAH-001 | BR-1 | SQL Design | NULL customer_id rows are excluded from output |
| TC-CAH-002 | BR-2 | SQL Design | Output is ordered by customer_id ascending |
| TC-CAH-003 | BR-3 | SQL Design, Output Schema | as_of column is included in output |
| TC-CAH-004 | BR-4 / AP1 | Anti-Pattern Analysis | Branches table is NOT sourced in V2 (dead-end sourcing eliminated) |
| TC-CAH-005 | BR-5 | V2 Job Config | Transformation result stored under name "addr_history" |
| TC-CAH-006 | BR-6 | V2 Job Config | ParquetFileWriter reads from "addr_history" |
| TC-CAH-007 | BR-7 / AP4 | Anti-Pattern Analysis, Output Schema | address_id is NOT in output; unused column eliminated from DataSourcing |
| TC-CAH-008 | -- | Writer Configuration | Output is Parquet format with 2 part files |
| TC-CAH-009 | -- | Writer Configuration | Write mode is Append |
| TC-CAH-010 | -- | Output Schema | Output schema has exactly 7 columns in correct order |
| TC-CAH-011 | AP8 | SQL Design | SQL is simplified (no unnecessary subquery) |
| TC-CAH-012 | -- | Edge Cases | Append mode accumulation across multiple effective dates |
| TC-CAH-013 | -- | Edge Cases | Zero-row output when all addresses have NULL customer_id |
| TC-CAH-014 | -- | Edge Cases | NULL values in non-filtered columns pass through |
| TC-CAH-015 | -- | Edge Cases | Boundary effective dates (first and last day of range) |
| TC-CAH-016 | -- | Proofmark | V2 output matches V1 output at 100% threshold |
| TC-CAH-017 | -- | V2 Job Config | Output directory is Output/double_secret_curated/customer_address_history/ |
| TC-CAH-018 | -- | V2 Job Config | firstEffectiveDate is 2024-10-01 |

---

## Test Cases

### TC-CAH-001: NULL customer_id Filtering

**Traces to:** BR-1
**Priority:** HIGH

**Objective:** Verify that rows where `customer_id IS NULL` are excluded from the output.

**Preconditions:**
- `datalake.addresses` contains at least one row with `customer_id = NULL` within the effective date range.

**Steps:**
1. Run CustomerAddressHistoryV2 for a single effective date where NULL customer_id rows exist in the source.
2. Read the output Parquet file(s).
3. Check that no row in the output has a NULL `customer_id`.
4. Query the source: `SELECT COUNT(*) FROM datalake.addresses WHERE customer_id IS NULL AND as_of = '{effective_date}'`. Confirm this count is > 0 (precondition validation).
5. Compare output row count to source row count minus the NULL rows.

**Expected Result:**
- Output contains zero rows with NULL customer_id.
- Output row count = source row count minus rows where customer_id IS NULL.

**Edge Cases:**
- All rows in source have NULL customer_id: output should have zero data rows but valid Parquet structure.
- customer_id = 0 (zero, not NULL): should be included in output since the filter is `IS NOT NULL`, not `!= 0`.

---

### TC-CAH-002: Output Ordering by customer_id

**Traces to:** BR-2
**Priority:** HIGH

**Objective:** Verify that output rows are ordered by `customer_id` ascending.

**Preconditions:**
- Source data contains multiple distinct customer_id values.

**Steps:**
1. Run CustomerAddressHistoryV2 for a single effective date.
2. Read all output Parquet part files in order (part-00000, part-00001).
3. Extract the `customer_id` column from the concatenated output.
4. Verify that the sequence is non-decreasing (ascending order).

**Expected Result:**
- For every consecutive pair of rows (i, i+1): `customer_id[i] <= customer_id[i+1]`.

**Edge Cases:**
- Multiple addresses for the same customer_id: rows with equal customer_id should appear consecutively (stable relative order).
- Single customer_id in entire dataset: trivially ordered.

---

### TC-CAH-003: as_of Column Included in Output

**Traces to:** BR-3
**Priority:** HIGH

**Objective:** Verify that the `as_of` column from the addresses table is present in the output and contains the snapshot date for each record.

**Preconditions:**
- Source data has a known as_of date.

**Steps:**
1. Run CustomerAddressHistoryV2 for effective date 2024-10-01.
2. Read the output Parquet file(s).
3. Confirm that `as_of` is one of the output columns.
4. Verify that the `as_of` values in the output match the as_of values in the source `datalake.addresses` table for the corresponding rows.

**Expected Result:**
- `as_of` column is present in output schema.
- Values match the source data's `as_of` column (pass-through, not modified).

---

### TC-CAH-004: Branches Table NOT Sourced in V2 (AP1 Elimination)

**Traces to:** BR-4, AP1
**Priority:** MEDIUM

**Objective:** Verify that V2 eliminates the dead-end `branches` DataSourcing entry that was present in V1 but never used.

**Preconditions:**
- V2 job config file exists at `JobExecutor/Jobs/customer_address_history_v2.json`.

**Steps:**
1. Read the V2 job config JSON.
2. Enumerate all modules of type `DataSourcing`.
3. Confirm that none of them reference table `branches`.
4. Confirm that only `addresses` is sourced.

**Expected Result:**
- V2 config contains exactly 1 DataSourcing module (for `addresses`).
- No reference to `branches` table anywhere in the config.

---

### TC-CAH-005: Transformation Result Named "addr_history"

**Traces to:** BR-5
**Priority:** HIGH

**Objective:** Verify that the Transformation module stores its result under the name `addr_history` in shared state.

**Preconditions:**
- V2 job config exists.

**Steps:**
1. Read the V2 job config JSON.
2. Find the module with `"type": "Transformation"`.
3. Confirm `"resultName": "addr_history"`.

**Expected Result:**
- Transformation module's `resultName` is exactly `"addr_history"`.

---

### TC-CAH-006: ParquetFileWriter Reads from "addr_history"

**Traces to:** BR-6
**Priority:** HIGH

**Objective:** Verify that the ParquetFileWriter module reads from the `addr_history` DataFrame.

**Preconditions:**
- V2 job config exists.

**Steps:**
1. Read the V2 job config JSON.
2. Find the module with `"type": "ParquetFileWriter"`.
3. Confirm `"source": "addr_history"`.

**Expected Result:**
- ParquetFileWriter's `source` field is exactly `"addr_history"`.

---

### TC-CAH-007: address_id Excluded from Output (AP4 Elimination)

**Traces to:** BR-7, AP4
**Priority:** MEDIUM

**Objective:** Verify that `address_id` is not present in the output and is not sourced in V2.

**Preconditions:**
- V2 job config exists.

**Steps:**
1. Read the V2 job config JSON.
2. Check the DataSourcing module's `columns` array. Confirm `address_id` is not listed.
3. Run the job for a single effective date.
4. Read the output Parquet file(s) and inspect the column schema.
5. Confirm `address_id` is not among the output columns.

**Expected Result:**
- `address_id` does not appear in the DataSourcing columns list.
- `address_id` does not appear in the output Parquet schema.

---

### TC-CAH-008: Parquet Output with 2 Part Files

**Traces to:** Writer Configuration
**Priority:** HIGH

**Objective:** Verify that the output directory contains exactly 2 Parquet part files.

**Preconditions:**
- Job has been run for at least one effective date with non-empty output.

**Steps:**
1. Run CustomerAddressHistoryV2 for a single effective date.
2. List files in `Output/double_secret_curated/customer_address_history/`.
3. Confirm exactly 2 files exist: `part-00000.parquet` and `part-00001.parquet`.

**Expected Result:**
- Output directory contains exactly `part-00000.parquet` and `part-00001.parquet`.

---

### TC-CAH-009: Write Mode is Append

**Traces to:** Writer Configuration
**Priority:** HIGH

**Objective:** Verify that the job config specifies Append write mode.

**Steps:**
1. Read the V2 job config JSON.
2. Find the ParquetFileWriter module.
3. Confirm `"writeMode": "Append"`.

**Expected Result:**
- `writeMode` is `"Append"`.

**Note:** Per the FSD's analysis of ParquetFileWriter.cs behavior, Append mode in this framework actually overwrites same-named part files because `File.Create` truncates existing files. The final output after running all dates will contain only the last effective date's data. This is expected V1-equivalent behavior.

---

### TC-CAH-010: Output Schema -- 7 Columns in Correct Order

**Traces to:** Output Schema
**Priority:** HIGH

**Objective:** Verify the output has exactly 7 columns in the specified order.

**Steps:**
1. Run CustomerAddressHistoryV2 for a single effective date.
2. Read the output Parquet file(s).
3. Extract the column names in order.

**Expected Result:**
- Columns are: `customer_id`, `address_line1`, `city`, `state_province`, `postal_code`, `country`, `as_of` (exactly 7, in this order).

---

### TC-CAH-011: SQL Simplification (AP8 Elimination)

**Traces to:** AP8
**Priority:** LOW

**Objective:** Verify that the V2 SQL is a single-level query (no unnecessary subquery) while preserving functional equivalence.

**Steps:**
1. Read the V2 job config JSON.
2. Extract the SQL string from the Transformation module.
3. Confirm the SQL does NOT contain a nested subquery (no `SELECT ... FROM (SELECT ...)`).
4. Confirm the SQL contains `WHERE a.customer_id IS NOT NULL` and `ORDER BY a.customer_id`.
5. Confirm the SELECT list is: `a.customer_id, a.address_line1, a.city, a.state_province, a.postal_code, a.country, a.as_of`.

**Expected Result:**
- SQL is a flat single-level query.
- SQL produces the same result as V1's nested subquery version.

---

### TC-CAH-012: Append Mode Accumulation Across Multiple Dates

**Traces to:** Edge Case (BRD: Append mode accumulation)
**Priority:** MEDIUM

**Objective:** Verify behavior when running across multiple effective dates. Per FSD analysis, since ParquetFileWriter uses `File.Create` for same-named part files, only the last date's data persists.

**Steps:**
1. Run CustomerAddressHistoryV2 for effective dates 2024-10-01 through 2024-10-03.
2. Inspect the output directory after all runs complete.
3. Read the Parquet part files and check row content.

**Expected Result:**
- Output contains only the data from the last effective date processed (2024-10-03), because `File.Create` overwrites same-named files.
- This matches V1 behavior.

---

### TC-CAH-013: Zero-Row Output

**Traces to:** Edge Case (NULL customer_id)
**Priority:** MEDIUM

**Objective:** Verify behavior when all source addresses have NULL customer_id (i.e., all rows are filtered out).

**Steps:**
1. Hypothetically, if `datalake.addresses` contained only rows with NULL customer_id for a given as_of date, the SQL `WHERE a.customer_id IS NOT NULL` would return zero rows.
2. The ParquetFileWriter should still produce valid Parquet file(s) with the correct schema but zero data rows.

**Expected Result:**
- Output Parquet files are valid (readable, correct schema) with zero data rows.
- No runtime error or crash.

**Note:** This may not be testable with real data if all as_of dates have at least one non-NULL customer_id row. Verify by querying: `SELECT COUNT(*) FROM datalake.addresses WHERE customer_id IS NOT NULL AND as_of = '{date}'`.

---

### TC-CAH-014: NULL Values in Non-Filtered Columns

**Traces to:** Edge Case
**Priority:** MEDIUM

**Objective:** Verify that NULL values in pass-through columns (address_line1, city, state_province, postal_code, country) are preserved as-is in the output.

**Steps:**
1. Query `datalake.addresses` for rows where any of `address_line1`, `city`, `state_province`, `postal_code`, or `country` is NULL.
2. Run CustomerAddressHistoryV2 for an effective date that includes such rows.
3. Read the output Parquet file(s) and locate the corresponding rows.
4. Confirm NULL values are preserved (not coalesced to empty string or any default).

**Expected Result:**
- NULL values in non-filtered columns pass through unchanged to the output.

---

### TC-CAH-015: Boundary Effective Dates

**Traces to:** Edge Case
**Priority:** MEDIUM

**Objective:** Verify correct behavior at the boundaries of the effective date range.

**Steps:**
1. Run CustomerAddressHistoryV2 for the first effective date (2024-10-01).
2. Verify output is produced and contains data matching `datalake.addresses` where `as_of = '2024-10-01'` and `customer_id IS NOT NULL`.
3. Run CustomerAddressHistoryV2 for the last effective date (2024-12-31).
4. Verify output is produced and contains data matching `datalake.addresses` where `as_of = '2024-12-31'` and `customer_id IS NOT NULL`.

**Expected Result:**
- Both boundary dates produce valid output with correct row counts matching filtered source data.

---

### TC-CAH-016: Proofmark Comparison -- V2 Matches V1

**Traces to:** Output Equivalence
**Priority:** CRITICAL

**Objective:** Verify that V2 output is byte-identical to V1 output using Proofmark at 100% threshold.

**Steps:**
1. Run V1 job (CustomerAddressHistory) for the full date range (2024-10-01 through 2024-12-31). Output goes to `Output/curated/customer_address_history/`.
2. Run V2 job (CustomerAddressHistoryV2) for the same full date range. Output goes to `Output/double_secret_curated/customer_address_history/`.
3. Run Proofmark:
   ```bash
   python3 -m proofmark compare \
     --config POC3/proofmark_configs/customer_address_history.yaml \
     --left Output/curated/customer_address_history/ \
     --right Output/double_secret_curated/customer_address_history/ \
     --output POC3/logs/proofmark_reports/customer_address_history.json
   ```
4. Verify exit code is 0 (PASS).

**Expected Result:**
- Proofmark reports 100% match.
- No row differences, no column differences, no schema differences.

**Proofmark Config:**
```yaml
comparison_target: "customer_address_history"
reader: parquet
threshold: 100.0
```

---

### TC-CAH-017: Output Directory Path

**Traces to:** V2 Convention
**Priority:** HIGH

**Objective:** Verify that V2 output goes to the correct directory.

**Steps:**
1. Read the V2 job config JSON.
2. Confirm the ParquetFileWriter `outputDirectory` is `"Output/double_secret_curated/customer_address_history/"`.
3. Run the job and verify files appear in that directory.

**Expected Result:**
- Output directory matches V2 convention.

---

### TC-CAH-018: firstEffectiveDate Configuration

**Traces to:** V2 Job Config
**Priority:** LOW

**Objective:** Verify that the V2 job config preserves the same bootstrap date as V1.

**Steps:**
1. Read the V2 job config JSON.
2. Confirm `"firstEffectiveDate": "2024-10-01"`.

**Expected Result:**
- `firstEffectiveDate` is `"2024-10-01"` (matches V1).

---

## Edge Case Summary

| Scenario | Expected Behavior | Test ID |
|----------|------------------|---------|
| All customer_id values NULL | Zero-row output, valid Parquet schema | TC-CAH-013 |
| customer_id = 0 (not NULL) | Included in output (IS NOT NULL passes) | TC-CAH-001 |
| NULL in address_line1/city/etc | Preserved as NULL in output | TC-CAH-014 |
| First effective date (2024-10-01) | Valid output produced | TC-CAH-015 |
| Last effective date (2024-12-31) | Valid output produced | TC-CAH-015 |
| Multiple dates (Append mode) | Last date's data overwrites prior | TC-CAH-012 |
| Weekend effective dates | No special handling -- processed normally | TC-CAH-015 |
| Duplicate addresses across dates | Each as_of snapshot treated independently | TC-CAH-012 |
| Single customer with multiple addresses | All addresses included, ordered by customer_id | TC-CAH-002 |
