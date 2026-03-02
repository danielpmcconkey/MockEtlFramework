# BondMaturitySchedule — Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01 | BR-1 | Only securities with security_type = 'Bond' are included |
| TC-02 | BR-2 | Empty DataFrame returned when no securities data exists |
| TC-03 | BR-3 | Empty DataFrame returned when no bonds exist after filtering |
| TC-04 | BR-4 | Holdings aggregated per bond: SUM(current_value) and COUNT(rows) |
| TC-05 | BR-5 | total_held_value rounded to 2 decimal places |
| TC-06 | BR-6 | Bonds with no matching holdings get zeros |
| TC-07 | BR-7 | as_of column set to __maxEffectiveDate |
| TC-08 | BR-8 | Output row ordering is deterministic |
| TC-09 | BR-9 | Holdings joined to bonds via security_id; non-bond holdings skipped |
| TC-10 | BR-10 | NULL ticker, security_name, sector default to empty string |
| TC-11 | BR-11 | Effective date range injected by executor, no explicit date filters |
| TC-12 | BR-1 | NULL security_type excluded (not equal to 'Bond') |
| TC-13 | BR-4, BR-6 | Multiple holdings per bond correctly summed and counted |
| TC-14 | BR-5 (W5) | Rounding mode: banker's vs half-away-from-zero on midpoint values |
| TC-15 | — | Output column order matches spec |
| TC-16 | — | Parquet writer config: numParts=1, writeMode=Overwrite |
| TC-17 | — | Proofmark: all columns STRICT, no EXCLUDED or FUZZY |
| TC-18 | — | Zero-row output produces valid empty Parquet file |
| TC-19 | — | Multi-day effective date range: cross-date aggregation behavior |
| TC-20 | — | Weekend effective date: holdings may be absent while securities present |

## Test Cases

### TC-01: Bond-Only Filter
- **Traces to:** BR-1
- **Input conditions:** Securities table contains rows with security_type values 'Bond', 'Stock', 'ETF', and 'MutualFund'. Holdings exist for securities of all types.
- **Expected output:** Only securities with security_type = 'Bond' appear in the output. No Stock, ETF, or MutualFund securities are present.
- **Verification method:** Run V2 job for a single effective date. Verify output contains only security_ids that correspond to Bond-type securities in the source data. Cross-check by querying `datalake.securities WHERE security_type = 'Bond'` for the same date.

### TC-02: Empty Securities Data
- **Traces to:** BR-2
- **Input conditions:** Effective date range selected such that no securities data exists (hypothetical edge case — may require a date outside the data range).
- **Expected output:** An empty DataFrame is written to Parquet. The Parquet file exists but contains zero data rows. The schema should still contain all 7 output columns.
- **Verification method:** Inspect the output Parquet file. Verify it has zero rows and the correct column schema (security_id, ticker, security_name, sector, total_held_value, holder_count, as_of).

### TC-03: No Bonds After Filtering
- **Traces to:** BR-3
- **Input conditions:** Securities table for the effective date contains securities but none with security_type = 'Bond' (e.g., only Stocks and ETFs).
- **Expected output:** An empty DataFrame is written to Parquet. Zero rows, but schema preserved.
- **Verification method:** Same as TC-02 — verify Parquet file has zero data rows with correct schema.

### TC-04: Aggregation Logic — SUM and COUNT
- **Traces to:** BR-4
- **Input conditions:** A bond security (e.g., security_id = 100) has 3 matching holdings rows with current_value of 1000.00, 2500.50, and 750.25.
- **Expected output:** For security_id 100: total_held_value = 4250.75, holder_count = 3.
- **Verification method:** Run V2 job. Query the output Parquet for security_id 100. Manually compute SUM(1000.00 + 2500.50 + 750.25) = 4250.75 and COUNT = 3. Compare.

### TC-05: Rounding to 2 Decimal Places
- **Traces to:** BR-5
- **Input conditions:** Holdings for a bond sum to a value with more than 2 decimal places (e.g., current_value entries: 100.111, 200.222, 300.333 => raw sum = 600.666).
- **Expected output:** total_held_value = 600.67 (rounded to 2dp).
- **Verification method:** Verify total_held_value in output matches ROUND(SUM, 2). Note: SQLite rounds half-away-from-zero. For typical monetary data (2dp source values), the sum itself typically has <= 2dp and ROUND is a no-op. This test verifies the ROUND function is applied.

### TC-06: Bond With No Holdings
- **Traces to:** BR-6
- **Input conditions:** A bond security exists in securities but has zero matching rows in holdings (no holdings row with that security_id).
- **Expected output:** The bond still appears in the output with total_held_value = 0.00 and holder_count = 0.
- **Verification method:** Identify a bond security_id with no holdings match. Verify it appears in the output with zero values. This validates the LEFT JOIN behavior.

### TC-07: as_of From __maxEffectiveDate
- **Traces to:** BR-7
- **Input conditions:** Run the V2 job for a specific effective date (e.g., 2024-10-15). The securities table has as_of = 2024-10-15 for that date.
- **Expected output:** Every row in the output has as_of = 2024-10-15. The FSD derives this via MAX(s.as_of) from date-filtered data, which equals __maxEffectiveDate.
- **Verification method:** Read the output Parquet. Confirm all as_of values equal the effective date used in the run. Compare V1 and V2 — both should produce the same as_of value.

### TC-08: Deterministic Row Ordering
- **Traces to:** BR-8
- **Input conditions:** Multiple bond securities exist in the data.
- **Expected output:** V2 output is ordered by security_id ascending (per the FSD's ORDER BY s.security_id). V1 output follows source iteration order (MEDIUM confidence per BRD). For single-day runs, these should typically agree.
- **Verification method:** Run both V1 and V2 for the same effective date. Compare row ordering. V2 guarantees ORDER BY security_id. If V1 differs, this is a known divergence documented in the FSD — Proofmark comparison should still pass if the Parquet reader sorts by key.

### TC-09: Holdings Join — Non-Bond Holdings Skipped
- **Traces to:** BR-9
- **Input conditions:** Holdings table contains rows whose security_id maps to non-Bond securities (Stocks, ETFs, etc.).
- **Expected output:** These holdings do not contribute to any output row. They are excluded because the securities WHERE clause filters to Bonds only, and the LEFT JOIN only includes bonds.
- **Verification method:** Verify no output row has inflated total_held_value from non-bond holdings. Compare the sum of current_value for a given bond's security_id against the output total_held_value.

### TC-10: NULL String Fields Default to Empty String
- **Traces to:** BR-10
- **Input conditions:** A bond security has NULL ticker, NULL security_name, or NULL sector in the securities table.
- **Expected output:** The corresponding output columns contain empty string (''), not NULL.
- **Verification method:** Check output Parquet for the specific security_id. Verify ticker, security_name, and sector are empty strings, not null. The FSD uses COALESCE(s.ticker, ''), COALESCE(s.security_name, ''), COALESCE(s.sector, '').

### TC-11: Effective Date Injection
- **Traces to:** BR-11
- **Input conditions:** Run the V2 job via the executor for a specific effective date. No minEffectiveDate/maxEffectiveDate in the job config JSON.
- **Expected output:** DataSourcing correctly picks up the injected __minEffectiveDate and __maxEffectiveDate from shared state. Output reflects data only for the specified effective date range.
- **Verification method:** Run the job for a known date and verify output as_of matches the effective date. Verify no data from other dates leaks into the output (for a single-day run, only that date's data should be present).

### TC-12: NULL security_type Excluded
- **Traces to:** BR-1
- **Input conditions:** Securities table contains rows where security_type is NULL.
- **Expected output:** These securities do not appear in the output. In SQL, NULL != 'Bond' evaluates to UNKNOWN (falsy), so the WHERE clause excludes them.
- **Verification method:** Confirm no security_id with NULL security_type appears in the output.

### TC-13: Multiple Holdings Per Bond
- **Traces to:** BR-4, BR-6
- **Input conditions:** Bond A has 5 holdings, Bond B has 1 holding, Bond C has 0 holdings.
- **Expected output:** Bond A: holder_count = 5, total_held_value = sum of 5 current_values. Bond B: holder_count = 1, total_held_value = that one current_value. Bond C: holder_count = 0, total_held_value = 0.
- **Verification method:** For each bond in the output, manually compute expected values from the source holdings data. Validate all three scenarios (many, one, zero holdings).

### TC-14: W5 — Rounding Mode Midpoint Behavior
- **Traces to:** BR-5, W5 from FSD
- **Input conditions:** Construct a scenario (or look for natural data) where the SUM of current_value for a bond lands exactly at a midpoint (e.g., X.XX5). For example, holdings sum to exactly 100.125.
- **Expected output:** V1 uses C# Math.Round with MidpointRounding.ToEven (banker's rounding): 100.125 -> 100.12. V2 uses SQLite ROUND (half-away-from-zero): 100.125 -> 100.13. If such a midpoint occurs, V1 and V2 will differ by 0.01.
- **Verification method:** Compare V1 and V2 output for the specific security. If a difference is detected, confirm the source values sum to an exact midpoint. This is flagged in the FSD as the first suspect for any Proofmark mismatch. Resolution: upgrade total_held_value to FUZZY with tolerance 0.005 in the Proofmark config.

### TC-15: Output Column Order
- **Traces to:** FSD Section 4
- **Input conditions:** Run the V2 job for any valid effective date.
- **Expected output:** Parquet output columns are in this exact order: security_id, ticker, security_name, sector, total_held_value, holder_count, as_of.
- **Verification method:** Read the Parquet file schema metadata. Verify column names and order match the specification exactly.

### TC-16: Parquet Writer Configuration
- **Traces to:** BRD Writer Configuration, FSD Section 7
- **Input conditions:** Run the V2 job.
- **Expected output:** Output is written to `Output/double_secret_curated/bond_maturity_schedule/` as a single Parquet part file (numParts=1). Write mode is Overwrite (previous content replaced).
- **Verification method:** Verify exactly one `part-00000.parquet` file exists in the output directory. Verify no stale files from prior runs remain (Overwrite mode clears the directory).

### TC-17: Proofmark Configuration — All Columns STRICT
- **Traces to:** FSD Section 8
- **Input conditions:** Run Proofmark comparison between V1 (Output/curated/bond_maturity_schedule/) and V2 (Output/double_secret_curated/bond_maturity_schedule/).
- **Expected output:** All columns compared strictly. No EXCLUDED or FUZZY overrides. Threshold = 100.0. Reader = parquet.
- **Verification method:** Confirm the Proofmark YAML config has: comparison_target "bond_maturity_schedule", reader "parquet", threshold 100.0, no columns section. Run Proofmark and expect a PASS. If total_held_value fails due to W5, escalate per the FSD resolution path.

### TC-18: Zero-Row Output Produces Valid Parquet
- **Traces to:** BR-2, BR-3
- **Input conditions:** Effective date chosen such that either no securities exist or no bonds exist after filtering.
- **Expected output:** A valid Parquet file is written with the correct 7-column schema but zero data rows.
- **Verification method:** Read the output Parquet. Verify it parses without error, has the expected schema, and contains zero rows. This validates that the ParquetFileWriter handles empty DataFrames gracefully.

### TC-19: Multi-Day Effective Date Range — Cross-Date Aggregation
- **Traces to:** BRD Edge Case 5, FSD Appendix
- **Input conditions:** Run the job with an effective date range spanning multiple days (e.g., min=2024-10-01, max=2024-10-03). Securities and holdings have data for all 3 days.
- **Expected output:** V1 aggregates across ALL rows from all dates without date-level grouping — the same holding appearing on multiple dates gets counted/summed multiple times (BRD Edge Case 5). V2 uses GROUP BY which collapses duplicates across dates for the same security_id, producing one row per security with MAX(as_of). Under normal auto-advance (single-day, min=max), both produce identical results.
- **Verification method:** Compare V1 and V2 output for a multi-day range. If they differ, this is the documented V2 improvement (FSD Section 5, point 6). For single-day runs (the normal case), both must match.

### TC-20: Weekend Effective Date
- **Traces to:** BRD Edge Case 4
- **Input conditions:** Effective date falls on a Saturday or Sunday. Securities has data for all calendar days. Holdings may skip weekends.
- **Expected output:** If holdings has no rows for the weekend date, bonds still appear in the output (via LEFT JOIN) with total_held_value = 0 and holder_count = 0. The as_of column reflects the weekend date.
- **Verification method:** Run the job for a known weekend date (e.g., a Saturday). Verify bonds appear with zero holdings aggregates if no holdings data exists for that date. Verify as_of equals the weekend date.
