# CustomerAddressHistory -- Governance Report

## Links
- BRD: Phase3/brd/customer_address_history_brd.md
- FSD: Phase3/fsd/customer_address_history_fsd.md
- Test Plan: Phase3/tests/customer_address_history_tests.md
- V2 Config: JobExecutor/Jobs/customer_address_history_v2.json

## Summary of Changes
The original job used a SQL Transformation with an unused branches DataSourcing module, an unused address_id column, and an unnecessary subquery wrapper. The V2 removes the branches DataSourcing module, removes address_id from the column list, and simplifies the SQL to a direct SELECT with WHERE and ORDER BY (removing the unnecessary subquery).

## Anti-Pattern Scorecard

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed unused `branches` DataSourcing module |
| AP-2    | N                   | N/A                | No duplicated logic |
| AP-3    | N                   | N/A                | Original already uses SQL Transformation |
| AP-4    | Y                   | Y                  | Removed `address_id` from DataSourcing columns (not in output) |
| AP-5    | N                   | N/A                | No NULL/default asymmetry |
| AP-6    | N                   | N/A                | No row-by-row iteration |
| AP-7    | N                   | N/A                | No magic values |
| AP-8    | Y                   | Y                  | Removed unnecessary subquery wrapper; direct SELECT with WHERE and ORDER BY |
| AP-9    | N                   | N/A                | Name accurately describes the job |
| AP-10   | N                   | N/A                | No inter-job dependencies needed |

## Comparison Results
- Dates verified: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Write mode: Append
- Row count per date: 223 (varies slightly as addresses change)

## Confidence Assessment
**HIGH** -- Simple SELECT with WHERE filter. All business rules directly observable. No fix iterations required for this job.
