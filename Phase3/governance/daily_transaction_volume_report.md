# DailyTransactionVolume -- Governance Report

## Links
- BRD: Phase3/brd/daily_transaction_volume_brd.md
- FSD: Phase3/fsd/daily_transaction_volume_fsd.md
- Test Plan: Phase3/tests/daily_transaction_volume_tests.md
- V2 Config: JobExecutor/Jobs/daily_transaction_volume_v2.json

## Summary of Changes
The original job re-derived aggregate metrics (count, total, average) from raw datalake.transactions despite having a declared SameDay dependency on DailyTransactionSummary, and used an unnecessary CTE with unused MIN/MAX calculations. The V2 leverages the upstream curated.daily_transaction_summary table, computing volume metrics from already-aggregated per-account summaries. The unnecessary CTE and unused calculations are eliminated.

## Anti-Pattern Scorecard

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | N                   | N/A                | No unused data sources |
| AP-2    | Y                   | Y                  | Instead of re-aggregating from raw datalake.transactions, V2 reads from curated.daily_transaction_summary and derives volume metrics from already-computed per-account summaries |
| AP-3    | N                   | N/A                | Original already uses SQL Transformation |
| AP-4    | Y                   | Y                  | Original sourced transaction_id, account_id, txn_type but only used amount and as_of; V2 sources only transaction_count and total_amount from summary table |
| AP-5    | N                   | N/A                | No asymmetric NULL handling |
| AP-6    | N                   | N/A                | No External module |
| AP-7    | N                   | N/A                | No magic values |
| AP-8    | Y                   | Y                  | Removed unnecessary CTE with unused MIN/MAX calculations; V2 uses a direct single-level query |
| AP-9    | N                   | N/A                | Name accurately reflects output |
| AP-10   | N                   | N/A                | Dependency on DailyTransactionSummary already declared in original; V2 now actually leverages it |

## Comparison Results
- Dates verified: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Write mode: Append
- Row count per date: 1

## Confidence Assessment
**HIGH** -- All 8 business rules directly observable. The AP-2 fix is architecturally significant, properly leveraging the upstream dependency. No fix iterations required for this job.
