# CreditScoreSnapshot -- Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | All credit score rows pass through with no filtering or transformation |
| TC-02   | BR-2           | Empty credit_scores input produces empty output with correct schema |
| TC-03   | BR-3 / AP1     | Branches table is not sourced in V2 (dead-end eliminated) |
| TC-04   | Output Schema  | Output contains exactly 5 columns in correct order |
| TC-05   | Output Schema  | as_of column is present via DataSourcing auto-injection |
| TC-06   | Writer Config  | CSV output uses Overwrite mode, CRLF line endings, header, no trailer |
| TC-07   | AP3 / AP6      | V2 uses Tier 1 (no External module) -- SQL replaces row-by-row foreach |
| TC-08   | AP4            | Unused columns (from branches) not sourced in V2 |
| TC-09   | Edge Case      | Zero-row input date produces header-only CSV |
| TC-10   | Edge Case      | NULL values in source columns are passed through unchanged |
| TC-11   | Edge Case      | Multi-day auto-advance run: only last date survives (Overwrite) |
| TC-12   | Edge Case      | Month-end boundary date produces normal output |
| TC-13   | Edge Case      | Quarter-end boundary date produces normal output |
| TC-14   | Edge Case      | Weekend date produces header-only CSV (no weekend data in datalake) |
| TC-15   | Proofmark      | Proofmark comparison passes with zero exclusions and zero fuzzy columns |
| TC-16   | FSD: Tier 1    | V2 produces byte-identical output to V1 across full date range |

## Test Cases

### TC-01: All credit score rows pass through with no filtering or transformation
- **Traces to:** BR-1
- **Input conditions:** Run V2 job for a single weekday (e.g., 2024-10-01) where `datalake.credit_scores` contains data.
- **Expected output:** Every row in `datalake.credit_scores` for that effective date appears in the CSV output. No rows are filtered, aggregated, or modified. The row count in the output matches `SELECT COUNT(*) FROM datalake.credit_scores WHERE as_of = '2024-10-01'`.
- **Verification method:** Query the source table for the effective date and compare row count against the V2 CSV output (excluding the header row). Spot-check several rows to confirm all field values are identical to source. The FSD Transformation SQL is `SELECT credit_score_id, customer_id, bureau, score, as_of FROM credit_scores` with no WHERE clause [FSD Section 5], confirming full pass-through.

### TC-02: Empty credit_scores input produces empty output with correct schema
- **Traces to:** BR-2
- **Input conditions:** Run V2 job for a date where `datalake.credit_scores` has zero rows (e.g., a weekend date like 2024-10-05 or 2024-10-06, if no data exists for those dates).
- **Expected output:** The CSV output file contains only the header row: `credit_score_id,customer_id,bureau,score,as_of` followed by a CRLF line ending. Zero data rows.
- **Verification method:** Read the output CSV and confirm it contains exactly one line (the header). Verify the header column names match the output schema. Note the FSD caveat [FSD Section 5]: the Transformation module skips SQLite table registration for empty DataFrames [Transformation.cs:46], which could cause the SQL SELECT to fail with "no such table." If this occurs, the test documents a behavioral difference from V1's empty-input guard [CreditScoreProcessor.cs:17-21]. This edge case is explicitly flagged for Phase D testing.

### TC-03: Branches table is not sourced in V2
- **Traces to:** BR-3, AP1 elimination
- **Input conditions:** Inspect the V2 job config JSON (`credit_score_snapshot_v2.json`).
- **Expected output:** The V2 config contains exactly one DataSourcing module entry for `credit_scores`. There is no DataSourcing entry for `branches`. The V1 config sourced `datalake.branches` [credit_score_snapshot.json:14-17] but the External module never accessed it [CreditScoreProcessor.cs:15].
- **Verification method:** Read `credit_score_snapshot_v2.json` and verify the modules array contains only one DataSourcing entry with `"table": "credit_scores"`. No entry with `"table": "branches"` should exist. This confirms AP1 (dead-end sourcing) is eliminated per FSD Section 3.

### TC-04: Output contains exactly 5 columns in correct order
- **Traces to:** BRD Output Schema
- **Input conditions:** Run V2 job for a single weekday where data exists.
- **Expected output:** The CSV header row contains exactly 5 columns in this order: `credit_score_id`, `customer_id`, `bureau`, `score`, `as_of`. No additional columns are present.
- **Verification method:** Read the first line of the output CSV and split on commas. Verify 5 column names in the exact order specified. The FSD documents that SQL SELECT column order matches V1's `outputColumns` definition [CreditScoreProcessor.cs:10-13] to ensure byte-identical CSV headers [FSD Section 5, Decision 3].

### TC-05: as_of column is present via DataSourcing auto-injection
- **Traces to:** BRD Output Schema (as_of)
- **Input conditions:** Run V2 job for a single weekday (e.g., 2024-10-15).
- **Expected output:** Every row's `as_of` value matches the effective date of the run. The `as_of` column is present even though it is not listed in the DataSourcing `columns` array -- the framework auto-appends it [DataSourcing.cs:69-72].
- **Verification method:** Read the V2 CSV and verify the `as_of` column exists and all values equal the expected effective date. Cross-reference with `SELECT DISTINCT as_of FROM datalake.credit_scores WHERE as_of = '2024-10-15'`. The FSD notes that `as_of` is omitted from the columns array and auto-appended by DataSourcing [FSD Section 2, Module 1 Note].

### TC-06: Writer configuration matches V1
- **Traces to:** BRD Writer Configuration
- **Input conditions:** Run V2 job for a single weekday and inspect the output CSV.
- **Expected output:**
  - Header row is present (first line is column names)
  - Line endings are CRLF (`\r\n`)
  - Write mode is Overwrite (file is replaced on each run, not appended)
  - No trailer row exists after the last data row
- **Verification method:** Read the output file in binary mode. Verify:
  1. First line matches `credit_score_id,customer_id,bureau,score,as_of\r\n`
  2. Every line ends with `\r\n` (CRLF)
  3. No trailer line exists at the end of the file
  4. Run the job twice for the same date -- the second run's output should completely replace the first (Overwrite mode). File size should be identical between runs for the same date.
  Config verified against FSD Section 7: includeHeader=true, writeMode=Overwrite, lineEnding=CRLF, no trailerFormat.

### TC-07: V2 uses Tier 1 -- no External module
- **Traces to:** AP3 elimination, AP6 elimination
- **Input conditions:** Inspect the V2 job config JSON.
- **Expected output:** The V2 config contains no External module entry. The module chain is: DataSourcing -> Transformation -> CsvFileWriter. The V1 External module (`CreditScoreProcessor`) that performed a trivial foreach pass-through [CreditScoreProcessor.cs:24-35] is replaced with a SQL SELECT in the Transformation module.
- **Verification method:** Read `credit_score_snapshot_v2.json` and verify:
  1. No module entry with `"type": "External"` exists
  2. A Transformation module exists with `"resultName": "output"` and a SQL query
  3. The SQL is `SELECT credit_score_id, customer_id, bureau, score, as_of FROM credit_scores`
  This confirms AP3 (unnecessary External) and AP6 (row-by-row iteration) are eliminated per FSD Sections 1 and 3.

### TC-08: Unused columns not sourced in V2
- **Traces to:** AP4 elimination
- **Input conditions:** Inspect the V2 job config JSON.
- **Expected output:** The DataSourcing module for `credit_scores` lists only `["credit_score_id", "customer_id", "bureau", "score"]`. No columns from the eliminated `branches` table are sourced. The `as_of` column is not listed (auto-injected by the framework).
- **Verification method:** Read `credit_score_snapshot_v2.json` and verify the `columns` array in the credit_scores DataSourcing entry contains exactly 4 column names. No extraneous columns from `branches` (branch_id, branch_name, city, state_province) appear anywhere in the config. This confirms AP4 per FSD Section 3.

### TC-09: Zero-row input date produces header-only CSV
- **Traces to:** Edge Case (BRD: Empty credit_scores)
- **Input conditions:** Run V2 job for a date where `datalake.credit_scores` has no rows (verify with `SELECT COUNT(*) FROM datalake.credit_scores WHERE as_of = '<date>'` returning 0).
- **Expected output:** The output CSV contains only the header row with CRLF ending. No data rows. The file size is the length of the header string plus CRLF.
- **Verification method:** Read the output file and count lines. Expect exactly 1 line (header). Note: per FSD Section 5, the Transformation module may throw "no such table" if credit_scores has zero rows because `RegisterTable` skips empty DataFrames [Transformation.cs:46]. If this occurs, document it as a known behavioral difference from V1's explicit empty guard [CreditScoreProcessor.cs:17-21] and flag for Phase D resolution.

### TC-10: NULL values in source columns are passed through unchanged
- **Traces to:** Edge Case (BRD: All fields are direct pass-through)
- **Input conditions:** Query `datalake.credit_scores` for rows containing NULL values in any of the 5 output columns. Run V2 job for a date containing such rows.
- **Expected output:** NULL values from the source appear as empty (unquoted) fields in the CSV output, matching the CsvFileWriter's default null handling. No coalescing, default substitution, or transformation is applied.
- **Verification method:** Identify source rows with NULLs via `SELECT * FROM datalake.credit_scores WHERE bureau IS NULL OR score IS NULL`. If such rows exist, locate them in the V2 CSV output and verify the corresponding fields are empty (not "NULL", not "0", not any other substituted value). BRD BR-1 states all fields are direct pass-through with no transformation.

### TC-11: Multi-day auto-advance run -- only last date survives (Overwrite)
- **Traces to:** Edge Case (BRD: Write Mode Implications)
- **Input conditions:** Run V2 job for two consecutive weekdays (e.g., 2024-10-01 then 2024-10-02) in an auto-advance scenario.
- **Expected output:** After both dates execute, the output CSV contains ONLY the data from 2024-10-02. The 2024-10-01 data is completely overwritten. Every `as_of` value in the file equals `2024-10-02`.
- **Verification method:** Run the job for the two-day range. Read the output CSV and verify:
  1. All `as_of` values are `2024-10-02` (the last date)
  2. No rows from 2024-10-01 survive
  3. Row count matches `SELECT COUNT(*) FROM datalake.credit_scores WHERE as_of = '2024-10-02'`
  This confirms the Overwrite write mode behavior documented in BRD (Write Mode Implications) and FSD Section 7.

### TC-12: Month-end boundary produces normal output
- **Traces to:** Edge Case (date boundaries)
- **Input conditions:** Run V2 job for 2024-10-31 (last day of October, a Thursday -- weekday).
- **Expected output:** Normal pass-through output. No summary rows, boundary markers, or special behavior. Row count matches the source table for that date.
- **Verification method:** Verify row count matches `SELECT COUNT(*) FROM datalake.credit_scores WHERE as_of = '2024-10-31'`. Verify no rows contain aggregated or summary values. No W-codes (W3a/W3b/W3c) apply to this job [FSD Section 3: "No W-codes apply"].

### TC-13: Quarter-end boundary produces normal output
- **Traces to:** Edge Case (date boundaries)
- **Input conditions:** Run V2 job for 2024-12-31 (last day of Q4, a Tuesday -- weekday).
- **Expected output:** Normal pass-through output. No quarterly summary rows or special behavior.
- **Verification method:** Same verification as TC-12 but for the quarter-end date. Confirm no W-codes trigger any special output behavior.

### TC-14: Weekend date produces header-only CSV
- **Traces to:** Edge Case (weekend dates)
- **Input conditions:** Run V2 job for a Saturday (e.g., 2024-10-05) or Sunday (e.g., 2024-10-06).
- **Expected output:** Since the datalake follows a full-load snapshot pattern with no weekend data, DataSourcing returns zero rows. The output CSV contains only the header row. Because write mode is Overwrite, any prior weekday data is replaced with this header-only file.
- **Verification method:** Verify `datalake.credit_scores` has no rows for the weekend date (`SELECT COUNT(*) ... WHERE as_of = '2024-10-05'` returns 0). Run the V2 job and verify the output file contains only the header. Note: same Transformation.RegisterTable caveat as TC-02 and TC-09 applies.

### TC-15: Proofmark comparison passes with zero exclusions and zero fuzzy columns
- **Traces to:** FSD Proofmark Config Design (Section 8)
- **Input conditions:** Run Proofmark with the designed config: `reader: csv`, `threshold: 100.0`, `header_rows: 1`, `trailer_rows: 0`, no `excluded_columns`, no `fuzzy_columns`.
- **Expected output:** Proofmark exits with code 0 (PASS). All 5 columns match exactly between V1 and V2 output.
- **Verification method:** Execute Proofmark comparison between `Output/curated/credit_score_snapshot.csv` (V1) and `Output/double_secret_curated/credit_score_snapshot.csv` (V2). Verify exit code is 0. Read the Proofmark report JSON and confirm zero mismatches. This validates the FSD's assertion that no exclusions or fuzzy overrides are needed because all columns are deterministic pass-through [FSD Section 8].

### TC-16: V2 produces byte-identical output to V1 across full date range
- **Traces to:** FSD Tier 1 Justification, Dual Mandate (output equivalence)
- **Input conditions:** Run both V1 and V2 jobs for the full date range (2024-10-01 through 2024-12-31). Since both use Overwrite mode, only the last effective date's output survives.
- **Expected output:** The V2 output at `Output/double_secret_curated/credit_score_snapshot.csv` is byte-identical to the V1 output at `Output/curated/credit_score_snapshot.csv`. Same header, same data rows, same column order, same CRLF line endings, same row count.
- **Verification method:** Run both V1 and V2 for the full date range. Compare the two CSV files byte-for-byte (e.g., `diff` or `md5sum`). The files should be identical. This validates the FSD's Tier 1 justification: the SQL `SELECT credit_score_id, customer_id, bureau, score, as_of FROM credit_scores` produces the exact same output as V1's External module foreach loop [FSD Section 5].
