# AccountTypeDistribution -- Governance Report

## Links
- BRD: Phase3/brd/account_type_distribution_brd.md
- FSD: Phase3/fsd/account_type_distribution_fsd.md
- Test Plan: Phase3/tests/account_type_distribution_tests.md
- V2 Config: JobExecutor/Jobs/account_type_distribution_v2.json

## Summary of Changes
The original job used an External module (AccountDistributionCalculator.cs) to count accounts by type and compute percentage distribution, with an unused branches DataSourcing module and unused columns. The V2 replaces the External module with a SQL Transformation using GROUP BY with subquery for total count and percentage calculation, removes the branches DataSourcing module, and trims accounts columns to only account_type.

## Anti-Pattern Scorecard

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed unused `branches` DataSourcing module entirely |
| AP-2    | N                   | N/A                | No duplicated logic |
| AP-3    | Y                   | Y                  | Replaced External module with SQL Transformation (GROUP BY + subquery) |
| AP-4    | Y                   | Y                  | Removed unused columns (account_id, customer_id, account_status, current_balance) from DataSourcing |
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
- Note: Required numeric precision fix in iteration 4 (NUMERIC column lacked scale constraint)

## Confidence Assessment
**HIGH** -- All business rules directly observable. The percentage rounding required a precision fix (iteration 4) to match the original NUMERIC(5,2) column behavior, which was resolved by adding matching precision/scale constraints to the double_secret_curated table.
