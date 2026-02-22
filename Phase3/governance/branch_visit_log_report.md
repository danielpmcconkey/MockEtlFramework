# BranchVisitLog -- Governance Report

## Links
- BRD: Phase3/brd/branch_visit_log_brd.md
- FSD: Phase3/fsd/branch_visit_log_fsd.md
- Test Plan: Phase3/tests/branch_visit_log_tests.md
- V2 Config: JobExecutor/Jobs/branch_visit_log_v2.json

## Summary of Changes
The original job used an External module (BranchVisitEnricher.cs) to perform two dictionary-based LEFT JOINs (visits to branches and visits to customers), with an unused addresses DataSourcing module and unused columns from branches. The V2 replaces the External module with a SQL Transformation using LEFT JOINs, removes the addresses DataSourcing module, and trims branches columns to only branch_id and branch_name. The asymmetric NULL handling (empty string for missing branch names, NULL for missing customer names) is preserved for output equivalence.

## Anti-Pattern Scorecard

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed unused `addresses` DataSourcing module entirely |
| AP-2    | N                   | N/A                | No duplicated logic |
| AP-3    | Y                   | Y                  | Replaced External module with SQL Transformation (LEFT JOINs) |
| AP-4    | Y                   | Y                  | Removed unused columns from branches (address_line1, city, state_province, postal_code, country) |
| AP-5    | Y                   | N (documented)     | Asymmetric NULL handling preserved for output equivalence: branch_name defaults to empty string, customer names default to NULL. Documented as known inconsistency. |
| AP-6    | Y                   | Y                  | Replaced row-by-row dictionary lookups with SQL LEFT JOINs |
| AP-7    | N                   | N/A                | No magic values |
| AP-8    | N                   | N/A                | No overly complex SQL |
| AP-9    | N                   | N/A                | Name accurately describes the job |
| AP-10   | N                   | N/A                | No inter-job dependencies needed |

## Comparison Results
- Dates verified: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Write mode: Append
- Row count per date: 29 (weekdays only)
- Note: Required timestamp format fix in iteration 2 (REPLACE for 'T' separator)

## Confidence Assessment
**HIGH** -- All business rules directly observable. The timestamp format issue was a framework-level concern (SQLite 'T' separator), not a business logic error. The AP-5 asymmetric NULL handling is documented as a known inconsistency.
