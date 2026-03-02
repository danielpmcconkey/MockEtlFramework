# OverdraftDailySummary — V2 Test Plan

## Job Info
- **V2 Config**: `overdraft_daily_summary_v2.json`
- **Tier**: Tier 1 (Framework Only)
- **External Module**: None (V1 used `OverdraftDailySummaryProcessor`; eliminated in V2 per AP3)

## Pre-Conditions
- **Source Table**: `datalake.overdraft_events` must be populated with columns `overdraft_amount` (numeric), `fee_amount` (numeric), `as_of` (date).
- **Effective Date Range**: Injected at runtime by the executor via shared state (`__minEffectiveDate`, `__maxEffectiveDate`). No hardcoded dates in V2 config.
- **V1 Baseline**: V1 output at `Output/curated/overdraft_daily_summary.csv` must exist for Proofmark comparison. Since V1 uses Overwrite mode, only the last effective date's output will be present.
- **V1 External Module**: V1 uses `ExternalModules.OverdraftDailySummaryProcessor` which is fully replaced by a Transformation (SQL) module in V2. The V1 External module must have been built and run to produce baseline output.
- **V1 Dead-End Table**: V1 sources `datalake.transactions` but the External processor never reads it. V2 removes this entirely. The `transactions` table is not required for V2 execution.
- **Sunday Test Data**: To validate W3a (WEEKLY_TOTAL), the test dataset's effective date range must include at least one Sunday as the `__maxEffectiveDate`. Without a Sunday in the max position, the WEEKLY_TOTAL row will not be generated and TC-W1 cannot be fully validated.

## Test Cases

### TC-1: Output Schema Validation
- **Expected columns** (exact order from FSD Section 4 SQL SELECT order):
  1. `event_date` (text — `as_of` date string for daily rows; `"WEEKLY_TOTAL"` for summary row)
  2. `overdraft_count` (integer — `COUNT(*)`)
  3. `total_overdraft_amount` (numeric — `SUM(overdraft_amount)`)
  4. `total_fees` (numeric — `SUM(fee_amount)`)
  5. `as_of` (date — text in `yyyy-MM-dd` format)
- **Column count**: 5
- Verify the header row in the CSV contains exactly these column names in this order.
- Verify no extra columns are present. V1 sourced 7 columns from `overdraft_events` but only 3 were ever read by the External processor (`overdraft_amount`, `fee_amount`, `as_of`). V2 sources only 2 (`overdraft_amount`, `fee_amount`; `as_of` is auto-appended).

### TC-2: Row Count Equivalence
- V1 and V2 must produce identical row counts for each effective date.
- For each effective date with overdraft events: expect one row per distinct `as_of` date in the source data.
- If `__maxEffectiveDate` is a Sunday: expect one additional `WEEKLY_TOTAL` row appended after the daily rows.
- The trailer `{row_count}` token must reflect the total number of DataFrame rows, including the WEEKLY_TOTAL row if present (BRD:BR-8, EC-3).
- Since writeMode is Overwrite, final output after full auto-advance contains only the last effective date's rows.

### TC-3: Data Content Equivalence
- All values must be byte-identical to V1 output.
- Run Proofmark comparison between `Output/curated/overdraft_daily_summary.csv` (V1) and `Output/double_secret_curated/overdraft_daily_summary.csv` (V2).
- **Precision risk (FSD OQ-1)**: V1 accumulates `overdraft_amount` and `fee_amount` using C# `decimal` type. V2 runs through SQLite REAL (IEEE 754 double). For simple `SUM` operations (addition only, no division/multiplication chains), the string representation should be identical. If Proofmark fails, the first hypothesis is floating-point divergence on monetary sums.
- **Row ordering risk (FSD OQ-2)**: V1's output order is determined by dictionary insertion order (which follows DataSourcing's `as_of` sort). V2 relies on SQLite's `GROUP BY as_of` producing ascending order without an explicit `ORDER BY`. If row ordering differs, add `ORDER BY as_of` to the first SELECT in the SQL.
- Verify that for daily rows, `event_date` and `as_of` columns contain the same value (BRD:BR-3).
- Verify that all fees are included regardless of `fee_waived` status (BRD:BR-2) -- no filtering on `fee_waived`.

### TC-4: Writer Configuration
- **includeHeader**: `true` -- verify header row is present in output CSV.
- **writeMode**: `Overwrite` -- verify each execution replaces the entire file (not appends).
- **lineEnding**: `LF` -- verify line endings are `\n` (not `\r\n`).
- **trailerFormat**: `TRAILER|{row_count}|{date}` -- verify trailer row is the last line, with `{row_count}` equal to the DataFrame row count and `{date}` equal to `__maxEffectiveDate`.
- **source**: `output` -- verify writer reads from the correct Transformation result name.
- **outputFile**: `Output/double_secret_curated/overdraft_daily_summary.csv` -- verify V2 writes to the correct path.

### TC-5: Anti-Pattern Elimination Verification

| AP-Code | What to Verify |
|---------|----------------|
| AP1 | V2 config does NOT contain a DataSourcing entry for the `transactions` table. V1 sourced `datalake.transactions` (6 columns) but the External processor never accessed `sharedState["transactions"]`. Verify the V2 config has exactly one DataSourcing entry (for `overdraft_events` only). |
| AP3 | V2 config does NOT contain an External module entry. V1 used `OverdraftDailySummaryProcessor` (a C# External module) for logic that is expressible as SQL GROUP BY + UNION ALL. Verify the V2 module chain is: DataSourcing -> Transformation -> CsvFileWriter (3 modules, no External). |
| AP4 | V2 DataSourcing sources only 2 columns: `overdraft_amount`, `fee_amount`. Verify `overdraft_id`, `account_id`, `customer_id`, `fee_waived`, `event_timestamp` are NOT in the V2 config. The framework auto-appends `as_of`. V1 sourced 7 columns; 5 were unused. |
| AP6 | V2 does NOT use row-by-row iteration. V1's External processor used `foreach (var row in overdraftEvents.Rows)` to manually group rows into a dictionary. V2 uses SQL `GROUP BY as_of` with `COUNT(*)` and `SUM()` aggregates -- set-based operation. Verify no External module is present in the V2 config. |

### TC-6: Edge Cases

| Edge Case | Test Description | Expected Behavior |
|-----------|-----------------|-------------------|
| EC-1: WEEKLY_TOTAL scope | When `__maxEffectiveDate` is a Sunday and the effective range spans multiple weeks, verify the WEEKLY_TOTAL row sums ALL daily groups across the entire range, not just the current calendar week. | WEEKLY_TOTAL `overdraft_count` = sum of all daily `overdraft_count` values; same for `total_overdraft_amount` and `total_fees`. This is V1 behavior per BRD:EC-1. |
| EC-2: Non-Sunday max date | When `__maxEffectiveDate` is NOT a Sunday, verify no WEEKLY_TOTAL row is present. | Output contains only daily summary rows. No WEEKLY_TOTAL row. Row count in trailer reflects only daily rows. |
| EC-3: Trailer row count with WEEKLY_TOTAL | When WEEKLY_TOTAL row is present, verify `{row_count}` in the trailer includes it. | Trailer `{row_count}` = number of daily rows + 1 (for WEEKLY_TOTAL). |
| EC-4: Empty source data | No overdraft events exist for the effective date range. | Empty DataFrame. CSV contains header and trailer only. Trailer `{row_count}` = 0. No WEEKLY_TOTAL row (no data, no Sunday check applies). |
| EC-5: Overwrite on multi-day | Run auto-advance across multiple effective dates. Verify only the last date's output survives. | File is overwritten each execution. Only final date's data persists. |
| EC-6: fee_waived ignored | Verify that `fee_waived` is NOT sourced and NOT used in V2. All fees are summed regardless of waiver status. | `total_fees` = `SUM(fee_amount)` for all events, same as V1 which also ignores `fee_waived`. |
| EC-7: Single date in range | Effective range contains only one date. | One daily row. If that date is a Sunday, WEEKLY_TOTAL row is also present (with identical values to the daily row). |
| EC-8: event_date = as_of for daily rows | Verify that for every daily row, the `event_date` column value equals the `as_of` column value. | Both columns carry the same `yyyy-MM-dd` date string for daily rows (BRD:BR-3). |

### TC-7: Proofmark Configuration
- **Config file**: `POC3/proofmark_configs/overdraft_daily_summary.yaml`
- **Expected settings**:
  - `comparison_target`: `"overdraft_daily_summary"`
  - `reader`: `csv`
  - `threshold`: `100.0` (strict -- 100% match required)
  - `csv.header_rows`: `1`
  - `csv.trailer_rows`: `1`
- **Excluded columns**: None (all output is deterministic per BRD).
- **Fuzzy columns**: None initially. If Proofmark fails due to floating-point divergence between C# `decimal` (V1) and SQLite REAL (V2), add fuzzy tolerance for `total_overdraft_amount` and `total_fees` columns with absolute tolerance (e.g., 0.01) and documented evidence. This should be a last resort after confirming the difference is purely representational.

## W-Code Test Cases

### TC-W1: W3a — End-of-week boundary (WEEKLY_TOTAL on Sundays)
- **What the wrinkle is**: V1 appends a `WEEKLY_TOTAL` summary row when `__maxEffectiveDate` falls on a Sunday. The WEEKLY_TOTAL row sums ALL daily groups across the entire effective date range (not just the current calendar week). The row uses `"WEEKLY_TOTAL"` as `event_date` and `MAX(as_of)` (the Sunday date) as `as_of`.
- **How V2 handles it**: The Transformation SQL uses a `UNION ALL` with a second SELECT that aggregates all rows in `overdraft_events`. The WHERE clause `(SELECT strftime('%w', MAX(as_of)) FROM overdraft_events) = '0'` gates inclusion on Sunday (SQLite `strftime('%w', ...)` returns `'0'` for Sunday).
- **What to verify**:
  - When `__maxEffectiveDate` is a Sunday: verify WEEKLY_TOTAL row is present as the last data row (before trailer).
  - Verify WEEKLY_TOTAL row's `overdraft_count` = total count across all daily rows.
  - Verify WEEKLY_TOTAL row's `total_overdraft_amount` = sum of all daily `total_overdraft_amount` values.
  - Verify WEEKLY_TOTAL row's `total_fees` = sum of all daily `total_fees` values.
  - Verify WEEKLY_TOTAL row's `as_of` = the Sunday date (MAX(as_of)).
  - Verify WEEKLY_TOTAL row's `event_date` = literal string `"WEEKLY_TOTAL"`.
  - When `__maxEffectiveDate` is NOT a Sunday: verify no WEEKLY_TOTAL row exists. The `WHERE` clause evaluates to false and the `UNION ALL` contributes zero rows.

### TC-W2: W9 — Wrong writeMode (Overwrite)
- **What the wrinkle is**: V1 uses `Overwrite` mode, meaning each execution replaces the entire CSV. During multi-day auto-advance, only the last effective date's output survives. Data from prior days is permanently lost.
- **How V2 handles it**: V2 config specifies `"writeMode": "Overwrite"`, matching V1 exactly.
- **What to verify**:
  - After running auto-advance across the full date range, verify the CSV contains only the last effective date's data.
  - Verify no data from prior effective dates is present in the file.
  - Verify `writeMode` in the V2 config JSON is exactly `"Overwrite"` (not `"Append"`).

## Notes
- **Floating-point precision is the primary risk**: V1 uses C# `decimal` for accumulation; V2 uses SQLite REAL (double). For simple addition of financial amounts, the outputs should be string-identical. However, if the source data contains many small values or values with long decimal representations, epsilon drift could cause divergence. If Proofmark comparison fails, investigate in this order: (1) floating-point precision on `total_overdraft_amount` or `total_fees`, (2) row ordering differences, (3) WEEKLY_TOTAL conditional logic.
- **Row ordering is the secondary risk**: V2 does not include an explicit `ORDER BY` in the daily summary SELECT. SQLite's `GROUP BY as_of` with pre-sorted data typically produces ascending order, but this is not guaranteed by the SQLite specification. If row order differs from V1, the fix is to add `ORDER BY as_of` to the first SELECT or wrap in an outer query.
- **No External module needed**: All V1 logic (GROUP BY aggregation, conditional UNION ALL for WEEKLY_TOTAL) is natively expressible in SQL. The V1 External module was a textbook case of AP3 + AP6.
- **Dead-end transactions table**: V1 sources `datalake.transactions` but the External processor never accesses `sharedState["transactions"]`. This is confirmed dead-end sourcing (AP1). V2 correctly removes this entirely.
- **Proofmark first-failure hypothesis**: If Proofmark comparison fails, investigate in this order: (1) decimal vs REAL precision on monetary sums, (2) row ordering (add ORDER BY), (3) WEEKLY_TOTAL conditional mismatch (strftime Sunday check), (4) trailer row count discrepancy.
