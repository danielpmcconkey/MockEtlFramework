# OverdraftFeeSummary — V2 Test Plan

## Job Info
- **V2 Config**: `overdraft_fee_summary_v2.json`
- **Tier**: Tier 1 (Framework Only)
- **External Module**: None (V1 also had no External module -- already Tier 1)

## Pre-Conditions
- **Source Table**: `datalake.overdraft_events` must be populated with columns `fee_amount` (numeric), `fee_waived` (boolean), `as_of` (date).
- **Effective Date Range**: Injected at runtime by the executor via shared state (`__minEffectiveDate`, `__maxEffectiveDate`). No hardcoded dates in V2 config.
- **V1 Baseline**: V1 output at `Output/curated/overdraft_fee_summary.csv` must exist for Proofmark comparison. Since V1 uses Overwrite mode, only the last effective date's output will be present.
- **V1 SQL Baseline**: V1 already uses a Transformation (SQL) module. The V1 SQL contains a CTE (`all_events`) with `ROW_NUMBER() OVER (PARTITION BY as_of ORDER BY overdraft_id) AS rn` that is never referenced in the outer query. V2 removes this dead CTE entirely.
- **Column Sourcing**: V1 sources 7 columns from `overdraft_events`; V2 sources only 2 (`fee_amount`, `fee_waived`; `as_of` auto-appended). Verify the reduced column set does not affect SQL execution.

## Test Cases

### TC-1: Output Schema Validation
- **Expected columns** (exact order from FSD Section 4 SQL SELECT order):
  1. `fee_waived` (boolean, stored as SQLite INTEGER 0/1)
  2. `total_fees` (numeric -- `ROUND(SUM(fee_amount), 2)`)
  3. `event_count` (integer -- `COUNT(*)`)
  4. `avg_fee` (numeric -- `ROUND(AVG(fee_amount), 2)`)
  5. `as_of` (date -- text in `yyyy-MM-dd` format)
- **Column count**: 5
- Verify the header row in the CSV contains exactly these column names in this order.
- Verify no extra columns are present. V1 sourced 7 columns but only `fee_amount`, `fee_waived`, and `as_of` were used in the outer SQL query. The `rn` column from the dead CTE never appeared in output.

### TC-2: Row Count Equivalence
- V1 and V2 must produce identical row counts for each effective date.
- For each effective date with overdraft events: expect up to 2 rows per `as_of` date (one for `fee_waived = 0/false`, one for `fee_waived = 1/true`), depending on whether both categories exist in the data.
- For dates with no overdraft events: 0 data rows (header-only CSV).
- Since writeMode is Overwrite, final output after full auto-advance contains only the last effective date's rows.
- **CTE removal safety**: Removing the `all_events` CTE does not change row counts because the CTE was a direct passthrough (SELECT all rows, add `rn`). The outer query's GROUP BY operates on the identical row set.

### TC-3: Data Content Equivalence
- All values must be byte-identical to V1 output.
- Run Proofmark comparison between `Output/curated/overdraft_fee_summary.csv` (V1) and `Output/double_secret_curated/overdraft_fee_summary.csv` (V2).
- **Key equivalence argument**: Both V1 and V2 execute SQL through the same SQLite Transformation module. The only SQL change is removing the dead CTE and changing the table alias from `ae` to `oe`. The GROUP BY, SUM, COUNT, AVG, ROUND, and ORDER BY operations are identical. No floating-point divergence is expected because the arithmetic path is the same.
- **CTE removal proof**: The V1 CTE `all_events` passes through all rows of `overdraft_events` and adds a `rn` column that is never referenced in the outer SELECT, GROUP BY, or ORDER BY. Removing the CTE changes nothing about which rows are grouped or how they are aggregated.

### TC-4: Writer Configuration
- **includeHeader**: `true` -- verify header row is present in output CSV.
- **writeMode**: `Overwrite` -- verify each execution replaces the entire file (not appends).
- **lineEnding**: `LF` -- verify line endings are `\n` (not `\r\n`).
- **trailerFormat**: not configured -- verify no trailer row exists in the output.
- **source**: `fee_summary` -- verify writer reads from the correct Transformation result name.
- **outputFile**: `Output/double_secret_curated/overdraft_fee_summary.csv` -- verify V2 writes to the correct path.

### TC-5: Anti-Pattern Elimination Verification

| AP-Code | What to Verify |
|---------|----------------|
| AP4 | V2 DataSourcing sources only 2 columns: `fee_amount`, `fee_waived`. Verify `overdraft_id`, `account_id`, `customer_id`, `overdraft_amount`, `event_timestamp` are NOT in the V2 config. The framework auto-appends `as_of`. V1 sourced 7 columns; 5 were unused (and `overdraft_id` was only used in the dead CTE). |
| AP8 | V2 Transformation SQL does NOT contain a CTE (`WITH all_events AS ...`). V1's CTE computed `ROW_NUMBER() OVER (PARTITION BY as_of ORDER BY overdraft_id) AS rn` but `rn` was never referenced in the outer query. Verify the V2 SQL is a single SELECT with GROUP BY -- no CTEs, no window functions. |

### TC-6: Edge Cases

| Edge Case | Test Description | Expected Behavior |
|-----------|-----------------|-------------------|
| EC-1: Dead CTE removal | Verify V2 produces identical output without the `ROW_NUMBER()` CTE that V1 had. | CTE was a passthrough; removal has no effect on grouping or aggregation. Output must be byte-identical. |
| EC-2: NULL fee_amount | If `fee_amount` is NULL for any row, standard SQL semantics apply: SUM and AVG ignore NULLs, COUNT(*) includes the row. | `event_count` includes rows with NULL fees. `total_fees` and `avg_fee` exclude NULL values from aggregation. BRD:EC-2 notes actual data shows min fee_amount = 0.00 (no NULLs observed), so this is a theoretical edge case. |
| EC-3: Overwrite on multi-day | Run auto-advance across multiple effective dates. Verify only the last date's output survives. | File is overwritten each execution. Only final date's data persists. |
| EC-4: Empty data | No overdraft events exist for the effective date range. | SQL produces 0 rows. CSV contains only header row (no trailer configured). |
| EC-5: Boolean rendering | Verify `fee_waived` column values in CSV output render as `0` and `1` (SQLite INTEGER representation of boolean). | V1 and V2 both pass through the same Transformation module `ToSqliteValue` mapping (`bool => 0/1`). CsvFileWriter calls `ToString()`, producing `"0"` and `"1"`. |
| EC-6: Single waiver category | All events for a date have the same `fee_waived` value (all true or all false). | Output contains only 1 data row for that date's `as_of` instead of 2. GROUP BY produces groups only for categories that exist. |
| EC-7: ORDER BY correctness | Verify output ordering: `fee_waived = 0` (false) rows appear before `fee_waived = 1` (true) rows. | SQLite stores booleans as INTEGER 0/1. `ORDER BY oe.fee_waived` sorts 0 before 1. Within the same `fee_waived` value, row order follows SQLite's GROUP BY output order (typically ascending `as_of`). |
| EC-8: ROUND behavior | Verify `total_fees` and `avg_fee` are rounded to exactly 2 decimal places. | `ROUND(SUM(...), 2)` and `ROUND(AVG(...), 2)` produce values with at most 2 decimal places. SQLite's `ROUND()` uses arithmetic rounding (away from zero at .5). |

### TC-7: Proofmark Configuration
- **Config file**: `POC3/proofmark_configs/overdraft_fee_summary.yaml`
- **Expected settings**:
  - `comparison_target`: `"overdraft_fee_summary"`
  - `reader`: `csv`
  - `threshold`: `100.0` (strict -- 100% match required)
  - `csv.header_rows`: `1`
  - `csv.trailer_rows`: `0`
- **Excluded columns**: None (all output is deterministic per BRD).
- **Fuzzy columns**: None. Both V1 and V2 execute SQL through the same SQLite Transformation module with `ROUND()` applied to all numeric outputs. The arithmetic path is identical -- no floating-point divergence is possible.

## W-Code Test Cases

No output-affecting wrinkles (W-codes) apply to this job.

The FSD evaluated all W-codes and ruled them out:

| W-Code | Reason Not Applicable |
|--------|----------------------|
| W1 (Sunday skip) | No day-of-week logic. |
| W2 (Weekend fallback) | No weekend date logic. |
| W3a/b/c (Boundary rows) | No summary row generation. |
| W4 (Integer division) | All arithmetic uses `ROUND(SUM(...))` and `ROUND(AVG(...))` on numeric type. |
| W5 (Banker's rounding) | SQLite `ROUND()` uses arithmetic rounding. No `MidpointRounding.ToEven`. |
| W6 (Double epsilon) | No double-precision accumulation. Values flow through SQLite `ROUND()` to 2 decimals. |
| W7 (Trailer inflated count) | No trailer configured. |
| W8 (Trailer stale date) | No trailer configured. |
| W9 (Wrong writeMode) | Overwrite is the configured mode and is simply how the job works. Not a wrinkle -- V1 intentional behavior. |
| W10 (Absurd numParts) | Not a Parquet writer. |
| W12 (Header every append) | Not in Append mode. |

## Notes
- **This is a low-risk migration**: V1 was already Tier 1 (framework-only). The only changes in V2 are: (1) removing the dead `ROW_NUMBER()` CTE (AP8), and (2) reducing sourced columns from 7 to 2 (AP4). Both are code-quality improvements that cannot affect output.
- **No External module involved**: Unlike many other jobs in this project, `OverdraftFeeSummary` has no External module in V1 and needs none in V2. The migration is purely SQL simplification.
- **CTE removal is the primary validation point**: The entire output equivalence argument rests on the fact that the `all_events` CTE with `ROW_NUMBER()` was a passthrough that added an unused column. Proofmark comparison will confirm this conclusively.
- **Cross-job duplication with FeeWaiverAnalysis (AP2)**: Both BRD (OQ-2) and FSD (OQ-1) note that this job and `FeeWaiverAnalysis` produce similar outputs (both GROUP BY `fee_waived` + `as_of`). Differences: this job does not JOIN to `accounts`, does not COALESCE NULLs, and had the dead `ROW_NUMBER` CTE. This is documented per AP2 but is not actionable within a single job's scope.
- **Proofmark first-failure hypothesis**: If Proofmark comparison fails, investigate in this order: (1) column alias differences (unlikely -- V2 uses identical alias names), (2) ORDER BY differences (both use `ORDER BY fee_waived`), (3) ROUND behavior edge cases. Given that V1 and V2 both execute through the same SQLite Transformation module, Proofmark failure would be genuinely surprising and likely indicates a config error rather than a logic difference.
