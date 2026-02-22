# CoveredTransactions -- Governance Report

## Links
- BRD: Phase3/brd/covered_transactions_brd.md
- FSD: Phase3/fsd/covered_transactions_fsd.md
- Test Plan: Phase3/tests/covered_transactions_tests.md
- V2 Config: JobExecutor/Jobs/covered_transactions_v2.json

## Summary of Changes
The original job used a justified External module (CoveredTransactionProcessor.cs) performing multi-query database access with snapshot fallback for accounts, customers, addresses, and segments. The V2 retains the External module approach (justified for snapshot fallback and multi-table lookups that cannot be expressed in the framework's single SQL Transformation model) but cleans up the code with improved comments explaining the hardcoded "Checking" and "US" filter values, and documents the intentional asymmetric NULL handling.

## Anti-Pattern Scorecard

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | N                   | N/A                | No DataSourcing modules (External does its own queries) |
| AP-2    | N                   | N/A                | No duplicated logic |
| AP-3    | N                   | N/A                | External module is genuinely justified for snapshot fallback |
| AP-4    | N                   | N/A                | No DataSourcing modules to trim |
| AP-5    | Y                   | N (documented)     | Intentional asymmetry: account match required (row skipped), customer demographics optional (NULL on miss). Documented. |
| AP-6    | N                   | N/A                | Row iteration necessary for multi-table join with lookups |
| AP-7    | Y                   | Y                  | Added comments explaining "Checking" = FDIC-insured checking accounts, "US" = US-based addresses |
| AP-8    | N                   | N/A                | SQL queries are appropriately complex for their purpose |
| AP-9    | N                   | N/A                | Name is reasonable (covered = FDIC-covered checking transactions) |
| AP-10   | N                   | N/A                | All sources are datalake tables; no inter-job dependencies |

## Comparison Results
- Dates verified: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Write mode: Append
- Row count per date: 85 (varies by date)

## Confidence Assessment
**HIGH** -- Complex but well-understood job with snapshot fallback logic. All 11 business rules documented with HIGH confidence. The External module is one of only 2 (along with CustomerAddressDeltas) retained as genuinely justified.
