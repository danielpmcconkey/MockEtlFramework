# MonthlyTransactionTrend — V2 Test Plan

## Job Info
- **V2 Config**: `monthly_transaction_trend_v2.json`
- **Tier**: 1 (Framework Only)
- **External Module**: None

## Pre-Conditions
- **Source table**: `datalake.transactions` must be populated with columns: `amount`, `as_of` (auto-appended by framework)
- **Effective date range**: `firstEffectiveDate = 2024-10-01`, auto-advance through 2024-12-31
- **Shared state**: Executor must inject `__minEffectiveDate` and `__maxEffectiveDate` per run
- **V1 baseline**: `Output/curated/monthly_transaction_trend.csv` must exist for comparison
- **Output directory**: `Output/double_secret_curated/` must be writable

## Test Cases

### TC-1: Output Schema Validation
- **Expected columns** (exact order per FSD Section 4):
  1. `as_of` (TEXT, date formatted yyyy-MM-dd)
  2. `daily_transactions` (INTEGER)
  3. `daily_amount` (REAL)
  4. `avg_transaction_amount` (REAL)
- Verify the CSV header row contains exactly these 4 column names in this order
- Verify no extra columns are present
- Verify column names are lowercase with underscores (no camelCase, no spaces)
- Verify column count is 4 (matches V1 exactly)

### TC-2: Row Count Equivalence
- V1 and V2 must produce identical row counts per effective date
- Over the full run (2024-10-01 through 2024-12-31), total data row counts must match
- Each effective date should produce exactly one row (GROUP BY as_of with single-date data yields one aggregation row per run)
- With Append mode, the final file should contain one header row plus one data row per effective date processed

### TC-3: Data Content Equivalence
- All values must be byte-identical to V1 output
- Compare `Output/curated/monthly_transaction_trend.csv` (V1) against `Output/double_secret_curated/monthly_transaction_trend.csv` (V2)
- `as_of` column: must match V1's date strings exactly
- `daily_transactions`: INTEGER from COUNT(*), must match exactly
- `daily_amount`: `ROUND(SUM(amount), 2)` in SQLite -- deterministic, must match exactly
- `avg_transaction_amount`: `ROUND(AVG(amount), 2)` in SQLite -- deterministic, must match exactly
- Row ordering must match: `ORDER BY as_of ASC`
- **No W-codes apply** -- there are zero output-affecting wrinkles for this job, so full byte-identical comparison is expected

### TC-4: Writer Configuration
- **Writer type**: CsvFileWriter (matches V1)
- **includeHeader**: `true` -- header written on first run only (suppressed on Append when file already exists)
- **writeMode**: `Append` -- each daily run appends data row(s); file grows across dates
- **lineEnding**: `LF` -- all lines (header, data) use Unix-style line endings (`\n`)
- **trailerFormat**: Not specified -- no trailer lines should appear in the output
- **outputFile**: `Output/double_secret_curated/monthly_transaction_trend.csv` (V2 output path)
- **Append + Header behavior**: Verify multi-day output structure is:
  ```
  header
  day1_data_row
  day2_data_row
  day3_data_row
  ...
  ```
- Verify no duplicate headers appear after the first run
- Verify no trailer lines appear anywhere in the output

### TC-5: Anti-Pattern Elimination Verification

#### TC-5a: AP1 — Dead-end sourcing eliminated
- V1 sources `datalake.branches` (`branch_id`, `branch_name`) but never references it in the SQL transformation
- V2 must NOT have a DataSourcing entry for `branches`
- Verify V2 config contains only one DataSourcing module (for `transactions`)
- Verify output is unaffected by removal (branches data was never referenced in the SQL)

#### TC-5b: AP4 — Unused columns eliminated
- V1 sources `transaction_id`, `account_id`, `txn_type`, `amount` from `transactions`
- V2 sources only `["amount"]` (plus `as_of` auto-appended by framework)
- Verify V2 DataSourcing `columns` array is `["amount"]`
- Verify `transaction_id`, `account_id`, and `txn_type` are not sourced
- Verify output is unaffected (these columns were never used in the SQL -- only `amount` and `as_of` are referenced)

#### TC-5c: AP8 — Unnecessary CTE eliminated
- V1 SQL wraps the aggregation in a CTE: `WITH base AS (SELECT ...) SELECT ... FROM base ORDER BY as_of`
- The CTE is a pure pass-through -- the outer SELECT re-selects all 4 CTE columns with no additional filtering or transformation
- V2 SQL uses a single flat `SELECT ... GROUP BY as_of ORDER BY as_of` with no CTE
- Verify V2 SQL does not contain `WITH base AS` or any CTE construct
- Verify aggregation logic is preserved verbatim: `COUNT(*)`, `ROUND(SUM(amount), 2)`, `ROUND(AVG(amount), 2)`

#### TC-5d: AP10 — Redundant hardcoded date filter eliminated
- V1 SQL includes `WHERE as_of >= '2024-10-01'` inside the CTE
- This is redundant with the framework's effective date injection via `__minEffectiveDate`/`__maxEffectiveDate` (DataSourcing already limits data to the effective date range)
- V2 SQL must NOT contain any hardcoded date filter
- Verify V2 SQL has no `WHERE` clause
- Verify output is unaffected because `firstEffectiveDate: "2024-10-01"` ensures the executor never injects dates before 2024-10-01 (the filter was always redundant)

### TC-6: Edge Cases

#### TC-6a: Empty input (no transactions for an effective date)
- When no transactions exist for a given `as_of` date, GROUP BY produces 0 rows
- CsvFileWriter should write 0 data rows for that day
- On first run (file does not exist): output is header only (no trailer since no trailerFormat)
- File should still be created even with 0 data rows

#### TC-6b: Single transaction for an effective date
- GROUP BY produces exactly 1 row
- `daily_transactions` = 1
- `daily_amount` = that transaction's amount (rounded to 2 decimal places)
- `avg_transaction_amount` = same as `daily_amount` (single value, AVG = the value itself)

#### TC-6c: ROUND behavior consistency
- All monetary values use SQLite `ROUND(..., 2)` which is deterministic
- `ROUND(SUM(amount), 2)` for `daily_amount` -- ROUND is applied to the aggregate, not to individual amounts
- `ROUND(AVG(amount), 2)` for `avg_transaction_amount` -- same pattern
- Verify no floating-point drift across the full date range (2024-10-01 through 2024-12-31)

#### TC-6d: Single date per run during auto-advance
- The executor runs one effective date at a time (JobExecutorService.cs:100-101)
- Only data for that single date reaches the Transformation module
- Each run produces exactly one data row (one GROUP BY as_of group)
- Verify that the file accumulates one data row per effective date across the full run

#### TC-6e: Append mode file growth
- Day 1: header + 1 data row
- Day 2: header + 2 data rows (day 1 data + day 2 data)
- Day N: header + N data rows
- Verify monotonic growth -- file should never shrink or lose previously appended data

### TC-7: Proofmark Configuration
- **Expected proofmark settings** (from FSD Section 8):
  ```yaml
  comparison_target: "monthly_transaction_trend"
  reader: csv
  threshold: 100.0
  csv:
    header_rows: 1
    trailer_rows: 0
  ```
- **threshold**: `100.0` -- all computations are deterministic; exact match required
- **header_rows**: `1` -- single header row on first write
- **trailer_rows**: `0` -- no trailer (no trailerFormat specified in the writer config)
- **excluded_columns**: None -- all columns are deterministic
- **fuzzy_columns**: None -- all monetary columns use SQLite ROUND(..., 2) which is deterministic; no double-precision accumulation issues

## W-Code Test Cases

No W-codes apply to this job. The FSD confirms (Section 3, Wrinkles table) that none of the cataloged wrinkles (W1 through W12) affect this job:
- No External module (W1, W2, W3a-c, W4, W5, W6, W7, W12 not applicable)
- No trailer (W7, W8, W12 not applicable)
- Correct writeMode (Append is appropriate for a trend file -- W9 not applicable)
- No Parquet output (W10 not applicable)

All computations are deterministic, the write mode is correct, and no External module introduces bugs. Full byte-identical comparison is the expected outcome.

## Notes
- This is a clean Tier 1 job with no wrinkles and no External module -- straightforward SQL aggregation
- The job name "MonthlyTransactionTrend" is slightly misleading (AP9) -- it produces **daily** aggregates, not monthly. However, per AP9 prescription, we cannot rename V1 jobs. The daily granularity supports downstream monthly trend analysis.
- The only V1 issues are code-quality anti-patterns (AP1, AP4, AP8, AP10) that are eliminated without any output impact
- The `as_of` column is NOT listed in the V2 DataSourcing `columns` array because the framework's DataSourcing module automatically appends it (per `Lib/Modules/DataSourcing.cs:69-72`)
- The V1 SQL's `WHERE as_of >= '2024-10-01'` removal is safe because the executor's `firstEffectiveDate` alignment makes it impossible for earlier dates to be processed
