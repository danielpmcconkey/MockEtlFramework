# SecuritiesDirectory -- Test Plan

## References
- BRD: `POC3/brd/securities_directory_brd.md`
- FSD: `POC3/fsd/securities_directory_fsd.md`

---

## Happy Path Tests

### TC-01: All securities rows present in output
- **Description:** Every row in `datalake.securities` within the effective date range appears in the CSV output. No filtering or exclusion is applied.
- **Expected:** V2 row count matches V1 row count exactly. Proofmark comparison PASS at 100% threshold.
- **Traces to:** BR-2, FSD Section 4 (no WHERE clause)

### TC-02: Output columns match expected schema
- **Description:** The CSV output contains exactly 7 columns: `security_id`, `ticker`, `security_name`, `security_type`, `sector`, `exchange`, `as_of`, in that order.
- **Expected:** Header row matches V1 header row exactly. Proofmark validates column alignment via header comparison.
- **Traces to:** BRD Output Schema, BR-1, BR-6, FSD Section 4 (Column mapping)

### TC-03: Rows ordered by security_id ascending
- **Description:** Output rows are sorted by `security_id` in ascending order (default SQL ORDER BY direction).
- **Expected:** V2 output order matches V1. Proofmark comparison PASS.
- **Traces to:** BR-7, BR-1, FSD Section 4 (ORDER BY s.security_id)

### TC-04: All column values are pass-throughs
- **Description:** Every value in the output is a direct pass-through from `datalake.securities` with no transformation, computation, or filtering applied.
- **Expected:** Cell-by-cell values in V2 match V1 exactly. Proofmark strict comparison PASS.
- **Traces to:** BR-1, BR-2, BRD Output Schema, FSD Section 4 (Column mapping -- all "Pass-through")

### TC-05: as_of column included from source
- **Description:** The `as_of` column is present in the output, containing the per-row `as_of` date from the securities source table (not a scalar or computed value).
- **Expected:** Each row's `as_of` reflects the date that specific securities record was sourced from. V2 matches V1.
- **Traces to:** BR-6, FSD Section 3 (as_of automatically appended by DataSourcing), FSD Section 4

### TC-06: Effective dates injected by executor
- **Description:** The job config contains no hardcoded date filters. DataSourcing uses `__minEffectiveDate` / `__maxEffectiveDate` from shared state to filter the `as_of` column.
- **Expected:** V2 config has no explicit date parameters in DataSourcing modules. Output covers the full effective date range provided at runtime.
- **Traces to:** BR-5, FSD Section 3 (Effective date handling)

---

## Edge Case Tests

### TC-07: Multi-day effective date range produces multi-date output
- **Description:** With a 92-day range (2024-10-01 through 2024-12-31), the output contains one row per security per `as_of` date. For 50 securities, this means ~4,600 rows (securities has weekend data).
- **Expected:** V2 row count matches V1 exactly. Proofmark comparison PASS.
- **Traces to:** BRD Edge Case #2, FSD Section 5 (Write mode implications)

### TC-08: Overwrite mode -- only final run output survives
- **Description:** On multi-day auto-advance, each run overwrites the CSV file. The final output reflects the full cumulative date range because DataSourcing pulls min-to-max effective dates.
- **Expected:** After a full run, a single CSV file exists at the output path. Proofmark compares this final file.
- **Traces to:** BRD Write Mode Implications, FSD Section 5

### TC-09: Securities with weekend data
- **Description:** Unlike many tables that have weekday-only data, the securities table includes weekend dates. The job outputs these rows without filtering.
- **Expected:** Weekend `as_of` dates appear in output. V2 matches V1 since neither applies day-of-week filtering.
- **Traces to:** BRD Edge Case #3

### TC-10: NULL values passed through as-is
- **Description:** Any NULL values in securities columns (e.g., NULL sector or exchange) are passed through. CsvFileWriter renders them as empty strings in CSV.
- **Expected:** NULL handling is identical between V1 and V2 since both use the same CsvFileWriter framework module. Proofmark comparison PASS.
- **Traces to:** BRD Edge Case #4

### TC-11: RFC 4180 quoting for special characters
- **Description:** Values containing commas, double quotes, or newlines are properly quoted per RFC 4180 by the CsvFileWriter.
- **Expected:** Both V1 and V2 use CsvFileWriter, so quoting behavior is identical. Proofmark comparison PASS.
- **Traces to:** BRD Edge Case #5, FSD Section 5

### TC-12: Result name is securities_dir (not output)
- **Description:** The Transformation stores its result as `securities_dir` in shared state, and the CsvFileWriter reads from `securities_dir`. Using the wrong result name would produce an empty or missing output.
- **Expected:** V2 config uses `resultName: securities_dir` in Transformation and `source: securities_dir` in CsvFileWriter. Job executes successfully.
- **Traces to:** BR-4, FSD Section 4 (Result name), FSD Section 5 (source)

---

## Anti-Pattern Elimination Verification

### TC-13: AP1 eliminated -- holdings DataSourcing removed
- **Description:** V1 sources `datalake.holdings` (6 columns) but the SQL never references the holdings table. V2 removes this dead-end DataSourcing module entirely.
- **Expected:** V2 JSON config has no DataSourcing entry for `holdings`. Only the `securities` DataSourcing module is present. Output is byte-identical to V1.
- **Traces to:** BR-3, BRD Edge Case #1, FSD Section 3 (Removed: holdings), FSD Section 7 (AP1)

### TC-14: AP4 eliminated via AP1 -- no unused columns
- **Description:** By removing the entire `holdings` DataSourcing module, all 6 of its unused columns are eliminated. The `securities` DataSourcing module sources only the columns used in the SQL.
- **Expected:** V2 config sources exactly `["security_id", "ticker", "security_name", "security_type", "sector", "exchange"]` from securities. No unused columns sourced.
- **Traces to:** FSD Section 3, FSD Section 7 (AP4)

### TC-15: Tier 1 maintained -- no unnecessary module changes
- **Description:** V1 already uses Tier 1 (DataSourcing -> Transformation -> CsvFileWriter). V2 preserves this module chain structure, only removing the dead-end source.
- **Expected:** V2 config module chain is DataSourcing (securities) -> Transformation -> CsvFileWriter. No External module.
- **Traces to:** FSD Section 2 (Tier 1, justification), FSD Section 7 (AP3 -- not applicable)

---

## Writer Config Verification

### TC-16: CSV output with header row
- **Description:** Output CSV includes a header row as the first line, matching V1's `includeHeader: true`.
- **Expected:** First line of CSV is: `security_id,ticker,security_name,security_type,sector,exchange,as_of` (or equivalent). Proofmark reads it with `header_rows: 1`.
- **Traces to:** BRD Writer Configuration (includeHeader: true), FSD Section 5

### TC-17: No trailer in output
- **Description:** V1 config has no `trailerFormat`. V2 must also produce no trailer.
- **Expected:** CSV file ends with the last data row. No summary/trailer line appended. Proofmark config uses `trailer_rows: 0`.
- **Traces to:** BRD Writer Configuration (trailerFormat: not specified), FSD Section 5

### TC-18: Write mode is Overwrite
- **Description:** Each run replaces the entire CSV file.
- **Expected:** After a multi-day run, exactly one CSV file exists at the output path (no accumulation).
- **Traces to:** BRD Writer Configuration (writeMode: Overwrite), FSD Section 5

### TC-19: Line ending is LF
- **Description:** V1 config specifies `lineEnding: LF`. V2 must match.
- **Expected:** Output CSV uses Unix-style line endings (`\n`), not Windows-style (`\r\n`). Proofmark byte comparison validates this.
- **Traces to:** BRD Writer Configuration (lineEnding: LF), FSD Section 5

### TC-20: Output file path correct
- **Description:** V2 output writes to `Output/double_secret_curated/securities_directory.csv`, matching the BLUEPRINT convention for CSV jobs.
- **Expected:** File exists at the expected path after job execution. Proofmark `--right` path points to this file.
- **Traces to:** FSD Section 5 (outputFile)

---

## Proofmark Comparison Expectations

### TC-21: Proofmark config -- CSV with header, no trailer, strict
- **Description:** The Proofmark config uses `reader: csv`, `header_rows: 1`, `trailer_rows: 0`, `threshold: 100.0`, with zero excluded columns and zero fuzzy columns.
- **Expected:** Config at `POC3/proofmark_configs/securities_directory.yaml` matches FSD Section 8 exactly.
- **Traces to:** FSD Section 8, BRD Non-Deterministic Fields (none)

### TC-22: Proofmark full comparison PASS
- **Description:** Running Proofmark with `--left Output/curated/securities_directory.csv` and `--right Output/double_secret_curated/securities_directory.csv` produces exit code 0 (PASS).
- **Expected:** 100% row match. Zero mismatches. All 7 columns match strictly.
- **Traces to:** FSD Section 8

---

## Wrinkle Verification (Negative Tests)

### TC-23: No W-codes apply
- **Description:** The FSD confirms no output-affecting wrinkles apply to this job. Verify that no wrinkle-like behavior exists in the output (no Sunday skips, no weekend fallback, no summary rows, no trailers, no truncated percentages).
- **Expected:** Output is a clean, straightforward pass-through for all dates in range. Proofmark strict comparison PASS confirms no hidden wrinkles.
- **Traces to:** FSD Section 6 (full W-code checklist -- all "No")
