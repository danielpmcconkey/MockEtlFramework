# CreditScoreAverage -- Governance Report

## Links
- BRD: Phase3/brd/credit_score_average_brd.md
- FSD: Phase3/fsd/credit_score_average_fsd.md
- Test Plan: Phase3/tests/credit_score_average_tests.md
- V2 Config: JobExecutor/Jobs/credit_score_average_v2.json

## Summary of Changes
The original job used an External module (CreditScoreAverager.cs) to compute per-customer average credit scores and pivot bureau scores into separate columns, with an unused segments DataSourcing module and unused credit_score_id column. The V2 retains an External module (partially justified for the empty-DataFrame guard) but replaces the row-by-row iteration with set-based SQLite operations, removes the segments DataSourcing, and trims unused columns. The asymmetric NULL handling (empty strings for names, NULL for missing bureau scores) is preserved and documented.

## Anti-Pattern Scorecard

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed unused `segments` DataSourcing module |
| AP-2    | N                   | N/A                | No duplicated logic |
| AP-3    | Y                   | Partial            | External module retained for empty-DataFrame guard; internal logic uses set-based SQL instead of row-by-row iteration |
| AP-4    | Y                   | Y                  | Removed `credit_score_id` from DataSourcing columns |
| AP-5    | Y                   | N (documented)     | Asymmetric NULL handling reproduced for output equivalence: names coalesced to empty string, missing bureau scores remain NULL |
| AP-6    | Y                   | Y                  | Row-by-row foreach loops replaced with SQLite-based set operations |
| AP-7    | N                   | N/A                | No magic values |
| AP-8    | N                   | N/A                | No overly complex SQL |
| AP-9    | N                   | N/A                | Name accurately describes the job |
| AP-10   | N                   | N/A                | No inter-job dependencies needed |

## Comparison Results
- Dates verified: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Write mode: Overwrite
- Row count: 223

## Confidence Assessment
**HIGH** -- All business rules directly observable. The pivot logic (bureau scores into separate columns) and average calculation are well-understood. No fix iterations required for this job.
