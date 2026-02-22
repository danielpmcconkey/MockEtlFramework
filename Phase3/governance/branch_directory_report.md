# Governance Report: BranchDirectory

## Links
- BRD: Phase3/brd/branch_directory_brd.md
- FSD: Phase3/fsd/branch_directory_fsd.md
- Test Plan: Phase3/tests/branch_directory_tests.md
- V2 Module: ExternalModules/BranchDirectoryV2Writer.cs
- V2 Config: JobExecutor/Jobs/branch_directory_v2.json

## Summary of Changes
- Original approach: DataSourcing (branches) -> Transformation (SQL with ROW_NUMBER deduplication) -> DataFrameWriter to curated.branch_directory
- V2 approach: DataSourcing (branches) -> Transformation (same SQL) -> External (BranchDirectoryV2Writer) writing to double_secret_curated.branch_directory via DscWriterUtil
- Key difference: V2 retains the identical Transformation SQL and replaces only the DataFrameWriter with a thin V2 writer module. No business logic changes.

## Anti-Patterns Identified
- **Potentially unnecessary deduplication**: The ROW_NUMBER deduplication (PARTITION BY branch_id ORDER BY branch_id) may be unnecessary if branches data contains only one row per branch_id per as_of date. The deduplication is a safety measure but adds SQL complexity for a possibly non-existent scenario.

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 0 (assembly name fix and TRUNCATE-to-DELETE fix applied universally, but no job-specific logic fix needed)

## Confidence Assessment
- Overall confidence: HIGH
- SQL-based transformation with identical SQL preserved. No ambiguity in deduplication logic.
