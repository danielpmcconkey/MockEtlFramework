# PreferenceTrend -- V2 Test Plan

## Job Info
- **V2 Config**: `preference_trend_v2.json`
- **Tier**: 1 (Framework Only)
- **External Module**: None

## Pre-Conditions
- Source table available in `datalake` schema:
  - `customer_preferences` with columns: `preference_type`, `opted_in`, `as_of`
- Effective date range injected by executor (`__minEffectiveDate`, `__maxEffectiveDate`)
- `as_of` column auto-appended by DataSourcing module (not explicitly listed in columns config)
- V1 baseline output available at `Output/curated/preference_trend.csv`

## Test Cases

### TC-1: Output Schema Validation
- **Expected columns (exact order):** `preference_type`, `opted_in_count`, `opted_out_count`, `as_of`
- **Expected types:**
  - `preference_type`: string (one of: PAPER_STATEMENTS, E_STATEMENTS, MARKETING_EMAIL, MARKETING_SMS, PUSH_NOTIFICATIONS)
  - `opted_in_count`: integer (SUM result)
  - `opted_out_count`: integer (SUM result)
  - `as_of`: date
- Verify column order matches the SELECT order in the Transformation SQL (FSD Section 4)
- Verify no extra columns are present (e.g., no `preference_id`, no `customer_id`)
- **Traces to:** BR-1, BR-2, BR-3, FSD Section 4

### TC-2: Row Count Equivalence
- V1 and V2 must produce identical row counts across the full date range (2024-10-01 through 2024-12-31)
- One row per unique (preference_type, as_of) combination
- For single-date auto-advance runs: expect exactly 5 rows per execution (one per preference type)
- Over the full date range: expect ~460 rows total (5 preference types x ~92 business days) plus 1 header row
- Verify that removing `preference_id` and `customer_id` from DataSourcing (AP4) does not affect row count
- **Traces to:** BR-3, FSD Section 2

### TC-3: Data Content Equivalence
- All values must be byte-identical to V1 output
- Compare V2 CSV output at `Output/double_secret_curated/preference_trend.csv` against V1 baseline at `Output/curated/preference_trend.csv`
- Verify `preference_type` values match exactly (string comparison, case-sensitive)
- Verify `opted_in_count` and `opted_out_count` aggregates match exactly for every (preference_type, as_of) group
- Verify `as_of` dates match exactly
- **Traces to:** BR-1, BR-2, BR-3, FSD Section 4

### TC-4: Writer Configuration
- **type**: CsvFileWriter
- **source**: `pref_trend` (matches Transformation resultName; FSD Section 5)
- **outputFile**: `Output/double_secret_curated/preference_trend.csv` (V2 convention)
- **includeHeader**: true (header written only on first execution when file does not exist)
- **writeMode**: Append (each execution appends new rows to the file)
- **lineEnding**: LF
- **trailerFormat**: absent (no trailer)
- Verify the header line appears exactly once, at the top of the file
- Verify line endings are LF (not CRLF)
- Verify no trailer row exists at end of file
- **Traces to:** BRD Writer Configuration, FSD Section 5

### TC-5: Anti-Pattern Elimination Verification

#### AP4 (Unused columns) -- ELIMINATED
- Verify V2 `customer_preferences` DataSourcing columns are `["preference_type", "opted_in"]`
- Verify V1 config sources `["preference_id", "customer_id", "preference_type", "opted_in"]`
- Verify `preference_id` and `customer_id` are NOT in V2's column list
- Verify removal has no effect on output -- neither column is referenced in the Transformation SQL (`GROUP BY cp.preference_type, cp.as_of` and `SUM(CASE WHEN cp.opted_in ...)`)
- **Traces to:** FSD Section 7 (AP4), BRD Source Tables

#### No Other AP-codes Apply
- AP1 (Dead-end sourcing): V1 sources only one table (`customer_preferences`), and it is used. No dead-end.
- AP3 (Unnecessary External): V1 does not use an External module. Already Tier 1.
- AP5-AP10: Not applicable per FSD Section 7 analysis.

### TC-6: Edge Cases

#### TC-6a: All 5 Preference Types Present
- Verify that every execution date produces rows for all 5 preference types: E_STATEMENTS, MARKETING_EMAIL, MARKETING_SMS, PAPER_STATEMENTS, PUSH_NOTIFICATIONS
- If any preference type has zero customers for a given date, it should still appear with opted_in_count=0 and opted_out_count=0 (assuming the type exists in the source data for that date)
- **Traces to:** BRD Edge Case 5, FSD Section 4 Note 4

#### TC-6b: Header Suppression on Append
- On first execution (file does not exist): header row is written
- On subsequent executions (file exists): header is suppressed, only data rows appended
- Verify the final CSV has exactly one header row at line 1
- **Traces to:** BRD Edge Case 1, FSD Section 5 (CsvFileWriter.cs:47)

#### TC-6c: Re-run Duplication
- If the same effective date is re-run, duplicate rows are appended (no deduplication)
- Verify V1 and V2 behave identically on re-run -- both append duplicates
- **Traces to:** BRD Edge Case 3, FSD Section 5

#### TC-6d: Multi-date Range in Single Run
- If the effective date range spans multiple dates, GROUP BY as_of produces one set of 5 rows per date
- All rows for all dates are appended in a single write operation
- Verify total appended rows = 5 * number_of_dates_in_range
- **Traces to:** BRD Edge Case 2

#### TC-6e: Row Ordering (No ORDER BY)
- V1 SQL has no ORDER BY clause. Row order depends on SQLite GROUP BY implementation.
- V2 SQL also has no ORDER BY clause, matching V1.
- Verify row order in V2 matches V1 exactly. If Proofmark fails on ordering, investigate whether SQLite's GROUP BY iteration is consistent for the same input data.
- **Traces to:** BR-4, FSD Section 4 Note 3, FSD Section 9 (Open Question 1)

#### TC-6f: opted_in Column is NOT NULL
- The `opted_in` column is NOT NULL per datalake schema constraint
- The CASE expressions cover both `= 1` (true) and `= 0` (false), which are exhaustive
- No NULL branch is needed; verify SUM(opted_in_count) + SUM(opted_out_count) equals total row count for each group
- **Traces to:** FSD Section 4 Note 5

### TC-7: Proofmark Configuration
- **comparison_target**: `preference_trend`
- **reader**: `csv`
- **threshold**: `100.0` (strict -- all columns deterministic)
- **csv.header_rows**: `1`
- **csv.trailer_rows**: `0`
- **excluded columns**: None
- **fuzzy columns**: None
- Rationale: All output columns are fully deterministic. `preference_type` is a passthrough GROUP BY key. `opted_in_count` and `opted_out_count` are integer SUMs. `as_of` is a passthrough date. No timestamps, random values, or floating-point concerns.
- **Traces to:** FSD Section 8, BRD Non-Deterministic Fields

## W-Code Test Cases

### No W-codes Apply
- No output-affecting wrinkles were identified for this job (FSD Section 6).
- W4 (integer division): Not applicable -- SUM produces integers, no division operations exist.
- W7/W8 (trailer): Not applicable -- no trailer in V1 or V2.
- W9 (wrong writeMode): Not applicable -- Append is the correct semantic for a cumulative trend file.
- W12 (header every append): Not applicable -- CsvFileWriter correctly suppresses header on append.

## Notes
- This is a clean Tier 1 job. The SQL is minimal and unchanged between V1 and V2.
- The only structural change is removing unused columns `preference_id` and `customer_id` from DataSourcing (AP4). This does not affect output.
- No External module exists in V1 or V2 for this job.
- The Append write mode means the full-range output is a cumulative CSV. Proofmark comparison should cover the entire file after all dates are processed.
- If Proofmark reports row-order mismatches, the FSD prescribes adding `ORDER BY cp.preference_type, cp.as_of` to V2 SQL only if it matches V1's actual output order (FSD Section 9, Open Question 1).
