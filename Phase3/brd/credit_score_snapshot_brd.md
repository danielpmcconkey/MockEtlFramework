# BRD: CreditScoreSnapshot

## Overview
This job produces a simple pass-through snapshot of all credit scores from the data lake for the current effective date. It copies credit score records (id, customer_id, bureau, score) verbatim into the curated layer.

## Source Tables

| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| credit_scores | datalake | credit_score_id, customer_id, bureau, score | Sourced via DataSourcing for effective date range | [credit_score_snapshot.json:7-11] |
| branches | datalake | branch_id, branch_name, city, state_province | Sourced via DataSourcing but NOT USED in the External module | [credit_score_snapshot.json:13-18] |

## Business Rules

BR-1: All credit score records for the effective date are passed through to the output without filtering or transformation.
- Confidence: HIGH
- Evidence: [ExternalModules/CreditScoreProcessor.cs:25-34] Iterates all credit score rows and copies each field directly

BR-2: The output contains only the columns: credit_score_id, customer_id, bureau, score, as_of.
- Confidence: HIGH
- Evidence: [ExternalModules/CreditScoreProcessor.cs:10-13] `outputColumns` list defines exactly these 5 columns

BR-3: Data is written in Overwrite mode -- only the most recent effective date's data persists.
- Confidence: HIGH
- Evidence: [credit_score_snapshot.json:28] `"writeMode": "Overwrite"`
- Evidence: [curated.credit_score_snapshot] Only 1 as_of value (2024-10-31) with 669 rows

BR-4: If credit_scores DataFrame is empty, an empty DataFrame is returned.
- Confidence: HIGH
- Evidence: [ExternalModules/CreditScoreProcessor.cs:17-21] `if (creditScores == null || creditScores.Count == 0)` returns empty DataFrame

BR-5: The branches DataSourcing module is declared in the job config but is NOT used by the CreditScoreProcessor External module.
- Confidence: HIGH
- Evidence: [credit_score_snapshot.json:13-18] branches is sourced but [CreditScoreProcessor.cs] never references `sharedState["branches"]`

BR-6: No data transformation or aggregation is performed -- this is a pure copy/snapshot operation.
- Confidence: HIGH
- Evidence: [ExternalModules/CreditScoreProcessor.cs:27-33] Each field is directly copied: `row["credit_score_id"]`, `row["customer_id"]`, `row["bureau"]`, `row["score"]`, `row["as_of"]`

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| credit_score_id | credit_scores.credit_score_id | Direct pass-through | [CreditScoreProcessor.cs:29] |
| customer_id | credit_scores.customer_id | Direct pass-through | [CreditScoreProcessor.cs:30] |
| bureau | credit_scores.bureau | Direct pass-through | [CreditScoreProcessor.cs:31] |
| score | credit_scores.score | Direct pass-through | [CreditScoreProcessor.cs:32] |
| as_of | credit_scores.as_of | Direct pass-through | [CreditScoreProcessor.cs:33] |

## Edge Cases

- **NULL handling**: No explicit NULL handling. Values are copied as-is from the source DataFrame. If any source field is null, it will be null in the output.
  - Evidence: [CreditScoreProcessor.cs:29-33] Direct assignment without null checks
- **Weekend/date fallback**: Since DataSourcing sources credit_scores with the framework's effective date mechanism, and credit_scores only has weekday data (23 dates), weekend runs will produce empty DataFrames. With Overwrite mode, the table becomes empty on weekends.
  - Evidence: [datalake.credit_scores] 23 distinct as_of dates (weekdays only)
- **Zero-row behavior**: Empty input produces an empty DataFrame. The table is truncated (Overwrite) and left empty.
  - Evidence: [CreditScoreProcessor.cs:17-21]

## Traceability Matrix

| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [CreditScoreProcessor.cs:25-34] |
| BR-2 | [CreditScoreProcessor.cs:10-13] |
| BR-3 | [credit_score_snapshot.json:28], [curated.credit_score_snapshot row counts] |
| BR-4 | [CreditScoreProcessor.cs:17-21] |
| BR-5 | [credit_score_snapshot.json:13-18], [CreditScoreProcessor.cs full source] |
| BR-6 | [CreditScoreProcessor.cs:27-33] |

## Open Questions

- **Branches sourced but unused**: The job config sources the branches table, but the CreditScoreProcessor never uses it. This appears to be dead configuration. Confidence: HIGH that it is unused; business intent is LOW.
- **Weekend Overwrite behavior**: Same concern as CreditScoreAverage -- on weekends, the table is truncated and left empty. Confidence: MEDIUM on whether this is intentional.
- **Purpose of snapshot**: This job appears to be a simple staging/copy operation with no business logic. It may serve as a dependency or data availability check for downstream jobs. Confidence: MEDIUM.
