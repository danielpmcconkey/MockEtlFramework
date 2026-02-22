# FSD: BranchVisitPurposeBreakdownV2

## Overview
Replaces the original BranchVisitPurposeBreakdown job with a V2 implementation that writes to `double_secret_curated` instead of `curated`. The original uses a Transformation (SQL) step for grouping and joining, so the V2 retains the exact same SQL and replaces only the DataFrameWriter with a V2 External writer module.

## Design Decisions
- **Pattern B (Transformation writer)**: The original uses DataSourcing -> Transformation (SQL) -> DataFrameWriter. The V2 retains the same DataSourcing and Transformation steps, replacing DataFrameWriter with a V2 External writer.
- The SQL is preserved exactly as-is, including the CTE with window function (total_branch_visits computed but not output), INNER JOIN on both branch_id and as_of, and ORDER BY.
- Write mode is Append (matching original), so DscWriterUtil.Write is called with overwrite=false.
- The segments DataSourcing step is retained in the config (matching the original) even though it is unused by the SQL.
- The V2 writer reads the "purpose_breakdown" result from shared state (matching the Transformation step's resultName).

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=branch_visits, columns=[visit_id, customer_id, branch_id, visit_purpose], resultName=branch_visits |
| 2 | DataSourcing | schema=datalake, table=branches, columns=[branch_id, branch_name], resultName=branches |
| 3 | DataSourcing | schema=datalake, table=segments, columns=[segment_id, segment_name], resultName=segments (unused but retained) |
| 4 | Transformation | resultName=purpose_breakdown, SQL with CTE grouping by (branch_id, visit_purpose, as_of), INNER JOIN to branches |
| 5 | External | BranchVisitPurposeBreakdownV2Writer -- reads "purpose_breakdown" from shared state, writes to dsc |

## V2 External Module: BranchVisitPurposeBreakdownV2Writer
- File: ExternalModules/BranchVisitPurposeBreakdownV2Writer.cs
- Processing logic: Reads "purpose_breakdown" DataFrame from shared state (produced by Transformation step); writes to double_secret_curated via DscWriterUtil
- Output columns: branch_id, branch_name, visit_purpose, as_of, visit_count
- Target table: double_secret_curated.branch_visit_purpose_breakdown
- Write mode: Append (overwrite=false)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | Same SQL GROUP BY bv.branch_id, bv.visit_purpose, bv.as_of with COUNT(*) |
| BR-2 | Same SQL CTE computes total_branch_visits but final SELECT excludes it |
| BR-3 | Same SQL INNER JOIN branches ON branch_id AND as_of |
| BR-4 | Same SQL INNER JOIN semantics -- only matching branches included |
| BR-5 | DscWriterUtil.Write called with overwrite=false (Append) |
| BR-6 | Same SQL ORDER BY pc.as_of, pc.branch_id, pc.visit_purpose |
| BR-7 | segments DataSourcing retained in config but not referenced by SQL |
| BR-8 | Framework sources all calendar days; branch_visits and branches have weekend data |
| BR-9 | Dependency on BranchDirectory handled in Phase C |
| BR-10 | SQL GROUP BY on branch_visits drives rows; INNER JOIN limits to existing branches |
