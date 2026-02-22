# CustomerBranchActivity -- Governance Report

## Links
- BRD: Phase3/brd/customer_branch_activity_brd.md
- FSD: Phase3/fsd/customer_branch_activity_fsd.md
- Test Plan: Phase3/tests/customer_branch_activity_tests.md
- V2 Config: JobExecutor/Jobs/customer_branch_activity_v2.json

## Summary of Changes
The original job used an External module (CustomerBranchActivityBuilder.cs) to count branch visits per customer with dictionary-based lookups, with an unused branches DataSourcing module and unused columns from branch_visits. The V2 retains an External module (partially justified for the empty-DataFrame guard) but replaces manual dictionary loops with LINQ operations, removes the branches DataSourcing module, and trims branch_visits columns to only customer_id.

## Anti-Pattern Scorecard

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed unused `branches` DataSourcing module |
| AP-2    | N                   | N/A                | No duplicated logic |
| AP-3    | Y                   | Partial            | External module retained for empty-DataFrame guard; internal logic simplified with LINQ |
| AP-4    | Y                   | Y                  | Removed `visit_id`, `branch_id`, `visit_purpose` from DataSourcing (only `customer_id` needed) |
| AP-5    | N                   | N/A                | No asymmetric NULL handling (NULL names are intentional for missing customers) |
| AP-6    | Y                   | Y                  | Manual dictionary-building loops replaced with LINQ GroupBy and ToDictionary |
| AP-7    | N                   | N/A                | No magic values |
| AP-8    | N                   | N/A                | No overly complex SQL |
| AP-9    | N                   | N/A                | Name accurately describes the job |
| AP-10   | N                   | N/A                | No inter-job dependencies needed |

## Comparison Results
- Dates verified: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Write mode: Append
- Row count per date: 29 (weekdays only; no weekend output)

## Confidence Assessment
**HIGH** -- Straightforward GROUP BY + JOIN logic. All business rules directly observable. No fix iterations required for this job.
