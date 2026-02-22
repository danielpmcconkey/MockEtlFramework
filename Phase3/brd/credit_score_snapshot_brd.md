# CreditScoreSnapshot — Business Requirements Document

## Overview

Produces a direct pass-through copy of credit score records from the datalake to the curated schema. Each row contains one credit score for one customer from one bureau for the effective date. Output uses Overwrite mode.

## Source Tables

| Table | Schema | Columns Used | Purpose |
|-------|--------|-------------|---------|
| `datalake.credit_scores` | datalake | credit_score_id, customer_id, bureau, score | Source credit score records |
| `datalake.branches` | datalake | branch_id, branch_name, city, state_province | **SOURCED BUT NEVER USED** — not referenced by the External module |

## Business Rules

BR-1: Every credit score record for the effective date is passed through to the output without any filtering or transformation.
- Confidence: HIGH
- Evidence: [ExternalModules/CreditScoreProcessor.cs:25-35] Simple iteration copies all rows; no filter conditions
- Evidence: [curated.credit_score_snapshot] Row count matches datalake.credit_scores per date (669 rows for Oct 31)

BR-2: Output columns are: credit_score_id, customer_id, bureau, score, as_of — matching the source exactly.
- Confidence: HIGH
- Evidence: [ExternalModules/CreditScoreProcessor.cs:10-13] OutputColumns definition
- Evidence: [ExternalModules/CreditScoreProcessor.cs:27-34] Direct copy of all four fields plus as_of

BR-3: When credit_scores DataFrame is empty, an empty output is produced.
- Confidence: HIGH
- Evidence: [ExternalModules/CreditScoreProcessor.cs:17-21] Null/empty check returns empty DataFrame

BR-4: Output uses Overwrite mode — all data is replaced on each run.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/credit_score_snapshot.json:28] `"writeMode": "Overwrite"`

## Output Schema

| Column | Source | Transformation |
|--------|--------|----------------|
| credit_score_id | credit_scores.credit_score_id | Direct |
| customer_id | credit_scores.customer_id | Direct |
| bureau | credit_scores.bureau | Direct |
| score | credit_scores.score | Direct |
| as_of | credit_scores.as_of | Direct (from DataSourcing effective date) |

## Edge Cases

- **No credit scores for effective date**: Empty output (BR-3). Credit scores have no weekend data (no as_of for Oct 5-6 in datalake.credit_scores), so weekend runs produce empty output.
- **Data is unchanged from source**: This is a pure pass-through; no business logic applied.

## Anti-Patterns Identified

- **AP-1: Redundant Data Sourcing** — The `branches` table is sourced via DataSourcing but never referenced by the External module `CreditScoreProcessor`. The module only uses `credit_scores` from shared state. Evidence: [JobExecutor/Jobs/credit_score_snapshot.json:14-19] branches sourced with branch_id, branch_name, city, state_province; [ExternalModules/CreditScoreProcessor.cs] no reference to "branches". V2 approach: Remove the branches DataSourcing module entirely.

- **AP-3: Unnecessary External Module** — The External module is a pure pass-through: it copies every row without any transformation, filtering, or business logic. This can be trivially replaced by a SQL Transformation (`SELECT credit_score_id, customer_id, bureau, score, as_of FROM credit_scores`). Evidence: [ExternalModules/CreditScoreProcessor.cs:25-35] No filtering, aggregation, or computation — just row-by-row copy. V2 approach: Replace with a simple SQL Transformation.

- **AP-4: Unused Columns Sourced** — From the branches DataSourcing: branch_id, branch_name, city, state_province are all unused. From the credit_scores DataSourcing: all sourced columns are used. V2 approach: Remove branches entirely (covered by AP-1).

- **AP-6: Row-by-Row Iteration in External Module** — The External module iterates over rows one by one to copy them. A SELECT statement achieves the same result. Evidence: [ExternalModules/CreditScoreProcessor.cs:25-35] foreach loop over all rows. V2 approach: Replace with SQL SELECT.

- **AP-9: Misleading Job/Table Names** — The name "CreditScoreSnapshot" suggests a point-in-time snapshot with possible deduplication or selection logic, but the job is actually a verbatim copy. The name is slightly misleading since "snapshot" implies some curation. V2 approach: Document that the job is a pass-through; keep the name for compatibility.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [ExternalModules/CreditScoreProcessor.cs:25-35], [curated.credit_score_snapshot row counts] |
| BR-2 | [ExternalModules/CreditScoreProcessor.cs:10-13,27-34] |
| BR-3 | [ExternalModules/CreditScoreProcessor.cs:17-21] |
| BR-4 | [JobExecutor/Jobs/credit_score_snapshot.json:28] |

## Open Questions

None. This job is a straightforward pass-through with no ambiguity.
