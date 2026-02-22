# CustomerDemographics -- Governance Report

## Links
- BRD: Phase3/brd/customer_demographics_brd.md
- FSD: Phase3/fsd/customer_demographics_fsd.md
- Test Plan: Phase3/tests/customer_demographics_tests.md
- V2 Config: JobExecutor/Jobs/customer_demographics_v2.json

## Summary of Changes
The original job used an External module (CustomerDemographicsBuilder.cs) to compute age, age bracket, and primary phone/email per customer via row-by-row iteration, with an unused segments DataSourcing module and many unused columns. The V2 replaces the External module with a SQL Transformation using date arithmetic for age, CASE for age bracket, and window functions (ROW_NUMBER) for primary phone/email selection. It removes the segments DataSourcing module and trims unused columns from all source tables. Age bracket magic values are documented with SQL comments.

## Anti-Pattern Scorecard

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed unused `segments` DataSourcing module entirely |
| AP-2    | N                   | N/A                | No duplicated logic |
| AP-3    | Y                   | Y                  | Replaced External module with SQL Transformation + DataFrameWriter |
| AP-4    | Y                   | Y                  | Removed unused columns: `prefix`, `sort_name`, `suffix` from customers; `phone_id`, `phone_type` from phone_numbers; `email_id`, `email_type` from email_addresses |
| AP-5    | N                   | N/A                | NULL handling is consistent (empty string for missing phone/email) |
| AP-6    | Y                   | Y                  | Row-by-row iteration replaced with set-based SQL (JOINs, window functions, GROUP BY) |
| AP-7    | Y                   | Documented         | Age bracket boundaries (26, 35, 45, 55, 65) kept as literals with SQL comments explaining each bracket |
| AP-8    | N                   | N/A                | No overly complex SQL in original |
| AP-9    | N                   | N/A                | Name accurately reflects output |
| AP-10   | N                   | N/A                | No undeclared dependencies |

## Comparison Results
- Dates verified: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Write mode: Overwrite
- Row count: 223
- Note: Required date format fix in iteration 3 (strftime for birthdate column)

## Confidence Assessment
**HIGH** -- All 8 business rules directly observable. The birthdate formatting issue was a framework-level concern (SQLite date format), not a business logic error. The age bracket boundaries are documented with comments.
