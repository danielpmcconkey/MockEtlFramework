# OverdraftAmountDistribution — V2 Test Plan

## Job Info
- **V2 Config**: `overdraft_amount_distribution_v2.json`
- **Tier**: 2 (Framework + Minimal External)
- **External Module**: `OverdraftAmountDistributionProcessorV2` (minimal I/O adapter for inflated trailer)

## Pre-Conditions
- **Source table**: `datalake.overdraft_events` must be populated with columns: `overdraft_amount`, `as_of` (auto-appended by framework)
- **Effective date range**: `firstEffectiveDate = 2024-10-01`, auto-advance through 2024-12-31
- **Shared state**: Executor must inject `__minEffectiveDate` and `__maxEffectiveDate` per run
- **V1 baseline**: `Output/curated/overdraft_amount_distribution.csv` must exist for comparison
- **Output directory**: `Output/double_secret_curated/` must be writable
- **V1 External module**: `ExternalModules.OverdraftAmountDistributionProcessor` for reference behavior
- **V2 External module assembly**: Must be compiled and accessible at the configured `assemblyPath`

## Test Cases

### TC-1: Output Schema Validation
- **Expected columns** (exact order per FSD Section 4 and BRD Output Schema):
  1. `amount_bucket` (TEXT -- one of: "0-50", "50-100", "100-250", "250-500", "500+")
  2. `event_count` (INTEGER)
  3. `total_amount` (REAL/DECIMAL)
  4. `as_of` (TEXT, date value from first source row)
- Verify the CSV header row contains exactly these 4 column names in this order
- Verify no extra columns are present
- Verify column names are lowercase with underscores
- **Trailer row**: `TRAILER|{inputRowCount}|{maxDate:yyyy-MM-dd}` -- verify this is present as the last line of the file
- Verify trailer row is NOT part of the data schema (it is a pipe-delimited metadata row, not a CSV data row)

### TC-2: Row Count Equivalence
- V1 and V2 must produce identical data row counts per effective date
- Data rows = number of non-empty buckets (at most 5, at least 0)
- Empty buckets (zero overdraft events in that range) are excluded from output (BR-2)
- Trailer row is NOT counted as a data row
- With Overwrite mode, only the final effective date's output survives multi-day runs -- compare final file state only

### TC-3: Data Content Equivalence (note W-codes)
- Compare `Output/curated/overdraft_amount_distribution.csv` (V1) against `Output/double_secret_curated/overdraft_amount_distribution.csv` (V2)
- `amount_bucket`: must match V1's exact bucket label strings ("0-50", "50-100", "100-250", "250-500", "500+")
- `event_count`: INTEGER from COUNT(*), must match exactly
- `total_amount`: SUM of overdraft_amount per bucket -- see OQ-2 note below on decimal vs REAL precision
- `as_of`: must match V1's date format exactly -- **HIGH RISK** (see TC-W1 and OQ-1 for format concerns)
- Row ordering must match V1's dictionary insertion order: `0-50, 50-100, 100-250, 250-500, 500+` (only non-empty buckets)
- Trailer row must match: `TRAILER|{inputRowCount}|{maxDate:yyyy-MM-dd}`
- **W-codes W7 and W9 apply** -- see dedicated W-code test cases below

### TC-4: Writer Configuration
- **Writer type**: Direct file I/O via External module (NOT CsvFileWriter) -- matches V1
- **Output path**: `Output/double_secret_curated/overdraft_amount_distribution.csv` (V2 output path)
- **Header**: Written by External module as `amount_bucket,event_count,total_amount,as_of`
- **Line ending**: `Environment.NewLine` via StreamWriter default (`\n` on Linux) -- matches V1
- **Trailer**: `TRAILER|{inputRowCount}|{maxDate:yyyy-MM-dd}` -- written by External module after data rows
- **Write mode**: Overwrite (`new StreamWriter(outputPath, false)`) -- matches V1 (W9)
- **RFC 4180 quoting**: Not applied -- simple string interpolation, matching V1 (EC-4)
- Verify the External module bypasses the framework's CsvFileWriter entirely (no writer module in config after External)

### TC-5: Anti-Pattern Elimination Verification

#### TC-5a: AP3 — Unnecessary External module partially eliminated
- V1 uses External for ALL logic: bucketing, aggregation, file I/O
- V2 moves bucketing and aggregation into SQL Transformation (Tier 2 scalpel approach)
- V2 External module is reduced to a minimal I/O adapter that:
  1. Reads the already-bucketed `output` DataFrame from shared state
  2. Reads the `overdraft_events` DataFrame from shared state for input row count (W7) and as_of value
  3. Writes CSV with header, data rows, and inflated trailer
- Verify V2 External contains NO bucketing logic (no CASE/WHEN, no if/else bucket assignment)
- Verify V2 External contains NO aggregation logic (no COUNT, SUM, accumulation loops)
- Full AP3 elimination is blocked by W7 (inflated trailer requires External)

#### TC-5b: AP4 — Unused columns eliminated
- V1 DataSourcing sources 7 columns: `overdraft_id`, `account_id`, `customer_id`, `overdraft_amount`, `fee_amount`, `fee_waived`, `event_timestamp`
- V2 DataSourcing sources only `["overdraft_amount"]` (plus `as_of` auto-appended by framework)
- Verify V2 config DataSourcing `columns` array is `["overdraft_amount"]`
- Verify 6 unused columns (`overdraft_id`, `account_id`, `customer_id`, `fee_amount`, `fee_waived`, `event_timestamp`) are not sourced
- Verify output is unaffected (only `overdraft_amount` and `as_of` are used in the business logic)

#### TC-5c: AP6 — Row-by-row iteration eliminated
- V1 uses a `foreach` loop over every overdraft event row to assign buckets and accumulate counts/totals into a dictionary
- V2 replaces this with SQL `CASE/WHEN + GROUP BY` in the Transformation module (set-based aggregation)
- Verify V2 Transformation SQL contains `GROUP BY CASE WHEN ...` pattern
- Verify V2 External module does NOT iterate over source rows for bucketing/aggregation purposes

#### TC-5d: AP7 — Magic values documented
- V1 bucket boundaries (50, 100, 250, 500) are bare numeric literals in an if-chain without documentation
- V2 SQL boundaries must be documented with inline comments explaining they are overdraft amount range thresholds
- The boundary VALUES remain identical for output equivalence
- Verify V2 External module uses named string constants for trailer format and DataFrame names

### TC-6: Edge Cases

#### TC-6a: Empty source data (no overdraft events for effective date)
- When `overdraft_events` DataFrame is empty (0 rows), verify:
  - No CSV file is written (matching V1 early-return behavior at OverdraftAmountDistributionProcessor.cs:37-41)
  - OR if a file is written, it should contain header only with no data rows and no trailer
  - Shared state should contain an empty `output` DataFrame with the correct column schema

#### TC-6b: All events in a single bucket
- All overdraft amounts fall within one range (e.g., all <= 50)
- Output should contain exactly 1 data row for the populated bucket
- The other 4 buckets should be excluded from output (BR-2: empty bucket exclusion)
- Trailer input row count should reflect the total source event count, not 1

#### TC-6c: Events in all 5 buckets
- Overdraft amounts span all ranges
- Output should contain exactly 5 data rows in order: 0-50, 50-100, 100-250, 250-500, 500+
- Trailer input row count = total events across all buckets

#### TC-6d: Bucket boundary values (exact boundary amounts)
- Amount = 50.00 exactly: should fall in "0-50" bucket (BR-1: `<= 50`)
- Amount = 50.01: should fall in "50-100" bucket
- Amount = 100.00 exactly: should fall in "50-100" bucket (BR-1: `<= 100`)
- Amount = 250.00 exactly: should fall in "100-250" bucket (BR-1: `<= 250`)
- Amount = 500.00 exactly: should fall in "250-500" bucket (BR-1: `<= 500`)
- Amount = 500.01: should fall in "500+" bucket
- Verify boundary behavior matches V1's `if (amount <= 50m)` chain exactly

#### TC-6e: Overwrite on multi-day auto-advance (W9)
- On multi-day runs, each execution overwrites the file entirely
- Only the final effective date's output should persist in the file
- Verify intermediate dates' output does NOT survive in the final file
- This is expected V1 behavior (W9) -- not a bug, but must be replicated

#### TC-6f: Bucket ordering determinism (EC-5)
- Output rows must follow the fixed order: 0-50, 50-100, 100-250, 250-500, 500+
- V2 SQL uses `ORDER BY CASE bucket.amount_bucket WHEN '0-50' THEN 1 WHEN '50-100' THEN 2 ...` to enforce this
- V1 relies on dictionary insertion order (OverdraftAmountDistributionProcessor.cs:47-53)
- Verify both produce identical row ordering for all non-empty bucket combinations

### TC-7: Proofmark Configuration
- **Expected proofmark settings** (from FSD Section 8):
  ```yaml
  comparison_target: "overdraft_amount_distribution"
  reader: csv
  threshold: 100.0
  csv:
    header_rows: 1
    trailer_rows: 1
  ```
- **threshold**: `100.0` -- all computations should be deterministic; exact match required initially
- **header_rows**: `1` -- single header row written by External module
- **trailer_rows**: `1` -- Overwrite mode produces exactly one trailer at end of file per run
- **excluded_columns**: None initially -- all columns are deterministic given same source data
- **fuzzy_columns**: None initially -- starting strict per best practices. If `total_amount` shows epsilon differences (OQ-2: SQLite REAL vs C# decimal), add fuzzy tolerance with evidence during Phase D
- **Note**: If `as_of` format mismatch is detected (OQ-1), this requires a code fix in the V2 External module, NOT a proofmark override

## W-Code Test Cases

### TC-W1: W7 — Trailer Inflated Count
- **V1 behavior**: The trailer line uses the INPUT row count (total overdraft events before bucketing), not the OUTPUT row count (number of non-empty buckets)
- **Example**: 139 input events bucketed into 5 groups produce `TRAILER|139|2024-10-15`, not `TRAILER|5|2024-10-15`
- **Verification steps**:
  1. Count the total rows in `overdraft_events` DataFrame for the effective date
  2. Count the data rows in the output CSV (non-empty buckets)
  3. Verify the trailer's first numeric field matches the INPUT count, not the OUTPUT count
  4. Verify V2 External module reads `overdraft_events.Count` from shared state (not `output.Count`)
  5. Verify the trailer date field uses `__maxEffectiveDate` in `yyyy-MM-dd` format
  6. Verify V2 trailer is byte-identical to V1 trailer for every effective date
- **Code inspection**: V2 External module should contain a comment: `// W7: Trailer uses INPUT row count (inflated), not output bucket count. V1 behavior replicated for output equivalence.`

### TC-W2: W9 — Wrong writeMode (Overwrite)
- **V1 behavior**: StreamWriter opens with `append: false`, overwriting the file on each execution. During multi-day auto-advance, only the final effective date's output survives.
- **Verification steps**:
  1. Run V2 for effective date 2024-10-01 -- verify file is created with that date's data
  2. Run V2 for effective date 2024-10-02 -- verify file now contains ONLY 2024-10-02's data (2024-10-01's data is gone)
  3. Run V2 for the full date range -- verify final file contains only the last effective date's output
  4. Compare the final file state (not intermediate states) against V1 baseline
- **Code inspection**: V2 External module should use `new StreamWriter(outputPath, false)` and contain a comment: `// W9: V1 uses Overwrite -- prior days' data is lost on each run. Replicated for output equivalence.`

## Notes
- This is a Tier 2 job -- the External module is justified solely by W7 (inflated trailer count). If not for W7, this would be a clean Tier 1 job.
- **OQ-1 (HIGH RISK)**: The `as_of` format is a known risk. V1 calls `DateOnly.ToString()` on the first source row's `as_of` value (culture-dependent format). V2's SQL pipeline produces `as_of` as `yyyy-MM-dd` via Transformation.cs:110. The V2 External module must read `as_of` from the `overdraft_events` DataFrame (original `DateOnly` object) and call `.ToString()` to match V1's format, NOT from the `output` DataFrame (which contains the SQLite-formatted string). If format mismatch occurs, it is a code bug requiring a fix, not a proofmark tolerance adjustment.
- **OQ-2 (LOW-MEDIUM RISK)**: `total_amount` precision may differ between V2 (SQLite REAL/double via SUM) and V1 (C# decimal accumulation). Starting strict. If Proofmark detects epsilon differences, add a fuzzy tolerance on `total_amount` with evidence during Phase D.
- **OQ-3 (LOW RISK)**: Line endings use `Environment.NewLine` via StreamWriter on both V1 and V2. As long as both run on the same Linux platform, this is a non-issue.
- The V2 module chain is: `DataSourcing (overdraft_events) -> Transformation (SQL bucketing/aggregation) -> External (minimal CSV I/O with inflated trailer)`
- All business logic (CASE/WHEN bucketing, GROUP BY aggregation, empty bucket exclusion, bucket ordering) lives in the SQL Transformation, not in the External module
