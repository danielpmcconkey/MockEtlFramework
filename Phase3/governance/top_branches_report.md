# Governance Report: TopBranches

## Links
- BRD: Phase3/brd/top_branches_brd.md
- FSD: Phase3/fsd/top_branches_fsd.md
- Test Plan: Phase3/tests/top_branches_tests.md
- V2 Module: ExternalModules/TopBranchesV2Writer.cs
- V2 Config: JobExecutor/Jobs/top_branches_v2.json

## Summary of Changes
- Original approach: DataSourcing (branch_visits, branches) -> Transformation (SQL with CTE counting visits per branch, RANK() window function, JOIN to branches for names) -> DataFrameWriter to curated.top_branches
- V2 approach: DataSourcing (branch_visits, branches) -> Transformation (same SQL) -> External (TopBranchesV2Writer) writing to double_secret_curated.top_branches via DscWriterUtil
- Key difference: V2 retains the identical Transformation SQL and replaces only the DataFrameWriter with a thin V2 writer module. No business logic changes.

## Anti-Patterns Identified
- **Redundant SQL filter**: The SQL contains `WHERE bv.as_of >= '2024-10-01'` but since DataSourcing only loads the single effective date, this filter is always satisfied for dates on or after Oct 1.
- **Dependency**: This job depends on BranchVisitSummary (SameDay dependency), but it reads directly from datalake.branch_visits, not from the BranchVisitSummary output.

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 0 (assembly name fix and TRUNCATE-to-DELETE fix applied universally, but no job-specific logic fix needed)

## Confidence Assessment
- Overall confidence: HIGH
- SQL-based transformation with standard RANK() window function. The ranking logic and join to branches for names are straightforward.
