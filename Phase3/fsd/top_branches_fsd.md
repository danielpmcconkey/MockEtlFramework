# FSD: TopBranchesV2

## Overview
Replaces the original TopBranches job with a V2 implementation that writes to the `double_secret_curated` schema instead of `curated`. The V2 uses the same Transformation SQL as the original but replaces the DataFrameWriter with a V2Writer External module that writes to `double_secret_curated.top_branches` via DscWriterUtil.

## Design Decisions
- **Pattern B (Transformation + V2Writer)**: The original job uses DataSourcing (x2) -> Transformation -> DataFrameWriter. The V2 retains the same DataSourcing and Transformation steps, and replaces DataFrameWriter with a thin External writer module.
- The Transformation SQL is kept exactly as-is (same CTE with GROUP BY, same RANK() window function, same JOIN to branches, same ORDER BY).
- Write mode is Overwrite (matching original), so DscWriterUtil.Write is called with overwrite=true.
- **SameDay dependency on BranchVisitSummary**: The V2 job inherits this dependency. The V2 must be registered with the same dependency to ensure correct execution order.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=branch_visits, columns=[visit_id, branch_id], resultName=branch_visits |
| 2 | DataSourcing | schema=datalake, table=branches, columns=[branch_id, branch_name], resultName=branches |
| 3 | Transformation | SQL: CTE counts visits by branch_id, JOIN to branches for name and as_of, RANK() by visits DESC; resultName=top_branches |
| 4 | External | TopBranchesV2Writer -- reads top_branches DataFrame, writes to dsc |

## V2 External Module: TopBranchesV2Writer
- File: ExternalModules/TopBranchesV2Writer.cs
- Processing logic: Reads "top_branches" DataFrame from shared state (result of Transformation). Writes to double_secret_curated via DscWriterUtil with overwrite=true (Overwrite). Puts result in sharedState["output"].
- Output columns: branch_id, branch_name, total_visits, rank, as_of
- Target table: double_secret_curated.top_branches
- Write mode: Overwrite (overwrite=true)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | Transformation SQL CTE counts visits by branch_id |
| BR-2 | WHERE bv.as_of >= '2024-10-01' retained (redundant but preserved) |
| BR-3 | RANK() OVER (ORDER BY total_visits DESC) in SQL |
| BR-4 | INNER JOIN between visit_totals and branches -- only visited branches |
| BR-5 | b.as_of provides as_of column in output |
| BR-6 | ORDER BY rank, vt.branch_id preserved in SQL |
| BR-7 | DscWriterUtil.Write with overwrite=true |
| BR-8 | SameDay dependency on BranchVisitSummary must be registered |
| BR-9 | Both branch_visits and branches have data for all 31 days |
| BR-10 | Single-day DataSourcing ensures 1:1 join |
