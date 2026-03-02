# QuarterlyExecutiveKpis -- V2 Test Plan

## Job Info
- **V2 Config**: `quarterly_executive_kpis_v2.json`
- **Tier**: 1 (Framework Only)
- **Writer**: ParquetFileWriter
- **External Module**: None (AP3 fully eliminated)

## Pre-Conditions
- Source tables available in `datalake` schema:
  - `customers` with columns: `id`, `as_of`
  - `accounts` with columns: `account_id`, `current_balance`, `as_of`
  - `transactions` with columns: `transaction_id`, `amount`, `as_of`
  - `investments` with columns: `investment_id`, `current_value`, `as_of`
  - `compliance_events` with columns: `event_id`, `as_of`
- Effective date range injected by executor (`__minEffectiveDate`, `__maxEffectiveDate`)
- `as_of` column auto-appended by DataSourcing module
- V1 baseline output available at `Output/curated/quarterly_executive_kpis/`
- ExternalModules project builds successfully (`dotnet build`)

## Test Cases

### TC-1: Output Schema Validation
- **Expected columns (exact order):** `kpi_name`, `kpi_value`, `as_of`
- **Expected types:**
  - `kpi_name`: string
  - `kpi_value`: real/double (rounded to 2 decimal places)
  - `as_of`: date
- Verify exactly 8 data rows on weekdays (one per KPI), 0 data rows on weekends
- Verify KPI names appear in this exact fixed order (BR-5, FSD Section 4 SQL Design Note #2):
  1. `total_customers`
  2. `total_accounts`
  3. `total_balance`
  4. `total_transactions`
  5. `total_txn_amount`
  6. `total_investments`
  7. `total_investment_value`
  8. `compliance_events`

### TC-2: Row Count Equivalence
- V1 and V2 must produce identical row counts for the same effective date
- Weekday runs: 8 rows (one per KPI) (BR-5)
- Weekend runs: 0 rows (guard clause on empty customers fires) (BR-2)
- Run both V1 and V2 for the same effective date range and compare Parquet row counts
- Traces: BRD BR-2, BR-5; FSD Section 4

### TC-3: Data Content Equivalence
- All data values must match V1 output exactly
- Compare V2 Parquet at `Output/double_secret_curated/quarterly_executive_kpis/` against V1 baseline at `Output/curated/quarterly_executive_kpis/`
- Verify `kpi_name` strings match exactly (case-sensitive, no trailing whitespace)
- Verify `kpi_value` values match V1 to 2 decimal places
- Verify `as_of` dates match exactly
- Traces: BRD BR-5 through BR-9; FSD Section 4

### TC-4: Writer Configuration
- **type**: ParquetFileWriter (FSD Section 5)
- **source**: `output` (matches Transformation resultName)
- **outputDirectory**: `Output/double_secret_curated/quarterly_executive_kpis/` (V2 convention)
- **numParts**: `1` -- verify single part file in output directory
- **writeMode**: `Overwrite` -- verify directory is replaced on each run
- Traces: BRD Writer Configuration; FSD Section 5

### TC-5: Anti-Pattern Elimination Verification

#### AP3 (Unnecessary External Module) -- ELIMINATED
- Verify V2 config does NOT contain an External module entry
- Verify V2 uses Tier 1 chain: DataSourcing -> Transformation (SQL) -> ParquetFileWriter
- Verify all V1 business logic (counting, summing, unpivoting, weekend fallback) is expressed entirely in SQL
- Verify output equivalence despite the tier change
- Traces: FSD Section 2, Section 7 AP3

#### AP4 (Unused Columns) -- ELIMINATED
- Verify V2 column lists are trimmed to only used columns:
  - `customers`: `[id]` only (removed: `first_name`, `last_name` per BR-10)
  - `accounts`: `[account_id, current_balance]` (removed: `customer_id`)
  - `transactions`: `[transaction_id, amount]` (removed: `account_id`)
  - `investments`: `[investment_id, current_value]` (removed: `customer_id`)
  - `compliance_events`: `[event_id]` only (removed: `customer_id`, `event_type`, `status` per BR-9)
- Verify removal has no effect on output
- Traces: BRD BR-9, BR-10; FSD Section 3, Section 7 AP4

#### AP6 (Row-by-Row Iteration) -- ELIMINATED
- Verify V2 uses SQL `COUNT(*)` and `SUM(column)` instead of `foreach` loops
- Verify all 5 count KPIs use `COUNT(*)` (not DISTINCT) matching V1's `count++` pattern (BR-7)
- Verify all 3 sum KPIs use `SUM(column)` matching V1's accumulation pattern (BR-8)
- Verify output values are identical to V1's loop results
- Traces: BRD BR-7, BR-8; FSD Section 4 SQL Design Notes #3, #4

#### AP9 (Misleading Name) -- DOCUMENTED, NOT FIXABLE
- Verify V2 config retains the job name `QuarterlyExecutiveKpis` (cannot rename)
- Verify SQL and/or FSD contain documentation noting the misleading name
- Traces: BRD BR-3; FSD Section 7 AP9

#### AP2 (Duplicated Logic) -- DOCUMENTED, NOT FIXABLE
- Verify FSD documents the overlap with `executive_dashboard` job
- Cannot fix cross-job duplication within single-job scope
- Traces: BRD BR-4; FSD Section 7 AP2

### TC-6: Edge Cases

#### TC-6a: Weekend Guard Clause (Empty Customers)
- On weekends, `customers` table has no data in the datalake
- Expected: `WHERE EXISTS (SELECT 1 FROM customers)` evaluates to false for all 8 UNION ALL branches (BR-2)
- Result: 0 output rows -- empty Parquet file
- Verify: V2 Parquet output contains 0 data rows
- Verify: behavior matches V1 (V1 guard clause returns empty DataFrame when customers is null/empty)
- Traces: BRD BR-2, Edge Cases; FSD Section 4 SQL Design Note #1

#### TC-6b: Weekend Fallback (Dead Code Path)
- The weekend fallback logic (W2) shifts Saturday -> Friday, Sunday -> Friday in the `as_of` column
- However, this code path is effectively dead: customers is empty on weekends, so the guard clause fires before fallback can take effect
- Verify: V2 SQL includes the CASE expression for weekend fallback (behavioral equivalence)
- Verify: on weekday runs, `as_of` equals the effective date unchanged
- Traces: BRD BR-1, Edge Cases; FSD Section 6 W2

#### TC-6c: Empty Non-Customer Tables (Weekday)
- Guard clause only checks customers (BR-2). Other tables being empty does NOT prevent output.
- When `accounts` is empty: `total_accounts` = 0, `total_balance` = 0.00
- When `transactions` is empty: `total_transactions` = 0, `total_txn_amount` = 0.00
- When `investments` is empty: `total_investments` = 0, `total_investment_value` = 0.00
- When `compliance_events` is empty: `compliance_events` = 0
- Verify: remaining KPIs are computed normally
- Traces: BRD BR-2; FSD Section 4

#### TC-6d: Overwrite Mode Data Loss
- In auto-advance mode, each run replaces the Parquet directory entirely
- Only the last effective date's KPIs persist on disk
- Verify behavior matches V1
- Traces: BRD Write Mode Implications; FSD Section 5

#### TC-6e: Compliance Events Unfiltered Count
- V1 counts ALL compliance events regardless of `event_type` or `status` (BR-9)
- V2 uses `COUNT(*)` on compliance_events with no WHERE filter on event_type/status
- Verify: compliance_events KPI value matches V1 exactly
- Traces: BRD BR-9; FSD Section 4 SQL Design Note #5

#### TC-6f: COUNT Semantics (Not Distinct)
- All count KPIs use row count, not distinct count (BR-7)
- If DataSourcing returns multiple as_of dates, the same entity appears once per day
- Verify: V2's `COUNT(*)` matches V1's `count++` loop iteration pattern
- Traces: BRD BR-7; FSD Section 4 SQL Design Note #3

#### TC-6g: CAST to REAL for Count KPIs
- SQL casts count values to REAL for type consistency across UNION ALL branches (FSD Open Question #6)
- Verify: Parquet `kpi_value` column has consistent numeric type across all 8 rows
- If Proofmark reports type mismatches, the CAST may need adjustment
- Traces: FSD Section 9 Open Question #6

### TC-7: Proofmark Configuration
- **comparison_target**: `quarterly_executive_kpis`
- **reader**: `parquet`
- **threshold**: `100.0` (strict -- all output columns deterministic)
- **excluded columns**: None
- **fuzzy columns**: None
- Rationale: All 3 output columns (`kpi_name`, `kpi_value`, `as_of`) are deterministic. V1 uses `decimal` arithmetic (no W6 double epsilon issues). Source columns are `numeric(12,2)` or `numeric(14,2)`, so sums stay within 2 decimal places. No tolerance needed.
- Traces: FSD Section 8

## W-Code Test Cases

### TC-W1: W2 -- Weekend Fallback (Dead Code)
- **What the wrinkle is:** V1 shifts Saturday's `as_of` back 1 day to Friday, Sunday's back 2 days. Applied via DayOfWeek checks.
- **How V2 handles it:** SQL CASE expression using `strftime('%w', ...)` and `date(target_date, '-N day')`. Effectively dead code since customers is empty on weekends, but included for behavioral equivalence.
- **What to verify:**
  1. V2 SQL includes the weekend fallback CASE expression
  2. On weekday runs, `as_of` is the effective date unchanged
  3. On weekend runs, 0 rows are produced (guard clause fires before fallback applies)
  4. If customers data were present on weekends (hypothetical), Saturday dates would shift -1 day and Sunday dates would shift -2 days
- Traces: BRD BR-1; FSD Section 6 W2

### TC-W2: W5 -- Banker's Rounding
- **What the wrinkle is:** V1 uses `Math.Round(value, 2)` which defaults to `MidpointRounding.ToEven` (banker's rounding). Midpoint values like 2.5 round to 2, 3.5 rounds to 4.
- **How V2 handles it:** SQLite's `ROUND(kpi_value, 2)` uses banker's rounding by default, matching V1.
- **What to verify:**
  1. All 8 `kpi_value` fields are rounded to exactly 2 decimal places
  2. SQLite ROUND and C# Math.Round produce identical results for the test data
  3. This is effectively a no-op for this job (source values have <= 2 decimal places, counts are integers)
  4. V2 kpi_value matches V1 kpi_value for every row
- Traces: BRD BR-6; FSD Section 6 W5

## Notes
- This is a Tier 1 job -- no External module. All business logic is in a single SQL query using UNION ALL for the unpivot pattern.
- V1 uses `decimal` arithmetic (not `double`), so W6 (double epsilon) does NOT apply.
- The job name "quarterly" is misleading (AP9) -- it runs daily. Cannot rename without changing output paths.
- Overlap with `executive_dashboard` (AP2) -- both compute total_customers, total_accounts, total_balance, total_transactions, total_txn_amount. Cannot fix cross-job duplication.
- The `firstEffectiveDate` is `2024-10-01`. Verify this matches V1 config.
- Row ordering in the UNION ALL is deterministic and matches V1's fixed KPI list order (BR-5). If Proofmark reports order mismatches, this would indicate a UNION ALL evaluation order issue in SQLite.
