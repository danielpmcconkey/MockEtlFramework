# RegulatoryExposureSummary -- V2 Test Plan

## Job Info
- **V2 Config**: `regulatory_exposure_summary_v2.json`
- **Tier**: 2 (Framework + Minimal External)
- **External Module**: `RegulatoryExposureSummaryV2Processor` (ExternalModules/RegulatoryExposureSummaryV2Processor.cs)
- **Writer**: ParquetFileWriter

## Pre-Conditions
- Source tables available in `datalake` schema:
  - `compliance_events` with columns: `customer_id`, `as_of`
  - `wire_transfers` with columns: `customer_id`, `as_of`
  - `accounts` with columns: `customer_id`, `current_balance`, `as_of`
  - `customers` with columns: `id`, `first_name`, `last_name`, `as_of`
- Effective date range injected by executor (`__minEffectiveDate`, `__maxEffectiveDate`)
- `as_of` column auto-appended by DataSourcing module
- V1 baseline output available at `Output/curated/regulatory_exposure_summary/`
- V1 External module: `ExternalModules.RegulatoryExposureCalculator` (for reference behavior)
- ExternalModules project builds successfully (`dotnet build`)

## Test Cases

### TC-1: Output Schema Validation
- **Expected columns (exact set):** `customer_id`, `first_name`, `last_name`, `account_count`, `total_balance`, `compliance_events`, `wire_count`, `exposure_score`, `as_of`
- **Expected types:**
  - `customer_id`: integer
  - `first_name`: string
  - `last_name`: string
  - `account_count`: integer
  - `total_balance`: decimal (rounded to 2 decimal places)
  - `compliance_events`: integer
  - `wire_count`: integer
  - `exposure_score`: decimal (rounded to 2 decimal places)
  - `as_of`: date
- Verify one row per customer in the target-date-filtered customer list (BR-8)
- Verify zero rows when customers DataFrame is empty (BR-9)
- Traces: BRD Output Schema; FSD Section 10

### TC-2: Row Count Equivalence
- V1 and V2 must produce identical row counts for the same effective date
- Weekday runs: one row per customer matching the target date (BR-8)
- Weekend runs: depends on fallback behavior -- if Friday's customer data doesn't exist in the effective-date-filtered snapshot, ALL customer rows are used (BR-2)
- If customers is empty for the effective date: 0 rows (BR-9)
- Traces: BRD BR-2, BR-8, BR-9; FSD Section 10

### TC-3: Data Content Equivalence
- All data values must match V1 output exactly
- Compare V2 Parquet at `Output/double_secret_curated/regulatory_exposure_summary/` against V1 baseline at `Output/curated/regulatory_exposure_summary/`
- Verify each output column per row:
  - `customer_id`: integer value matches V1 (BR-8)
  - `first_name`: matches V1 (NULL coalesced to empty string per BR-11)
  - `last_name`: matches V1 (NULL coalesced to empty string per BR-11)
  - `account_count`: integer count matches V1 (BR-7)
  - `total_balance`: decimal value matches V1 to 2dp with banker's rounding (BR-6)
  - `compliance_events`: integer count matches V1 (BR-3)
  - `wire_count`: integer count matches V1 (BR-3)
  - `exposure_score`: decimal value matches V1 to 2dp with decimal arithmetic (BR-4, BR-5)
  - `as_of`: date matches V1 target date after weekend fallback (BR-10)
- Traces: BRD BR-3 through BR-11; FSD Sections 4, 5, 10

### TC-4: Writer Configuration
- **type**: ParquetFileWriter (FSD Section 6)
- **source**: `output` (matches External module's shared state key)
- **outputDirectory**: `Output/double_secret_curated/regulatory_exposure_summary/` (V2 convention)
- **numParts**: `1` -- verify single part file in output directory
- **writeMode**: `Overwrite` -- verify directory is replaced on each run
- Traces: BRD Writer Configuration; FSD Section 6

### TC-5: Anti-Pattern Elimination Verification

#### AP3 (Unnecessary External Module) -- PARTIALLY ELIMINATED
- Verify V2 reduces the External module scope from full Tier 3 to minimal Tier 2
- Verify V2 External module does ONLY these operations:
  1. Apply `Math.Round(total_balance, 2)` with banker's rounding (BR-6, W5)
  2. Compute `exposure_score` with decimal arithmetic and banker's rounding (BR-4, BR-5)
  3. Set `as_of` to `target_date` from SQL output (BR-10)
- Verify V2 External module does NOT perform:
  - Aggregation (done in SQL via GROUP BY)
  - Joining (done in SQL via LEFT JOIN)
  - Customer filtering (done in SQL via CTEs)
  - Weekend fallback date computation (done in SQL via strftime/CASE)
  - NULL coalescing (done in SQL via COALESCE)
- Verify output equivalence despite the tier change
- Traces: FSD Section 2, Section 5, Section 8 AP3

#### AP4 (Unused Columns) -- ELIMINATED
- Verify V2 column lists are trimmed to only used columns:
  - `compliance_events`: `[customer_id]` only (removed: `event_id`, `event_type`, `status`)
  - `wire_transfers`: `[customer_id]` only (removed: `wire_id`, `amount`, `direction`)
  - `accounts`: `[customer_id, current_balance]` (removed: `account_id`)
  - `customers`: `[id, first_name, last_name]` (no change -- all used)
- Verify removal has no effect on output
- Traces: BRD Source Tables; FSD Section 3, Section 8 AP4

#### AP6 (Row-by-Row Iteration) -- ELIMINATED
- Verify V2 uses SQL `GROUP BY` + `COUNT(*)` / `SUM()` instead of V1's `foreach` loops with Dictionary accumulation
- V1 iterates compliance events (lines 46-56), wire transfers (lines 58-68), and accounts (lines 72-89) with manual counting/summing
- V2 replaces with `comp_agg`, `wire_agg`, `acct_agg` CTEs using GROUP BY
- Verify aggregation values match V1 exactly
- Traces: BRD BR-3, BR-7; FSD Section 4, Section 8 AP6

#### AP7 (Magic Values) -- ELIMINATED
- Verify V2 External module uses named constants instead of magic numbers:
  - `ComplianceWeight = 30.0m` (was `30.0m` inline)
  - `WireWeight = 20.0m` (was `20.0m` inline)
  - `BalanceDivisor = 10000.0m` (was `10000.0m` inline)
- Verify constant names are descriptive and have comments explaining business meaning
- Verify output values are unchanged
- Traces: BRD BR-4; FSD Section 5, Section 8 AP7

#### AP2 (Duplicated Logic) -- DOCUMENTED, NOT FIXABLE
- Verify FSD documents the overlap with `customer_compliance_risk` job
- Cannot fix cross-job duplication within single-job scope
- Note: the two jobs have different formulas (this job uses decimal arithmetic and includes balance/10000; customer_compliance_risk uses double arithmetic and different weights)
- Traces: FSD Section 8 AP2

### TC-6: Edge Cases

#### TC-6a: Weekend Fallback (W2)
- Saturday effective dates: target date shifts back 1 day to Friday (BR-1)
- Sunday effective dates: target date shifts back 2 days to Friday (BR-1)
- Verify: SQL `strftime('%w', eff_date)` returns 6 for Saturday, 0 for Sunday
- Verify: `date(eff_date, '-1 day')` and `date(eff_date, '-2 days')` produce correct Friday dates
- Verify: output `as_of` column reflects the shifted target date (BR-10)
- Traces: BRD BR-1; FSD Section 4 Design Note #1, Section 7 W2

#### TC-6b: Customer Date Filter with Fallback (BR-2)
- Primary: only customers whose `as_of` matches target date (after weekend fallback) are included
- Fallback: if no customers match target date, ALL customer rows are used
- Verify: on weekday runs, customers are filtered to target date
- Verify: on weekend runs (Saturday/Sunday), DataSourcing returns weekend snapshot; customer filter finds no matches for Friday's target date; fallback uses all rows
- Verify: fallback could produce duplicate customer entries (one per as_of date) if multiple as_of dates present
- Traces: BRD BR-2, Edge Case #1, #4; FSD Section 4 Design Note #2

#### TC-6c: Unfiltered Aggregations (Cross-Date Inflation)
- Compliance events, wire transfers, and accounts are counted/summed across ALL rows in their DataFrames (BR-3)
- No date filter is applied within the aggregation CTEs
- If DataSourcing returns data for multiple as_of dates, counts may be inflated compared to single-date values
- Verify: V2 SQL aggregations have no WHERE filter on as_of (matching V1 behavior)
- Verify: inflated counts match V1 exactly
- Traces: BRD BR-3, Edge Case #2; FSD Section 4 Design Note #3

#### TC-6d: Empty Customers DataFrame (BR-9)
- If customers DataFrame is null or empty, empty output is produced
- Risk: Transformation module skips table registration for empty DataFrames, so the SQL would fail (FSD Open Question #3)
- Verify: V2 handles this gracefully (External module empty-input guard or pre-Transformation check)
- Verify: output is an empty DataFrame with correct columns
- Traces: BRD BR-9, Edge Case #3; FSD Section 5 pseudocode, Section 12 Open Question #3

#### TC-6e: Customers with Zero Activity
- Customers with no compliance events, wires, or accounts get counts of 0 and exposure_score of 0.00 (BR-8)
- Verify: LEFT JOIN produces NULL for missing aggregations, COALESCE converts to 0
- Verify: exposure_score formula with all-zero inputs = `(0 * 30) + (0 * 20) + (0 / 10000)` = 0.00
- Traces: BRD BR-8; FSD Section 4

#### TC-6f: NULL Name Coalescing (BR-11)
- NULL `first_name` or `last_name` values are coalesced to empty string `""`
- Verify: SQL COALESCE(fc.first_name, '') and COALESCE(fc.last_name, '') handle NULL correctly
- Verify: output matches V1's `?.ToString() ?? ""` behavior
- Traces: BRD BR-11; FSD Section 4 Design Note #6

#### TC-6g: Overwrite Mode Data Loss
- In auto-advance mode, each run replaces the Parquet directory entirely
- Only the last effective date's output persists on disk
- Verify behavior matches V1
- Traces: BRD Write Mode Implications; FSD Section 6

#### TC-6h: Row Ordering
- V1 iterates customers in DataFrame order (DataSourcing retrieval order from PostgreSQL)
- V2's SQL output follows SQLite's scan order from the CTE
- These should match (both follow insertion order), but may diverge
- If Proofmark reports row order mismatches, add `ORDER BY customer_id` to the SQL
- Traces: FSD Section 9 Proofmark Config, Section 12 Open Question #4

### TC-7: Proofmark Configuration
- **comparison_target**: `regulatory_exposure_summary`
- **reader**: `parquet`
- **threshold**: `100.0` (strict -- all output columns deterministic)
- **excluded columns**: None
- **fuzzy columns**: None
- Rationale: All 9 output columns are deterministic. V1 uses `decimal` arithmetic (not `double`), so there are no W6 floating-point epsilon concerns. The V2 External module replicates the same decimal arithmetic. Both `total_balance` and `exposure_score` should be byte-identical between V1 and V2. No tolerance needed.
- Traces: FSD Section 9

### TC-8: Exposure Score Formula Verification
- **Formula (BR-4):** `(compliance_events * 30) + (wire_count * 20) + (total_balance / 10000)`
- **Arithmetic precision (BR-5):** Uses `decimal` arithmetic (not `double`) -- `30.0m`, `20.0m`, `10000.0m` literals
- **Rounding (BR-5, BR-6):** Result rounded to 2 decimal places with banker's rounding (`MidpointRounding.ToEven`)
- Verify with sample calculations:
  - Customer with 2 compliance events, 3 wires, balance $50,000.00:
    - `(2 * 30) + (3 * 20) + (50000.00 / 10000)` = `60 + 60 + 5.00` = `125.00`
  - Customer with 0 events, 0 wires, 0 balance:
    - `(0 * 30) + (0 * 20) + (0 / 10000)` = `0.00`
  - Customer with 1 event, 1 wire, balance $12,345.67:
    - `(1 * 30) + (1 * 20) + (12345.67 / 10000)` = `30 + 20 + 1.234567` = `51.234567` -> rounded to `51.23`
  - Customer with balance that triggers banker's rounding:
    - Balance = $55,000.00: `55000.00 / 10000 = 5.50`, combined with integer components could produce midpoint -- verify banker's rounding applies correctly
- Verify V2 External module produces identical results to V1 for all customers in test data
- Traces: BRD BR-4, BR-5, BR-6; FSD Section 5

## W-Code Test Cases

### TC-W1: W2 -- Weekend Fallback
- **What the wrinkle is:** V1 shifts Saturday's effective date back 1 day (to Friday) and Sunday's back 2 days (to Friday) for the customer date filter and output `as_of`.
- **How V2 handles it:** SQL CASE expression using `strftime('%w', eff_date)` in the `target` CTE computes the shifted date. The `target_date` is passed through to the External module for the output `as_of` column.
- **What to verify:**
  1. V2 SQL `target` CTE correctly maps Saturday (6) to -1 day and Sunday (0) to -2 days
  2. The customer filter CTE uses `target_date` for filtering
  3. Output `as_of` on a Saturday run equals the prior Friday's date
  4. Output `as_of` on a Sunday run equals the prior Friday's date
  5. Output `as_of` on a weekday run equals the effective date unchanged
  6. Customer date filter fallback activates correctly when no customers match the shifted date
- Traces: BRD BR-1; FSD Section 7 W2

### TC-W2: W5 -- Banker's Rounding (Decimal Arithmetic)
- **What the wrinkle is:** V1 uses `Math.Round(decimal, 2)` which defaults to `MidpointRounding.ToEven`. Applied to both `total_balance` (line 114) and `exposure_score` (line 106). Uses decimal arithmetic throughout (not double).
- **How V2 handles it:** V2 External module uses `Math.Round(value, 2)` on `decimal` values, replicating V1's banker's rounding exactly. The SQL handles aggregation in REAL, but the External module receives the pre-aggregated values and applies decimal rounding.
- **What to verify:**
  1. `total_balance` is rounded to 2 decimal places with banker's rounding in the External module
  2. `exposure_score` is rounded to 2 decimal places with banker's rounding in the External module
  3. Decimal arithmetic is used for the exposure formula (not double)
  4. Midpoint values round to even: e.g., 2.345 -> 2.34, 2.355 -> 2.36, 2.5 -> 2 (if at 0dp)
  5. V2 values match V1 values exactly for all customers in test data
- Traces: BRD BR-5, BR-6; FSD Section 7 W5

## Risk Items to Monitor During Phase D

1. **SQLite REAL vs decimal precision on balance SUM (FSD Open Question #1):** The `raw_total_balance` computed in SQL uses SQLite REAL (double) for SUM. V1 uses C# decimal accumulation. If Proofmark detects mismatches on `total_balance` or `exposure_score`, the External module can be extended to re-sum balances from the raw `accounts` DataFrame using decimal accumulation.

2. **Empty customers DataFrame and SQLite table registration (FSD Open Question #3):** If customers is empty, the Transformation module skips registration, and the SQL will fail because the `customers` table won't exist. Monitor during Phase D testing; may need pre-Transformation empty guard.

3. **Row ordering (FSD Open Question #4):** V1 and V2 may produce rows in different order. If Proofmark comparison fails on row order, add `ORDER BY customer_id` to the SQL.

4. **SQLite `strftime('%w')` mapping (FSD Risk Register):** Verify that `strftime('%w')` returns 0 for Sunday and 6 for Saturday in the actual Microsoft.Data.Sqlite implementation.

5. **SQLite type coercion (FSD Risk Register):** Verify COALESCE with integer 0 preserves INTEGER type in Parquet output. If type mismatches occur, adjust COALESCE defaults or add CAST.

## Notes
- This is a Tier 2 job. The SQL handles all aggregation, joining, filtering, and NULL coalescing. The External module handles ONLY decimal arithmetic, banker's rounding, and as_of assignment.
- V1 uses `decimal` arithmetic (not `double`), so W6 (double epsilon) does NOT apply to the final output. However, the SQL intermediate values use REAL (double), which is a known risk (see Risk Items above).
- The `firstEffectiveDate` is `2024-10-01`. Verify this matches V1 config.
- AP7 magic values are eliminated in the External module via named constants (`ComplianceWeight`, `WireWeight`, `BalanceDivisor`). Output values are unchanged.
- The customer date filter fallback (BR-2) is a critical behavior: if no customers match the target date, ALL customer rows are used. This could cause duplicate customer entries in the output if multiple as_of dates are present.
