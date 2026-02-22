# Governance Report: CustomerBranchActivity

## Links
- BRD: Phase3/brd/customer_branch_activity_brd.md
- FSD: Phase3/fsd/customer_branch_activity_fsd.md
- Test Plan: Phase3/tests/customer_branch_activity_tests.md
- V2 Module: ExternalModules/CustomerBranchActivityV2Processor.cs
- V2 Config: JobExecutor/Jobs/customer_branch_activity_v2.json

## Summary of Changes
- Original approach: DataSourcing (branch_visits, customers, branches) -> External (CustomerBranchActivityBuilder) -> DataFrameWriter to curated.customer_branch_activity
- V2 approach: DataSourcing (branch_visits, customers, branches) -> External (CustomerBranchActivityV2Processor) writing to double_secret_curated.customer_branch_activity via DscWriterUtil
- Key difference: V2 combines processing and writing. Business logic (count visits per customer, enrich with customer name, INNER JOIN semantics) is identical.

## Anti-Patterns Identified
- **Unused DataSourcing step**: The `branches` table is sourced but never referenced by the External module.

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 0

## Confidence Assessment
- Overall confidence: HIGH
- Standard aggregation and enrichment pattern. Only customers with visits appear in output (INNER JOIN semantics).
