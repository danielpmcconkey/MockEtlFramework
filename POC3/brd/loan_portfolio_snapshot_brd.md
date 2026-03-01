# LoanPortfolioSnapshot — Business Requirements Document

## Overview
Produces a simplified snapshot of the loan portfolio by passing through loan account records with select columns, dropping date-related fields (origination_date, maturity_date). Output is a Parquet file per effective date.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `output`
- **outputDirectory**: `Output/curated/loan_portfolio_snapshot/`
- **numParts**: 1
- **writeMode**: Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.loan_accounts | loan_id, customer_id, loan_type, original_amount, current_balance, interest_rate, origination_date, maturity_date, loan_status | Effective date range (injected by executor) | [loan_portfolio_snapshot.json:8-12] |
| datalake.branches | branch_id, branch_name | Effective date range (injected by executor) | [loan_portfolio_snapshot.json:14-18] |

### Table Schemas (from database)

**loan_accounts**: loan_id (integer), customer_id (integer), loan_type (varchar: Auto/Mortgage/Personal/Student), original_amount (numeric), current_balance (numeric), interest_rate (numeric), origination_date (date), maturity_date (date), loan_status (varchar: Active/Delinquent), as_of (date). ~894 rows per as_of date.

**branches**: branch_id (integer), branch_name (varchar), address_line1 (varchar), city (varchar), state_province (varchar), postal_code (varchar), country (char), as_of (date).

## Business Rules

BR-1: The External module is a simple pass-through — it copies each loan_accounts row with a subset of columns, excluding `origination_date` and `maturity_date`.
- Confidence: HIGH
- Evidence: [LoanSnapshotBuilder.cs:24-38] — row-by-row copy with explicit column list omitting origination_date and maturity_date

BR-2: The `branches` table is sourced via DataSourcing but is NOT used by the External module. It is loaded into shared state but never accessed.
- Confidence: HIGH
- Evidence: [LoanSnapshotBuilder.cs] — no reference to `branches` DataFrame anywhere in the Execute method; only `loan_accounts` is retrieved from shared state (line 16)

BR-3: All loan records are included regardless of loan_status (Active, Delinquent). No filtering is applied.
- Confidence: HIGH
- Evidence: [LoanSnapshotBuilder.cs:26-38] — no conditional logic; all rows are processed

BR-4: Column values are passed through without transformation — no rounding, casting, or computation is performed.
- Confidence: HIGH
- Evidence: [LoanSnapshotBuilder.cs:28-36] — direct `row["column"]` assignment

BR-5: Empty output (zero-row DataFrame with correct schema) is produced if `loan_accounts` is null or empty.
- Confidence: HIGH
- Evidence: [LoanSnapshotBuilder.cs:18-22]

BR-6: The `as_of` column passes through from the source row (per-row as_of), not from `__maxEffectiveDate`.
- Confidence: HIGH
- Evidence: [LoanSnapshotBuilder.cs:37] — `["as_of"] = row["as_of"]`

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| loan_id | loan_accounts.loan_id | Direct pass-through | [LoanSnapshotBuilder.cs:29] |
| customer_id | loan_accounts.customer_id | Direct pass-through | [LoanSnapshotBuilder.cs:30] |
| loan_type | loan_accounts.loan_type | Direct pass-through | [LoanSnapshotBuilder.cs:31] |
| original_amount | loan_accounts.original_amount | Direct pass-through | [LoanSnapshotBuilder.cs:32] |
| current_balance | loan_accounts.current_balance | Direct pass-through | [LoanSnapshotBuilder.cs:33] |
| interest_rate | loan_accounts.interest_rate | Direct pass-through | [LoanSnapshotBuilder.cs:34] |
| loan_status | loan_accounts.loan_status | Direct pass-through | [LoanSnapshotBuilder.cs:35] |
| as_of | loan_accounts.as_of | Direct pass-through (per-row) | [LoanSnapshotBuilder.cs:37] |

**Excluded source columns**: origination_date, maturity_date (sourced by DataSourcing but dropped by External module)

## Non-Deterministic Fields
None identified. Pure pass-through with no computed fields.

## Write Mode Implications
**Overwrite** mode: Each effective date run replaces the entire output directory. In multi-day gap-fill scenarios, only the last day's output survives. Since as_of is per-row and data spans the effective date range, the final run captures all dates in its range.

## Edge Cases

1. **Branches unused**: The `branches` DataFrame is sourced but never referenced. Adds unnecessary database query overhead without affecting output.
   - Evidence: [loan_portfolio_snapshot.json:14-18] — branches sourced; [LoanSnapshotBuilder.cs] — not accessed

2. **No column filtering at DataSourcing level**: Both `origination_date` and `maturity_date` are selected in DataSourcing but dropped by the External module. The DataSourcing could have excluded them.
   - Evidence: [loan_portfolio_snapshot.json:12] — includes origination_date, maturity_date; [LoanSnapshotBuilder.cs:10-13] — output columns exclude them

3. **Snapshot data duplication**: With multi-date ranges and 894 rows per date, the output can contain multiple snapshots of the same loan (one per as_of date).
   - Evidence: [Database query] — 894 rows per date; as_of preserved per-row

4. **NULL values**: No null handling is performed. If any source column is null, it passes through as null.
   - Evidence: [LoanSnapshotBuilder.cs:28-37] — direct assignment without null checks

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Pass-through with column exclusion | [LoanSnapshotBuilder.cs:24-38] |
| BR-2: Branches unused | [LoanSnapshotBuilder.cs:16] — only loan_accounts accessed |
| BR-3: No loan_status filter | [LoanSnapshotBuilder.cs:26-38] |
| BR-4: No value transformation | [LoanSnapshotBuilder.cs:28-36] |
| BR-5: Empty output guard | [LoanSnapshotBuilder.cs:18-22] |
| BR-6: Per-row as_of | [LoanSnapshotBuilder.cs:37] |
| Output: Parquet, 1 part, Overwrite | [loan_portfolio_snapshot.json:22-27] |

## Open Questions

1. **Why is branches sourced?** The branches table is loaded but never used. Possibly a leftover from a version that included branch information in the snapshot, or planned for future use.
   - Confidence: HIGH — code clearly shows it is unused

2. **Why use External module for simple pass-through?** This logic could be accomplished entirely with a Transformation SQL SELECT. The External module adds unnecessary complexity for what is just a column projection.
   - Confidence: HIGH — the module does nothing that SQL could not do
