# PortfolioValueSummary -- V2 Test Plan

## Job Info
- **V2 Config**: `portfolio_value_summary_v2.json`
- **Tier**: 2 (Framework + Minimal External)
- **External Module**: `ExternalModules.PortfolioValueSummaryV2Processor`
- **Writer**: ParquetFileWriter (numParts=1, Overwrite)

## Pre-Conditions
- Source tables available in `datalake` schema:
  - `holdings` with columns: `customer_id`, `current_value`, `as_of` (auto-appended)
  - `customers` with columns: `id`, `first_name`, `last_name`, `as_of` (auto-appended)
- Effective date range injected by executor (`__minEffectiveDate`, `__maxEffectiveDate`)
- V1 baseline output available at `Output/curated/portfolio_value_summary/`

## Test Cases

### TC-1: Output Schema Validation
- **Requirement**: BR-3, BR-6, FSD Section 6
- **Expected columns (exact order):** `customer_id`, `first_name`, `last_name`, `total_portfolio_value`, `holding_count`, `as_of`
- **Expected types:**
  - `customer_id`: int
  - `first_name`: string
  - `last_name`: string
  - `total_portfolio_value`: decimal (rounded to 2 dp)
  - `holding_count`: int
  - `as_of`: DateOnly
- Verify column order matches FSD Section 6 (evidence: [PortfolioValueCalculator.cs:10-14])
- Verify no extra columns are present (e.g., no `holding_id`, `investment_id`, `security_id`, `quantity`)

### TC-2: Row Count Equivalence
- **Requirement**: BR-3, BR-2
- V1 and V2 must produce identical row counts for each effective date
- One row per unique `customer_id` with holdings on the target date
- Run both V1 and V2 for the full date range (2024-10-01 through 2024-12-31) and compare Parquet row counts
- Verify that removing the `investments` DataSourcing (AP1) and unused holdings columns (AP4) does not affect row count

### TC-3: Data Content Equivalence
- **Requirement**: BR-3, BR-4, BR-6, BR-7
- All values must be byte-identical to V1 output
- Compare V2 Parquet at `Output/double_secret_curated/portfolio_value_summary/` against V1 at `Output/curated/portfolio_value_summary/`
- Verify `customer_id` values match exactly (integer comparison)
- Verify `first_name` and `last_name` values match exactly (string comparison, case-sensitive)
- Verify `total_portfolio_value` values match exactly (decimal, rounded to 2 dp via `Math.Round(totalValue, 2)`)
- Verify `holding_count` values match exactly (integer)
- Verify `as_of` values match exactly (weekend-adjusted target date)

### TC-4: Writer Configuration
- **Requirement**: FSD Section 5
- **type**: ParquetFileWriter
- **source**: `output` (matches External module's shared state key)
- **numParts**: 1 (single Parquet part file)
- **writeMode**: Overwrite (directory replaced on each run)
- **outputDirectory**: `Output/double_secret_curated/portfolio_value_summary/` (V2 convention)
- Verify exactly one `.parquet` part file is written per run
- Verify Overwrite mode replaces directory contents on re-run

### TC-5: Anti-Pattern Elimination Verification

#### AP1 (Dead-end sourcing -- investments) -- ELIMINATED
- **Requirement**: BR-8, FSD Section 3 "Table REMOVED: investments"
- Verify V2 config does NOT contain a DataSourcing entry for `investments`
- Verify V1 config DOES contain `investments` DataSourcing (confirming V1 had the anti-pattern)
- Verify removal has no effect on output (investments DataFrame was never accessed by the External module)
- Evidence: [PortfolioValueCalculator.cs] never references `sharedState["investments"]`

#### AP3 (Unnecessary External) -- PARTIALLY ELIMINATED
- **Requirement**: FSD Section 2 Tier Justification
- Verify V2 uses Tier 2 (DataSourcing + External + Writer), not Tier 3 (External does everything)
- Verify DataSourcing handles all data retrieval (holdings and customers)
- Verify ParquetFileWriter handles output (External does not write files directly)
- Verify External module handles ONLY: weekend fallback (W2), empty-input guard, date filtering, aggregation, and customer name lookup

#### AP4 (Unused columns -- holdings) -- ELIMINATED
- **Requirement**: FSD Section 3 "Column rationale"
- Verify V2 `holdings` DataSourcing columns are `["customer_id", "current_value"]` only
- Verify `holding_id`, `investment_id`, `security_id`, `quantity` are NOT in V2's column list
- Verify removal has no effect on output (only `customer_id`, `current_value`, and `as_of` were ever accessed)
- Evidence: [PortfolioValueCalculator.cs:31-33,50-51]

#### AP6 (Row-by-row iteration) -- PARTIALLY ADDRESSED
- **Requirement**: FSD Section 8 AP6
- Verify External module uses clean Dictionary-based aggregation pattern (no nested loops, no repeated lookups)
- Full SQL elimination blocked by W2 requirement -- document this is acceptable

### TC-6: Edge Cases

#### TC-6a: Saturday Effective Date (Weekend Fallback)
- **Requirement**: BR-1, FSD Section 4 Wrinkle W2
- When `__maxEffectiveDate` is a Saturday, target date should be Friday (maxDate - 1)
- Verify holdings are filtered to Friday's date
- Verify output `as_of` column contains Friday's date, not Saturday's
- Evidence: [PortfolioValueCalculator.cs:28]

#### TC-6b: Sunday Effective Date (Weekend Fallback)
- **Requirement**: BR-1, FSD Section 4 Wrinkle W2
- When `__maxEffectiveDate` is a Sunday, target date should be Friday (maxDate - 2)
- Verify holdings are filtered to Friday's date
- Verify output `as_of` column contains Friday's date, not Sunday's
- Evidence: [PortfolioValueCalculator.cs:29]

#### TC-6c: Weekday Effective Date (No Fallback)
- **Requirement**: BR-1
- When `__maxEffectiveDate` is a weekday, target date should be used as-is
- Verify no date adjustment occurs

#### TC-6d: Empty Holdings DataFrame
- **Requirement**: BR-5, FSD Section 4 External Module Design step 5
- If `holdings` is null or has zero rows, output should be an empty DataFrame with correct schema
- Verify output has zero rows but all six expected columns

#### TC-6e: Empty Customers DataFrame
- **Requirement**: BR-5, FSD Section 4 External Module Design step 5
- If `customers` is null or has zero rows, output should be an empty DataFrame
- Note: this is V1 behavior -- even if holdings exist, empty customers produces empty output (not a LEFT JOIN)

#### TC-6f: Holdings With No Matching Customer
- **Requirement**: BR-6, FSD Section 4 External Module Design steps 7, 9
- A customer_id present in holdings but not in customers should still appear in output
- `first_name` and `last_name` default to empty strings (`""`)
- Verify the holding's value is included in `total_portfolio_value` aggregation
- Evidence: [PortfolioValueCalculator.cs:66-68]

#### TC-6g: Customer With No Holdings on Target Date
- **Requirement**: BR-2
- A customer present in `customers` but with no holdings for `targetDate` should NOT appear in output
- Iteration is driven by filtered holdings, not customers
- Evidence: [PortfolioValueCalculator.cs:47]

#### TC-6h: No Holdings for Target Date After Weekend Fallback
- **Requirement**: BR-1 edge case (holiday scenario)
- If Friday has no holdings data (e.g., holiday), filtered holdings is empty after fallback
- Output should be zero rows
- Evidence: BRD edge case #3

#### TC-6i: Multi-Day Range with Overwrite
- **Requirement**: FSD Section 5 writeMode, BRD Write Mode Implications
- In auto-advance mode, each run replaces the output directory
- Only the final effective date's output persists on disk
- Verify behavior matches V1

#### TC-6j: Rounding Behavior
- **Requirement**: BR-4, FSD Section 4 W5 MONITOR
- `total_portfolio_value` uses `Math.Round(totalValue, 2)` with default `MidpointRounding.ToEven` (Banker's rounding)
- Both V1 and V2 use the same call, so results should be identical
- If any midpoint value exists (e.g., x.xx5), both produce the same banker's-rounded result
- Verify no difference in rounding behavior between V1 and V2 output

### TC-7: Proofmark Configuration
- **Requirement**: FSD Section 9
- **comparison_target**: `portfolio_value_summary`
- **reader**: `parquet`
- **threshold**: `100.0` (strict -- all columns deterministic)
- **excluded columns**: None
- **fuzzy columns**: None
- Rationale: All output columns are deterministic. `total_portfolio_value` uses `decimal` arithmetic (not `double`), so W6 does not apply. No timestamps, UUIDs, or random values. BRD confirms "None identified" for non-deterministic fields.
- Expected Proofmark result: EXIT CODE 0 (PASS) with 100% match

### TC-8: Row Ordering
- **Requirement**: BR-10, FSD Section 4 note 6
- V1 iterates `Dictionary<int, ...>` producing rows in insertion order (order of first encounter of each `customer_id` in filtered holdings)
- V2 uses the same Dictionary iteration pattern
- If Proofmark comparison fails due to row ordering, investigate whether Parquet reader reorders rows
- Potential mitigation: add explicit sort by `customer_id` if ordering mismatch is confirmed

## W-Code Test Cases

### TC-W1: W2 -- Weekend Fallback
- **What the wrinkle is:** On Saturday, use Friday's data (maxDate - 1). On Sunday, use Friday's data (maxDate - 2). Weekdays use maxDate directly.
- **How V2 handles it:** External module checks `maxDate.DayOfWeek` and adjusts `targetDate`. Comment: `// W2: Weekend fallback -- use Friday's data on Sat/Sun`.
- **What to verify:**
  1. For a Saturday effective date, output `as_of` = Friday
  2. For a Sunday effective date, output `as_of` = Friday
  3. For a weekday effective date, output `as_of` = that weekday
  4. Holdings are filtered to the adjusted `targetDate`, not the raw `__maxEffectiveDate`
  5. V2 output matches V1 output for all weekend dates in the test range

### TC-W2: W5 -- Banker's Rounding (MONITOR)
- **What the wrinkle is:** `Math.Round(totalValue, 2)` uses default `MidpointRounding.ToEven`. Values at the exact midpoint (e.g., x.xx5) round to the nearest even number.
- **How V2 handles it:** Uses the identical `Math.Round(totalValue, 2)` call. Both V1 and V2 use C# default rounding.
- **What to verify:**
  1. V2 `total_portfolio_value` matches V1 exactly for every row
  2. If any midpoint values exist in the data, both V1 and V2 produce the same result
  3. No explicit `MidpointRounding` specification is needed since both use the default

## Notes
- This is a Tier 2 job. DataSourcing pulls `holdings` and `customers`, External module performs business logic, ParquetFileWriter handles output.
- The structural changes from V1 are: removing dead-end `investments` DataSourcing (AP1), removing unused columns from `holdings` (AP4), and moving from implicit Tier 3 to explicit Tier 2 (AP3 partial).
- The External module is justified because (a) W2 weekend fallback requires `__maxEffectiveDate` from shared state which SQL cannot access, and (b) `Transformation.RegisterTable` skips empty DataFrames, which would cause SQL failure on zero-data days.
- Row ordering (BR-10) is the highest-risk area for Proofmark comparison failure. If comparison fails, investigate row order first.
