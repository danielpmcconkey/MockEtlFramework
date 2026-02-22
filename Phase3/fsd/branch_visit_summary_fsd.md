# FSD: BranchVisitSummaryV2

## Overview
Replaces the original BranchVisitSummary job with a V2 implementation that writes to `double_secret_curated` instead of `curated`. The original uses a Transformation (SQL) step for counting visits per branch, so the V2 retains the exact same SQL and replaces only the DataFrameWriter with a V2 External writer module.

## Design Decisions
- **Pattern B (Transformation writer)**: The original uses DataSourcing -> Transformation (SQL) -> DataFrameWriter. The V2 retains the same DataSourcing and Transformation steps, replacing DataFrameWriter with a V2 External writer.
- The SQL is preserved exactly as-is, including the CTE grouping by (branch_id, as_of), INNER JOIN on both branch_id and as_of, and ORDER BY.
- Write mode is Append (matching original), so DscWriterUtil.Write is called with overwrite=false.
- The V2 writer reads the "visit_summary" result from shared state (matching the Transformation step's resultName).

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=branch_visits, columns=[visit_id, customer_id, branch_id, visit_purpose], resultName=branch_visits |
| 2 | DataSourcing | schema=datalake, table=branches, columns=[branch_id, branch_name], resultName=branches |
| 3 | Transformation | resultName=visit_summary, SQL with CTE grouping by (branch_id, as_of), INNER JOIN to branches |
| 4 | External | BranchVisitSummaryV2Writer -- reads "visit_summary" from shared state, writes to dsc |

## V2 External Module: BranchVisitSummaryV2Writer
- File: ExternalModules/BranchVisitSummaryV2Writer.cs
- Processing logic: Reads "visit_summary" DataFrame from shared state (produced by Transformation step); writes to double_secret_curated via DscWriterUtil
- Output columns: branch_id, branch_name, as_of, visit_count
- Target table: double_secret_curated.branch_visit_summary
- Write mode: Append (overwrite=false)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | Same SQL CTE GROUP BY bv.branch_id, bv.as_of with COUNT(*) |
| BR-2 | Same SQL INNER JOIN branches ON branch_id AND as_of |
| BR-3 | Same SQL INNER JOIN semantics -- only matching branches included |
| BR-4 | DscWriterUtil.Write called with overwrite=false (Append) |
| BR-5 | Same SQL ORDER BY vc.as_of, vc.branch_id |
| BR-6 | Framework sources all calendar days; both tables have weekend data |
| BR-7 | Dependency on BranchDirectory handled in Phase C |
| BR-8 | SQL GROUP BY on branch_visits drives rows; INNER JOIN limits to existing branches |
| BR-9 | Simpler aggregation than BranchVisitPurposeBreakdown -- no purpose dimension |
