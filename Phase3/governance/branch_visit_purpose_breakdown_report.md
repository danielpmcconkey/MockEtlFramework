# BranchVisitPurposeBreakdown -- Governance Report

## Links
- BRD: Phase3/brd/branch_visit_purpose_breakdown_brd.md
- FSD: Phase3/fsd/branch_visit_purpose_breakdown_fsd.md
- Test Plan: Phase3/tests/branch_visit_purpose_breakdown_tests.md
- V2 Config: JobExecutor/Jobs/branch_visit_purpose_breakdown_v2.json

## Summary of Changes
The original job used a SQL Transformation with an unnecessary CTE containing an unused window function (SUM(COUNT(*)) OVER for total_branch_visits that was computed but never selected), with an unused segments DataSourcing module and unused columns. The V2 simplifies the SQL to a direct GROUP BY with JOIN, removes the CTE and unused window function, removes the segments DataSourcing module, and trims branch_visits columns (removing customer_id and visit_id).

## Anti-Pattern Scorecard

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed unused `segments` DataSourcing module entirely |
| AP-2    | N                   | N/A                | No duplicated logic |
| AP-3    | N                   | N/A                | Already uses SQL Transformation |
| AP-4    | Y                   | Y                  | Removed unused columns (visit_id, customer_id) from branch_visits DataSourcing |
| AP-5    | N                   | N/A                | No asymmetric NULL handling |
| AP-6    | N                   | N/A                | No External module |
| AP-7    | N                   | N/A                | No magic values |
| AP-8    | Y                   | Y                  | Removed unnecessary CTE with unused total_branch_visits window function; simplified to direct GROUP BY with JOIN |
| AP-9    | N                   | N/A                | Name accurately describes the job |
| AP-10   | N                   | N/A                | No inter-job dependencies needed |

## Comparison Results
- Dates verified: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Write mode: Append
- Row count per date: 28 (varies by date)

## Confidence Assessment
**HIGH** -- Straightforward GROUP BY aggregation with JOIN. All business rules directly observable. No fix iterations required for this job.
