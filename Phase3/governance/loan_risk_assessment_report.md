# Governance Report: LoanRiskAssessment

## Links
- BRD: Phase3/brd/loan_risk_assessment_brd.md
- FSD: Phase3/fsd/loan_risk_assessment_fsd.md
- Test Plan: Phase3/tests/loan_risk_assessment_tests.md
- V2 Module: ExternalModules/LoanRiskAssessmentV2Processor.cs
- V2 Config: JobExecutor/Jobs/loan_risk_assessment_v2.json

## Summary of Changes
- Original approach: DataSourcing (loan_accounts, credit_scores, customers, segments) -> External (LoanRiskCalculator) -> DataFrameWriter to curated.loan_risk_assessment
- V2 approach: DataSourcing (loan_accounts, credit_scores, customers, segments) -> External (LoanRiskAssessmentV2Processor) writing to double_secret_curated.loan_risk_assessment via DscWriterUtil
- Key difference: V2 adds explicit `Math.Round(..., 2)` for the avg_credit_score column. The original relied on the curated table's NUMERIC column type to auto-round. V2 also combines processing and writing.

## Anti-Patterns Identified
- **Two unused DataSourcing steps**: Both `customers` and `segments` tables are sourced but never referenced by the External module.
- **Implicit numeric rounding**: The original computes avg_credit_score via `scores.Average()` producing a full-precision decimal, relying on the database column to round on INSERT.
- **Hardcoded risk tier thresholds**: The risk tier classification thresholds (750, 650, 550) are embedded in a C# switch expression rather than being configurable.

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 1 (Iteration 3 -- added `Math.Round(..., 2)` to avg_credit_score computation)

## Confidence Assessment
- Overall confidence: HIGH
- The rounding fix resolved the only discrepancy. Risk tier classification logic is clear with well-defined thresholds. NULL handling for customers without credit scores (avg_credit_score = NULL, risk_tier = "Unknown") is explicitly coded and documented.
