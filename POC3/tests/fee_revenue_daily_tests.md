# FeeRevenueDaily — V2 Test Plan

## Job Info
- **V2 Config**: `fee_revenue_daily_v2.json`
- **Tier**: Tier 2 (Framework + Minimal External)
- **External Module**: `FeeRevenueDailyV2Processor` — minimal bridge that materializes `__maxEffectiveDate` into a single-row DataFrame (`effective_date_ref`). Zero business logic.

## Pre-Conditions
- **Source Table**: `datalake.overdraft_events` must be populated with columns `fee_amount` (numeric), `fee_waived` (boolean), `as_of` (date).
- **Date Range**: DataSourcing uses hardcoded dates `2024-10-01` to `2024-12-31` (overrides executor-injected dates). The full range is sourced on every run regardless of effective date.
- **Effective Date Injection**: The executor must inject `__maxEffectiveDate` into shared state for the External module to read. If missing, falls back to `DateTime.Today` (EC-5).
- **External Module Build**: `ExternalModules.dll` must be compiled and accessible at the configured `assemblyPath`.
- **V1 Baseline**: V1 output at `Output/curated/fee_revenue_daily.csv` must exist for Proofmark comparison. Since V1 uses Overwrite mode, only the last effective date's output (2024-12-31) will be present in the baseline.

## Test Cases

### TC-1: Output Schema Validation
- **Expected columns** (exact order from FSD Section 4 / V1 External module):
  1. `event_date` (string — `yyyy-MM-dd` or `"MONTHLY_TOTAL"`)
  2. `charged_fees` (double-precision numeric)
  3. `waived_fees` (double-precision numeric)
  4. `net_revenue` (double-precision numeric)
  5. `as_of` (string — `yyyy-MM-dd`)
- **Column count**: 5
- Verify the header row in the CSV contains exactly these column names in this order.
- Verify no extra columns are present (V1 sources 7 columns but only outputs 5).

### TC-2: Row Count Equivalence
- V1 and V2 must produce identical row counts for each effective date.
- For non-end-of-month dates: exactly 1 row (the daily aggregation row).
- For end-of-month dates (Oct 31, Nov 30, Dec 31): exactly 2 rows (daily row + MONTHLY_TOTAL row).
- For dates with no overdraft events: 0 data rows (header-only CSV).
- Since writeMode is Overwrite, final output after full auto-advance contains only the last effective date's rows.

### TC-3: Data Content Equivalence
- All values must be byte-identical to V1 output.
- Run Proofmark comparison between `Output/curated/fee_revenue_daily.csv` (V1) and `Output/double_secret_curated/fee_revenue_daily.csv` (V2).
- **W6 concern**: V1 accumulates via sequential `foreach` loop (double), V2 uses SQLite `SUM()` on REAL columns. For the known dataset (fee amounts of exactly `35.00` or `0.00`), IEEE 754 represents these exactly, so results should be bit-identical. If Proofmark fails with epsilon-level differences, apply fuzzy tolerance (see TC-W2 below).
- **BRD BR-2 correction**: The FSD identified that the BRD incorrectly states V1 filters to `maxDate - 1 day`. Actual V1 code filters to `maxDate` itself. V2 follows the actual V1 code. Verify output matches V1, not the BRD description.

### TC-4: Writer Configuration
- **includeHeader**: `true` — verify header row is present in output CSV.
- **writeMode**: `Overwrite` — verify each execution replaces the entire file (not appends).
- **lineEnding**: `LF` — verify line endings are `\n` (not `\r\n`).
- **trailerFormat**: not configured — verify no trailer row exists in the output.
- **outputFile**: `Output/double_secret_curated/fee_revenue_daily.csv` — verify V2 writes to the correct path.

### TC-5: Anti-Pattern Elimination Verification

| AP-Code | What to Verify |
|---------|----------------|
| AP3 | The V2 External module (`FeeRevenueDailyV2Processor`) contains **zero business logic**. It only reads `__maxEffectiveDate` and writes it to a single-row DataFrame. All fee categorization, aggregation, net revenue calculation, and MONTHLY_TOTAL logic are in the Transformation SQL. Inspect the External module source to confirm no `SUM`, `foreach` accumulation, or fee logic exists. |
| AP4 | V2 DataSourcing sources only 3 columns: `fee_amount`, `fee_waived`, `as_of`. Verify `overdraft_id`, `account_id`, `customer_id`, `overdraft_amount`, `event_timestamp` are NOT in the V2 config. (V1 sourced 7 columns.) |
| AP6 | No `foreach` row-by-row iteration for fee accumulation in V2. All aggregation is via SQL `SUM(CASE WHEN ...)`. Verify the External module has no loop-based accumulation. |
| AP10 | **Partially retained** — the hardcoded date range `2024-10-01` to `2024-12-31` is intentionally kept because EC-1 requires it (MONTHLY_TOTAL sums the full sourced range). Verify the FSD documents this as intentional retention, not an oversight. |

### TC-6: Edge Cases

| Edge Case | Test Description | Expected Behavior |
|-----------|-----------------|-------------------|
| EC-1: Monthly total scope bug | On end-of-month dates, verify the MONTHLY_TOTAL row sums ALL rows in `overdraft_events` (full hardcoded range 2024-10-01 to 2024-12-31), NOT just the current month. | V2 SQL second UNION ALL branch has no `as_of` filter on `oe`, scanning all sourced rows. Output matches V1's `overdraftEvents.Rows` iteration. |
| EC-3: Overwrite on multi-day | Run auto-advance across multiple days. Verify only the last effective date's output survives in the CSV. | File is overwritten each execution. Only final date's data persists. |
| EC-4: No events for date | Run for a date with zero overdraft events. | Empty DataFrame returned. CSV contains only header row. No MONTHLY_TOTAL row emitted (EXISTS guard suppresses it). |
| EC-5: Missing `__maxEffectiveDate` | Remove `__maxEffectiveDate` from shared state before External module runs. | External module falls back to `DateOnly.FromDateTime(DateTime.Today)`. SQL filters to today's date. |
| Empty input | `overdraft_events` table is completely empty for the hardcoded date range. | DataSourcing returns empty DataFrame. SQL produces 0 rows. CSV is header-only. |
| End-of-month with no data | Effective date is last day of month but no events exist for that date. | Daily row returns 0 rows. EXISTS subquery returns false, so MONTHLY_TOTAL is also suppressed. Output is header-only. |

### TC-7: Proofmark Configuration
- **Config file**: `POC3/proofmark_configs/fee_revenue_daily.yaml`
- **Expected settings**:
  - `comparison_target`: `"fee_revenue_daily"`
  - `reader`: `csv`
  - `threshold`: `100.0` (strict — start with 100% match requirement)
  - `csv.header_rows`: `1`
  - `csv.trailer_rows`: `0`
- **Excluded columns**: None (all output is deterministic).
- **Fuzzy columns**: None initially. Fallback config documented for W6 if strict comparison fails:
  - `charged_fees`: absolute tolerance `0.0000001`
  - `waived_fees`: absolute tolerance `0.0000001`
  - `net_revenue`: absolute tolerance `0.0000001`

## W-Code Test Cases

### TC-W1: W3b — End-of-Month Boundary (MONTHLY_TOTAL)
- **What the wrinkle is**: V1 appends a `MONTHLY_TOTAL` summary row on the last day of each month. V1 detects this with `maxDate.Day == DateTime.DaysInMonth(maxDate.Year, maxDate.Month)`.
- **How V2 handles it**: SQL uses `WHERE edr.effective_date = date(edr.effective_date, 'start of month', '+1 month', '-1 day')` to detect last-day-of-month. The second UNION ALL branch emits the MONTHLY_TOTAL row only when this condition is true.
- **What to verify**:
  - Run for Oct 31, Nov 30, Dec 31 — each must produce a MONTHLY_TOTAL row.
  - Run for Oct 30, Nov 29, Dec 30 — none should produce a MONTHLY_TOTAL row.
  - The MONTHLY_TOTAL row's `event_date` column must contain the literal string `"MONTHLY_TOTAL"`.
  - The MONTHLY_TOTAL row's `as_of` column must contain the effective date (same as the daily row's `as_of`).
  - The MONTHLY_TOTAL row must appear AFTER the daily row (UNION ALL ordering).

### TC-W2: W6 — Double Epsilon (Floating-Point Accumulation)
- **What the wrinkle is**: V1 uses `double` (IEEE 754) for fee accumulation instead of `decimal`. This can introduce floating-point epsilon errors.
- **How V2 handles it**: C# `decimal` from DataSourcing is mapped to SQLite `REAL` (double-precision) by the framework's `Transformation.GetSqliteType`. SQLite `SUM()` on REAL columns uses double arithmetic, naturally matching V1.
- **What to verify**:
  - Compare `charged_fees`, `waived_fees`, and `net_revenue` values between V1 and V2 to the bit level.
  - For the current dataset (all fees are exactly `35.00`), double represents this exactly, so strict comparison should pass.
  - If Proofmark strict comparison fails, enable the fuzzy fallback config with absolute tolerance `0.0000001` on `charged_fees`, `waived_fees`, `net_revenue`.

### TC-W3: W9 — Wrong writeMode (Overwrite)
- **What the wrinkle is**: V1 uses Overwrite mode, meaning each execution replaces the entire CSV. During multi-day auto-advance, only the last effective date's output survives.
- **How V2 handles it**: V2 config specifies `"writeMode": "Overwrite"`, matching V1 exactly.
- **What to verify**:
  - After running auto-advance from 2024-10-01 through 2024-12-31, verify the CSV contains only 2024-12-31's data (2 rows: daily + MONTHLY_TOTAL since Dec 31 is end of month).
  - Verify no data from prior effective dates is present in the file.

## Notes
- **BRD BR-2 is inaccurate**: The BRD claims V1 filters to `__maxEffectiveDate - 1 day` and cites `maxDate.AddDays(-1)`. The FSD corrected this — actual V1 code at `FeeRevenueDailyProcessor.cs:30-32` filters to `maxDate` itself (no subtraction). V2 follows the actual code. This discrepancy should be flagged if the BRD is ever revised.
- **EC-1 is a V1 bug**: The MONTHLY_TOTAL row sums the full sourced date range (all 3 months), not just the current month. The variable names `monthCharged`/`monthWaived` are misleading. V2 intentionally reproduces this bug for output equivalence.
- **AP10 intentional retention**: The hardcoded date range cannot be eliminated without breaking EC-1. This is documented and justified in the FSD.
- **Proofmark first-failure hypothesis**: If Proofmark comparison fails, investigate in this order: (1) W6 double-precision differences, (2) MONTHLY_TOTAL row presence/absence on boundary dates, (3) date filtering logic (BR-2 correction).
