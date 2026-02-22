# FSD: CreditScoreSnapshotV2

## Overview
CreditScoreSnapshotV2 replicates the exact pass-through behavior of CreditScoreSnapshot, copying all credit score records for the effective date to the curated layer. The V2 uses DscWriterUtil to write to `double_secret_curated.credit_score_snapshot`.

## Design Decisions
- **Pattern A (External module)**: Original uses DataSourcing + External + DataFrameWriter. V2 keeps DataSourcing steps identical and replaces External+DataFrameWriter with a single V2 External.
- **Write mode**: Overwrite (overwrite=true) to match original.
- **Branches DataSourcing retained**: The original sources branches but never uses them. V2 retains this for behavioral equivalence.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | credit_scores: credit_score_id, customer_id, bureau, score |
| 2 | DataSourcing | branches: branch_id, branch_name, city, state_province |
| 3 | External | CreditScoreSnapshotV2Processor |

## V2 External Module: CreditScoreSnapshotV2Processor
- File: ExternalModules/CreditScoreSnapshotV2Processor.cs
- Processing logic: Simple pass-through of all credit score rows
- Output columns: credit_score_id, customer_id, bureau, score, as_of
- Target table: double_secret_curated.credit_score_snapshot
- Write mode: Overwrite (overwrite=true)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 (All records pass through) | Loop copies all credit score rows |
| BR-2 (5 output columns) | OutputColumns list matches exactly |
| BR-3 (Overwrite mode) | DscWriterUtil.Write with overwrite=true |
| BR-4 (Empty input guard) | Early return with empty DataFrame |
| BR-5 (Branches unused) | Branches DataSourcing retained but not referenced |
| BR-6 (No transformation) | Direct field copy without modification |
