# FSD: CustomerSegmentMapV2

## Overview
Replaces the original CustomerSegmentMap job with a V2 implementation that writes to the `double_secret_curated` schema instead of `curated`. Since the original uses a Transformation (SQL) step, the V2 retains the same SQL and adds a V2 External writer module that reads the transformation result and writes it to `double_secret_curated.customer_segment_map` via DscWriterUtil.

## Design Decisions
- **Pattern B (Transformation + V2 Writer)**: The original job uses DataSourcing x3 -> Transformation -> DataFrameWriter. The V2 retains the same DataSourcing and Transformation steps, then replaces DataFrameWriter with a V2 External writer module.
- The V2 writer reads the transformation result DataFrame from shared state (key "seg_map") and writes it to double_secret_curated.
- Write mode is Append (matching original), so DscWriterUtil.Write is called with overwrite=false.
- The branches DataSourcing step is retained (matching original) even though it is unused by the SQL.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=customers_segments, columns=[customer_id, segment_id], resultName=customers_segments |
| 2 | DataSourcing | schema=datalake, table=segments, columns=[segment_id, segment_name, segment_code], resultName=segments |
| 3 | DataSourcing | schema=datalake, table=branches, columns=[branch_id, branch_name, city, state_province], resultName=branches (unused but retained) |
| 4 | Transformation | Same SQL as original: INNER JOIN on segment_id + as_of, ORDER BY customer_id, segment_id; resultName=seg_map |
| 5 | External | CustomerSegmentMapV2Writer -- reads seg_map, writes to dsc |

## V2 External Module: CustomerSegmentMapV2Writer
- File: ExternalModules/CustomerSegmentMapV2Writer.cs
- Processing logic: Reads the "seg_map" DataFrame from shared state (result of the Transformation step), writes it to double_secret_curated via DscWriterUtil with overwrite=false (Append mode), then puts it in sharedState["output"].
- Output columns: customer_id, segment_id, segment_name, segment_code, as_of (as produced by the Transformation SQL)
- Target table: double_secret_curated.customer_segment_map
- Write mode: Append (overwrite=false)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | Same Transformation SQL with INNER JOIN on segment_id AND as_of |
| BR-2 | Same JOIN keyword (inner join semantics) |
| BR-3 | Same SELECT columns: customer_id, segment_id, segment_name, segment_code, as_of |
| BR-4 | Same ORDER BY customer_id, segment_id |
| BR-5 | DscWriterUtil.Write called with overwrite=false (Append) |
| BR-6 | branches DataSourcing retained in config but unused |
| BR-7 | Same Transformation module type (pure SQL, no External processing) |
| BR-8 | Consistent row counts ensured by identical SQL |
| BR-9 | Same date-alignment JOIN condition (cs.as_of = s.as_of) |
