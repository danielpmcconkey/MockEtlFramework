# LoanRiskAssessment -- Governance Report

## Links
- BRD: Phase3/brd/loan_risk_assessment_brd.md
- FSD: Phase3/fsd/loan_risk_assessment_fsd.md
- Test Plan: Phase3/tests/loan_risk_assessment_tests.md
- V2 Config: JobExecutor/Jobs/loan_risk_assessment_v2.json

## Summary of Changes
The original job used an External module (LoanRiskCalculator.cs) to compute average credit scores per customer and assign risk tiers via row-by-row iteration, with 2 unused DataSourcing modules (customers, segments) and unused columns from credit_scores. The V2 replaces the External module with a SQL Transformation using AVG + GROUP BY for score averaging, LEFT JOIN to loan_accounts, and CASE WHEN for risk tier assignment. Risk tier thresholds are documented with SQL comments.

## Anti-Pattern Scorecard

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed `customers` and `segments` DataSourcing modules (never referenced by External module) |
| AP-2    | N                   | N/A                | No duplicated upstream logic |
| AP-3    | Y                   | Y                  | Replaced External module with SQL Transformation (AVG + JOIN + CASE) |
| AP-4    | Y                   | Y                  | Removed `credit_score_id` and `bureau` from credit_scores DataSourcing (only customer_id and score needed) |
| AP-5    | N                   | N/A                | NULL handling is consistent (NULL avg_credit_score maps to "Unknown" risk_tier) |
| AP-6    | Y                   | Y                  | Three foreach loops replaced by set-based SQL |
| AP-7    | Y                   | Y (documented)     | Risk tier thresholds (750, 650, 550) documented in SQL comments |
| AP-8    | N                   | N/A                | No overly complex SQL in original |
| AP-9    | N                   | N/A                | Job name accurately describes what it does |
| AP-10   | N                   | N/A                | No undeclared dependencies |

## Comparison Results
- Dates verified: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Write mode: Overwrite
- Row count: 90

## Confidence Assessment
**HIGH** -- All 6 business rules directly observable. The risk tier thresholds are clearly defined in code. No fix iterations required for this job.
