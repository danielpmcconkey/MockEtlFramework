# TopBranches -- Governance Report

## Links
- BRD: Phase3/brd/top_branches_brd.md
- FSD: Phase3/fsd/top_branches_fsd.md
- Test Plan: Phase3/tests/top_branches_tests.md
- V2 Config: JobExecutor/Jobs/top_branches_v2.json

## Summary of Changes
The original job re-derived per-branch visit counts from raw datalake.branch_visits despite having a declared SameDay dependency on BranchVisitSummary (which already computes visit counts per branch), with an unused visit_id column, a hardcoded date filter, and a CTE. The V2 reads from curated.branch_visit_summary instead of re-deriving visit counts, applies RANK() directly to the upstream data, removes the hardcoded date filter, and simplifies the SQL.

## Anti-Pattern Scorecard

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | N                   | N/A                | Both original DataSourcing modules were used |
| AP-2    | Y                   | Y                  | V2 reads from curated.branch_visit_summary instead of re-deriving visit counts from datalake |
| AP-3    | N                   | N/A                | Original already uses Transformation (not External module) |
| AP-4    | Y                   | Y                  | Original sourced visit_id from branch_visits which was unused; V2 does not source raw branch_visits at all |
| AP-5    | N                   | N/A                | No NULL/default handling involved |
| AP-6    | N                   | N/A                | No External module with row-by-row iteration |
| AP-7    | Y                   | Y                  | Removed hardcoded date '2024-10-01' from WHERE clause (DataSourcing handles date filtering) |
| AP-8    | Y                   | Y                  | Removed CTE; simplified to a direct SELECT with RANK() from upstream table |
| AP-9    | N                   | N/A                | Job name accurately describes what it does |
| AP-10   | N                   | N/A                | Dependency on BranchVisitSummary already declared; V2 now actually uses upstream output |

## Comparison Results
- Dates verified: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Write mode: Overwrite
- Row count: 20 (varies by date based on branch visit distribution)

## Confidence Assessment
**HIGH** -- All 7 business rules directly observable. The AP-2 fix properly leverages the upstream BranchVisitSummary dependency. No fix iterations required for this job.
