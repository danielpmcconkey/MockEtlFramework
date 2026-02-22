# FSD: BranchDirectoryV2

## Overview
Replaces the original BranchDirectory job with a V2 implementation that writes to `double_secret_curated` instead of `curated`. The original uses a Transformation (SQL) step for deduplication via ROW_NUMBER, so the V2 retains the exact same SQL and replaces only the DataFrameWriter with a V2 External writer module.

## Design Decisions
- **Pattern B (Transformation writer)**: The original uses DataSourcing -> Transformation (SQL) -> DataFrameWriter. The V2 retains the same DataSourcing and Transformation steps, replacing DataFrameWriter with a V2 External writer that reads the transformation result and writes to double_secret_curated.
- The SQL is preserved exactly as-is, including the ROW_NUMBER deduplication and ORDER BY branch_id.
- Write mode is Overwrite (matching original), so DscWriterUtil.Write is called with overwrite=true.
- The V2 writer reads the "branch_dir" result from shared state (matching the Transformation step's resultName).

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=branches, columns=[branch_id, branch_name, address_line1, city, state_province, postal_code, country], resultName=branches |
| 2 | Transformation | resultName=branch_dir, SQL with ROW_NUMBER deduplication by branch_id |
| 3 | External | BranchDirectoryV2Writer -- reads "branch_dir" from shared state, writes to dsc |

## V2 External Module: BranchDirectoryV2Writer
- File: ExternalModules/BranchDirectoryV2Writer.cs
- Processing logic: Reads "branch_dir" DataFrame from shared state (produced by Transformation step); writes to double_secret_curated via DscWriterUtil
- Output columns: branch_id, branch_name, address_line1, city, state_province, postal_code, country, as_of
- Target table: double_secret_curated.branch_directory
- Write mode: Overwrite (overwrite=true)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | Same SQL with ROW_NUMBER PARTITION BY branch_id ORDER BY branch_id, WHERE rn = 1 |
| BR-2 | SQL preserved identically; deduplication logic unchanged |
| BR-3 | Same SELECT clause: branch_id, branch_name, address_line1, city, state_province, postal_code, country, as_of |
| BR-4 | DscWriterUtil.Write called with overwrite=true (Overwrite) |
| BR-5 | Same SQL ORDER BY branch_id |
| BR-6 | Framework sources all calendar days; no weekend filtering |
| BR-7 | Dependency tracked in control.job_dependencies (handled in Phase C) |
| BR-8 | No additional WHERE filters in SQL |
