# MerchantCategoryDirectory -- V2 Test Plan

## Job Info
- **V2 Config**: `merchant_category_directory_v2.json`
- **Tier**: 1 (Framework Only)
- **External Module**: None

## Pre-Conditions

1. PostgreSQL is accessible at `172.18.0.1` with user `claude`, database `atc`.
2. The `datalake.merchant_categories` table exists and contains data for the date range 2024-10-01 through 2024-12-31.
3. The V1 baseline output exists at `Output/curated/merchant_category_directory.csv`.
4. The V2 output directory `Output/double_secret_curated/` exists or will be created by the framework.
5. No prior V2 output file exists at `Output/double_secret_curated/merchant_category_directory.csv` (clean slate for Append mode testing).
6. The V1 job config at `JobExecutor/Jobs/merchant_category_directory.json` is unmodified (reference only).

## Test Cases

### TC-1: Output Schema Validation

**Objective:** Verify V2 output CSV has the correct columns in the correct order.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 1.1 | Run V2 job for a single effective date (e.g., 2024-10-01). | Job completes successfully. |
| 1.2 | Read the header row of `Output/double_secret_curated/merchant_category_directory.csv`. | Header is: `mcc_code,mcc_description,risk_level,as_of` |
| 1.3 | Verify column count is exactly 4. | 4 columns per row. |
| 1.4 | Verify column order matches V1 header exactly. | Order: `mcc_code`, `mcc_description`, `risk_level`, `as_of`. |

### TC-2: Row Count Equivalence

**Objective:** Verify V2 produces the same number of data rows as V1.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 2.1 | Count total lines in V1 output at `Output/curated/merchant_category_directory.csv`. | Record as `V1_lines`. |
| 2.2 | Run V2 job across the full date range (2024-10-01 to 2024-12-31) via auto-advance. | Job completes for all dates. |
| 2.3 | Count total lines in V2 output at `Output/double_secret_curated/merchant_category_directory.csv`. | Record as `V2_lines`. |
| 2.4 | Compare V1_lines and V2_lines. | Values are identical. Expected: 1 header + (20 rows/day x 92 days) = 1841 lines total. |
| 2.5 | Verify 20 data rows per effective date by sampling 3 dates (start, middle, end of range). | Each sampled date contributes exactly 20 rows. |

### TC-3: Data Content Equivalence

**Objective:** Verify V2 output data matches V1 byte-for-byte (no W-codes apply that would cause intentional divergence).

| Step | Action | Expected Result |
|------|--------|-----------------|
| 3.1 | Run Proofmark comparison between V1 and V2 output files. | 100% match -- all rows identical. |
| 3.2 | Spot-check 3 random dates: verify `mcc_code`, `mcc_description`, `risk_level`, `as_of` values match between V1 and V2. | All field values are identical. |
| 3.3 | Verify the risk_level distribution for any single date: 15 Low, 3 Medium, 2 High. | Distribution matches BRD BR-4. |
| 3.4 | Verify all 20 distinct MCC codes appear for each sampled date. | 20 unique `mcc_code` values per date. |

**W-code notes:**
- No W-codes affect data content for this job. W9 (Append mode) affects file structure, not data values. W9 is intentionally reproduced (see TC-4).

### TC-4: Writer Configuration

**Objective:** Verify all CsvFileWriter settings match V1.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 4.1 | Inspect V2 config: `writeMode` field. | Value is `"Append"`. |
| 4.2 | Inspect V2 config: `includeHeader` field. | Value is `true`. |
| 4.3 | Inspect V2 config: `lineEnding` field. | Value is `"LF"`. |
| 4.4 | Inspect V2 config: `trailerFormat` field. | Field is absent (no trailer). |
| 4.5 | Inspect V2 config: `outputFile` field. | Value is `"Output/double_secret_curated/merchant_category_directory.csv"`. |
| 4.6 | Verify output file uses LF line endings (not CRLF). | `od -c` or `file` command confirms LF-only line endings. |
| 4.7 | Verify header row appears exactly once in the output file (Append mode header suppression). | Only 1 line in the file matches the header pattern `mcc_code,mcc_description,risk_level,as_of`. |
| 4.8 | Verify no trailer rows exist anywhere in the output file. | No lines match a trailer pattern (e.g., starting with `TRAILER`). |

### TC-5: Anti-Pattern Elimination Verification

**Objective:** Confirm all identified AP-codes are eliminated in V2.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 5.1 | **AP1 -- Dead-end sourcing.** Inspect V2 config for `cards` DataSourcing entry. | No `cards` DataSourcing entry exists. Only `merchant_categories` is sourced. |
| 5.2 | **AP1 verification.** Count the number of DataSourcing modules in V2 config. | Exactly 1 DataSourcing module (was 2 in V1). |
| 5.3 | **AP4 -- Unused columns.** Verify every column in the `merchant_categories` DataSourcing entry is used in the SQL. | Columns `mcc_code`, `mcc_description`, `risk_level` are all present in the SQL SELECT clause. No unused columns sourced. |
| 5.4 | **AP4 verification (moot).** Confirm the `cards` columns (`card_id`, `customer_id`, `card_type`) are not present anywhere in V2 config. | No reference to card-related columns. |

### TC-6: Edge Cases

**Objective:** Verify correct behavior under edge conditions.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 6.1 | **Weekend data.** Run V2 for a Saturday date (e.g., 2024-10-05) and a Sunday date (e.g., 2024-10-06). | Both produce 20 data rows each. No skip, no fallback. |
| 6.2 | **Append accumulation.** Run V2 for 3 consecutive dates. Verify the output file grows by 20 rows each day. | After day 1: 1 header + 20 data = 21 lines. After day 2: 21 + 20 = 41 lines. After day 3: 41 + 20 = 61 lines. |
| 6.3 | **Header written once.** After multi-day run, verify the header row appears only at line 1. | No duplicate headers at any position in the file. |
| 6.4 | **as_of column correctness.** For each effective date run, verify the `as_of` value in the output rows matches the effective date. | `as_of` matches the current effective date for all 20 rows of that date's output. |
| 6.5 | **First effective date.** Verify V2 config `firstEffectiveDate` is `2024-10-01`. | Matches V1's `firstEffectiveDate`. |
| 6.6 | **Clean file start.** Delete the output file, run V2 for a single date. Verify the file is created fresh with a header row. | File created with header + 20 data rows = 21 lines. |

### TC-7: Proofmark Configuration

**Objective:** Verify the Proofmark YAML config is correct for this job.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 7.1 | Verify `comparison_target` is `"merchant_category_directory"`. | Correct. |
| 7.2 | Verify `reader` is `"csv"`. | Correct. |
| 7.3 | Verify `threshold` is `100.0`. | Correct -- strict full match required. |
| 7.4 | Verify `csv.header_rows` is `1`. | Correct -- one header row at top of file. |
| 7.5 | Verify `csv.trailer_rows` is `0`. | Correct -- no trailer configured. |
| 7.6 | Verify no `columns.excluded` entries exist. | Correct -- no non-deterministic fields. |
| 7.7 | Verify no `columns.fuzzy` entries exist. | Correct -- all columns are string or date pass-throughs, no numeric precision concerns. |
| 7.8 | Run Proofmark with this config against V1 and V2 outputs. | Proofmark reports 100% match. |

## W-Code Test Cases

### TC-W9: Wrong writeMode (Append)

**Objective:** Verify V2 reproduces V1's Append mode behavior, which causes the file to accumulate redundant reference data rows across dates.

| Step | Action | Expected Result |
|------|--------|-----------------|
| W9.1 | Inspect V2 config `writeMode`. | Value is `"Append"` -- matches V1. |
| W9.2 | Run V2 for 2024-10-01 only. Record the file size and line count. | 1 header + 20 data rows = 21 lines. |
| W9.3 | Run V2 for 2024-10-02 (next date). Record the file size and line count. | 21 + 20 = 41 lines. File was appended to, not overwritten. |
| W9.4 | Verify no second header row was written on the second run. | Only one header row exists (line 1). |
| W9.5 | Verify the 20 rows appended for 2024-10-02 have `as_of = 2024-10-02`, while the first 20 data rows have `as_of = 2024-10-01`. | Dates are correct per block. |
| W9.6 | Run V2 across the full date range. Compare total line count with V1. | Both files have 1841 lines (1 header + 92 days x 20 rows). |

## Notes

- This is the simplest possible job in the framework: a single-table pass-through SELECT with no joins, no aggregation, no computed columns, and no External module.
- The only substantive V2 change is removing the dead `cards` DataSourcing entry (AP1/AP4). This has zero impact on output data since V1 never used `cards` in its SQL.
- Row ordering within a given `as_of` date is determined by SQLite's query plan. Since V1 and V2 use the identical SQL (`SELECT mc.mcc_code, mc.mcc_description, mc.risk_level, mc.as_of FROM merchant_categories mc`) through the same framework path, row order should be identical. If Proofmark detects ordering differences, add `ORDER BY mc.mcc_code` to V2 SQL and verify it matches V1's actual order.
- The job name `MerchantCategoryDirectory` accurately describes the output (a directory/listing of merchant categories). No AP9 concern.
