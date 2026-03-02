# PreferenceChangeCount -- Test Plan

## Job Summary

**V2 Job**: PreferenceChangeCountV2
**Tier**: 1 (Framework Only) -- DataSourcing -> Transformation (SQL) -> ParquetFileWriter
**Writer**: ParquetFileWriter (numParts=1, writeMode=Overwrite)
**Output**: `Output/double_secret_curated/preference_change_count/`

---

## Test Cases

### TC-01: Happy Path -- Per-Customer Aggregation Produces Correct Counts

**Traces to**: BR-2, BR-5, FSD Section 4 (V2 SQL)
**Description**: For each customer on each `as_of` date, `preference_count` equals the total number of preference rows (COUNT(*)) for that customer on that date.
**Expected behavior**: V2 output has one row per (customer_id, as_of) pair. The `preference_count` column matches the row count from `SELECT COUNT(*) FROM datalake.customer_preferences WHERE customer_id = X AND as_of = Y` for every customer/date combination.
**Proofmark verification**: Strict Parquet comparison at 100% threshold. Row counts and all column values must match V1 exactly.

### TC-02: Happy Path -- Email Opt-In Flag

**Traces to**: BR-3, FSD Section 4
**Description**: `has_email_opt_in` is 1 if the customer has at least one preference row with `preference_type = 'MARKETING_EMAIL'` AND `opted_in = true` on that date, otherwise 0.
**Expected behavior**: MAX(CASE WHEN preference_type = 'MARKETING_EMAIL' AND opted_in = 1 THEN 1 ELSE 0 END) produces the correct binary flag. A customer with multiple MARKETING_EMAIL rows where at least one is opted_in=true gets 1. A customer with only opted_in=false MARKETING_EMAIL rows gets 0. A customer with no MARKETING_EMAIL rows gets 0.
**Proofmark verification**: Column `has_email_opt_in` in V2 Parquet matches V1 exactly.

### TC-03: Happy Path -- SMS Opt-In Flag

**Traces to**: BR-4, FSD Section 4
**Description**: `has_sms_opt_in` is 1 if the customer has at least one preference row with `preference_type = 'MARKETING_SMS'` AND `opted_in = true` on that date, otherwise 0.
**Expected behavior**: Identical logic to TC-02 but for MARKETING_SMS. All values match V1.
**Proofmark verification**: Column `has_sms_opt_in` in V2 Parquet matches V1 exactly.

### TC-04: Happy Path -- Output Schema and Column Order

**Traces to**: BRD Output Schema, FSD Section 4 (Column order), FSD Traceability Matrix
**Description**: V2 Parquet output has exactly 5 columns in order: `customer_id`, `preference_count`, `has_email_opt_in`, `has_sms_opt_in`, `as_of`.
**Expected behavior**: The Parquet schema matches V1 byte-for-byte. No extra columns, no missing columns, no reordering.
**Proofmark verification**: Proofmark Parquet reader validates schema match implicitly -- mismatched schemas would cause comparison failure.

### TC-05: Happy Path -- Writer Config Match

**Traces to**: BRD Writer Configuration, FSD Section 5
**Description**: V2 uses ParquetFileWriter with `numParts=1`, `writeMode=Overwrite`, outputting to `Output/double_secret_curated/preference_change_count/`.
**Expected behavior**: Single Parquet part file in the output directory. After multi-day auto-advance, only the final run's output exists on disk (Overwrite mode). Output directory structure matches V1's `Output/curated/preference_change_count/`.
**Proofmark verification**: Proofmark config uses `reader: parquet`. Comparison succeeds if writer config matches.

### TC-06: Happy Path -- Full Date Range Execution

**Traces to**: FSD Section 5 (Write mode note), BRD Write Mode Implications
**Description**: Running V2 for the full date range (2024-10-01 through 2024-12-31) produces output containing rows for all `as_of` dates in the range, since SQL groups by `as_of` and DataSourcing pulls the full date range.
**Expected behavior**: The final Parquet output contains rows spanning the entire effective date range, not just the last date. Row count matches V1 for the same date range.
**Proofmark verification**: Total row count match between V1 and V2 Parquet directories.

---

### Edge Cases

### TC-07: Edge Case -- Dead RANK() Elimination Does Not Affect Output

**Traces to**: BR-1, FSD Section 7 (AP8), BRD Edge Case 1
**Description**: V1's SQL computes `RANK() OVER (PARTITION BY customer_id, preference_type ORDER BY preference_id) AS rnk` in the `all_prefs` CTE, but `rnk` is never referenced downstream. V2 removes the entire CTE and RANK() computation.
**Expected behavior**: Removing the RANK() and CTEs produces identical output because: (a) the CTE passes through all rows unchanged (no WHERE/HAVING), (b) `rnk` is never used in the `summary` CTE or final SELECT, (c) the GROUP BY operates on the same row set. Proofmark comparison passes at 100%.
**Proofmark verification**: Full strict comparison -- if CTE removal introduced any row-level difference, Proofmark catches it.

### TC-08: Edge Case -- Multiple Opt-In Rows Per Customer Per Type

**Traces to**: BR-3, BR-4, BRD Edge Case 4
**Description**: A customer may have multiple rows for the same preference_type on the same date (e.g., two MARKETING_EMAIL rows, one opted_in=true, one opted_in=false). The MAX(CASE) expression should return 1 if ANY matching row has opted_in=true.
**Expected behavior**: Even if a customer has mixed opted_in values for the same preference_type on the same date, `has_email_opt_in` or `has_sms_opt_in` is 1 as long as at least one row has opted_in=true. This matches V1's MAX() behavior.
**Proofmark verification**: Covered by the full Parquet comparison. Any deviation would appear as a value mismatch.

### TC-09: Edge Case -- Customer With No Email/SMS Preferences

**Traces to**: BR-3, BR-4, FSD Section 4
**Description**: A customer who has preference rows but none with preference_type = 'MARKETING_EMAIL' or 'MARKETING_SMS' should have `has_email_opt_in = 0` and `has_sms_opt_in = 0`.
**Expected behavior**: The CASE WHEN conditions return 0 (the ELSE branch) for every row, so MAX() returns 0. This is correct and matches V1.
**Proofmark verification**: Covered by full comparison.

### TC-10: Edge Case -- Opted-In Boolean Handling in SQLite

**Traces to**: FSD Section 4 (opted_in comparison), FSD SQL Design Rationale
**Description**: The PostgreSQL source column `opted_in` is boolean. DataSourcing loads it through the framework which converts to SQLite integers (true -> 1, false -> 0). The SQL compares `opted_in = 1`.
**Expected behavior**: The boolean-to-integer conversion is handled by the framework's DataSourcing/Transformation pipeline. The `= 1` comparison in SQL correctly matches `true` values. Output matches V1, which uses the same `opted_in = 1` comparison.
**Proofmark verification**: Any conversion issue would manifest as incorrect opt-in flag values, caught by strict comparison.

### TC-11: Edge Case -- Row Ordering in Parquet

**Traces to**: FSD Open Question 3
**Description**: Neither V1 nor V2 SQL includes an ORDER BY clause. Row order depends on SQLite's GROUP BY processing order.
**Expected behavior**: Since V2 uses the same GROUP BY keys (`customer_id, as_of`) and the same SQLite engine, row ordering should match V1. If Proofmark fails due to ordering, an ORDER BY clause may need to be added (documented as FSD open question).
**Proofmark verification**: If Proofmark is order-sensitive for Parquet, ordering differences would cause failure. This test case tracks the risk.

---

### Anti-Pattern Elimination Verification

### TC-12: AP1 -- Dead-End Source (customers table) Eliminated

**Traces to**: BR-6, FSD Section 3 (NOT sourced in V2), FSD Section 7 (AP1)
**Description**: V1 sources `datalake.customers` (id, prefix, first_name, last_name) but the SQL never references it. V2 must NOT source this table.
**Expected behavior**: The V2 job config (`preference_change_count_v2.json`) does not contain a DataSourcing entry for the `customers` table. Output is unaffected because the table was never used.
**Proofmark verification**: If eliminating the dead source somehow changed output (it shouldn't), Proofmark catches it.

### TC-13: AP4 -- Unused Columns (preference_id, updated_date) Eliminated

**Traces to**: BR-1, BR-7, FSD Section 3, FSD Section 7 (AP4)
**Description**: V1 sources `preference_id` (only used in dead RANK()) and `updated_date` (never referenced). V2 must NOT source these columns.
**Expected behavior**: V2 DataSourcing config for `customer_preferences` lists only `[customer_id, preference_type, opted_in]`. No `preference_id` or `updated_date`. Output is unaffected.
**Proofmark verification**: Column removal from DataSourcing is a read-side change. If it affected output, Proofmark catches it.

### TC-14: AP8 -- Unused CTEs and RANK() Eliminated

**Traces to**: BR-1, FSD Section 4, FSD Section 7 (AP8)
**Description**: V1 SQL has two CTEs (`all_prefs` with RANK(), `summary` with aggregation). V2 replaces with a single direct SELECT...GROUP BY.
**Expected behavior**: The simplified SQL produces identical output. This is the core equivalence claim. Proofmark validates it.
**Proofmark verification**: The entire Proofmark comparison for this job is essentially testing this claim.

### TC-15: AP9 -- Misleading Name Documented

**Traces to**: BRD Edge Case 3, FSD Section 7 (AP9)
**Description**: The job name "PreferenceChangeCount" implies change tracking, but the job counts total rows. V2 cannot rename the job (output path must match).
**Expected behavior**: V2 job is named `PreferenceChangeCountV2`. Output path is `preference_change_count` (unchanged). The misleading name is documented in the FSD but not correctable.
**Proofmark verification**: Not directly testable via Proofmark. Verified by FSD review.

---

### Proofmark Configuration Verification

### TC-16: Proofmark Config Correctness

**Traces to**: FSD Section 8 (Proofmark Config)
**Description**: The Proofmark config for this job must use `reader: parquet`, `threshold: 100.0`, with zero excluded columns and zero fuzzy columns.
**Expected behavior**: Config file at `POC3/proofmark_configs/preference_change_count.yaml` contains:
```yaml
comparison_target: "preference_change_count"
reader: parquet
threshold: 100.0
```
No `columns` section. No `csv` section. Strict comparison only.
**Rationale**: All output columns are deterministic integers or dates from aggregation (COUNT, MAX CASE, GROUP BY key). No non-deterministic fields per BRD. No floating-point operations requiring fuzzy tolerance.

---

## Traceability Summary

| BRD Requirement | Test Case(s) |
|-----------------|-------------|
| BR-1 (Dead RANK computation) | TC-07, TC-14 |
| BR-2 (preference_count = COUNT(*)) | TC-01, TC-06 |
| BR-3 (has_email_opt_in flag) | TC-02, TC-08, TC-09 |
| BR-4 (has_sms_opt_in flag) | TC-03, TC-08, TC-09 |
| BR-5 (GROUP BY customer_id, as_of) | TC-01, TC-06 |
| BR-6 (customers table unused) | TC-12 |
| BR-7 (updated_date unused) | TC-13 |
| BRD Output Schema | TC-04 |
| BRD Writer Config | TC-05 |
| BRD Edge Case 1 (Dead RANK) | TC-07 |
| BRD Edge Case 3 (Misleading name) | TC-15 |
| BRD Edge Case 4 (Multiple opt-in rows) | TC-08 |
| FSD AP1 elimination | TC-12 |
| FSD AP4 elimination | TC-13 |
| FSD AP8 elimination | TC-14 |
| FSD AP9 documentation | TC-15 |
| FSD Proofmark Config | TC-16 |
