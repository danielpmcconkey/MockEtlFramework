# FSD: CreditScoreAverageV2

## Overview
CreditScoreAverageV2 replicates the exact business logic of CreditScoreAverage, computing per-customer average credit scores with individual bureau breakdowns. The V2 uses the same DataSourcing steps and an External module that replicates the original CreditScoreAverager logic, adding DscWriterUtil.Write() to write to `double_secret_curated.credit_score_average`.

## Design Decisions
- **Pattern A (External module)**: The original uses DataSourcing + External + DataFrameWriter. The V2 keeps DataSourcing steps identical and replaces the External+DataFrameWriter with a single V2 External that includes writing.
- **Write mode**: Overwrite (overwrite=true) to match original's Overwrite write mode.
- **Segments DataSourcing retained**: The original sources segments but never uses them. The V2 retains this sourcing for behavioral equivalence.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | credit_scores: credit_score_id, customer_id, bureau, score |
| 2 | DataSourcing | customers: id, first_name, last_name |
| 3 | DataSourcing | segments: segment_id, segment_name |
| 4 | External | CreditScoreAverageV2Processor |

## V2 External Module: CreditScoreAverageV2Processor
- File: ExternalModules/CreditScoreAverageV2Processor.cs
- Processing logic: Groups credit scores by customer, computes average, pivots bureau scores into columns, joins with customer names
- Output columns: customer_id, first_name, last_name, avg_score, equifax_score, transunion_score, experian_score, as_of
- Target table: double_secret_curated.credit_score_average
- Write mode: Overwrite (overwrite=true)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 (Average score) | scores.Average(s => s.score) computation |
| BR-2 (Bureau pivot) | Switch on bureau.ToLower() for equifax/transunion/experian columns |
| BR-3 (Case-insensitive) | bureau.ToLower() in switch |
| BR-4 (Inner join) | Skip customers not in customerNames |
| BR-5 (DBNull for missing bureau) | Initialize bureau scores as DBNull.Value |
| BR-6 (as_of from customers) | custRow["as_of"] passed through |
| BR-7 (Overwrite mode) | DscWriterUtil.Write with overwrite=true |
| BR-8 (Empty input guard) | Early return with empty DataFrame |
| BR-9 (Segments unused) | Segments DataSourcing retained but not referenced |
| BR-10 (Duplicate bureau) | Last bureau score overwrites in column; all contribute to average |
