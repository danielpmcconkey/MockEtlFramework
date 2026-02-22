# HighBalanceAccounts -- Governance Report

## Links
- BRD: Phase3/brd/high_balance_accounts_brd.md
- FSD: Phase3/fsd/high_balance_accounts_fsd.md
- Test Plan: Phase3/tests/high_balance_accounts_tests.md
- V2 Config: JobExecutor/Jobs/high_balance_accounts_v2.json

## Summary of Changes
The original job used an External module (HighBalanceFilter.cs) to perform a simple filter (balance > 10000) and dictionary-based LEFT JOIN (accounts to customers), with an unused account_status column. The V2 replaces the External module with a SQL Transformation (`SELECT ... FROM accounts a LEFT JOIN customers c ... WHERE a.current_balance > 10000`), removes the unused account_status column, and documents the 10000 threshold with a SQL comment.

## Anti-Pattern Scorecard

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | N                   | N/A                | No redundant DataSourcing in original |
| AP-2    | N                   | N/A                | No duplicated upstream logic |
| AP-3    | Y                   | Y                  | Replaced External module with SQL Transformation |
| AP-4    | Y                   | Y                  | Removed `account_status` from accounts DataSourcing columns |
| AP-5    | N                   | N/A                | NULL handling is consistent (COALESCE to '' for both name fields) |
| AP-6    | Y                   | Y                  | Row-by-row foreach replaced by set-based SQL JOIN + WHERE |
| AP-7    | Y                   | Y (documented)     | Threshold 10000 documented in SQL comment; value retained for output match |
| AP-8    | N                   | N/A                | No overly complex SQL in original |
| AP-9    | N                   | N/A                | Job name accurately describes what it does |
| AP-10   | N                   | N/A                | No undeclared dependencies |

## Comparison Results
- Dates verified: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Write mode: Overwrite
- Row count: 54

## Confidence Assessment
**HIGH** -- All 6 business rules directly observable. Simple filter + JOIN logic. No fix iterations required for this job.
