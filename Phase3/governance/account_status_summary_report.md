# AccountStatusSummary -- Governance Report

## Links
- BRD: Phase3/brd/account_status_summary_brd.md
- FSD: Phase3/fsd/account_status_summary_fsd.md
- Test Plan: Phase3/tests/account_status_summary_tests.md
- V2 Config: JobExecutor/Jobs/account_status_summary_v2.json

## Summary of Changes
The original job used an External module (AccountStatusCounter.cs) to perform a GROUP BY count via row-by-row dictionary building, with an unused segments DataSourcing module and unused columns (account_id, customer_id, current_balance). The V2 replaces the External module with a SQL Transformation (`SELECT account_type, account_status, COUNT(*) AS account_count, as_of FROM accounts GROUP BY account_type, account_status, as_of`), removes the segments DataSourcing module, and trims accounts columns to only account_type and account_status.

## Anti-Pattern Scorecard

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed unused `segments` DataSourcing module entirely |
| AP-2    | N                   | N/A                | No duplicated logic |
| AP-3    | Y                   | Y                  | Replaced External module with SQL GROUP BY Transformation |
| AP-4    | Y                   | Y                  | Removed unused columns (account_id, customer_id, current_balance) from DataSourcing |
| AP-5    | N                   | N/A                | No asymmetric NULL handling |
| AP-6    | Y                   | Y                  | Replaced row-by-row dictionary counting with SQL GROUP BY + COUNT |
| AP-7    | N                   | N/A                | No magic values |
| AP-8    | N                   | N/A                | No overly complex SQL |
| AP-9    | N                   | N/A                | Name accurately describes the job |
| AP-10   | N                   | N/A                | No inter-job dependencies needed |

## Comparison Results
- Dates verified: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Write mode: Overwrite
- Row count: 3

## Confidence Assessment
**HIGH** -- Straightforward GROUP BY aggregation. All business rules directly observable. No fix iterations required for this job.
