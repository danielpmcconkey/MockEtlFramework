# CustomerSegmentMap -- Governance Report

## Links
- BRD: Phase3/brd/customer_segment_map_brd.md
- FSD: Phase3/fsd/customer_segment_map_fsd.md
- Test Plan: Phase3/tests/customer_segment_map_tests.md
- V2 Config: JobExecutor/Jobs/customer_segment_map_v2.json

## Summary of Changes
The original job used a clean SQL Transformation with a proper JOIN, but included an unused branches DataSourcing module. The V2 retains the same SQL logic and removes only the unused branches DataSourcing module.

## Anti-Pattern Scorecard

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed unused `branches` DataSourcing module entirely |
| AP-2    | N                   | N/A                | No duplicated logic |
| AP-3    | N                   | N/A                | Original already uses SQL Transformation |
| AP-4    | N                   | N/A                | All sourced columns from customers_segments and segments are used |
| AP-5    | N                   | N/A                | No asymmetric NULL handling |
| AP-6    | N                   | N/A                | No External module |
| AP-7    | N                   | N/A                | No magic values |
| AP-8    | N                   | N/A                | SQL is already clean and minimal |
| AP-9    | N                   | N/A                | Name accurately reflects output |
| AP-10   | N                   | N/A                | No undeclared dependencies |

## Comparison Results
- Dates verified: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Write mode: Append
- Row count per date: 291

## Confidence Assessment
**HIGH** -- Straightforward JOIN with minimal anti-patterns. Only required removing one unused DataSourcing module. No fix iterations required for this job.
