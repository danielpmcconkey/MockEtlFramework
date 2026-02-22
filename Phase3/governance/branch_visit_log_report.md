# Governance Report: BranchVisitLog

## Links
- BRD: Phase3/brd/branch_visit_log_brd.md
- FSD: Phase3/fsd/branch_visit_log_fsd.md
- Test Plan: Phase3/tests/branch_visit_log_tests.md
- V2 Module: ExternalModules/BranchVisitLogV2Processor.cs
- V2 Config: JobExecutor/Jobs/branch_visit_log_v2.json

## Summary of Changes
- Original approach: DataSourcing (branch_visits, branches, customers, addresses) -> External (BranchVisitEnricher) -> DataFrameWriter to curated.branch_visit_log
- V2 approach: DataSourcing (branch_visits, branches, customers, addresses) -> External (BranchVisitLogV2Processor) writing to double_secret_curated.branch_visit_log via DscWriterUtil
- Key difference: V2 combines processing and writing. Business logic (enrich visits with customer names and branch names via dictionary lookups) is identical.

## Anti-Patterns Identified
- **Unused DataSourcing step**: The `addresses` table is sourced but never referenced by the External module. Dead configuration.
- **Null-safe inconsistency**: Branch name defaults to empty string when not found, but customer names default to `(null!, null!)` -- a C# null-forgiveness operator that silently passes nulls. These are two different fallback strategies for the same type of lookup miss.

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 0

## Confidence Assessment
- Overall confidence: HIGH
- Standard enrichment pattern with clear join semantics. The null handling difference between branch and customer lookups is consistent with the original and does not affect output correctness.
