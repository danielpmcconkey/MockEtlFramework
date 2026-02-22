# BranchDirectory -- Governance Report

## Links
- BRD: Phase3/brd/branch_directory_brd.md
- FSD: Phase3/fsd/branch_directory_fsd.md
- Test Plan: Phase3/tests/branch_directory_tests.md
- V2 Config: JobExecutor/Jobs/branch_directory_v2.json

## Summary of Changes
The original job used a SQL Transformation with an unnecessary CTE containing ROW_NUMBER for deduplication, when no duplicate branch_ids exist in the source data. The V2 simplifies the SQL to a direct SELECT with ORDER BY, removing the CTE and ROW_NUMBER construct.

## Anti-Pattern Scorecard

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | N                   | N/A                | No redundant DataSourcing |
| AP-2    | N                   | N/A                | No duplicated logic |
| AP-3    | N                   | N/A                | Already uses SQL Transformation |
| AP-4    | N                   | N/A                | All sourced columns are used in output |
| AP-5    | N                   | N/A                | No asymmetric NULL handling |
| AP-6    | N                   | N/A                | No External module |
| AP-7    | N                   | N/A                | No magic values |
| AP-8    | Y                   | Y                  | Removed unnecessary CTE with ROW_NUMBER deduplication; replaced with simple SELECT since no duplicate branch_ids exist |
| AP-9    | N                   | N/A                | Name accurately describes the job |
| AP-10   | N                   | N/A                | No inter-job dependencies needed |

## Comparison Results
- Dates verified: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Write mode: Overwrite
- Row count: 40

## Confidence Assessment
**HIGH** -- Simple pass-through directory lookup with no complex logic. The only anti-pattern was unnecessary SQL complexity, which was straightforward to simplify.
