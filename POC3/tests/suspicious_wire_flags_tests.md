# SuspiciousWireFlags -- V2 Test Plan

## Job Info
- **V2 Config**: `suspicious_wire_flags_v2.json`
- **Tier**: 1 (Framework Only -- replaced V1 External module with SQL Transformation)
- **External Module**: None (V1 used `SuspiciousWireFlagProcessor.cs`, eliminated via AP3)

## Pre-Conditions
- Source tables available in `datalake` schema:
  - `wire_transfers` with columns: `wire_id`, `customer_id`, `direction`, `amount`, `counterparty_name`, `counterparty_bank`, `status`, `as_of`
- Effective date range injected by executor (`__minEffectiveDate`, `__maxEffectiveDate`)
- `as_of` column auto-appended by DataSourcing module
- V1 baseline output available at `Output/curated/suspicious_wire_flags/`

## Test Cases

### TC-1: Output Schema Validation
- **Requirement**: FSD Section 10, BRD Output Schema
- **Expected columns (exact order):** `wire_id`, `customer_id`, `direction`, `amount`, `counterparty_name`, `status`, `flag_reason`, `as_of`
- **Expected types:**
  - `wire_id`: integer (passthrough)
  - `customer_id`: integer (passthrough)
  - `direction`: string (passthrough)
  - `amount`: real/numeric (CAST to REAL in V2)
  - `counterparty_name`: string (COALESCE applied for NULLs)
  - `status`: string (passthrough)
  - `flag_reason`: string (computed: `'OFFSHORE_COUNTERPARTY'` or `'HIGH_AMOUNT'`)
  - `as_of`: date (passthrough)
- Verify 8 columns total -- no extra columns (e.g., no `counterparty_bank`)
- Verify column order matches the SELECT order in the Transformation SQL

### TC-2: Row Count Equivalence
- **Requirement**: BR-3, BR-10
- V1 and V2 must produce identical row counts
- **With current data, expected row count is 0** (BR-10: no OFFSHORE counterparties, max amount $49,959 < $50,000)
- Verify that removing dead-end `accounts` and `customers` DataSourcing (AP1) does not affect row count
- Verify that removing `counterparty_bank` from `wire_transfers` columns (AP4) does not affect row count

### TC-3: Data Content Equivalence
- **Requirement**: BR-1, BR-2, BR-3, BR-8, BR-11
- All values must be byte-identical to V1 output
- Compare V2 Parquet output at `Output/double_secret_curated/suspicious_wire_flags/` against V1 baseline at `Output/curated/suspicious_wire_flags/`
- With current data, both outputs should be empty Parquet files with matching schema
- Verify Parquet schema (column names and types) matches between V1 and V2 even with zero rows

### TC-4: Writer Configuration
- **Requirement**: FSD Section 5, BRD Writer Configuration
- **type**: ParquetFileWriter
- **source**: `output` (matches Transformation resultName)
- **numParts**: 1 (single Parquet part file)
- **writeMode**: Overwrite (directory replaced on each run)
- **outputDirectory**: `Output/double_secret_curated/suspicious_wire_flags/` (V2 convention)
- Verify exactly one `.parquet` part file is written
- Verify Overwrite mode replaces directory contents on re-run

### TC-5: Anti-Pattern Elimination Verification

#### AP1 (Dead-end sourcing) -- ELIMINATED
- **Requirement**: FSD Section 7, BR-4, BR-5
- Verify V2 config does NOT contain a DataSourcing entry for `accounts`
- Verify V2 config does NOT contain a DataSourcing entry for `customers`
- Verify V1 config DOES source both `accounts` and `customers` (confirming V1 had the anti-pattern)
- Verify neither table is referenced anywhere in V1's External module processing logic
- Verify removal has no effect on output

#### AP3 (Unnecessary External module) -- ELIMINATED
- **Requirement**: FSD Section 2, FSD Section 7
- Verify V2 uses `Transformation` (SQL) instead of an External module
- Verify V1 used `SuspiciousWireFlagProcessor.cs` as an External module
- Verify the V1 External logic (foreach loop with two conditions) is fully captured by the V2 SQL `CASE WHEN` + `WHERE` clause
- Verify output equivalence is maintained after replacement

#### AP4 (Unused columns) -- ELIMINATED
- **Requirement**: FSD Section 7, BR-6, BR-7
- Verify V2 `wire_transfers` DataSourcing columns are `[wire_id, customer_id, direction, amount, counterparty_name, status]`
- Verify `counterparty_bank` is NOT in V2's column list (BR-6)
- Verify `suffix` is not sourced (entire `customers` table removed via AP1, which eliminates BR-7)
- Verify removal has no effect on output

#### AP6 (Row-by-row iteration) -- ELIMINATED
- **Requirement**: FSD Section 7
- Verify V1 External module uses `foreach (var row in wireTransfers.Rows)` loop
- Verify V2 replaces this with set-based SQL (`CASE WHEN` + `WHERE`)
- Verify output equivalence is maintained

#### AP7 (Magic values) -- ELIMINATED
- **Requirement**: FSD Section 7
- Verify V1 has hardcoded `"OFFSHORE"` and `50000` without explanatory context
- Verify V2 SQL includes descriptive comments for each threshold (BR-1 for OFFSHORE, BR-2 for $50,000)
- Verify the threshold values themselves are unchanged (output must match)

### TC-6: Edge Cases

#### TC-6a: Empty Output with Current Data (BR-10, BRD Edge Case 1)
- **Requirement**: BR-10, FSD Section 4 Note 7
- Current dataset has no OFFSHORE counterparties and no wire amounts > $50,000 (max is $49,959)
- Expected: valid Parquet file with correct 8-column schema but zero data rows
- Verify V2 produces the same empty output as V1
- Verify Proofmark can compare two empty Parquet files and report PASS

#### TC-6b: OFFSHORE Counterparty Detection -- Case Sensitivity (BR-1, BRD Edge Case 4)
- **Requirement**: BR-1, FSD Section 4 Notes 3-4
- The OFFSHORE check must be case-sensitive (V1 uses `String.Contains("OFFSHORE")` which is ordinal/case-sensitive)
- V2 uses `INSTR(counterparty_name, 'OFFSHORE')` which is case-sensitive in SQLite
- Verify: `"OFFSHORE"` matches, `"offshore"` does NOT match, `"Offshore"` does NOT match
- Verify: `"ABC OFFSHORE LTD"` matches (substring), `"OFFSHOREBANK"` matches (substring)
- Note: With current data, no counterparty names contain "OFFSHORE" at all, so this is a design validation, not a runtime test against live data

#### TC-6c: Amount Threshold -- Strictly Greater Than (BR-2, BRD Edge Case 6)
- **Requirement**: BR-2, FSD Section 4
- The threshold is strictly > $50,000, not >= $50,000
- Verify: amount = $50,001 would be flagged as HIGH_AMOUNT
- Verify: amount = $50,000 would NOT be flagged (BRD Edge Case 6)
- Verify: amount = $49,999 would NOT be flagged
- Note: With current data max amount is $49,959, so no wires trigger this condition

#### TC-6d: Mutually Exclusive Flags -- OFFSHORE Takes Priority (BR-2, BR-11, BRD Edge Case 3)
- **Requirement**: BR-2, BR-11
- A wire matching both OFFSHORE counterparty AND amount > $50,000 receives only `OFFSHORE_COUNTERPARTY`
- V2 implements this via CASE WHEN ordering (first match wins)
- Verify: the CASE evaluates OFFSHORE first, HIGH_AMOUNT second
- Verify: no wire can have both flags simultaneously
- Note: With current data, no wires trigger either condition

#### TC-6e: NULL Counterparty Name (BR-8, BRD Edge Case 2)
- **Requirement**: BR-8, FSD Section 4 Note 2
- NULL `counterparty_name` is coalesced to empty string `''`
- An empty string will never match `INSTR(..., 'OFFSHORE') > 0`
- Verify: V2 SQL uses `COALESCE(counterparty_name, '')` in both the CASE and WHERE clauses
- Verify: NULL counterparty wires are excluded from output (they match neither flag condition)

#### TC-6f: Empty/NULL Wire Transfers Input (BR-9)
- **Requirement**: BR-9
- If `wire_transfers` DataSourcing returns zero rows, the SQL produces zero output rows
- Expected: empty Parquet file with correct schema
- V1 External module has an explicit empty-input guard; V2 SQL naturally handles this

#### TC-6g: Overwrite Mode with Auto-Advance (BRD Write Mode Implications)
- **Requirement**: FSD Section 5, BRD Write Mode Implications
- Overwrite mode means each execution replaces the entire Parquet directory
- Multi-day auto-advance retains only the last effective date's output on disk
- Verify behavior matches V1 (W9 noted as POSSIBLE but reproduced regardless)

#### TC-6h: Amount Type Preservation (FSD Open Question 2)
- **Requirement**: FSD Section 10 (amount column), FSD Open Question 2
- V1 converts amount via `Convert.ToDecimal()`, V2 uses `CAST(amount AS REAL)`
- If Parquet encodes these differently (decimal vs double column type), Proofmark may flag a schema mismatch
- Verify: the `amount` column values survive the CAST round-trip without precision loss
- Fallback: if epsilon differences appear, add a FUZZY override with tight absolute tolerance (e.g., 0.01)

#### TC-6i: Row Ordering (FSD Open Question 3)
- **Requirement**: FSD Open Question 3
- V1 iterates rows in DataSourcing return order; V2 SQL has no explicit ORDER BY
- With current data both outputs are empty, so ordering is moot
- If future data triggers flags and Proofmark fails on ordering, add `ORDER BY as_of, wire_id` to SQL

### TC-7: Proofmark Configuration
- **Requirement**: FSD Section 8
- **comparison_target**: `suspicious_wire_flags`
- **reader**: `parquet`
- **threshold**: `100.0` (strict -- all columns deterministic)
- **excluded columns**: None
- **fuzzy columns**: None
- Rationale: All output columns are deterministic. No non-deterministic fields identified (BRD: "None identified"). Every column is either a direct passthrough or a deterministic computed value (`flag_reason`). With current data, output is empty -- Proofmark should validate that both empty Parquet files have matching schema.
- **Fallback note**: If `amount` column shows epsilon-level differences due to `CAST(amount AS REAL)` vs `Convert.ToDecimal()`, add a FUZZY override per FSD Open Question 2.

## W-Code Test Cases

### TC-W1: W9 -- Overwrite WriteMode (POSSIBLE)
- **Requirement**: FSD Section 6
- **What the wrinkle is:** V1 uses Overwrite mode. Multi-day auto-advance retains only the last day's output. This may or may not be intentional for a "current flags" snapshot job.
- **How V2 handles it:** V2 uses Overwrite to match V1 exactly. The behavior is reproduced regardless of whether it is a bug or by design.
- **What to verify:**
  1. V2 config specifies `"writeMode": "Overwrite"`
  2. After auto-advance across multiple dates, only the final date's output persists
  3. Behavior matches V1

## Notes
- This is a V1 External module (Tier 2/3) converted to Tier 1 (Framework Only). The V1 `SuspiciousWireFlagProcessor.cs` foreach loop is fully replaced by a SQL `CASE WHEN` + `WHERE` clause.
- Five anti-patterns were eliminated: AP1 (2 dead-end sources: accounts, customers), AP3 (unnecessary External), AP4 (unused counterparty_bank and suffix), AP6 (row-by-row iteration), AP7 (magic values).
- With current data, both V1 and V2 produce zero output rows. This means the flag logic (OFFSHORE check, amount threshold) is effectively untested against real data during Proofmark comparison. The test cases above document expected behavior for completeness, but runtime validation depends on data containing triggering values.
- The FSD corrected an initial mistake with `LIKE '%OFFSHORE%'` (case-insensitive in SQLite for ASCII) to `INSTR()` (case-sensitive). Verify the final V2 SQL uses `INSTR`, not `LIKE`.
- No output-affecting wrinkles (W-codes) are confirmed for this job. W9 (Overwrite mode) is noted as POSSIBLE but is reproduced regardless.
