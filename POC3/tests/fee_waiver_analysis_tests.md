# FeeWaiverAnalysis — V2 Test Plan

## Job Info
- **V2 Config**: `fee_waiver_analysis_v2.json`
- **Tier**: Tier 1 (Framework Only)
- **External Module**: None

## Pre-Conditions
- **Source Table**: `datalake.overdraft_events` must be populated with columns `fee_amount` (numeric), `fee_waived` (boolean), `as_of` (date).
- **Effective Date Range**: Injected at runtime by the executor via shared state (`__minEffectiveDate`, `__maxEffectiveDate`). No hardcoded dates in V2 config.
- **V1 Source Tables**: V1 also sources `datalake.accounts`, but this is dead-end sourcing (AP1) — no account columns appear in the output. V2 removes this entirely.
- **V1 Baseline**: V1 output at `Output/curated/fee_waiver_analysis.csv` must exist for Proofmark comparison. Since V1 uses Overwrite mode, only the last effective date's output will be present.
- **Accounts Table Uniqueness Assumption**: The FSD's dead-end JOIN removal depends on `accounts` having unique `(account_id, as_of)` pairs. If this assumption is violated, V2 output will differ from V1 (see TC-6 edge cases).

## Test Cases

### TC-1: Output Schema Validation
- **Expected columns** (exact order from FSD Section 4 / V1 SQL SELECT order):
  1. `fee_waived` (boolean, stored as SQLite INTEGER 0/1)
  2. `event_count` (integer — `COUNT(*)`)
  3. `total_fees` (numeric — `ROUND(SUM(...), 2)`)
  4. `avg_fee` (numeric — `ROUND(AVG(...), 2)`)
  5. `as_of` (date — text in `yyyy-MM-dd` format)
- **Column count**: 5
- Verify the header row in the CSV contains exactly these column names in this order.
- Verify no extra columns are present (V1 sourced 7 columns from `overdraft_events` + 7 from `accounts` but only outputs 5).

### TC-2: Row Count Equivalence
- V1 and V2 must produce identical row counts for each effective date.
- For each effective date with overdraft events: expect up to 2 rows (one for `fee_waived = 0/false`, one for `fee_waived = 1/true`), depending on whether both categories exist in the data.
- For dates with no overdraft events: 0 data rows (header-only CSV).
- Since writeMode is Overwrite, final output after full auto-advance contains only the last effective date's rows.
- **Critical**: If V1's dead-end LEFT JOIN to `accounts` was causing row duplication (due to non-unique `(account_id, as_of)` pairs), V2's row counts will be LOWER. This is the first thing to check if row counts diverge.

### TC-3: Data Content Equivalence
- All values must be byte-identical to V1 output.
- Run Proofmark comparison between `Output/curated/fee_waiver_analysis.csv` (V1) and `Output/double_secret_curated/fee_waiver_analysis.csv` (V2).
- Both V1 and V2 execute SQL through the same SQLite Transformation module, so the arithmetic path (ROUND, SUM, AVG on REAL columns) is identical. No floating-point divergence expected.
- **Key risk**: The dead-end JOIN removal (AP1). If `accounts` has non-unique `(account_id, as_of)` pairs, V1's LEFT JOIN would inflate `COUNT(*)`, `SUM()`, and `AVG()` results. V2 without the JOIN would produce different (correct) values. If Proofmark fails, this is the first hypothesis.

### TC-4: Writer Configuration
- **includeHeader**: `true` — verify header row is present in output CSV.
- **writeMode**: `Overwrite` — verify each execution replaces the entire file (not appends).
- **lineEnding**: `LF` — verify line endings are `\n` (not `\r\n`).
- **trailerFormat**: not configured — verify no trailer row exists in the output.
- **source**: `fee_waiver_summary` — verify writer reads from the correct Transformation result name.
- **outputFile**: `Output/double_secret_curated/fee_waiver_analysis.csv` — verify V2 writes to the correct path.

### TC-5: Anti-Pattern Elimination Verification

| AP-Code | What to Verify |
|---------|----------------|
| AP1 | V2 config does NOT contain a DataSourcing entry for the `accounts` table. V1 sourced the entire `accounts` table and LEFT JOINed to it, but no account columns appeared in the output. Verify the V2 config has exactly one DataSourcing entry (for `overdraft_events` only). |
| AP4 | V2 DataSourcing sources only 2 columns: `fee_amount`, `fee_waived`. Verify `overdraft_id`, `account_id`, `customer_id`, `overdraft_amount`, `event_timestamp` are NOT in the V2 config. The framework auto-appends `as_of`. V1 sourced 7 columns from `overdraft_events` plus 7 from `accounts` — 12 unused columns eliminated. |
| AP1+AP4 combined | Verify the V2 Transformation SQL has no `LEFT JOIN accounts` clause. The SQL should operate on `overdraft_events` alone with a simple GROUP BY. |

### TC-6: Edge Cases

| Edge Case | Test Description | Expected Behavior |
|-----------|-----------------|-------------------|
| EC-1: Dead-end JOIN removal | Compare V1 and V2 output after removing the LEFT JOIN to `accounts`. | If `accounts` has unique `(account_id, as_of)` pairs (expected for daily full-load snapshots), output is identical. If non-unique, V2 will have lower `event_count` and `total_fees`. **This is the primary risk of this migration.** |
| EC-2: NULL fee_amount | Verify rows with NULL `fee_amount` are coalesced to `0.0` before SUM and AVG. | NULL fees count toward `event_count` (via COUNT(*)) and pull `avg_fee` toward zero. `total_fees` treats NULLs as 0.0. This matches V1's CASE WHEN NULL THEN 0.0 logic. |
| EC-3: Overwrite on multi-day | Run auto-advance across multiple days. Verify only the last effective date's output survives. | File is overwritten each execution. Only final date's data persists. |
| EC-4: Empty data | No overdraft events exist for the effective date range. | SQL produces 0 rows. CSV contains only header row. |
| EC-5: Unused columns removed | Verify V2 runs successfully with only `fee_amount` and `fee_waived` sourced (plus auto-appended `as_of`). | Job executes without errors. SQL references only sourced columns. |
| Single waiver category | All events for a date have the same `fee_waived` value (all true or all false). | Output contains only 1 data row for that date instead of 2. |
| ORDER BY correctness | Verify output ordering: `fee_waived = 0` (false) rows appear before `fee_waived = 1` (true) rows. | SQLite stores booleans as INTEGER 0/1. `ORDER BY oe.fee_waived` sorts 0 before 1. |

### TC-7: Proofmark Configuration
- **Config file**: `POC3/proofmark_configs/fee_waiver_analysis.yaml`
- **Expected settings**:
  - `comparison_target`: `"fee_waiver_analysis"`
  - `reader`: `csv`
  - `threshold`: `100.0` (strict — 100% match required)
  - `csv.header_rows`: `1`
  - `csv.trailer_rows`: `0`
- **Excluded columns**: None (all output is deterministic per BRD).
- **Fuzzy columns**: None. Both V1 and V2 execute the same SQL through the same SQLite Transformation module. `ROUND()` is applied to all numeric outputs, ensuring identical results. No floating-point divergence path exists.

## W-Code Test Cases

### TC-W1: W9 — Wrong writeMode (Overwrite)
- **What the wrinkle is**: V1 uses Overwrite mode, meaning each execution replaces the entire CSV. During multi-day auto-advance, only the last effective date's output survives. Data from prior days is permanently lost.
- **How V2 handles it**: V2 config specifies `"writeMode": "Overwrite"`, matching V1 exactly.
- **What to verify**:
  - After running auto-advance across the full date range, verify the CSV contains only the last effective date's data.
  - Verify no data from prior effective dates is present in the file.
  - Verify `writeMode` in the V2 config JSON is exactly `"Overwrite"` (not `"Append"`).

## Notes
- **Dead-end JOIN removal is the primary risk**: This is the single most important thing to validate. The FSD makes a well-reasoned argument that `accounts` has unique `(account_id, as_of)` pairs (daily full-load snapshots), so removing the LEFT JOIN should not change output. However, if Proofmark comparison fails, this decision should be re-examined FIRST. If the `accounts` table does have duplicate `(account_id, as_of)` rows, the V2 FSD design needs revision — either re-add the JOIN or add deduplication logic.
- **Verification query**: To validate the uniqueness assumption before running Proofmark, execute: `SELECT account_id, as_of, COUNT(*) FROM datalake.accounts GROUP BY account_id, as_of HAVING COUNT(*) > 1;` — if this returns any rows, the dead-end JOIN removal will cause output divergence.
- **No External module needed**: This is a clean Tier 1 job. All business logic (GROUP BY, SUM, AVG, ROUND, CASE WHEN, ORDER BY) is natively supported by SQLite. No shared-state bridging is needed because effective dates are injected by the framework.
- **NULL handling is intentional**: The CASE WHEN NULL THEN 0.0 pattern causes NULL fee amounts to be included in COUNT(*) and to drag AVG toward zero. This is V1 behavior that must be preserved, not a bug to fix.
- **Proofmark first-failure hypothesis**: If Proofmark comparison fails, investigate in this order: (1) Dead-end JOIN removal causing row count changes (EC-1), (2) ORDER BY differences between V1 JOIN query and V2 single-table query, (3) NULL fee_amount handling.
