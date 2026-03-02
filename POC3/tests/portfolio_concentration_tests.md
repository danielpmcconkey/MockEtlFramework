# PortfolioConcentration — V2 Test Plan

## Job Info
- **V2 Config**: `portfolio_concentration_v2.json`
- **Tier**: 2 (Framework + Minimal External / SCALPEL)
- **External Module**: `PortfolioConcentrationV2Processor` (minimal: type coercion long->int + W4 integer division)

## Pre-Conditions
1. PostgreSQL is accessible at `172.18.0.1` with `datalake.holdings` and `datalake.securities` populated.
2. The V1 baseline output exists at `Output/curated/portfolio_concentration/` (Parquet).
3. The V2 External module (`PortfolioConcentrationV2Processor`) is compiled and accessible at the assembly path.
4. The effective date range includes at least one date with holdings and securities data (e.g., 2024-10-01 through 2024-10-15).
5. The V2 job config sources only `holdings` (4 columns) and `securities` (2 columns). No `investments` DataSourcing entry exists (AP1 eliminated).

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01 | Output Schema | Output columns and Parquet types match V1 exactly |
| TC-02 | BR-1 through BR-5 | Row count equivalence between V1 and V2 output |
| TC-03 | BR-1 through BR-7, BR-10 | Data content equivalence via Proofmark |
| TC-04 | Writer Config | Parquet output: numParts=1, Overwrite mode, correct output path |
| TC-05 | AP1, AP3, AP4, AP6, AP7 | Anti-pattern elimination verification |
| TC-06 | Edge Cases | Empty input, division by zero, NULL sectors, cross-date aggregation |
| TC-07 | FSD Section 8 | Proofmark configuration correctness |
| TC-W4 | W4 | Integer division produces sector_pct = 0 (or 1) |
| TC-W6 | W6 | Double-precision arithmetic for sector_value and total_value |

## Test Cases

### TC-01: Output Schema Validation
- **Traces to:** BRD Output Schema, FSD Section 11 (Output Type Map)
- **Input conditions:** Standard job run for any date with holdings and securities data.
- **Expected output:** The output Parquet file contains exactly these columns in this order with the correct Parquet types:

| Column | CLR Type | Parquet Type |
|--------|----------|-------------|
| customer_id | `int` | INT32 |
| investment_id | `int` | INT32 |
| sector | `string` | STRING |
| sector_value | `double` | DOUBLE |
| total_value | `double` | DOUBLE |
| sector_pct | `decimal` | DECIMAL |
| as_of | `DateOnly` | DATE |

- **Verification method:**
  - Read the V2 Parquet output schema using a Parquet inspection tool or by loading in code.
  - Confirm column names, order, and types match V1 exactly.
  - Pay special attention to `customer_id` and `investment_id` being INT32 (not INT64) -- the V2 External module must coerce `long` to `int` for this. If these are INT64, the External module's type coercion is broken.
  - Confirm `sector_pct` is DECIMAL (not INT64 or DOUBLE) -- the External module must cast the integer division result to `decimal`.

### TC-02: Row Count Equivalence
- **Traces to:** BR-1, BR-3
- **Input conditions:** Run V2 for the same effective date range as V1.
- **Expected output:** The number of rows in V2 output matches V1 exactly. Each row represents a unique `(customer_id, investment_id, sector)` tuple.
- **Verification method:**
  - Count rows in both V1 and V2 Parquet output files.
  - The counts must be identical.
  - Cross-reference with: `SELECT COUNT(DISTINCT (h.customer_id, h.investment_id, COALESCE(s.sector, 'Unknown'))) FROM datalake.holdings h LEFT JOIN datalake.securities s ON h.security_id = s.security_id AND h.as_of = s.as_of WHERE h.as_of = '{date}'` to confirm the expected number of unique tuples.

### TC-03: Data Content Equivalence
- **Traces to:** BR-1 through BR-7, BR-10
- **Input conditions:** Run V2 for the same effective date range as V1.
- **Expected output:** Every row in V2 matches V1 (subject to W6 fuzzy tolerance on `sector_value` and `total_value`).
  - `customer_id`: integer matching V1's `Convert.ToInt32`.
  - `investment_id`: integer matching V1's `Convert.ToInt32`.
  - `sector`: string, with NULL/missing sectors defaulting to `"Unknown"` (BR-10).
  - `sector_value`: double-precision sum per (customer, investment, sector) -- may differ at epsilon level (W6).
  - `total_value`: double-precision sum per customer -- may differ at epsilon level (W6).
  - `sector_pct`: decimal, always 0 (or 1 in edge case) due to W4 integer division.
  - `as_of`: DateOnly from `__maxEffectiveDate`.
- **Verification method:**
  - Run Proofmark with the config from FSD Section 8.
  - Proofmark must report 100.0% pass rate.
  - `sector_value` and `total_value` use FUZZY comparison with tolerance 1e-10 (absolute) to accommodate potential double accumulation order differences.
  - All other columns use STRICT comparison.

### TC-04: Writer Configuration
- **Traces to:** BRD Writer Configuration, FSD Section 5
- **Input conditions:** Standard job run producing output.
- **Expected output:**
  - **Output format:** Parquet
  - **Output path:** `Output/double_secret_curated/portfolio_concentration/`
  - **numParts:** 1 (single Parquet part file)
  - **writeMode:** Overwrite -- running the job twice replaces the entire output directory.
- **Verification method:**
  - Verify the output directory exists at the expected path.
  - Verify exactly 1 Parquet part file exists in the directory.
  - Run the job twice for different effective dates. After the second run, confirm only the second date's data exists in the output (Overwrite behavior). The `as_of` column should show only the second date.

### TC-05: Anti-Pattern Elimination Verification
- **Traces to:** FSD Section 7

#### TC-05a: AP1 -- Dead-End Sourcing Eliminated
- **V1 problem:** `datalake.investments` is sourced with columns `[investment_id, customer_id, account_type, current_value]` but the External module never accesses `sharedState["investments"]`. The variable is assigned on line 18 but never used.
- **V2 expectation:** The V2 job config has NO DataSourcing entry for `investments`. Only `holdings` and `securities` are sourced.
- **Verification method:** Read `portfolio_concentration_v2.json`. Confirm no module with `"table": "investments"` exists. Confirm only two DataSourcing modules are present (holdings and securities).

#### TC-05b: AP3 -- Unnecessary External Module Partially Eliminated
- **V1 problem:** The entire pipeline -- sector lookup dictionary build, double-pass row iteration, aggregation, and output row construction -- is in a single monolithic External module (`PortfolioConcentrationCalculator`).
- **V2 expectation:** Business logic (JOIN, GROUP BY, SUM) is in the SQL Transformation. The External module handles ONLY type coercion (`long` -> `int`) and W4 integer division. Zero business logic (no joins, no grouping, no aggregation) in the External.
- **Verification method:** Inspect the V2 External module source code. Confirm it contains no dictionary construction for sector lookup, no `SUM` accumulation, no multi-pass iteration. Confirm it reads a pre-aggregated DataFrame (`sector_agg`) and only applies type casts and W4 division.

#### TC-05c: AP4 -- Unused Columns Eliminated
- **V1 problem:** `holdings` sources 6 columns (`holding_id, investment_id, security_id, customer_id, quantity, current_value`); only 4 are used. `securities` sources 5 columns (`security_id, ticker, security_name, security_type, sector`); only 2 are used.
- **V2 expectation:**
  - `holdings`: `["customer_id", "investment_id", "security_id", "current_value"]` (4 columns, removed `holding_id` and `quantity`).
  - `securities`: `["security_id", "sector"]` (2 columns, removed `ticker`, `security_name`, `security_type`).
- **Verification method:** Read `portfolio_concentration_v2.json`. Confirm the exact column lists for both DataSourcing entries match the above.

#### TC-05d: AP6 -- Row-by-Row Iteration Eliminated
- **V1 problem:** Three separate `foreach` loops: (1) build sector lookup dictionary from securities rows, (2) iterate holdings to accumulate customer totals, (3) iterate holdings again to accumulate sector values.
- **V2 expectation:** All three operations replaced by a single SQL statement: LEFT JOIN for sector lookup, subquery for customer totals, GROUP BY for sector aggregation.
- **Verification method:** Confirm the V2 Transformation SQL contains `LEFT JOIN`, subquery with `SUM/GROUP BY` for customer totals, and outer `GROUP BY` for sector aggregation. Confirm the V2 External module does NOT contain `foreach` loops over raw holdings/securities rows for aggregation purposes.

#### TC-05e: AP7 -- Magic Values Addressed
- **V1 problem:** `"Unknown"` is hardcoded as the default sector string with no named constant.
- **V2 expectation:** In SQL, `COALESCE(s.sector, 'Unknown')` is idiomatic. In the External module, a named constant `DefaultSector = "Unknown"` is defined.
- **Verification method:** Inspect the V2 External module source. Confirm a named constant exists for the `"Unknown"` default sector string.

### TC-06: Edge Cases

#### TC-06a: Empty Input -- Holdings (BR-6, BRD Edge Case)
- **Input conditions:** Run for an effective date where `datalake.holdings` has zero rows.
- **Expected output:** The output Parquet file contains zero data rows. The V2 External module returns an empty DataFrame with the correct output column names.
- **Verification method:** If such a date exists, run for it and verify zero-row output. Otherwise, verify by code inspection that the External module handles `sector_agg` with zero rows correctly (stores empty DataFrame as `"output"`).

#### TC-06b: Empty Input -- Securities (BR-6, BRD Edge Case)
- **Input conditions:** Run for an effective date where `datalake.securities` has zero rows but `datalake.holdings` has data.
- **Expected output:** V1 returns an empty DataFrame when securities is null/empty (guard clause at line 20-24). V2's SQL uses LEFT JOIN, so rows would still be produced with `sector = 'Unknown'`. This is a potential behavioral divergence.
- **Verification method:** Verify that the V2 External module checks for empty `sector_agg` (which would be empty if the SQL Transformation produces zero rows due to upstream empty tables). If the SQL produces rows despite empty securities (because of LEFT JOIN), compare against V1's empty-output behavior. If divergence exists, the External module may need a guard clause matching V1's `securities.Count == 0` check.

#### TC-06c: Division by Zero (BRD Edge Case 3, FSD OQ-3)
- **Input conditions:** A customer whose `total_value`, when cast to `int`, is 0. This would happen if ALL of that customer's holdings have `current_value` between 0.0 and 1.0 (so `(int)totalValue == 0`).
- **Expected output:** Both V1 and V2 throw `DivideByZeroException`. This is identical behavior -- V2 replicates V1's bug.
- **Verification method:** Query `SELECT customer_id, SUM(current_value) FROM datalake.holdings WHERE as_of = '{date}' GROUP BY customer_id HAVING SUM(current_value) < 1.0` to check whether any customer's total truncates to 0. If none exist in test data, this is a theoretical edge case. If any exist, both V1 and V2 should fail identically.

#### TC-06d: NULL Sector Handling (BR-3, BR-10, BRD Edge Case 4)
- **Input conditions:** A holding whose `security_id` does not exist in `datalake.securities`, or a securities row with a NULL `sector` value.
- **Expected output:** The output row has `sector = "Unknown"`. V2's SQL `COALESCE(s.sector, 'Unknown')` handles both cases: no matching securities row (NULL from LEFT JOIN) and matching row with NULL sector.
- **Verification method:** Query for holdings with unmatched security_ids or securities with NULL sectors. Verify the corresponding output rows have `sector = "Unknown"`. Compare against V1 output to confirm identical behavior.

#### TC-06e: Cross-Date Aggregation (BRD Edge Case 6, FSD OQ-4)
- **Input conditions:** Effective date range spanning multiple days. A security_id that has different `sector` values on different as_of dates.
- **Expected output:** For single-day auto-advance (the standard execution pattern), V1 and V2 produce identical results because each day is processed independently. For multi-day ranges, V2's SQL joins on `security_id AND as_of` (keeping each day's sector mapping), while V1's dictionary overwrites per security_id (keeping the last-seen sector). This could diverge.
- **Verification method:** This is a theoretical concern. Verify that auto-advance processes one day at a time (min = max effective date). If so, cross-date aggregation divergence is impossible. Query `SELECT security_id, COUNT(DISTINCT sector) FROM datalake.securities GROUP BY security_id HAVING COUNT(DISTINCT sector) > 1` to check if any security changes sectors across dates.

#### TC-06f: Overwrite Multi-Day Behavior
- **Input conditions:** Run auto-advance over a multi-day range (e.g., 2024-10-01 through 2024-10-03).
- **Expected output:** After all days process, the output Parquet contains ONLY the final day's data. Prior days are overwritten. The `as_of` column shows only the last effective date.
- **Verification method:** Run auto-advance. After completion, read the Parquet output and verify the `as_of` column has a single distinct value matching the last date in the range.

### TC-07: Proofmark Configuration
- **Traces to:** FSD Section 8
- **Input conditions:** Proofmark config for this job.
- **Expected output:** Config specifies:
  - `comparison_target: "portfolio_concentration"`
  - `reader: parquet`
  - `threshold: 100.0`
  - No EXCLUDED columns
  - FUZZY on `sector_value`: tolerance 0.0000000001, tolerance_type absolute
  - FUZZY on `total_value`: tolerance 0.0000000001, tolerance_type absolute
  - `customer_id`, `investment_id`, `sector`, `sector_pct`, `as_of` are all STRICT
- **Verification method:** Read the Proofmark YAML config at `POC3/proofmark_configs/portfolio_concentration.yaml` and verify all fields match the FSD's Proofmark config. Confirm fuzzy tolerances and reasons are present for both `sector_value` and `total_value`. Confirm `sector_pct` is NOT fuzzy (integer division always produces exact 0 or 1).

## W-Code Test Cases

### TC-W4: Integer Division (sector_pct)
- **Traces to:** W4 (FSD Section 6), BR-5
- **Wrinkle:** V1 computes `sector_pct` via `int sectorInt = (int)sectorValue; int totalInt = (int)totalValue; decimal sectorPct = (decimal)(sectorInt / totalInt);`. This truncates the double values to 32-bit integers BEFORE division, then performs integer division (C# `int / int` = `int`), which produces 0 for any case where `sectorInt < totalInt`. The result is then cast to `decimal`.
- **Input conditions:** Run for a date with diverse holdings data.
- **Expected output:**
  - For all rows where a customer has multiple sectors: `sector_pct = 0` (because `sectorInt < totalInt`, so integer division produces 0).
  - For a customer with only one sector AND `sectorInt == totalInt`: `sector_pct = 1`.
  - Verify that V2 uses the same `(int)doubleValue` cast pattern (truncate toward zero), NOT `Math.Truncate((decimal)numerator / denominator)` -- the FSD explains why the exact V1 pattern is needed here.
- **Verification method:**
  - Read V2 output `sector_pct` column. Confirm all values are 0 or 1.
  - Compare against V1 output `sector_pct` column -- they must be identical.
  - Inspect the V2 External module source to confirm it uses `(int)sectorValue` and `(int)totalValue` followed by `(decimal)(sectorInt / totalInt)`, not the KNOWN_ANTI_PATTERNS.md generic W4 prescription.
  - Run a spot check: pick a row, manually compute `(int)sectorValue / (int)totalValue`, and verify it matches the output.

### TC-W6: Double-Precision Arithmetic (sector_value, total_value)
- **Traces to:** W6 (FSD Section 6), BR-4
- **Wrinkle:** V1 accumulates `current_value` as `double` (not `decimal`), introducing floating-point epsilon errors. V2's SQL Transformation accumulates via `SUM()` on SQLite REAL columns (also double-precision), replicating the same class of errors. However, the accumulation ORDER may differ between V1's `foreach` iteration and SQLite's internal SUM algorithm, potentially producing epsilon-level differences.
- **Input conditions:** Run for a date with enough holdings data that double accumulation produces visible epsilon artifacts (e.g., values like `15234.720000000001` instead of `15234.72`).
- **Expected output:**
  - `sector_value` and `total_value` are double-precision values with potential epsilon artifacts, matching V1's output within the fuzzy tolerance of 1e-10.
  - Values should NOT be rounded or truncated to exact decimal -- the epsilon errors are part of the V1 output and must be replicated.
- **Verification method:**
  - Run Proofmark with FUZZY tolerance on `sector_value` and `total_value` (1e-10 absolute).
  - Proofmark must report 100.0% pass rate.
  - If any rows exceed the tolerance, investigate whether the divergence is due to accumulation order differences. If so, consider moving aggregation to the External module to exactly replicate V1's iteration order.
  - Spot-check a few values: pick a `(customer_id, investment_id, sector)` tuple, manually sum the `current_value` as `double` for the matching holdings, and compare against the output `sector_value`.

## Notes
- **Parquet type fidelity is critical.** The V2 External module exists primarily to ensure `customer_id` and `investment_id` are `int` (Parquet INT32), not `long` (Parquet INT64), and that `sector_pct` is `decimal` (Parquet DECIMAL), not `long`. If these types are wrong, Proofmark schema comparison will fail even if the numerical values match.
- **The W4 prescription in KNOWN_ANTI_PATTERNS.md does not apply directly.** The generic W4 fix says to use `Math.Truncate((decimal)numerator / denominator)`, but this assumes proper decimal division followed by truncation. In this job, the V1 bug is more severe: truncation happens BEFORE division (losing fractional monetary values), then integer division produces 0. The FSD correctly identifies that the exact V1 pattern `(int)sectorValue / (int)totalValue` must be replicated.
- **Row ordering may differ.** V1's row order depends on `Dictionary<(int, int, string), double>` iteration order (insertion order in .NET). V2 uses `ORDER BY customer_id, investment_id, sector` in SQL. Proofmark likely compares Parquet data values regardless of physical row order. If row order matters for Proofmark, the External module may need to replicate V1's insertion-order iteration. Per FSD OQ-1, confidence is MEDIUM that row order does not matter.
- **`investments` DataSourcing is completely removed.** V1's line 18 assigns `sharedState["investments"]` but never uses it. The V2 External module does not reference investments at all. Any test that checks shared state keys should confirm `investments` is NOT present (it was never sourced).
- **Guard clause divergence risk.** V1's guard clause checks `holdings == null || holdings.Count == 0 || securities == null || securities.Count == 0` and returns empty. V2's SQL uses LEFT JOIN, which could produce rows even when securities is empty (all sectors would be "Unknown"). If this behavioral difference matters, the V2 External module may need to add a guard clause that checks securities emptiness. See TC-06b.
