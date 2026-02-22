# Governance Report: AccountTypeDistribution

## Links
- BRD: Phase3/brd/account_type_distribution_brd.md
- FSD: Phase3/fsd/account_type_distribution_fsd.md
- Test Plan: Phase3/tests/account_type_distribution_tests.md
- V2 Module: ExternalModules/AccountTypeDistributionV2Processor.cs
- V2 Config: JobExecutor/Jobs/account_type_distribution_v2.json

## Summary of Changes
- Original approach: DataSourcing (accounts, branches) -> External (AccountDistributionCalculator) -> DataFrameWriter to curated.account_type_distribution
- V2 approach: DataSourcing (accounts, branches) -> External (AccountTypeDistributionV2Processor) writing to double_secret_curated.account_type_distribution via DscWriterUtil
- Key difference: V2 adds explicit `Math.Round(..., 2)` for the percentage column. The original relied on the curated table's `NUMERIC(5,2)` column type to auto-round on INSERT. V2 also combines processing and writing into a single step.

## Anti-Patterns Identified
- **Unused DataSourcing step**: The `branches` table is sourced but never referenced by the External module.
- **Implicit numeric rounding**: The original code computes percentage as a floating-point double but relies on the database column type `NUMERIC(5,2)` to silently round to 2 decimal places on INSERT. This is a fragile pattern because the rounding is not visible in code.

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 1 (Iteration 3 -- added `Math.Round(..., 2)` to percentage computation to match the implicit rounding performed by the curated schema's NUMERIC(5,2) column)

## Confidence Assessment
- Overall confidence: HIGH
- The rounding discrepancy was the only issue, and it was caused by schema differences rather than logic errors. The V2 now explicitly rounds, which is actually an improvement over the original's implicit rounding.
