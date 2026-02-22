# Governance Report: LoanPortfolioSnapshot

## Links
- BRD: Phase3/brd/loan_portfolio_snapshot_brd.md
- FSD: Phase3/fsd/loan_portfolio_snapshot_fsd.md
- Test Plan: Phase3/tests/loan_portfolio_snapshot_tests.md
- V2 Module: ExternalModules/LoanPortfolioSnapshotV2Processor.cs
- V2 Config: JobExecutor/Jobs/loan_portfolio_snapshot_v2.json

## Summary of Changes
- Original approach: DataSourcing (loan_accounts, branches) -> External (LoanSnapshotBuilder) -> DataFrameWriter to curated.loan_portfolio_snapshot
- V2 approach: DataSourcing (loan_accounts, branches) -> External (LoanPortfolioSnapshotV2Processor) writing to double_secret_curated.loan_portfolio_snapshot via DscWriterUtil
- Key difference: V2 combines processing and writing. Business logic (pass through 7 columns from loan_accounts, excluding origination_date and maturity_date) is identical.

## Anti-Patterns Identified
- **Unused DataSourcing step**: The `branches` table is sourced but never referenced by the External module.
- **Over-fetching columns**: The loan_accounts DataSourcing sources origination_date and maturity_date which are explicitly excluded from the output. These columns are loaded into memory only to be discarded.

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 0 (assembly name fix and TRUNCATE-to-DELETE fix applied universally, but no job-specific logic fix needed)

## Confidence Assessment
- Overall confidence: HIGH
- Simple pass-through logic with column selection (include 7, exclude 2). No transformations or calculations.
