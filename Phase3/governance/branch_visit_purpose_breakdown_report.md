# Governance Report: BranchVisitPurposeBreakdown

## Links
- BRD: Phase3/brd/branch_visit_purpose_breakdown_brd.md
- FSD: Phase3/fsd/branch_visit_purpose_breakdown_fsd.md
- Test Plan: Phase3/tests/branch_visit_purpose_breakdown_tests.md
- V2 Module: ExternalModules/BranchVisitPurposeBreakdownV2Writer.cs
- V2 Config: JobExecutor/Jobs/branch_visit_purpose_breakdown_v2.json

## Summary of Changes
- Original approach: DataSourcing (branch_visits, branches, segments) -> Transformation (SQL with GROUP BY and window function) -> DataFrameWriter to curated.branch_visit_purpose_breakdown
- V2 approach: DataSourcing (branch_visits, branches, segments) -> Transformation (same SQL) -> External (BranchVisitPurposeBreakdownV2Writer) writing to double_secret_curated.branch_visit_purpose_breakdown via DscWriterUtil
- Key difference: V2 retains the identical Transformation SQL and replaces only the DataFrameWriter with a thin V2 writer module. No business logic changes.

## Anti-Patterns Identified
- **Unused DataSourcing step**: The `segments` table is sourced but never referenced in the Transformation SQL.
- **Dead code in SQL**: The CTE computes `total_branch_visits` via a `SUM(COUNT(*)) OVER (PARTITION BY ...)` window function, but this column is not included in the final SELECT. This adds execution cost without contributing to the output.

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 0

## Confidence Assessment
- Overall confidence: HIGH
- SQL-based transformation with identical SQL preserved. The dead window function does not affect output correctness.
