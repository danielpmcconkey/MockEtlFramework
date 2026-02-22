# LoanPortfolioSnapshot -- Business Requirements Document

## Overview

This job produces a snapshot of the loan portfolio by copying all loan account records while dropping the `origination_date` and `maturity_date` columns. The output is written in Overwrite mode, so only the latest effective date's data persists.

## Source Tables

### datalake.loan_accounts
- **Columns used**: `loan_id`, `customer_id`, `loan_type`, `original_amount`, `current_balance`, `interest_rate`, `loan_status`, `as_of`
- **Columns sourced but unused by output**: `origination_date`, `maturity_date` -- these are sourced in the DataSourcing module and present in the DataFrame, but explicitly skipped by the External module when building output rows
- **Evidence**: [ExternalModules/LoanSnapshotBuilder.cs:24] comment "Pass-through: copy loan rows, skipping origination_date and maturity_date"
- **Filter**: None -- all loan account rows are included

### datalake.branches (UNUSED)
- **Columns sourced**: `branch_id`, `branch_name`
- **Usage**: NONE -- the LoanSnapshotBuilder External module never references the `branches` DataFrame
- **Evidence**: [ExternalModules/LoanSnapshotBuilder.cs] No reference to `branches` key in sharedState. The module only accesses `loan_accounts`.
- See AP-1.

## Business Rules

BR-1: All loan account records are included in the output without any filtering.
- Confidence: HIGH
- Evidence: [ExternalModules/LoanSnapshotBuilder.cs:26-38] Iterates all rows in `loanAccounts.Rows` with no conditional filter
- Evidence: [curated.loan_portfolio_snapshot] 90 rows for as_of = 2024-10-31 matches [datalake.loan_accounts] 90 rows for same date

BR-2: The output excludes `origination_date` and `maturity_date` columns from the source data.
- Confidence: HIGH
- Evidence: [ExternalModules/LoanSnapshotBuilder.cs:4-8] Output columns list: `loan_id, customer_id, loan_type, original_amount, current_balance, interest_rate, loan_status, as_of` -- no origination_date or maturity_date
- Evidence: [curated.loan_portfolio_snapshot] Table schema has no origination_date or maturity_date columns

BR-3: All other columns are passed through unchanged.
- Confidence: HIGH
- Evidence: [ExternalModules/LoanSnapshotBuilder.cs:28-38] Direct assignment from `row["column_name"]` for each output column

BR-4: The output uses Overwrite mode.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/loan_portfolio_snapshot.json:28] `"writeMode": "Overwrite"`

BR-5: If the loan_accounts DataFrame is null or empty, an empty output is produced.
- Confidence: HIGH
- Evidence: [ExternalModules/LoanSnapshotBuilder.cs:18-21] null/empty check

## Output Schema

| Column | Source | Transformation |
|--------|--------|----------------|
| loan_id | datalake.loan_accounts.loan_id | Pass-through |
| customer_id | datalake.loan_accounts.customer_id | Pass-through |
| loan_type | datalake.loan_accounts.loan_type | Pass-through |
| original_amount | datalake.loan_accounts.original_amount | Pass-through |
| current_balance | datalake.loan_accounts.current_balance | Pass-through |
| interest_rate | datalake.loan_accounts.interest_rate | Pass-through |
| loan_status | datalake.loan_accounts.loan_status | Pass-through |
| as_of | datalake.loan_accounts.as_of | Pass-through |

## Edge Cases

- **Empty loan_accounts DataFrame**: Returns empty output
- **Overwrite mode**: Only the last effective date's data persists
- **No filtering**: All loan statuses (Active, Delinquent, Paid Off) are included

## Anti-Patterns Identified

- **AP-1: Redundant Data Sourcing** -- The `branches` DataSourcing module fetches `branch_id` and `branch_name` from `datalake.branches`, but the External module (`LoanSnapshotBuilder`) never accesses the `branches` DataFrame. The branches data is completely unused. V2 approach: Remove the branches DataSourcing module entirely.

- **AP-3: Unnecessary External Module** -- The LoanSnapshotBuilder External module is a trivial pass-through that copies rows while selecting a subset of columns. This is a simple SQL `SELECT` statement: `SELECT loan_id, customer_id, loan_type, original_amount, current_balance, interest_rate, loan_status, as_of FROM loan_accounts`. V2 approach: Replace with a SQL Transformation.

- **AP-6: Row-by-Row Iteration in External Module** -- The External module iterates over all loan rows with a `foreach` loop to copy them one at a time. This is an identity operation (column projection) that SQL handles natively. V2 approach: Replace with SQL Transformation (see AP-3).

- **AP-9: Misleading Job/Table Names** -- The name "LoanPortfolioSnapshot" implies some form of aggregation or summarization of the loan portfolio. In reality, the job is a simple column projection (dropping two date columns). It produces a detail-level table, not a "snapshot" in the analytical sense. V2 approach: Flag in documentation. Do not rename (output must match original).

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [ExternalModules/LoanSnapshotBuilder.cs:26-38] no filter applied |
| BR-2 | [ExternalModules/LoanSnapshotBuilder.cs:4-8] output columns exclude origination_date, maturity_date |
| BR-3 | [ExternalModules/LoanSnapshotBuilder.cs:28-38] direct assignment for all output columns |
| BR-4 | [JobExecutor/Jobs/loan_portfolio_snapshot.json:28] `"writeMode": "Overwrite"` |
| BR-5 | [ExternalModules/LoanSnapshotBuilder.cs:18-21] null/empty guard |

## Open Questions

- None. The job logic is extremely simple and fully observable.
