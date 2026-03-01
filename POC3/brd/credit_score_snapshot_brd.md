# CreditScoreSnapshot -- Business Requirements Document

## Overview
Produces a simple pass-through snapshot of all credit score records for the effective date range. Each credit score row is copied as-is into the output CSV. Despite sourcing the branches table, the External module does not use it.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/credit_score_snapshot.csv`
- **includeHeader**: true
- **writeMode**: Overwrite
- **lineEnding**: CRLF
- **trailerFormat**: (not configured -- no trailer)

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.credit_scores | credit_score_id, customer_id, bureau, score | Effective date range (injected by executor) | [credit_score_snapshot.json:8-11] |
| datalake.branches | branch_id, branch_name, city, state_province | Effective date range (injected by executor) | [credit_score_snapshot.json:14-17] |

## Business Rules

BR-1: All credit score rows from the DataSourcing output are passed through directly with no filtering, aggregation, or transformation.
- Confidence: HIGH
- Evidence: [CreditScoreProcessor.cs:25-34] -- `foreach` loop copies every row field-by-field into output.

BR-2: When the credit_scores DataFrame is null or empty, the output is an empty DataFrame with the correct schema.
- Confidence: HIGH
- Evidence: [CreditScoreProcessor.cs:17-21] -- null/empty guard returns empty DataFrame.

BR-3: The branches table is sourced by DataSourcing but is NOT used by the External module. It is loaded into shared state but never accessed.
- Confidence: HIGH
- Evidence: [CreditScoreProcessor.cs:15] -- only `credit_scores` is retrieved from shared state. No reference to `branches`.

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| credit_score_id | credit_scores.credit_score_id | Pass-through | [CreditScoreProcessor.cs:29] |
| customer_id | credit_scores.customer_id | Pass-through | [CreditScoreProcessor.cs:30] |
| bureau | credit_scores.bureau | Pass-through | [CreditScoreProcessor.cs:31] |
| score | credit_scores.score | Pass-through | [CreditScoreProcessor.cs:32] |
| as_of | credit_scores.as_of | Pass-through | [CreditScoreProcessor.cs:33] |

## Non-Deterministic Fields
None identified.

## Write Mode Implications
- **Overwrite**: Each execution replaces the entire output file. For multi-day auto-advance runs, only the last effective date's output survives on disk.

## Edge Cases
- **Empty credit_scores**: Output is an empty DataFrame with correct schema. CSV will have header only.
- **Branches table unused**: Loaded into shared state but never referenced by the External module.
- **All fields are direct pass-through**: No type conversion, rounding, or filtering is applied.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Pass-through of all credit score rows | [CreditScoreProcessor.cs:25-34] |
| Empty input guard | [CreditScoreProcessor.cs:17-21] |
| Branches table unused | [CreditScoreProcessor.cs:15] |
| No trailer | [credit_score_snapshot.json:28-32] |
| CRLF line endings | [credit_score_snapshot.json:30] |
| Overwrite write mode | [credit_score_snapshot.json:29] |

## Open Questions
- OQ-1: The branches table is sourced but unused. It is unclear whether this is intentional dead code or a missing feature. Confidence: MEDIUM -- branches are loaded by DataSourcing but never accessed.
