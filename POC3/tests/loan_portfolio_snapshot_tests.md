# LoanPortfolioSnapshot — V2 Test Plan

## Job Info
- **V2 Config**: `loan_portfolio_snapshot_v2.json`
- **Tier**: 1 (Framework Only)
- **External Module**: None (V1 External `LoanSnapshotBuilder.cs` eliminated per AP3)

## Pre-Conditions

1. PostgreSQL is accessible at `172.18.0.1` with `claude/claude` credentials, database `atc`.
2. Table `datalake.loan_accounts` contains data for the effective date range starting `2024-10-01` (~894 rows per as_of date).
3. V1 baseline output exists at `Output/curated/loan_portfolio_snapshot/` (Parquet, produced by V1 config `loan_portfolio_snapshot.json`).
4. V2 config `loan_portfolio_snapshot_v2.json` is deployed to `JobExecutor/Jobs/`.
5. Proofmark config `POC3/proofmark_configs/loan_portfolio_snapshot.yaml` is deployed.
6. The `Output/double_secret_curated/loan_portfolio_snapshot/` directory exists or will be created by the framework.

## Test Cases

### TC-1: Output Schema Validation

**Objective**: Verify the V2 Parquet output has exactly the correct columns in the correct order.

**Expected schema** (8 columns, in order):
| # | Column | Type | Source |
|---|--------|------|--------|
| 1 | loan_id | integer | loan_accounts.loan_id |
| 2 | customer_id | integer | loan_accounts.customer_id |
| 3 | loan_type | string | loan_accounts.loan_type |
| 4 | original_amount | numeric | loan_accounts.original_amount |
| 5 | current_balance | numeric | loan_accounts.current_balance |
| 6 | interest_rate | numeric | loan_accounts.interest_rate |
| 7 | loan_status | string | loan_accounts.loan_status |
| 8 | as_of | date | loan_accounts.as_of |

**Steps**:
1. Run V2 job: `dotnet run --project JobExecutor -- LoanPortfolioSnapshotV2`
2. Read the Parquet file(s) in `Output/double_secret_curated/loan_portfolio_snapshot/` and inspect the schema.
3. Compare column names, column order, and data types against V1 output at `Output/curated/loan_portfolio_snapshot/`.

**Pass criteria**: Column names, order, and types match V1 exactly. Notably, `origination_date` and `maturity_date` must NOT appear in the V2 output (they were dropped by V1's External module and are not sourced in V2).

### TC-2: Row Count Equivalence

**Objective**: Verify V2 produces the same number of rows as V1.

**Steps**:
1. Read V1 Parquet output and count rows.
2. Read V2 Parquet output and count rows.
3. Compare counts.
4. Cross-reference with source: `SELECT COUNT(*) FROM datalake.loan_accounts WHERE as_of BETWEEN '{min_date}' AND '{max_date}'` — this should match both outputs since the job is a pure pass-through with no filtering.

**Pass criteria**: Row counts are identical between V1 and V2. Both should equal the total loan_accounts rows in the effective date range.

### TC-3: Data Content Equivalence

**Objective**: Verify V2 output data matches V1 exactly.

**Steps**:
1. Run Proofmark comparison between `Output/curated/loan_portfolio_snapshot/` and `Output/double_secret_curated/loan_portfolio_snapshot/`.
2. All columns are direct pass-through with no computation — there should be zero differences.
3. If differences are found, investigate whether they are related to data type conversions (e.g., numeric precision through SQLite REAL conversion).

**W-code note**: No W-codes apply to this job. All values are direct pass-through with no rounding, no computation, and no aggregation. There is no reason for any value to differ between V1 and V2.

**Pass criteria**: 100% row match with zero column-level differences. Proofmark passes at threshold 100.0.

### TC-4: Writer Configuration

**Objective**: Verify the ParquetFileWriter config matches V1 behavior.

| Property | Expected Value | Verification Method |
|----------|---------------|---------------------|
| type | ParquetFileWriter | Inspect V2 JSON config |
| source | `output` | Inspect V2 JSON config |
| outputDirectory | `Output/double_secret_curated/loan_portfolio_snapshot/` | Confirm file is written to this path |
| numParts | `1` | Exactly 1 Parquet part file in output directory |
| writeMode | `Overwrite` | Run job twice; second run replaces first (directory content reflects second run only) |

**Steps**:
1. Inspect `loan_portfolio_snapshot_v2.json` writer section for all properties above.
2. Run the job and verify output directory.
3. Count part files in output directory — must be exactly 1.
4. Run the job again and confirm the output is replaced (not appended).

**Pass criteria**: All writer properties match V1 configuration. Output path differs only in the directory (`double_secret_curated` vs `curated`). Exactly 1 part file produced.

### TC-5: Anti-Pattern Elimination Verification

**Objective**: Confirm all identified anti-patterns are eliminated in V2.

| AP Code | Anti-Pattern | Verification |
|---------|-------------|--------------|
| AP1 | Dead-end sourcing (`branches`) | V2 JSON config has NO DataSourcing module for `branches`. Only `loan_accounts` is sourced. |
| AP3 | Unnecessary External module | V2 JSON config has NO module with `"type": "External"`. Module chain is `DataSourcing -> Transformation -> ParquetFileWriter`. |
| AP4 | Unused columns (`origination_date`, `maturity_date`) | V2 DataSourcing `columns` array does NOT include `origination_date` or `maturity_date`. Only the 7 business columns are listed (as_of is auto-appended by the framework). |
| AP6 | Row-by-row iteration | No External module means no `foreach` loop. SQL SELECT is a set-based operation. |

**Steps**:
1. Parse `loan_portfolio_snapshot_v2.json`:
   - Confirm no `"type": "External"` entries.
   - Confirm no DataSourcing with `"table": "branches"`.
   - Confirm the `loan_accounts` DataSourcing columns list is `["loan_id", "customer_id", "loan_type", "original_amount", "current_balance", "interest_rate", "loan_status"]` — exactly 7 columns, no `origination_date`, no `maturity_date`.
2. Confirm exactly 3 modules: 1x DataSourcing, 1x Transformation, 1x ParquetFileWriter.
3. Inspect the Transformation SQL: should be a simple `SELECT` of the 8 output columns from `loan_accounts`.

**Pass criteria**: All four AP codes are demonstrably eliminated. Module count reduced from 4 (V1) to 3 (V2).

### TC-6: Edge Cases

#### TC-6a: Column Exclusion Correctness (BR-1)

**Objective**: Confirm `origination_date` and `maturity_date` do NOT appear in V2 output.

**Steps**:
1. Read V2 Parquet schema metadata.
2. Verify neither `origination_date` nor `maturity_date` is present.
3. Verify the same exclusion in V1 output (baseline confirmation).

**Pass criteria**: Output schema has exactly 8 columns. `origination_date` and `maturity_date` are absent.

#### TC-6b: All Loan Statuses Included (BR-3)

**Objective**: Confirm no filtering on `loan_status` — both Active and Delinquent loans appear.

**Steps**:
1. Query source: `SELECT DISTINCT loan_status FROM datalake.loan_accounts`.
2. Query V2 output: extract distinct loan_status values from the Parquet file.
3. Confirm all source statuses are present in output.

**Pass criteria**: V2 output contains all loan_status values present in the source data (Active, Delinquent).

#### TC-6c: No Value Transformation (BR-4)

**Objective**: Confirm values are passed through without modification — no rounding, casting, or computation.

**Steps**:
1. Select a sample of 10 rows from V2 output.
2. For each row, query the corresponding source row: `SELECT loan_id, customer_id, loan_type, original_amount, current_balance, interest_rate, loan_status, as_of FROM datalake.loan_accounts WHERE loan_id = {id} AND as_of = '{date}'`.
3. Compare all column values — they must be identical.

**Pass criteria**: All sampled values match source data exactly. No rounding of `original_amount`, `current_balance`, or `interest_rate`.

#### TC-6d: Per-Row as_of (BR-6)

**Objective**: Confirm `as_of` comes from each source row, not from `__maxEffectiveDate`.

**Steps**:
1. In V2 output, verify that `as_of` values span the full effective date range (multiple distinct dates should appear if the range covers multiple days).
2. Confirm the `as_of` values match the source data per-row, not a single constant date.

**Pass criteria**: Multiple distinct `as_of` values in output matching the source data's date range.

#### TC-6e: NULL Pass-Through (BR-4)

**Objective**: Confirm NULL values in source columns pass through as NULL (not empty string, not zero, not default).

**Steps**:
1. Query: `SELECT * FROM datalake.loan_accounts WHERE original_amount IS NULL OR current_balance IS NULL OR interest_rate IS NULL OR loan_type IS NULL OR loan_status IS NULL LIMIT 5`.
2. If any NULLs exist, find the corresponding rows in V2 output and confirm the values are NULL (not coerced).

**Pass criteria**: NULL values pass through unchanged. If no NULLs exist in source data, this test is vacuously passed.

#### TC-6f: Empty loan_accounts (BR-5)

**Objective**: Verify behavior when no loan_accounts data exists for the effective date range.

**Steps**:
1. This is a theoretical edge case — the dataset has ~894 rows per as_of date, making empty input unlikely.
2. If testable with a synthetic date range outside the data range, run the job for that date.
3. Expected: V1 produces an empty DataFrame with correct schema. V2 may produce a framework error because DataSourcing returns empty data, Transformation skips SQLite table registration, and the SQL fails on a missing table.

**Pass criteria**: Behavior matches V1 (empty output with correct schema). Note: FSD documents this as a known edge case with mitigation deferred to Phase D if triggered.

### TC-7: Proofmark Configuration

**Objective**: Validate the Proofmark YAML config is correct and complete.

**Expected config** (`POC3/proofmark_configs/loan_portfolio_snapshot.yaml`):
```yaml
comparison_target: "loan_portfolio_snapshot"
reader: parquet
threshold: 100.0
```

**Verification checklist**:
| Field | Expected | Rationale |
|-------|----------|-----------|
| comparison_target | `"loan_portfolio_snapshot"` | Matches job/output name |
| reader | `parquet` | Output is ParquetFileWriter |
| threshold | `100.0` | Strict match — pure pass-through, all columns deterministic, no computation |
| csv | (absent) | Not applicable for Parquet output |
| columns.excluded | (none) | No non-deterministic fields per BRD ("Pure pass-through with no computed fields") |
| columns.fuzzy | (none) | No floating-point computation — all values are direct pass-through from PostgreSQL numeric types |

**Pass criteria**: YAML file matches expected config exactly. No excluded or fuzzy columns. No CSV section.

## W-Code Test Cases

No W-codes apply to this job. The FSD explicitly confirms:
- No Sunday skip (W1), weekend fallback (W2), or boundary logic (W3a/W3b/W3c)
- No integer division (W4) or rounding (W5)
- No double-precision accumulation (W6)
- No CSV trailer (W7/W8)
- Write mode Overwrite is appropriate (W9 — not applicable)
- numParts is 1 for ~894 rows (W10 — not applicable)

No W-code test cases are required.

## Notes

1. **Output path difference**: V1 writes to `Output/curated/loan_portfolio_snapshot/`, V2 writes to `Output/double_secret_curated/loan_portfolio_snapshot/`. This is by design per POC3 spec and is NOT a defect.
2. **Branches table removal**: V1 sources `datalake.branches` (DataSourcing step 2) but the External module never accesses it. V2 correctly removes this dead-end sourcing (AP1). This change has zero impact on output but improves performance by eliminating an unnecessary database query.
3. **Column sourcing optimization**: V1 sources `origination_date` and `maturity_date` from `loan_accounts` only to have the External module drop them. V2 does not source these columns at all (AP4). The DataSourcing framework auto-appends `as_of` when it is not in the explicit column list, so the 7 listed columns + auto-appended `as_of` = 8 output columns.
4. **Simplest possible Tier 1 job**: This is arguably the simplest V2 conversion in the entire portfolio. The SQL is a trivial `SELECT` with no WHERE, no JOIN, no aggregation, no functions. If this job fails Proofmark, something is fundamentally wrong with the framework pipeline, not with the business logic.
5. **Multi-date Overwrite**: Since writeMode is Overwrite, only the final effective date's execution output survives. Proofmark comparison should be run after the full date range completes.
6. **Parquet type fidelity**: Watch for numeric type handling through the SQLite REAL intermediary. PostgreSQL `numeric` values go through C# `decimal` -> SQLite `REAL` (IEEE 754 double) -> Parquet. If V1's External module preserves `decimal` precision through direct row copying, but V2's SQL Transformation routes through SQLite REAL, there could be precision differences on values with many decimal places. However, for financial amounts (typically 2 decimal places), this should not be an issue.
