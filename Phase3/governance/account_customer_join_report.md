# AccountCustomerJoin -- Governance Report

## Links
- BRD: Phase3/brd/account_customer_join_brd.md
- FSD: Phase3/fsd/account_customer_join_fsd.md
- Test Plan: Phase3/tests/account_customer_join_tests.md
- V2 Config: JobExecutor/Jobs/account_customer_join_v2.json

## Summary of Changes
The original job used an External module (AccountCustomerDenormalizer.cs) to perform a dictionary-based LEFT JOIN between accounts and customers, with an unused addresses DataSourcing module. The V2 replaces the External module with a SQL Transformation using a LEFT JOIN (`SELECT a.account_id, a.customer_id, COALESCE(c.first_name, '') AS first_name, COALESCE(c.last_name, '') AS last_name, a.account_type, a.account_status, a.current_balance, a.as_of FROM accounts a LEFT JOIN customers c ON a.customer_id = c.id AND a.as_of = c.as_of`), and removes the addresses DataSourcing module.

## Anti-Pattern Scorecard

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed unused `addresses` DataSourcing module entirely |
| AP-2    | N                   | N/A                | No duplicated logic |
| AP-3    | Y                   | Y                  | Replaced External module with SQL Transformation (LEFT JOIN) |
| AP-4    | N                   | N/A                | All sourced columns from accounts and customers are used |
| AP-5    | N                   | N/A                | No asymmetric NULL handling |
| AP-6    | Y                   | Y                  | Replaced row-by-row dictionary lookup with SQL LEFT JOIN |
| AP-7    | N                   | N/A                | No magic values |
| AP-8    | N                   | N/A                | No overly complex SQL |
| AP-9    | N                   | N/A                | Name accurately describes the job |
| AP-10   | N                   | N/A                | No inter-job dependencies needed |

## Comparison Results
- Dates verified: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Write mode: Overwrite
- Row count: 277

## Confidence Assessment
**HIGH** -- Simple LEFT JOIN logic, all business rules directly observable, no ambiguities. No fix iterations required for this job.
