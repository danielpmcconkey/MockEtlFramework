# ExecutiveDashboard -- V2 Test Plan

## Job Info
- **V2 Config**: `executive_dashboard_v2.json`
- **Tier**: 2 (Framework + Minimal External)
- **External Module**: `ExecutiveDashboardV2Processor` (ExternalModules/ExecutiveDashboardV2Processor.cs)

## Pre-Conditions
- Source tables available in `datalake` schema:
  - `transactions` with columns: `transaction_id`, `account_id`, `amount`, `as_of`
  - `accounts` with columns: `account_id`, `current_balance`, `as_of`
  - `customers` with columns: `id`, `as_of`
  - `loan_accounts` with columns: `loan_id`, `current_balance`, `as_of`
  - `branch_visits` with columns: `visit_id`, `as_of`
- Effective date range injected by executor (`__minEffectiveDate`, `__maxEffectiveDate`)
- `as_of` column auto-appended by DataSourcing module
- V1 baseline output available at `Output/curated/executive_dashboard.csv`
- V1 External module: `ExternalModules.ExecutiveDashboardBuilder` (for reference behavior)
- ExternalModules project builds successfully (`dotnet build`)

## Test Cases

### TC-1: Output Schema Validation
- **Expected columns (exact order):** `metric_name`, `metric_value`, `as_of`
- **Expected types:**
  - `metric_name`: string
  - `metric_value`: decimal (rounded to 2 decimal places)
  - `as_of`: date
- Verify exactly 9 data rows on weekdays (one per metric), 0 data rows on weekends
- Verify metric names appear in this exact fixed order:
  1. `total_customers`
  2. `total_accounts`
  3. `total_balance`
  4. `total_transactions`
  5. `total_txn_amount`
  6. `avg_txn_amount`
  7. `total_loans`
  8. `total_loan_balance`
  9. `total_branch_visits`

### TC-2: Row Count Equivalence
- V1 and V2 must produce identical row counts for the same effective date
- Weekday runs: 9 rows (one per metric)
- Weekend runs: 0 rows (guard clause fires)
- Run both V1 and V2 for the same effective date range and compare CSV data row counts
- Note: trailer row is not a data row -- compare data rows only

### TC-3: Data Content Equivalence
- All data values must match V1 output exactly
- Compare V2 CSV at `Output/double_secret_curated/executive_dashboard.csv` against V1 baseline at `Output/curated/executive_dashboard.csv`
- **W5 affects comparison:** `metric_value` uses banker's rounding (MidpointRounding.ToEven). Verify V2 rounds identically to V1 for midpoint values (e.g., 2.5 rounds to 2, 3.5 rounds to 4).
- **W9 affects comparison:** Overwrite mode means only the last effective date's output persists. Compare outputs for the same single effective date.
- Verify `metric_name` strings match exactly (case-sensitive, no trailing whitespace)
- Verify `metric_value` decimal values match to 2 decimal places
- Verify `as_of` dates match exactly
- **Trailer exclusion:** The trailer line contains `{timestamp}` which is non-deterministic. Trailer comparison must be excluded (proofmark trailer_rows: 1 handles this).

### TC-4: Writer Configuration
- **type**: CsvFileWriter
- **source**: `output` (matches External module's shared state key)
- **outputFile**: `Output/double_secret_curated/executive_dashboard.csv` (V2 convention)
- **includeHeader**: `true` -- verify header row present: `metric_name,metric_value,as_of`
- **writeMode**: `Overwrite` -- verify file is replaced on each run (W9)
- **lineEnding**: `LF` -- verify Unix line endings (no `\r\n`)
- **trailerFormat**: `SUMMARY|{row_count}|{date}|{timestamp}` -- verify trailer line format:
  - `{row_count}` = number of data rows written (9 on weekdays, 0 on weekends)
  - `{date}` = effective date
  - `{timestamp}` = UTC execution timestamp (non-deterministic)

### TC-5: Anti-Pattern Elimination Verification

#### AP1 (Dead-end sourcing) -- ELIMINATED
- Verify V2 config does NOT contain DataSourcing entries for `branches` or `segments`
- Verify V1 config DOES contain `branches` and `segments` DataSourcing entries
- Verify removal has no effect on output (V1 External module never references branches or segments)

#### AP4 (Unused columns) -- ELIMINATED
- Verify V2 column lists are trimmed to only used columns:
  - `transactions`: `[transaction_id, account_id, amount]` (removed: `txn_type`)
  - `accounts`: `[account_id, current_balance]` (removed: `customer_id`, `account_type`, `account_status`)
  - `customers`: `[id]` (removed: `first_name`, `last_name`)
  - `loan_accounts`: `[loan_id, current_balance]` (removed: `customer_id`, `loan_type`)
  - `branch_visits`: `[visit_id]` (removed: `customer_id`, `branch_id`, `visit_purpose`)
- Verify removal has no effect on output

#### AP6 (Row-by-row iteration) -- ELIMINATED
- Verify V2 External module uses LINQ `.Sum()` instead of `foreach` loops for:
  - `total_balance` (sum of `accounts.current_balance`)
  - `total_txn_amount` (sum of `transactions.amount`)
  - `total_loan_balance` (sum of `loan_accounts.current_balance`)
- Verify output values are identical to V1's `foreach` loop results

#### AP3 (Unnecessary External) -- PARTIALLY ADDRESSED
- Verify V2 still uses an External module (Tier 2) but with reduced scope
- The External module is justified by: guard clause behavior (SQLite skips empty tables), as_of fallback logic, and vertical pivot
- Verify External module does NOT perform DataSourcing (data comes from framework DataSourcing modules)
- Verify External module does NOT write output files (CsvFileWriter handles output)

### TC-6: Edge Cases

#### TC-6a: Weekend Guard Clause (Empty customers/accounts/loans)
- On weekends, `customers`, `accounts`, and `loan_accounts` tables have no data
- Expected: guard clause fires, producing 0-row output with columns `[metric_name, metric_value, as_of]`
- Verify: CSV file contains header row + trailer only, no data rows
- Verify: trailer `{row_count}` = 0

#### TC-6b: Empty Transactions (Weekday)
- When `transactions` is null or empty but customers/accounts/loans exist:
  - `total_transactions` = 0
  - `total_txn_amount` = 0
  - `avg_txn_amount` = 0 (division by zero guarded)
- Verify: remaining 6 metrics are computed normally

#### TC-6c: Empty Branch Visits
- When `branch_visits` is null or empty:
  - `total_branch_visits` = 0
- Verify: remaining 8 metrics are computed normally

#### TC-6d: as_of Fallback
- Primary: `as_of` = `customers.Rows[0]["as_of"]`
- Fallback: if customer as_of is null AND transactions is non-null/non-empty, use `transactions.Rows[0]["as_of"]`
- Verify: when customer as_of is available, it is used for all 9 metric rows
- Verify: when customer as_of is null, transaction as_of is used
- Verify: all 9 metric rows share the same as_of value

#### TC-6e: Overwrite Mode Data Loss
- In auto-advance mode, each run overwrites the previous CSV
- Only the last effective date's metrics persist on disk
- Verify behavior matches V1 (W9)

#### TC-6f: Non-Distinct Counts
- `total_customers` and `total_accounts` are raw `.Count` values (not DISTINCT)
- In multi-day effective date ranges, the same customer/account appears once per day
- Verify counts match V1 behavior (row counts, not distinct entity counts)

### TC-7: Proofmark Configuration
- **comparison_target**: `executive_dashboard`
- **reader**: `csv`
- **threshold**: `100.0` (strict -- all data columns deterministic)
- **csv.header_rows**: `1`
- **csv.trailer_rows**: `1` (strips non-deterministic timestamp trailer from comparison)
- **excluded columns**: None
- **fuzzy columns**: None
- Rationale: All data columns (`metric_name`, `metric_value`, `as_of`) are deterministic. V1 uses `decimal` arithmetic throughout (no `double` epsilon issues). Banker's rounding (W5) is deterministic given the same input. The trailer's `{timestamp}` is non-deterministic but excluded via `trailer_rows: 1`.

## W-Code Test Cases

### TC-W1: W5 -- Banker's Rounding
- **What the wrinkle is:** V1 uses `Math.Round(value, 2)` which defaults to `MidpointRounding.ToEven` (banker's rounding). For midpoint values like 2.5, this rounds to the nearest even number (2) rather than always rounding up (3).
- **How V2 handles it:** V2 uses `Math.Round(value, 2, MidpointRounding.ToEven)` explicitly, with a comment documenting the choice.
- **What to verify:**
  1. All 9 `metric_value` fields are rounded to exactly 2 decimal places
  2. Midpoint values round to even (e.g., 2.5 -> 2.00, 3.5 -> 4.00, 4.5 -> 4.00)
  3. V2 metric_value matches V1 metric_value for every row
  4. Verify the explicit `MidpointRounding.ToEven` enum is used in V2 source code (not relying on default)

### TC-W2: W9 -- Overwrite Mode (Data Loss)
- **What the wrinkle is:** V1 uses `writeMode: Overwrite` for a daily dashboard. In auto-advance mode, each day's run overwrites the previous day's output. Only the last day's metrics survive on disk.
- **How V2 handles it:** V2 reproduces `writeMode: Overwrite` exactly, with a comment documenting the behavior.
- **What to verify:**
  1. V2 config specifies `writeMode: Overwrite`
  2. Running V2 for date A, then date B, results in only date B's output on disk
  3. The overwritten file contains a valid header, 9 data rows (or 0 for weekends), and one trailer
  4. Behavior matches V1 exactly

## Notes
- The V1 External module (`ExecutiveDashboardBuilder`) uses `foreach` loops for balance/amount summation. V2 replaces these with LINQ `.Sum()` (AP6 elimination). Verify output equivalence carefully -- LINQ `.Sum()` over `decimal` should produce identical results to sequential `+=` accumulation over `decimal`, but this is worth confirming.
- V1 uses `decimal` throughout (not `double`), so W6 (double epsilon) does NOT apply. No floating-point precision concerns.
- The trailer `{timestamp}` makes raw file comparison impossible. Use proofmark with `trailer_rows: 1` to strip the trailer, or compare only data rows.
- V2 removes 2 dead-end DataSourcing entries (branches, segments) and trims columns across all 5 remaining sources. These are structural changes that should not affect output. Verify with proofmark.
- The guard clause is critical: it checks `customers`, `accounts`, AND `loan_accounts` (not transactions or branch_visits). Ensure V2 replicates the exact same guard logic.
- The V2 External module class name is `ExecutiveDashboardV2Processor` (not `ExecutiveDashboardV2Builder`). Verify the config's `typeName` matches the class name exactly including namespace: `ExternalModules.ExecutiveDashboardV2Processor`.
