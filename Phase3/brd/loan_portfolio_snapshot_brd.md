# BRD: LoanPortfolioSnapshot

## Overview
This job produces a daily snapshot of the loan portfolio by extracting selected columns from loan_accounts data, excluding the origination_date and maturity_date fields. The output is written to `curated.loan_portfolio_snapshot` in Overwrite mode.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| loan_accounts | datalake | loan_id, customer_id, loan_type, original_amount, current_balance, interest_rate, origination_date, maturity_date, loan_status | No filter; all rows pass through with selected columns | [JobExecutor/Jobs/loan_portfolio_snapshot.json:5-11] DataSourcing config; [ExternalModules/LoanSnapshotBuilder.cs:26-38] row iteration |
| branches | datalake | branch_id, branch_name | Sourced but NOT used in External module logic | [loan_portfolio_snapshot.json:13-17] DataSourcing config; Not referenced in LoanSnapshotBuilder.cs |

## Business Rules

BR-1: All loan_accounts rows for the effective date are included in the output — there is no filter on loan_type, loan_status, or any other column.
- Confidence: HIGH
- Evidence: [ExternalModules/LoanSnapshotBuilder.cs:26-38] Iterates all rows without any filter condition
- Evidence: [curated.loan_portfolio_snapshot] Row count of 90 matches datalake.loan_accounts count of 90 per as_of

BR-2: The output includes 7 columns from loan_accounts plus as_of, specifically excluding origination_date and maturity_date.
- Confidence: HIGH
- Evidence: [ExternalModules/LoanSnapshotBuilder.cs:10-14] Output columns defined as: loan_id, customer_id, loan_type, original_amount, current_balance, interest_rate, loan_status, as_of
- Evidence: [ExternalModules/LoanSnapshotBuilder.cs:24] Comment: "Pass-through: copy loan rows, skipping origination_date and maturity_date"

BR-3: All column values are passed through without transformation (no rounding, no type conversion on output).
- Confidence: HIGH
- Evidence: [ExternalModules/LoanSnapshotBuilder.cs:28-38] Direct assignment from `row["column"]` to output row

BR-4: Output is written in Overwrite mode — each run truncates the entire table before writing.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/loan_portfolio_snapshot.json:28] `"writeMode": "Overwrite"`
- Evidence: [curated.loan_portfolio_snapshot] Only one as_of date (2024-10-31) present

BR-5: If loan_accounts DataFrame is null or empty, the job produces an empty output DataFrame.
- Confidence: HIGH
- Evidence: [ExternalModules/LoanSnapshotBuilder.cs:18-22] Null/empty check returns empty DataFrame

BR-6: The branches DataFrame is sourced by the job config but is NOT used by the External module.
- Confidence: HIGH
- Evidence: [loan_portfolio_snapshot.json:13-17] Branches sourced; [LoanSnapshotBuilder.cs] No reference to "branches" in sharedState

BR-7: The as_of column in the output comes directly from each loan_accounts row.
- Confidence: HIGH
- Evidence: [ExternalModules/LoanSnapshotBuilder.cs:37] `["as_of"] = row["as_of"]`

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| loan_id | loan_accounts.loan_id | Pass-through | [LoanSnapshotBuilder.cs:30] |
| customer_id | loan_accounts.customer_id | Pass-through | [LoanSnapshotBuilder.cs:31] |
| loan_type | loan_accounts.loan_type | Pass-through | [LoanSnapshotBuilder.cs:32] |
| original_amount | loan_accounts.original_amount | Pass-through | [LoanSnapshotBuilder.cs:33] |
| current_balance | loan_accounts.current_balance | Pass-through | [LoanSnapshotBuilder.cs:34] |
| interest_rate | loan_accounts.interest_rate | Pass-through | [LoanSnapshotBuilder.cs:35] |
| loan_status | loan_accounts.loan_status | Pass-through | [LoanSnapshotBuilder.cs:36] |
| as_of | loan_accounts.as_of | Pass-through | [LoanSnapshotBuilder.cs:37] |

## Edge Cases
- **NULL handling**: No explicit NULL handling in the module — values are passed through as-is. If any source column is NULL, it will appear as NULL in the output.
- **Weekend/date fallback**: loan_accounts has weekday-only data. On weekend effective dates, the DataFrame would be empty, triggering the empty-output guard (BR-5).
- **Zero-row behavior**: Empty DataFrame is valid and written (table truncated with no rows inserted).
- **Unused data**: branches table is loaded but unused — potential inefficiency.

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [LoanSnapshotBuilder.cs:26-38], [curated data row count verification] |
| BR-2 | [LoanSnapshotBuilder.cs:10-14, 24] |
| BR-3 | [LoanSnapshotBuilder.cs:28-38] |
| BR-4 | [loan_portfolio_snapshot.json:28], [curated data observation] |
| BR-5 | [LoanSnapshotBuilder.cs:18-22] |
| BR-6 | [loan_portfolio_snapshot.json:13-17], [LoanSnapshotBuilder.cs] |
| BR-7 | [LoanSnapshotBuilder.cs:37] |

## Open Questions
- The branches table is sourced but unused. This could be intended for future enrichment (e.g., branch association for loans) or is a configuration oversight. Confidence: MEDIUM that this is an oversight — no impact on output.
- The exclusion of origination_date and maturity_date appears intentional (documented in code comment). The business reason could be data sensitivity or downstream irrelevance. Confidence: HIGH that this is deliberate.
