# SmsOptInRate -- V2 Test Plan

## Job Info
- **V2 Config**: `sms_opt_in_rate_v2.json`
- **Tier**: 1 (Framework Only)
- **External Module**: None

## Pre-Conditions
- Source tables available in `datalake` schema:
  - `customer_preferences` with columns: `customer_id`, `preference_type`, `opted_in`, `as_of`
  - `customers_segments` with columns: `customer_id`, `segment_id`, `as_of`
  - `segments` with columns: `segment_id`, `segment_name`, `as_of`
- Effective date range injected by executor (`__minEffectiveDate`, `__maxEffectiveDate`)
- `as_of` column auto-appended by DataSourcing module
- V1 baseline output available at `Output/curated/sms_opt_in_rate/`

## Test Cases

### TC-1: Output Schema Validation
- **Requirement**: FSD Section 4 (Transformation SQL), BRD Output Schema
- **Expected columns (exact order):** `segment_name`, `opted_in_count`, `total_count`, `opt_in_rate`, `as_of`
- **Expected types:**
  - `segment_name`: string
  - `opted_in_count`: integer (SUM result)
  - `total_count`: integer (COUNT result)
  - `opt_in_rate`: integer (integer division result, always 0 or 1)
  - `as_of`: date
- Verify column order matches the SELECT order in the Transformation SQL
- Verify no extra columns are present (e.g., no `preference_id`)

### TC-2: Row Count Equivalence
- **Requirement**: BR-1, BR-5, BR-6
- V1 and V2 must produce identical row counts
- One row per unique (`segment_name`, `as_of`) combination
- Run both V1 and V2 for the same effective date range and compare Parquet row counts
- Verify that removing `preference_id` from `customer_preferences` columns (AP4) does not affect row count

### TC-3: Data Content Equivalence
- **Requirement**: BR-2, BR-3, BR-4, BR-5
- All values must be byte-identical to V1 output
- Compare V2 Parquet output at `Output/double_secret_curated/sms_opt_in_rate/` against V1 baseline at `Output/curated/sms_opt_in_rate/`
- **W4 affects comparison:** `opt_in_rate` values must be exactly 0 or 1 (integer division truncation). Verify V2 matches V1's truncated values, not a corrected decimal rate.
- Verify `segment_name` values match exactly (string comparison, case-sensitive)
- Verify `opted_in_count` and `total_count` aggregates match exactly
- Verify `as_of` dates match exactly

### TC-4: Writer Configuration
- **Requirement**: FSD Section 5, BRD Writer Configuration
- **type**: ParquetFileWriter
- **source**: `sms_opt_in` (matches Transformation resultName)
- **numParts**: 1 (single Parquet part file)
- **writeMode**: Overwrite (directory replaced on each run)
- **outputDirectory**: `Output/double_secret_curated/sms_opt_in_rate/` (V2 convention)
- Verify exactly one `.parquet` part file is written
- Verify Overwrite mode replaces directory contents on re-run

### TC-5: Anti-Pattern Elimination Verification

#### AP4 (Unused columns) -- ELIMINATED
- **Requirement**: FSD Section 7, BRD Source Tables
- Verify V2 `customer_preferences` DataSourcing columns are `[customer_id, preference_type, opted_in]`
- Verify `preference_id` is NOT in V2's column list
- Verify V1 config DOES source `preference_id` (confirming V1 had the anti-pattern)
- Verify removal has no effect on output (`preference_id` was never referenced in SQL)

#### AP1 (Dead-end sourcing) -- NOT APPLICABLE
- **Requirement**: FSD Section 7, BRD Edge Case 5
- Confirm V1 does NOT source any unused tables for this job (unlike EmailOptInRate, which sources `phone_numbers`)
- Verify all three V1 DataSourcing entries (`customer_preferences`, `customers_segments`, `segments`) are referenced in the SQL
- No dead-end sources to eliminate

### TC-6: Edge Cases

#### TC-6a: MARKETING_SMS Filter (BR-1)
- **Requirement**: BR-1
- Verify only rows with `preference_type = 'MARKETING_SMS'` are included
- Verify other preference types (`MARKETING_EMAIL`, `E_STATEMENTS`, `PAPER_STATEMENTS`, `PUSH_NOTIFICATIONS`) are excluded
- Cross-check: query `datalake.customer_preferences` directly to confirm MARKETING_SMS rows exist and their count matches the output aggregates

#### TC-6b: Empty Input -- No MARKETING_SMS Preferences (BRD Edge Case 3)
- **Requirement**: BR-1, BRD Edge Case 3
- If no rows have `preference_type = 'MARKETING_SMS'`, the WHERE clause filters all rows
- Expected: empty Parquet part file (0 rows, schema preserved)
- Verify ParquetFileWriter produces a valid empty Parquet file

#### TC-6c: Customer in Multiple Segments (BRD Edge Case 2)
- **Requirement**: BR-6, BRD Edge Case 2
- A customer appearing in `customers_segments` with multiple `segment_id` values contributes their preference to each segment independently
- Verify: if customer X is in segments A and B, and has opted_in=1 for MARKETING_SMS, both segment A and segment B get +1 to `opted_in_count` and +1 to `total_count`

#### TC-6d: Customer Without Segment Mapping (BR-6)
- **Requirement**: BR-6
- INNER JOINs exclude customers not present in `customers_segments`
- Verify: a customer with a MARKETING_SMS preference but no entry in `customers_segments` does NOT appear in any segment's count

#### TC-6e: opted_in = NULL
- **Requirement**: FSD Section 4 SQL Design Note 6
- NULL values in `opted_in` are treated as non-1 by the CASE expression (falls to ELSE 0)
- NULL rows are still counted by COUNT(*)
- Verify: NULL `opted_in` contributes 0 to `opted_in_count` but +1 to `total_count`

#### TC-6f: Overwrite Mode with Auto-Advance (BRD Write Mode Implications)
- **Requirement**: FSD Section 5, BRD Write Mode Implications
- In auto-advance mode across multiple dates, only the last day's output persists on disk
- Within a single run, GROUP BY `as_of` produces rows for all dates in the effective range
- Verify behavior matches V1

#### TC-6g: Structural Twin Consistency (BRD Edge Case 4)
- **Requirement**: BRD Edge Case 4
- This job is structurally identical to EmailOptInRate with only the preference_type filter changed from `'MARKETING_EMAIL'` to `'MARKETING_SMS'`
- Verify the SQL structure matches EmailOptInRate V2, differing only in the WHERE value
- Verify the same W4 integer division behavior applies to both jobs

### TC-7: Proofmark Configuration
- **Requirement**: FSD Section 8
- **comparison_target**: `sms_opt_in_rate`
- **reader**: `parquet`
- **threshold**: `100.0` (strict -- all columns deterministic)
- **excluded columns**: None
- **fuzzy columns**: None
- Rationale: All output columns are deterministic. Integer division (W4) produces exact integer results (0 or 1) with no floating-point epsilon concerns. No timestamps or random values. BRD states "Non-Deterministic Fields: None identified."

## W-Code Test Cases

### TC-W1: W4 -- Integer Division Truncation
- **Requirement**: BR-4, FSD Section 6, BRD Edge Case 1
- **What the wrinkle is:** `opt_in_rate` is computed as `CAST(SUM(...) AS INTEGER) / CAST(COUNT(*) AS INTEGER)`, which performs integer division. The result is always 0 (when not all customers opted in) or 1 (when 100% of customers in a segment opted in). A segment with 99% opt-in rate gets `opt_in_rate = 0`.
- **How V2 handles it:** V2 reproduces the exact same SQL expression with a `/* W4: ... */` comment documenting the wrinkle. SQLite's native integer division produces the identical truncation behavior.
- **What to verify:**
  1. For a segment where `opted_in_count < total_count`, verify `opt_in_rate = 0`
  2. For a segment where `opted_in_count = total_count`, verify `opt_in_rate = 1`
  3. Verify `opt_in_rate` is never a decimal value (e.g., 0.65)
  4. Verify V2 `opt_in_rate` matches V1 `opt_in_rate` for every row

## Notes
- This is a clean Tier 1 job. The SQL is straightforward and unchanged between V1 and V2.
- The only structural change is removing the unused `preference_id` column from `customer_preferences` DataSourcing (AP4). This does not affect output.
- Unlike the structural twin EmailOptInRate, this job has NO dead-end data sources (AP1 does not apply). BRD Edge Case 5 explicitly confirms this.
- The integer division bug (W4) is the only output-affecting wrinkle. It is preserved intentionally for output equivalence.
- The V2 SQL includes a `/* W4: ... */` comment documenting the wrinkle -- verify the comment is present in the config.
- No External module exists in V1 or V2 for this job.
