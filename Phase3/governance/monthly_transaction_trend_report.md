# MonthlyTransactionTrend -- Governance Report

## Links
- BRD: Phase3/brd/monthly_transaction_trend_brd.md
- FSD: Phase3/fsd/monthly_transaction_trend_fsd.md
- Test Plan: Phase3/tests/monthly_transaction_trend_tests.md
- V2 Config: JobExecutor/Jobs/monthly_transaction_trend_v2.json

## Summary of Changes
The original job re-derived daily transaction statistics from raw datalake.transactions despite having a declared SameDay dependency on DailyTransactionVolume (which computes identical metrics), with an unused branches DataSourcing module, unused columns, an unnecessary CTE, and a hardcoded date filter. The V2 reads from curated.daily_transaction_volume instead of re-deriving, performs a simple column rename (total_transactions -> daily_transactions, total_amount -> daily_amount, avg_amount -> avg_transaction_amount), removes the branches DataSourcing, and eliminates the CTE and hardcoded date. The misleading name "MonthlyTransactionTrend" (produces daily data, not monthly) is documented but not changed.

## Anti-Pattern Scorecard

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed `branches` DataSourcing module (never referenced in SQL) |
| AP-2    | Y                   | Y                  | V2 reads from curated.daily_transaction_volume instead of re-deriving from datalake.transactions |
| AP-3    | N                   | N/A                | Original already uses Transformation (not External module) |
| AP-4    | Y                   | Y                  | Original sourced account_id and txn_type which were unused; V2 reads only needed columns from upstream table |
| AP-5    | N                   | N/A                | No NULL/default handling involved |
| AP-6    | N                   | N/A                | No External module with row-by-row iteration |
| AP-7    | Y                   | Y                  | Removed hardcoded date '2024-10-01' from WHERE clause (DataSourcing handles date filtering) |
| AP-8    | Y                   | Y                  | Removed unnecessary CTE wrapper; V2 is a simple column-renaming SELECT |
| AP-9    | Y                   | N (documented)     | Name "MonthlyTransactionTrend" is misleading (produces daily data, not monthly); cannot rename for output compatibility |
| AP-10   | N                   | N/A                | Dependency on DailyTransactionVolume already declared; V2 now actually uses upstream output |

## Comparison Results
- Dates verified: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Write mode: Append
- Row count per date: 1

## Confidence Assessment
**HIGH** -- All 6 business rules directly observable. The AP-2 fix is architecturally significant, properly leveraging the upstream dependency. The most anti-pattern-laden job in the set (6 distinct anti-patterns). No fix iterations required for this job.
