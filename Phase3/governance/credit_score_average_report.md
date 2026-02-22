# Governance Report: CreditScoreAverage

## Links
- BRD: Phase3/brd/credit_score_average_brd.md
- FSD: Phase3/fsd/credit_score_average_fsd.md
- Test Plan: Phase3/tests/credit_score_average_tests.md
- V2 Module: ExternalModules/CreditScoreAverageV2Processor.cs
- V2 Config: JobExecutor/Jobs/credit_score_average_v2.json

## Summary of Changes
- Original approach: DataSourcing (credit_scores, customers, segments) -> External (CreditScoreAverager) -> DataFrameWriter to curated.credit_score_average
- V2 approach: DataSourcing (credit_scores, customers, segments) -> External (CreditScoreAverageV2Processor) writing to double_secret_curated.credit_score_average via DscWriterUtil
- Key difference: V2 adds explicit `Math.Round(..., 2)` for the avg_score column. The original relied on the curated table's `NUMERIC(6,2)` column type to auto-round. V2 also combines processing and writing.

## Anti-Patterns Identified
- **Unused DataSourcing step**: The `segments` table is sourced but never referenced by the External module.
- **Implicit numeric rounding**: The original computes avg_score via `scores.Average()` producing a full-precision decimal, relying on the database column `NUMERIC(6,2)` to round on INSERT.
- **Case-insensitive bureau matching**: Bureau names are matched via `bureau.ToLower()`, which is more defensive than needed given the data only contains properly-cased bureau names.

## Comparison Results
- Dates compared: 31 (Oct 1-31, 2024)
- Match percentage: 100%
- Fix iterations required for this specific job: 1 (Iteration 3 -- added `Math.Round(..., 2)` to avg_score computation)

## Confidence Assessment
- Overall confidence: HIGH
- The rounding fix resolved the only discrepancy. Bureau pivoting logic and customer name enrichment are straightforward.
