# ProductPenetration -- V2 Test Plan

## Job Info
- **V2 Config**: `product_penetration_v2.json`
- **Tier**: 1 (Framework Only)
- **External Module**: None

## Pre-Conditions
- Source tables available in `datalake` schema:
  - `customers` with column: `id` (plus auto-appended `as_of`)
  - `accounts` with column: `customer_id` (plus auto-appended `as_of`)
  - `cards` with column: `customer_id` (plus auto-appended `as_of`)
  - `investments` with column: `customer_id` (plus auto-appended `as_of`)
- Effective date range injected by executor (`__minEffectiveDate`, `__maxEffectiveDate`)
- `as_of` column auto-appended by DataSourcing module for all four tables
- V1 baseline output available at `Output/curated/product_penetration.csv`

## Test Cases

### TC-1: Output Schema Validation
- **Expected columns (exact order):** `product_type`, `customer_count`, `product_count`, `penetration_rate`, `as_of`
- **Expected types:**
  - `product_type`: string (one of: "accounts", "cards", "investments")
  - `customer_count`: integer (COUNT DISTINCT result)
  - `product_count`: integer (COUNT DISTINCT result)
  - `penetration_rate`: integer (integer division result -- always 0 or 1 due to W4)
  - `as_of`: date
- Verify column order matches the SELECT order in the Transformation SQL (FSD Section 4)
- Verify no extra columns are present (e.g., no `first_name`, `last_name`, `account_id`, `card_id`, `investment_id`)
- **Traces to:** BR-1, BR-5, BRD Output Schema, FSD Section 4

### TC-2: Row Count Equivalence
- V1 and V2 must produce exactly 3 rows of data (plus 1 header row) per execution
- The `LIMIT 3` clause constrains output to exactly 3 rows regardless of the cross-join expansion
- Overwrite mode means only the last execution's output persists on disk
- **Traces to:** BR-4, FSD Section 4 Note 3

### TC-3: Data Content Equivalence
- All values must be byte-identical to V1 output
- Compare V2 CSV output at `Output/double_secret_curated/product_penetration.csv` against V1 baseline at `Output/curated/product_penetration.csv`
- Verify `product_type` values are exactly: "accounts", "cards", "investments" (in the order produced by the UNION ALL)
- Verify `customer_count` matches across V1 and V2 for every row
- Verify `product_count` matches across V1 and V2 for every row
- Verify `penetration_rate` values match exactly (integer division -- 0 or 1 only)
- Verify `as_of` values match exactly
- **Traces to:** BR-1, BR-2, BR-5, FSD Section 4

### TC-4: Writer Configuration
- **type**: CsvFileWriter
- **source**: `output` (matches Transformation resultName; FSD Section 5)
- **outputFile**: `Output/double_secret_curated/product_penetration.csv` (V2 convention)
- **includeHeader**: true
- **writeMode**: Overwrite (file replaced on each execution)
- **lineEnding**: LF
- **trailerFormat**: absent (no trailer)
- Verify header line appears in the output file
- Verify line endings are LF (not CRLF)
- Verify no trailer row exists
- Verify Overwrite mode replaces file contents on re-run (not appends)
- **Traces to:** BRD Writer Configuration, FSD Section 5

### TC-5: Anti-Pattern Elimination Verification

#### AP4 (Unused columns) -- ELIMINATED
- **customers table:**
  - Verify V2 DataSourcing columns are `["id"]`
  - Verify V1 sourced `["id", "first_name", "last_name"]`
  - Verify `first_name` and `last_name` are NOT in V2's column list
  - Confirm neither column is referenced in the Transformation SQL
  - **Traces to:** BR-6, FSD Section 7 (AP4)

- **accounts table:**
  - Verify V2 DataSourcing columns are `["customer_id"]`
  - Verify V1 sourced `["account_id", "customer_id", "account_type"]`
  - Verify `account_id` and `account_type` are NOT in V2's column list
  - Confirm only `COUNT(DISTINCT customer_id)` is used in SQL
  - **Traces to:** FSD Section 7 (AP4)

- **cards table:**
  - Verify V2 DataSourcing columns are `["customer_id"]`
  - Verify V1 sourced `["card_id", "customer_id"]`
  - Verify `card_id` is NOT in V2's column list
  - **Traces to:** FSD Section 7 (AP4)

- **investments table:**
  - Verify V2 DataSourcing columns are `["customer_id"]`
  - Verify V1 sourced `["investment_id", "customer_id"]`
  - Verify `investment_id` is NOT in V2's column list
  - **Traces to:** FSD Section 7 (AP4)

#### No Other AP-codes Apply
- AP1 (Dead-end sourcing): All four sourced tables are referenced in the SQL. No dead-end.
- AP3 (Unnecessary External): V1 uses Transformation, not External (BR-7). Already Tier 1.
- AP5-AP10: Not applicable per FSD Section 7 analysis.

### TC-6: Edge Cases

#### TC-6a: Integer Division Bug (W4) -- Penetration Rate Values
- For the known data set (2024-10-01): 2230 customers, 2230 account holders, 2230 card holders, 427 investment holders
- Expected penetration_rate values:
  - accounts: 2230/2230 = 1 (integer division)
  - cards: 2230/2230 = 1 (integer division)
  - investments: 427/2230 = 0 (integer division truncates to 0)
- Verify penetration_rate is NEVER a fractional decimal (e.g., 0.19)
- **Traces to:** BR-2, FSD Section 4 Note 1

#### TC-6b: Cross-Join as_of Determinism
- The `JOIN customers ON 1=1 LIMIT 3` cross-join picks `as_of` from whichever customer row SQLite iterates first
- Since DataSourcing orders by `as_of` (DataSourcing.cs:85), this should be the minimum as_of in range
- For single-date auto-advance runs, min as_of equals the single effective date, so the value is deterministic
- Verify V1 and V2 produce the same `as_of` value for each row
- **Traces to:** BR-5, FSD Section 4 Note 2, FSD Section 9 (Open Question 1)

#### TC-6c: LIMIT 3 Safety Guard
- The UNION ALL produces exactly 3 rows in product_stats (accounts, cards, investments)
- The cross-join with customers multiplies these by N customer rows
- LIMIT 3 constrains the output back to 3 rows
- Verify exactly 3 data rows are present in the output file (not 3 * N)
- **Traces to:** BR-4, FSD Section 4 Note 3

#### TC-6d: Product Type Row Ordering
- UNION ALL ordering is accounts, cards, investments (in that order in the SQL)
- Verify V2 output rows appear in the same order as V1
- If Proofmark reports ordering mismatches, investigate SQLite's UNION ALL + cross-join iteration behavior
- **Traces to:** FSD Section 9 (Open Question 3)

#### TC-6e: Overwrite Mode -- Only Last Date Persists
- With Overwrite writeMode, each auto-advance execution replaces the file
- After the full date range completes, only the last date's output (2024-12-31) remains on disk
- Verify both V1 and V2 final files contain only the last execution date's data
- **Traces to:** BRD Write Mode Implications, FSD Section 5

#### TC-6f: Empty Tables (Weekend Dates)
- If effective date falls on a weekend/holiday with no data in datalake, all four DataSourcing modules return zero-row DataFrames
- Zero-row DataFrames are not registered as SQLite tables (Transformation.cs:46), causing SQL failure
- Both V1 and V2 would fail identically -- no output file produced
- The date range 2024-10-01 through 2024-12-31 is not expected to hit this case
- **Traces to:** BRD Edge Cases (Weekend dates), FSD Section 9 (Open Question 2)

#### TC-6g: Division by Zero
- If `customer_count` is 0 (no customers for the effective date), integer division `product_count / 0` causes a runtime error
- This is not expected in the test date range (customers table has data for all business days)
- Both V1 and V2 would fail identically
- **Traces to:** BRD Edge Cases (Empty tables)

### TC-7: Proofmark Configuration
- **comparison_target**: `product_penetration`
- **reader**: `csv`
- **threshold**: `100.0` (strict)
- **csv.header_rows**: `1`
- **csv.trailer_rows**: `0`
- **excluded columns**: None (starting strict per FSD Section 8 rationale)
- **fuzzy columns**: None (all values are integers -- no floating-point concerns)
- Rationale: All computed values are integers (COUNT results and integer-division results). The `as_of` column is potentially non-deterministic due to the cross-join (BRD Non-Deterministic Fields, confidence MEDIUM), but we start strict and only add exclusions if comparison fails with evidence.
- **Potential adjustment**: If Proofmark reports `as_of` mismatches, add `as_of` as an EXCLUDED column with documented evidence from the comparison report.
- **Traces to:** FSD Section 8, BRD Non-Deterministic Fields

## W-Code Test Cases

### TC-W1: W4 -- Integer Division Truncation
- **What the wrinkle is:** `penetration_rate` is computed as `CAST(cnt AS INTEGER) / CAST(total_customers AS INTEGER)`, which performs integer division in SQLite. Results are always 0 (product_count < customer_count) or 1 (product_count == customer_count).
- **How V2 handles it:** V2 reproduces the identical SQL expression. SQLite's native integer division produces the same truncation behavior. The FSD documents this as intentional replication of W4.
- **What to verify:**
  1. For accounts (2230/2230): verify penetration_rate = 1
  2. For cards (2230/2230): verify penetration_rate = 1
  3. For investments (427/2230): verify penetration_rate = 0
  4. Verify penetration_rate is never a decimal value (e.g., 0.19)
  5. Verify V2 penetration_rate matches V1 penetration_rate for every row across all dates
- **Traces to:** BR-2, FSD Section 4 Note 1, FSD Section 6 (W4)

## Notes
- This is a clean Tier 1 job. V1 already uses DataSourcing + Transformation + CsvFileWriter with no External module (BR-7).
- The only structural change is removing unused columns from all four DataSourcing configs (AP4). This does not affect output since the removed columns are never referenced in the Transformation SQL.
- The integer division bug (W4) is the only output-affecting wrinkle. It produces penetration_rate values of 0 or 1 exclusively.
- The cross-join `JOIN customers ON 1=1 LIMIT 3` is an unusual pattern for obtaining the `as_of` column. It is preserved exactly from V1. If it causes non-deterministic `as_of` values in Proofmark comparison, the FSD prescribes adding `as_of` as an EXCLUDED column (FSD Section 8, Potential Proofmark Adjustments).
- Overwrite mode means only the final execution date's output is on disk after the full run. Proofmark comparison covers just that final file.
- The CTE structure is preserved identically from V1 to minimize risk of behavioral divergence (FSD Section 4 Note 4).
