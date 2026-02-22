# Governance Report: AccountStatusSummary

## Links
- BRD: Phase3/brd/account_status_summary_brd.md
- FSD: Phase3/fsd/account_status_summary_fsd.md
- Test Plan: Phase3/tests/account_status_summary_tests.md
- V2 Module: ExternalModules/AccountStatusSummaryV2Processor.cs
- V2 Config: JobExecutor/Jobs/account_status_summary_v2.json

## Summary of Changes
- Original approach: DataSourcing (accounts, segments) -> External (AccountStatusCounter) -> DataFrameWriter to curated.account_status_summary
- V2 approach: DataSourcing (accounts, segments) -> External (AccountStatusSummaryV2Processor) writing to double_secret_curated.account_status_summary via DscWriterUtil
- Key difference: V2 External module combines processing and writing. Business logic (group accounts by account_type + account_status, count per group) is identical.

## Anti-Patterns Identified
- **Unused DataSourcing step**: The `segments` table is sourced but never referenced by the External module. Dead configuration.

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 0 (assembly name fix and TRUNCATE-to-DELETE fix applied universally, but no job-specific logic fix needed)

## Confidence Assessment
- Overall confidence: HIGH
- Simple grouping and counting logic. Output is deterministic and small (3 rows per date).
