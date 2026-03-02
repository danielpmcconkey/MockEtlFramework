# LoanRiskAssessment -- V2 Test Plan

## Job Info
- **V2 Config**: `loan_risk_assessment_v2.json`
- **Tier**: Tier 2 (Framework + Minimal External)
- **External Module**: `ExternalModules.LoanRiskAssessmentV2Processor`

## Pre-Conditions
1. PostgreSQL database accessible at `172.18.0.1` with `datalake` schema intact.
2. `datalake.loan_accounts` and `datalake.credit_scores` tables populated for effective date range starting `2024-10-01`.
3. V1 baseline output exists at `Output/curated/loan_risk_assessment/` (Parquet, 2 parts).
4. V2 External module `ExternalModules.LoanRiskAssessmentV2Processor` compiled and available in `ExternalModules/bin/Debug/net8.0/ExternalModules.dll`.
5. `dotnet build` succeeds with zero errors.
6. Proofmark tool available at `Tools/proofmark/`.

## Test Cases

### TC-1: Output Schema Validation
**Objective:** Verify V2 output Parquet files contain exactly the correct columns in the correct order and with the correct Parquet types.

**Steps:**
1. Run V2 job for a single effective date (e.g., `2024-10-01`).
2. Read the output Parquet file(s) from `Output/double_secret_curated/loan_risk_assessment/`.
3. Inspect column names and order.
4. Inspect Parquet column types for each field.

**Expected:**
- Column order: `loan_id, customer_id, loan_type, current_balance, interest_rate, loan_status, avg_credit_score, risk_tier, as_of`
- `loan_id`: int? (nullable int)
- `customer_id`: int? (nullable int)
- `loan_type`: string
- `current_balance`: decimal? (nullable decimal) -- NOT double
- `interest_rate`: decimal? (nullable decimal) -- NOT double
- `loan_status`: string
- `avg_credit_score`: decimal? (nullable decimal) -- NOT double
- `risk_tier`: string
- `as_of`: DateOnly? (nullable DateOnly)

**Evidence:** [LoanRiskCalculator.cs:10-14] (column order), [FSD Sec 10] (type mapping), [FSD Open Question #3] (decimal restoration for current_balance/interest_rate).

---

### TC-2: Row Count Equivalence
**Objective:** Verify V2 produces the same number of output rows as V1 for each effective date.

**Steps:**
1. Run V1 job for effective date `2024-10-01`.
2. Run V2 job for effective date `2024-10-01`.
3. Count rows in V1 output (`Output/curated/loan_risk_assessment/`).
4. Count rows in V2 output (`Output/double_secret_curated/loan_risk_assessment/`).
5. Repeat for at least 3 effective dates across the operational range.

**Expected:**
- Row counts match exactly for every effective date tested.
- Expected ~894 rows per date (matching `datalake.loan_accounts` row count per date).

**Evidence:** V1 iterates all loan_accounts rows [LoanRiskCalculator.cs:46]; V2 SQL uses `FROM loan_accounts la` as the driving table [FSD Sec 4]. Every loan row appears in output regardless of credit score existence (LEFT JOIN).

---

### TC-3: Data Content Equivalence
**Objective:** Verify V2 output data matches V1 output data byte-for-byte via Proofmark comparison.

**Steps:**
1. Run V1 job across the full operational date range (auto-advance from `2024-10-01`).
2. Run V2 job across the same date range.
3. Execute Proofmark comparison using `POC3/proofmark_configs/loan_risk_assessment.yaml`.

**Expected:**
- Proofmark reports 100.0% match with zero mismatched rows.
- All 9 columns pass strict comparison.

**Known Risk Areas:**
- **avg_credit_score precision:** V2 computes AVG in SQLite (double) then casts to decimal in the External module. V1 computes AVG in C# using decimal arithmetic [LoanRiskCalculator.cs:41]. For integer credit score inputs averaged across small sets (~3 bureaus), the double-to-decimal conversion should be exact. If Proofmark fails on this column, add a fuzzy tolerance on `avg_credit_score` with evidence.
- **current_balance / interest_rate type:** These pass through SQLite as double. The V2 External module must convert them back to decimal [FSD Open Question #3]. If the conversion introduces precision loss, Proofmark will catch it.
- **DBNull.Value vs null:** V1 uses `DBNull.Value` for missing avg_credit_score [LoanRiskCalculator.cs:68]. Verify ParquetFileWriter handles this identically to `null` [FSD Open Question #4].

**Note (BRD Discrepancy):** BRD BR-2 states `>= 700` for "Low Risk" but V1 code uses `>= 750` [LoanRiskCalculator.cs:60]. V2 correctly follows V1 code, not the BRD. This is NOT a test failure -- it is a known BRD error documented in [FSD Sec 4, point 4].

---

### TC-4: Writer Configuration
**Objective:** Verify V2 writer config matches V1 writer behavior.

**Steps:**
1. Inspect V2 job config writer section.
2. Run V2 job for a single date and verify output structure.
3. Run V2 job for two consecutive dates and verify overwrite behavior.

**Expected:**
- Writer type: `ParquetFileWriter`
- source: `output`
- outputDirectory: `Output/double_secret_curated/loan_risk_assessment/`
- numParts: `2` (produces `part-00000.parquet` and `part-00001.parquet`)
- writeMode: `Overwrite` (each run replaces all files in the directory)
- After running for date D1 then D2, only D2's data exists on disk.

**Evidence:** [loan_risk_assessment.json:39-43] (V1 config), [FSD Sec 5] (V2 config).

---

### TC-5: Anti-Pattern Elimination Verification
**Objective:** Verify all identified V1 anti-patterns are eliminated in V2 without affecting output.

#### TC-5a: AP1 -- Dead-End Sourcing Eliminated
**Steps:**
1. Inspect V2 job config JSON.
2. Verify no DataSourcing entry for `customers` table.
3. Verify no DataSourcing entry for `segments` table.

**Expected:** V2 config has exactly 2 DataSourcing entries: `loan_accounts` and `credit_scores`. The `customers` and `segments` entries from V1 are removed.

**Evidence:** [LoanRiskCalculator.cs:16-17] -- only `loan_accounts` and `credit_scores` are retrieved; [BRD BR-4, BR-5].

#### TC-5b: AP3 -- Unnecessary External Module Partially Eliminated
**Steps:**
1. Inspect V2 External module source code (`LoanRiskAssessmentV2Processor.cs`).
2. Verify it contains NO business logic (no joins, no AVG computation, no risk tier thresholds).
3. Verify it performs ONLY: (a) empty-input guard, (b) decimal type casting for avg_credit_score, (c) DateOnly reconstruction for as_of, (d) decimal restoration for current_balance/interest_rate.

**Expected:** V2 External module is a type-casting shim. All join, aggregation, and classification logic is in the SQL Transformation.

#### TC-5c: AP4 -- Unused Columns Eliminated
**Steps:**
1. Inspect V2 DataSourcing config for `credit_scores`.
2. Verify columns are `["customer_id", "score"]` only.

**Expected:** `credit_score_id` and `bureau` are NOT sourced in V2. V1 sourced both but never used them [LoanRiskCalculator.cs:30-31].

#### TC-5d: AP6 -- Row-by-Row Iteration Eliminated
**Steps:**
1. Verify V2 SQL Transformation handles the LEFT JOIN, AVG computation, and CASE expression.
2. Verify V2 External module only iterates rows for type conversion (not business logic).

**Expected:** No nested foreach loops for business logic in V2 External module. The single foreach in the External is for mechanical type casting only.

#### TC-5e: AP7 -- Magic Values Eliminated
**Steps:**
1. Inspect V2 External module for hardcoded numeric or string literals.
2. Verify threshold values (750, 650, 550) appear ONLY in the SQL Transformation (where named constants are not possible), not in the External module.

**Expected:** V2 External module uses named constants if any threshold logic were present. SQL contains inline values with comments citing V1 evidence.

---

### TC-6: Edge Cases

#### TC-6a: Customer with No Credit Scores
**Objective:** Verify loans for customers with zero credit score records produce null avg_credit_score and "Unknown" risk_tier.

**Steps:**
1. Identify a customer_id that exists in `loan_accounts` but has no entries in `credit_scores` for the test date range.
2. Run V2 job.
3. Inspect the output row for that customer.

**Expected:**
- `avg_credit_score` = null (or DBNull.Value equivalent in Parquet)
- `risk_tier` = "Unknown"
- All other loan fields pass through unchanged.
- Row IS present in output (LEFT JOIN ensures inclusion).

**Evidence:** [LoanRiskCalculator.cs:67-69], [FSD Sec 4 SQL: ELSE 'Unknown'].

#### TC-6b: Multi-Bureau Averaging
**Objective:** Verify credit scores from all 3 bureaus (Equifax, Experian, TransUnion) are averaged together per customer.

**Steps:**
1. Pick a customer with known scores from all 3 bureaus.
2. Manually compute the expected average.
3. Compare with V2 output.

**Expected:** avg_credit_score = AVG of all scores for that customer across all bureaus and all dates in the effective range, computed as a decimal value.

**Evidence:** [LoanRiskCalculator.cs:28-37] -- no bureau filtering; [FSD Sec 4 SQL: GROUP BY customer_id only].

#### TC-6c: Multi-Date Score Accumulation
**Objective:** Verify that when the effective date range spans multiple days, credit scores from ALL days are averaged together (not just the latest).

**Steps:**
1. Run V2 for a multi-day effective date range.
2. Verify avg_credit_score reflects scores from all dates, not just one.

**Expected:** The AVG(score) subquery operates on the full credit_scores DataFrame (all dates in effective range), matching V1's behavior of iterating all credit score rows without as_of filtering.

**Evidence:** [LoanRiskCalculator.cs:28-37], [BRD Edge Case #4].

#### TC-6d: Risk Tier Boundary Values
**Objective:** Verify risk tier thresholds are applied correctly at exact boundary values.

**Steps:**
1. Identify or construct scenarios where avg_credit_score falls exactly on threshold boundaries: 750, 650, 550.
2. Verify tier assignment.

**Expected:**
- avg = 750.0 -> "Low Risk"
- avg = 749.999... -> "Medium Risk"
- avg = 650.0 -> "Medium Risk"
- avg = 649.999... -> "High Risk"
- avg = 550.0 -> "High Risk"
- avg = 549.999... -> "Very High Risk"
- avg = null -> "Unknown"

**Evidence:** [LoanRiskCalculator.cs:58-64, 69] (thresholds use `>=`).

#### TC-6e: Overwrite Mode Data Loss (W9 behavior)
**Objective:** Confirm that multi-day auto-advance runs result in only the last day's data on disk.

**Steps:**
1. Run V2 for date `2024-10-01`.
2. Verify output files exist.
3. Run V2 for date `2024-10-02`.
4. Verify output files contain ONLY `2024-10-02` data.

**Expected:** Overwrite mode replaces all Parquet files on each run. Only the last execution date's data survives.

---

### TC-7: Proofmark Configuration
**Objective:** Verify the Proofmark config is correct and complete.

**Steps:**
1. Read `POC3/proofmark_configs/loan_risk_assessment.yaml`.
2. Validate against Proofmark CONFIG_GUIDE.md schema.

**Expected:**
```yaml
comparison_target: "loan_risk_assessment"
reader: parquet
threshold: 100.0
```
- `reader: parquet` -- correct, both V1 and V2 use ParquetFileWriter.
- `threshold: 100.0` -- strict, all columns deterministic.
- No `columns.excluded` -- no non-deterministic fields.
- No `columns.fuzzy` -- starting strict per best practices. If avg_credit_score precision diverges in Phase D, add fuzzy override with evidence.
- No `csv` section -- Parquet reader, not applicable.

---

## W-Code Test Cases

### TC-W9: Wrong writeMode
**Objective:** Verify V2 replicates V1's Overwrite write mode behavior.

**Steps:**
1. Confirm V2 config sets `writeMode: "Overwrite"`.
2. Run V2 for date D1, then D2 in sequence (auto-advance).
3. Inspect output directory after both runs.

**Expected:**
- After D1: output directory contains 2 Parquet parts with D1 data.
- After D2: output directory contains 2 Parquet parts with ONLY D2 data. D1 data is gone.
- This matches V1 behavior exactly [loan_risk_assessment.json:43].

**Documented as:** `// V1 uses Overwrite -- prior days' data is lost on each run.`

---

## Notes

1. **BRD vs V1 Code Discrepancy (Low Risk threshold):** BRD BR-2 states `>= 700` for "Low Risk". V1 source code uses `>= 750` [LoanRiskCalculator.cs:60]. V2 follows V1 code for output equivalence. Proofmark comparison against V1 output will validate this is correct. The BRD needs correction upstream.

2. **Empty Table Transformation Failure Risk:** If either `loan_accounts` or `credit_scores` is empty, `Transformation.RegisterTable` skips registration [Transformation.cs:46], and the SQL will fail with "no such table." The V2 External module's empty-input guard may never execute if the Transformation throws first. For the operational date range (2024-10-01 through 2024-12-31), both tables always have data, so this edge case will not trigger in Phase D testing. If it does, a pre-Transformation External module or SQL restructuring may be needed.

3. **Type Fidelity Through SQLite:** The `current_balance` and `interest_rate` columns are `numeric` (decimal) in PostgreSQL, but SQLite stores them as `REAL` (double). The V2 External module converts them back to `decimal`. If this round-trip introduces precision loss (e.g., `1234.56` as decimal -> `1234.5599999...` as double -> `1234.56` as decimal), Proofmark will catch it. Monitor these columns in Phase D results.

4. **DBNull.Value Handling:** V1 uses `DBNull.Value` for null avg_credit_score. ParquetFileWriter checks `r[col] is null` [ParquetFileWriter.cs:118], which returns `false` for `DBNull.Value`. The Developer must verify that `DBNull.Value` and `null` produce identical Parquet output, or use whichever matches V1.

5. **Row Ordering:** V1 iterates `loanAccounts.Rows` in DataSourcing order. V2's SQL LEFT JOIN preserves `loan_accounts` row order as the driving table. If Proofmark reports row-order mismatches, an `ORDER BY la.as_of, la.loan_id` clause can be added to the SQL.
