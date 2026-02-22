# AccountBalanceSnapshot -- Governance Report

## Links
- BRD: Phase3/brd/account_balance_snapshot_brd.md
- FSD: Phase3/fsd/account_balance_snapshot_fsd.md
- Test Plan: Phase3/tests/account_balance_snapshot_tests.md
- V2 Config: JobExecutor/Jobs/account_balance_snapshot_v2.json

## Summary of Changes
The original job used an External module (AccountSnapshotBuilder.cs) to perform a trivial row-by-row copy of 6 columns from datalake.accounts, with an unused branches DataSourcing module and unused columns (open_date, interest_rate, credit_limit). The V2 replaces the External module with a simple SQL Transformation (`SELECT account_id, customer_id, account_type, account_status, current_balance, as_of FROM accounts`), removes the branches DataSourcing module, and trims the accounts column list to only the 5 columns actually needed.

## Anti-Pattern Scorecard

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed unused `branches` DataSourcing module entirely |
| AP-2    | N                   | N/A                | No duplicated logic |
| AP-3    | Y                   | Y                  | Replaced External module with SQL Transformation |
| AP-4    | Y                   | Y                  | Removed unused columns (open_date, interest_rate, credit_limit) from DataSourcing |
| AP-5    | N                   | N/A                | No asymmetric NULL handling |
| AP-6    | Y                   | Y                  | Replaced row-by-row iteration with single SELECT statement |
| AP-7    | N                   | N/A                | No magic values |
| AP-8    | N                   | N/A                | No overly complex SQL |
| AP-9    | N                   | N/A                | Name accurately describes the job |
| AP-10   | N                   | N/A                | No inter-job dependencies needed |

## Comparison Results
- Dates verified: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Write mode: Append
- Row count per date: 277

## Confidence Assessment
**HIGH** -- The job is a straightforward pass-through of account data with no complex business logic. All business rules are directly observable in code with HIGH confidence. No fix iterations were required specifically for this job.
