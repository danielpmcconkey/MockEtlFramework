# PreferenceSummary -- Test Plan

## Job Summary

**V2 Job**: PreferenceSummaryV2
**Tier**: 1 (Framework Only) -- DataSourcing -> Transformation (SQL) -> CsvFileWriter
**Writer**: CsvFileWriter (includeHeader=true, trailerFormat=`TRAILER|{row_count}|{date}`, writeMode=Overwrite, lineEnding=LF)
**Output**: `Output/double_secret_curated/preference_summary.csv`

---

## Test Cases

### TC-01: Happy Path -- Per-Type Aggregation Produces Correct Opt-In Counts

**Traces to**: BR-1, FSD Section 4 (Transformation SQL)
**Description**: For each distinct `preference_type`, `opted_in_count` equals the number of rows where `opted_in = true` (1) for that type across the effective date range.
**Expected behavior**: V2 output has one row per preference_type. `opted_in_count` matches `SELECT COUNT(*) FROM datalake.customer_preferences WHERE preference_type = X AND opted_in = true AND as_of BETWEEN min AND max` for each type.
**Proofmark verification**: Strict CSV comparison at 100% threshold with header_rows=1 and trailer_rows=1.

### TC-02: Happy Path -- Per-Type Opt-Out Counts

**Traces to**: BR-1, FSD Section 4
**Description**: For each distinct `preference_type`, `opted_out_count` equals the number of rows where `opted_in = false` (0) for that type.
**Expected behavior**: `opted_out_count` matches `SUM(CASE WHEN opted_in = 0 THEN 1 ELSE 0 END)` per type. Matches V1's External module counter logic.
**Proofmark verification**: Covered by full strict CSV comparison.

### TC-03: Happy Path -- Total Customers Derivation

**Traces to**: BR-2, FSD Section 4 (SQL Design Note 3)
**Description**: `total_customers` equals `opted_in_count + opted_out_count` for each preference type, matching V1's `kvp.Value.optedIn + kvp.Value.optedOut` logic.
**Expected behavior**: V2's SQL computes `SUM(CASE opted_in=1) + SUM(CASE opted_in=0) AS total_customers`. This is identical to V1's addition of the two counters. If all rows are either opted_in=true or opted_in=false (no NULLs), this equals COUNT(*) per group. Values match V1 exactly.
**Proofmark verification**: Strict comparison catches any arithmetic deviation.

### TC-04: Happy Path -- as_of From Earliest Row

**Traces to**: BR-3, FSD Section 4 (SQL Design Note 4)
**Description**: V1 sets `as_of` from `prefs.Rows[0]["as_of"]` -- the first row of the DataFrame, which is the minimum date because DataSourcing orders by `as_of`. V2 uses `MIN(as_of)` in the GROUP BY.
**Expected behavior**: All output rows have the same `as_of` value, equal to the minimum effective date in the DataSourcing range. For a single-date run, all rows share that date. For multi-date runs, all rows share the earliest date. Matches V1.
**Proofmark verification**: Any as_of mismatch would appear in strict column comparison.

### TC-05: Happy Path -- Output Schema and Column Order

**Traces to**: BRD Output Schema, FSD Traceability Matrix
**Description**: V2 CSV output has columns: `preference_type`, `opted_in_count`, `opted_out_count`, `total_customers`, `as_of` -- matching V1's output DataFrame schema.
**Expected behavior**: The header row in the CSV lists exactly these 5 columns in this order. No extra columns, no missing columns, no reordering.
**Proofmark verification**: CSV comparison implicitly validates column structure through header matching.

### TC-06: Happy Path -- Five Preference Types in Output

**Traces to**: BRD Edge Case 3, FSD Section 4
**Description**: The datalake contains 5 distinct preference types: PAPER_STATEMENTS, E_STATEMENTS, MARKETING_EMAIL, MARKETING_SMS, PUSH_NOTIFICATIONS. Each gets one row in the output.
**Expected behavior**: V2 output has exactly 5 data rows (plus 1 header and 1 trailer). Row count matches V1.
**Proofmark verification**: Row count mismatch would be flagged by Proofmark.

---

### Writer Config Verification

### TC-07: Writer -- CSV Header Present

**Traces to**: BRD Writer Configuration (includeHeader=true), FSD Section 5
**Description**: V2 output CSV includes a header row as the first line.
**Expected behavior**: First line of the CSV is the column names, comma-separated. Matches V1.
**Proofmark verification**: Proofmark config sets `header_rows: 1` to skip the header during data comparison.

### TC-08: Writer -- Trailer Format

**Traces to**: BR-7, BRD Writer Configuration (trailerFormat), FSD Section 5
**Description**: V2 output CSV has a trailer row in the format `TRAILER|{row_count}|{date}` where `{row_count}` is the number of data rows and `{date}` is `__maxEffectiveDate`.
**Expected behavior**: The last line of the CSV is `TRAILER|5|YYYY-MM-DD` (5 preference types, date = max effective date). The trailer uses pipe delimiters, not commas. `{row_count}` is substituted by CsvFileWriter with the DataFrame row count. `{date}` is substituted with the max effective date from shared state. Matches V1 byte-for-byte.
**Proofmark verification**: Proofmark config sets `trailer_rows: 1` to strip the trailer. If the trailer format differs, the row stripping might fail or leave residual data that causes comparison failure.

### TC-09: Writer -- LF Line Endings

**Traces to**: BRD Writer Configuration (lineEnding=LF), FSD Section 5
**Description**: V2 output uses LF (`\n`) line endings, not CRLF (`\r\n`).
**Expected behavior**: Every line in the CSV terminates with a single `\n` byte. Matches V1.
**Proofmark verification**: Line ending mismatches would cause byte-level differences in comparison.

### TC-10: Writer -- Overwrite Mode

**Traces to**: BRD Write Mode Implications, FSD Section 5
**Description**: V2 uses writeMode=Overwrite. Each execution replaces the entire CSV file.
**Expected behavior**: After multi-day auto-advance, only the last day's output persists on disk. The file contains data for the last effective date only (since each run replaces the file). Matches V1 behavior.
**Proofmark verification**: Both V1 and V2 files are the final-day output. Comparison is between these final files.

---

### Edge Cases

### TC-11: Edge Case -- NULL Preference Type Coalesced to Empty String

**Traces to**: BR-1, FSD Section 4 (SQL Design Note 1)
**Description**: V1 applies `row["preference_type"]?.ToString() ?? ""` which coalesces NULL preference_type to empty string. V2 uses `COALESCE(preference_type, '')` in both SELECT and GROUP BY.
**Expected behavior**: If any rows have NULL `preference_type`, they are grouped under an empty-string key, matching V1's Dictionary behavior. If no NULLs exist in the data, this has no effect but is still correct.
**Proofmark verification**: If NULLs existed and were handled differently, the row values would mismatch.

### TC-12: Edge Case -- Row Ordering Matches V1 Dictionary Insertion Order

**Traces to**: BR-8, FSD Section 4 (SQL Design Note 5)
**Description**: V1's output order is determined by Dictionary insertion order in C#, which follows the order preference types are first encountered when iterating the DataFrame. V2 uses `ORDER BY MIN(rowid)` to replicate this.
**Expected behavior**: Output rows appear in this order: PAPER_STATEMENTS, E_STATEMENTS, MARKETING_EMAIL, MARKETING_SMS, PUSH_NOTIFICATIONS. This matches V1's order, which is determined by the PostgreSQL heap scan order through DataSourcing into the Dictionary.
**Proofmark verification**: If Proofmark is row-order-sensitive for CSV, mismatched ordering would cause failure. The ORDER BY MIN(rowid) strategy is specifically designed to prevent this.

### TC-13: Edge Case -- Cross-Date Accumulation

**Traces to**: BRD Edge Case 1, FSD Open Question 3
**Description**: When the effective date range spans multiple days, counts accumulate across all dates. V2's GROUP BY preference_type (without date filtering) accumulates identically to V1's foreach loop (which also does not filter by date).
**Expected behavior**: Both V1 and V2 accumulate counts across all dates in the range. The `as_of` column shows the earliest date (MIN(as_of) in V2, Rows[0] in V1). Since writeMode is Overwrite, the final output reflects the last auto-advance day's run -- which for a single-day effective range shows just that day's data.
**Proofmark verification**: The final output files (after auto-advance) match. Cross-date accumulation is identical in both versions.

### TC-14: Edge Case -- Boolean Handling (opted_in through SQLite)

**Traces to**: FSD Section 4 (SQL Design Note 2)
**Description**: PostgreSQL `boolean` values are converted to SQLite integers (true->1, false->0) by the framework's Transformation module. V2 SQL uses `CASE WHEN opted_in = 1` and `CASE WHEN opted_in = 0`.
**Expected behavior**: The boolean-to-integer conversion is handled transparently by the framework. V2's integer comparisons in SQL produce the same counts as V1's `Convert.ToBoolean()` approach.
**Proofmark verification**: Incorrect boolean handling would produce wrong opt-in/opt-out counts, caught by strict comparison.

### TC-15: Edge Case -- Empty DataFrame (Zero Rows for Date Range)

**Traces to**: BR-6, FSD Open Question 1
**Description**: If `customer_preferences` has no rows for the effective date range, V1 produces an empty DataFrame. V2's SQL Transformation would fail because the SQLite table is not registered for empty DataFrames.
**Expected behavior**: This edge case does NOT arise in the 2024-10-01 to 2024-12-31 date range (data exists for all dates). The behavioral difference is documented but will not affect Proofmark validation. If it did occur, V2 would error out rather than produce an empty CSV.
**Proofmark verification**: Not testable in Phase D -- datalake has data for all dates in range. Documented as a known limitation.

---

### Anti-Pattern Elimination Verification

### TC-16: AP1 -- Dead-End Source (customers table) Eliminated

**Traces to**: BR-4, FSD Section 3, FSD Section 7 (AP1)
**Description**: V1 sources `datalake.customers` (id, first_name, last_name) but the External module never references the customers DataFrame. V2 must NOT source this table.
**Expected behavior**: V2 job config (`preference_summary_v2.json`) has no DataSourcing entry for `customers`. Output is unaffected because the table was never used by V1's processing logic.
**Proofmark verification**: If eliminating the dead source somehow changed output, Proofmark catches it.

### TC-17: AP3 -- Unnecessary External Module Eliminated

**Traces to**: FSD Section 2 (Tier Justification), FSD Section 7 (AP3)
**Description**: V1 uses `PreferenceSummaryCounter.cs` -- a C# External module doing GROUP BY + conditional counting that is entirely expressible in SQL. V2 replaces it with a Tier 1 SQL Transformation.
**Expected behavior**: V2 job config uses `Transformation` module type (not `External`). No `PreferenceSummaryV2Processor.cs` file exists. The SQL produces identical output to the C# loop.
**Proofmark verification**: The entire Proofmark comparison validates that the SQL replacement produces equivalent output.

### TC-18: AP4 -- Unused Columns Eliminated

**Traces to**: BR-5, FSD Section 3, FSD Section 7 (AP4)
**Description**: V1 sources `preference_id`, `customer_id`, and `updated_date` from `customer_preferences` but none are used in the aggregation logic. V2 sources only `[preference_type, opted_in]`.
**Expected behavior**: V2 DataSourcing config lists only `[preference_type, opted_in]` (plus `as_of` auto-appended by framework). No unused columns sourced.
**Proofmark verification**: Column removal from DataSourcing is a read-side change. Output equivalence confirmed by comparison.

### TC-19: AP6 -- Row-by-Row Iteration Eliminated

**Traces to**: FSD Section 7 (AP6)
**Description**: V1 uses a `foreach` loop with manual Dictionary counting. V2 replaces this with SQL `GROUP BY` + `SUM(CASE WHEN ...)` -- a set-based aggregation.
**Expected behavior**: V2 does not use an External module. The SQL GROUP BY is the set-based equivalent of V1's row-by-row loop. Output matches V1.
**Proofmark verification**: Covered by the full comparison. Set-based and row-based approaches produce identical results for this aggregation.

---

### Proofmark Configuration Verification

### TC-20: Proofmark Config Correctness

**Traces to**: FSD Section 8 (Proofmark Config)
**Description**: The Proofmark config for this job must use `reader: csv`, `threshold: 100.0`, `header_rows: 1`, `trailer_rows: 1`, with zero excluded columns and zero fuzzy columns.
**Expected behavior**: Config file at `POC3/proofmark_configs/preference_summary.yaml` contains:
```yaml
comparison_target: "preference_summary"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```
No `columns` section. Strict comparison only.
**Rationale**: All output columns are deterministic -- string passthroughs (preference_type), integer aggregates (opted_in_count, opted_out_count, total_customers), and a date (as_of). No non-deterministic fields per BRD. No floating-point operations. The trailer uses standard CsvFileWriter tokens ({row_count}, {date}) which are deterministic. `trailer_rows: 1` because writeMode=Overwrite produces a single trailer at file end.

---

## Traceability Summary

| BRD Requirement | Test Case(s) |
|-----------------|-------------|
| BR-1 (Group by preference_type, count opted_in/opted_out) | TC-01, TC-02, TC-06, TC-11 |
| BR-2 (total_customers = opted_in + opted_out) | TC-03 |
| BR-3 (as_of from first row / earliest date) | TC-04 |
| BR-4 (customers table unused) | TC-16 |
| BR-5 (updated_date unused) | TC-18 |
| BR-6 (empty DataFrame guard) | TC-15 |
| BR-7 (trailer with row_count and date) | TC-08 |
| BR-8 (row order = Dictionary insertion order) | TC-12 |
| BRD Output Schema | TC-05 |
| BRD Writer Config (includeHeader) | TC-07 |
| BRD Writer Config (trailerFormat) | TC-08 |
| BRD Writer Config (writeMode=Overwrite) | TC-10 |
| BRD Writer Config (lineEnding=LF) | TC-09 |
| BRD Edge Case 1 (cross-date accumulation) | TC-13 |
| BRD Edge Case 3 (5 preference types) | TC-06 |
| BRD Edge Case 4 (as_of from first row) | TC-04 |
| FSD AP1 elimination | TC-16 |
| FSD AP3 elimination | TC-17 |
| FSD AP4 elimination | TC-18 |
| FSD AP6 elimination | TC-19 |
| FSD Proofmark Config | TC-20 |
