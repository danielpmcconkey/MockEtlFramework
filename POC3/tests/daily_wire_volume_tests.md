# DailyWireVolume — V2 Test Plan

## Job Info
- **V2 Config**: `daily_wire_volume_v2.json`
- **Tier**: 1 (Framework Only)
- **External Module**: None

## Pre-Conditions
- Data source: `datalake.wire_transfers` must be populated with wire transfer records containing at minimum the columns `amount` and `as_of`
- Full table schema: `wire_id` (integer), `customer_id` (integer), `account_id` (integer), `direction` (varchar), `amount` (numeric), `counterparty_name` (varchar), `counterparty_bank` (varchar), `status` (varchar), `wire_timestamp` (timestamp), `as_of` (date)
- DataSourcing uses hard-coded effective dates: `minEffectiveDate: "2024-10-01"`, `maxEffectiveDate: "2024-12-31"` (BR-1). The executor-injected effective date is overridden. Every execution sources the full Q4 2024 range regardless of which date the executor runs for.
- The `as_of` column is auto-appended by the DataSourcing module when not listed in the `columns` array (per DataSourcing.cs:69-72)
- V1 baseline output must exist at `Output/curated/daily_wire_volume.csv` for comparison

## Test Cases

### TC-1: Output Schema Validation
- Expected columns in exact order (from FSD Section 4):
  1. `wire_date` (DATE) -- aliased from `as_of`
  2. `wire_count` (INTEGER) -- `COUNT(*)`
  3. `total_amount` (REAL) -- `ROUND(SUM(amount), 2)`
  4. `as_of` (DATE) -- bare `as_of`, duplicate of `wire_date` (BR-6)
- Verify the CSV header line reads: `wire_date,wire_count,total_amount,as_of`
- Verify column count is exactly 4 per row

### TC-2: Row Count Equivalence
- V1 and V2 must produce identical row counts for the same execution sequence
- One row per distinct `as_of` date in the 2024-10-01 to 2024-12-31 range that has wire transfer data
- Dates with zero wire transfers produce no output row (GROUP BY produces no group for absent dates)
- Since writeMode is Append and dates are hard-coded, running across the full date range (92 days) appends the same full-quarter aggregation 92 times. V1 and V2 row counts must match after the same number of executions.

### TC-3: Data Content Equivalence
- All values must be byte-identical to V1 output
- `wire_date` and `as_of` columns must contain identical date strings for every row
- `wire_count` integer values must match exactly
- `total_amount` values must match exactly (both pass through the same SQLite `ROUND(SUM(amount), 2)` pipeline)
- The duplicate `as_of` column (BR-6) must be present and contain the same value as `wire_date` in every row
- **W-codes affecting comparison**: W9 (Append mode with hard-coded dates -- see TC-W1 below). No other W-codes apply to this job.

### TC-4: Writer Configuration
- **Writer type**: CsvFileWriter
- **includeHeader**: `true` -- header written on first execution only (CsvFileWriter.cs:47 suppresses on Append when file exists)
- **writeMode**: `Append` -- each execution appends all rows to the existing file. Verify file grows with each execution, not overwritten.
- **lineEnding**: `LF` -- verify line endings are `\n` (not `\r\n`)
- **trailerFormat**: not specified -- verify no trailer rows are appended
- **outputFile**: `Output/double_secret_curated/daily_wire_volume.csv` (V2 path)

### TC-5: Anti-Pattern Elimination Verification
- **AP4 (Unused columns)**: Verify V2 DataSourcing sources only `["amount"]`. V1 sourced 6 columns (`wire_id`, `customer_id`, `direction`, `amount`, `status`, `wire_timestamp`); only `amount` and auto-appended `as_of` are used in the SQL. Confirm the V2 config `columns` array contains exactly `["amount"]`.
- **AP8 (Redundant SQL WHERE clause)**: Verify V2 SQL does NOT contain `WHERE as_of >= '2024-10-01' AND as_of <= '2024-12-31'`. The DataSourcing module already constrains the date range at the PostgreSQL level, making the WHERE clause a no-op. Confirm the SQL is: `SELECT as_of AS wire_date, COUNT(*) AS wire_count, ROUND(SUM(amount), 2) AS total_amount, as_of FROM wire_transfers GROUP BY as_of ORDER BY as_of`
- **AP10 (Over-sourcing dates -- partial)**: The hard-coded `minEffectiveDate`/`maxEffectiveDate` are preserved because they ARE the business logic (job always produces full Q4 aggregation). The redundant SQL WHERE filter (the true AP10/AP8 symptom) is eliminated. Verify the DataSourcing config retains `minEffectiveDate: "2024-10-01"` and `maxEffectiveDate: "2024-12-31"`.

### TC-6: Edge Cases
- **Empty input behavior**: If no wire transfer data exists in the 2024-10-01 to 2024-12-31 range, the GROUP BY produces zero rows. The CsvFileWriter should write only the header (on first run) or nothing (on subsequent Append runs). Verify no crash occurs.
- **Weekends/holidays with no wire data**: Dates with zero wire transfers produce no output row. The output may have date gaps (e.g., weekends). Verify that date gaps in V2 output match V1 exactly.
- **Hard-coded dates override executor injection**: Run V2 with an executor effective date outside Q4 2024 (e.g., 2025-01-15). Verify it still produces the full Q4 2024 aggregation, not an empty or single-day result.
- **Re-run / Append duplication**: Run V2 twice for the same effective date. Verify the output file contains two copies of the same data (Append mode). Verify this matches V1 behavior.
- **All wire statuses included (BR-4)**: Verify that Completed, Pending, and Rejected wire transfers are all counted. No filtering on `status` or `direction` columns.

### TC-7: Proofmark Configuration
- **Expected proofmark settings from FSD Section 8:**
  ```yaml
  comparison_target: "daily_wire_volume"
  reader: csv
  threshold: 100.0
  csv:
    header_rows: 1
    trailer_rows: 0
  ```
- **Threshold**: 100.0 (exact match required)
- **Excluded columns**: None -- all fields are deterministic
- **Fuzzy columns**: None initially. Both V1 and V2 execute through the same SQLite `ROUND(SUM(amount), 2)` pipeline.
- **Contingency**: If `total_amount` causes comparison failure due to floating-point representation differences, add fuzzy matching with absolute tolerance 0.01 on `total_amount`.

## W-Code Test Cases

### TC-W1: W9 -- Append Mode with Static Date Range
- **What the wrinkle is**: V1 uses Append mode (`writeMode: "Append"`) combined with a hard-coded date range spanning the full Q4 2024. Every execution appends the same full-quarter aggregation. This produces duplicate data on re-runs. Whether this is intentional or misconfigured is ambiguous, but it IS V1's behavior.
- **How V2 handles it**: V2 preserves `writeMode: "Append"` with the same hard-coded date range. The behavior is identical.
- **What to verify**:
  1. Run V2 for two consecutive effective dates (e.g., 2024-10-01 and 2024-10-02)
  2. Verify the output file contains TWO complete copies of the full Q4 daily aggregation (header only at the top)
  3. Compare against V1 output after the same two-run sequence -- byte-identical expected

### TC-W2: BR-6 -- Duplicate as_of Column
- **What the wrinkle is**: The SQL `SELECT as_of AS wire_date, ..., as_of` produces a duplicate column. The output contains both `wire_date` (aliased from `as_of`) and bare `as_of` as separate columns with identical values. This is not a W-code per se but an output-affecting quirk documented in BR-6.
- **How V2 handles it**: V2 SQL preserves the exact same SELECT list with `as_of` appearing twice.
- **What to verify**:
  1. Verify the CSV header contains exactly 4 columns: `wire_date,wire_count,total_amount,as_of`
  2. For every data row, verify `wire_date` == `as_of` (column 1 == column 4)
  3. Verify V1 and V2 both exhibit this duplication

## Notes
- The V1 SQL contains a redundant WHERE clause that duplicates the DataSourcing effective date filter. V2 removes this (AP8). This is safe because DataSourcing already constrains the data at the PostgreSQL level. The SQLite table never contains rows outside the specified date range.
- BRD Open Question 1 (Append mode with static date range) remains unresolved -- V2 reproduces V1 behavior as-is.
- BRD Open Question 2 (all statuses included) remains unresolved -- V2 reproduces V1 behavior (no filter on status/direction).
- This is a Tier 1 job with no External module. The entire migration is a config change (column reduction + SQL simplification). Risk is low.
