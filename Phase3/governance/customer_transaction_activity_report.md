# CustomerTransactionActivity -- Governance Report

## Links
- BRD: Phase3/brd/customer_transaction_activity_brd.md
- FSD: Phase3/fsd/customer_transaction_activity_fsd.md
- Test Plan: Phase3/tests/customer_transaction_activity_tests.md
- V2 Config: JobExecutor/Jobs/customer_transaction_activity_v2.json

## Summary of Changes
The original job used an External module (CustomerTxnActivityBuilder.cs) to count and sum transactions per customer via dictionary-based account-to-customer lookups, with an unused transaction_id column. The V2 replaces the External module with a SQL Transformation using JOIN + GROUP BY with COUNT/SUM and conditional CASE for debit/credit counting, removes the unused transaction_id column, and eliminates the two foreach loops.

## Anti-Pattern Scorecard

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | N                   | N/A                | No unused data sources |
| AP-2    | N                   | N/A                | No duplicated logic |
| AP-3    | Y                   | Y                  | Replaced External module with SQL Transformation (JOIN + GROUP BY with COUNT/SUM) |
| AP-4    | Y                   | Y                  | Removed unused `transaction_id` from transactions DataSourcing |
| AP-5    | N                   | N/A                | Asymmetric empty guards replaced by SQL JOIN that handles both cases naturally |
| AP-6    | Y                   | Y                  | Two foreach loops replaced with a single SQL query using JOIN + GROUP BY |
| AP-7    | N                   | N/A                | No magic values |
| AP-8    | N                   | N/A                | No complex SQL |
| AP-9    | N                   | N/A                | Name accurately reflects output |
| AP-10   | N                   | N/A                | No undeclared dependencies |

## Comparison Results
- Dates verified: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Write mode: Append
- Row count per date: 196 (weekdays only; varies by date)

## Confidence Assessment
**HIGH** -- All 11 business rules directly observable. The JOIN + GROUP BY replacement is a clean equivalent of the original dictionary-based logic. No fix iterations required for this job.
