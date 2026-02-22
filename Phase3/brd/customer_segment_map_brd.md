# BRD: CustomerSegmentMap

## Overview
This job produces a mapping of customers to their segments by joining the `customers_segments` association table with the `segments` reference table. It enriches each mapping with segment_name and segment_code, and writes the result to `curated.customer_segment_map` using Append mode, accumulating data across all effective dates.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| customers_segments | datalake | customer_id, segment_id | Left side of JOIN; provides the customer-to-segment mapping | [JobExecutor/Jobs/customer_segment_map.json:7-10] |
| segments | datalake | segment_id, segment_name, segment_code | Right side of JOIN; provides segment details | [JobExecutor/Jobs/customer_segment_map.json:12-16] |
| branches | datalake | branch_id, branch_name, city, state_province | Sourced into shared state but NOT used in the transformation SQL | [JobExecutor/Jobs/customer_segment_map.json:18-22] |

## Business Rules
BR-1: The output is produced by joining customers_segments with segments on segment_id AND as_of date.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_segment_map.json:28] SQL: `JOIN segments s ON cs.segment_id = s.segment_id AND cs.as_of = s.as_of`

BR-2: The join is an INNER JOIN, meaning only customer-segment mappings with a matching segment record (same segment_id AND same as_of) produce output rows.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_segment_map.json:28] `JOIN` (not LEFT JOIN) — inner join semantics

BR-3: The output includes customer_id, segment_id, segment_name, segment_code, and as_of.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_segment_map.json:27] `SELECT cs.customer_id, cs.segment_id, s.segment_name, s.segment_code, cs.as_of`

BR-4: Results are ordered by customer_id, then segment_id.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_segment_map.json:28] `ORDER BY cs.customer_id, cs.segment_id`

BR-5: The output is written using Append mode, accumulating rows across all effective dates.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_segment_map.json:34] `"writeMode": "Append"`
- Evidence: [curated.customer_segment_map] Contains 9021 rows across all 31 dates (291 mappings/day * 31 days = 9021)

BR-6: The branches table is sourced but not used (dead data sourcing).
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_segment_map.json:18-22] branches is sourced as a DataSourcing module
- Evidence: [JobExecutor/Jobs/customer_segment_map.json:27-28] SQL only references `customers_segments` and `segments` tables, not `branches`

BR-7: This is a pure SQL Transformation job — no External module is used.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_segment_map.json] Module pipeline: DataSourcing x3 -> Transformation -> DataFrameWriter (no External module)

BR-8: Each effective date produces the same number of rows (291) since both customers_segments and segments have consistent row counts across dates.
- Confidence: HIGH
- Evidence: [curated.customer_segment_map] `SELECT as_of, COUNT(*) GROUP BY as_of` shows 291 rows per date
- Evidence: [datalake.customers_segments] 291 rows per as_of; [datalake.segments] 8 rows per as_of

BR-9: The join condition includes date alignment (as_of matching), ensuring segment data is temporally consistent.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_segment_map.json:28] `cs.as_of = s.as_of` in the JOIN condition

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | customers_segments.customer_id | Direct mapping | [JobExecutor/Jobs/customer_segment_map.json:27] `cs.customer_id` |
| segment_id | customers_segments.segment_id | Direct mapping | [JobExecutor/Jobs/customer_segment_map.json:27] `cs.segment_id` |
| segment_name | segments.segment_name | Joined from segments table | [JobExecutor/Jobs/customer_segment_map.json:27] `s.segment_name` |
| segment_code | segments.segment_code | Joined from segments table | [JobExecutor/Jobs/customer_segment_map.json:27] `s.segment_code` |
| as_of | customers_segments.as_of | Passed through | [JobExecutor/Jobs/customer_segment_map.json:27] `cs.as_of` |

## Edge Cases
- **Unmatched segment_ids**: If a customer_segment row references a segment_id not present in the segments table (for the same as_of), the INNER JOIN excludes it. No data loss evidence in current data.
- **Date alignment**: The JOIN requires `cs.as_of = s.as_of`. Both tables have data for all 31 days (including weekends), so no date gaps affect the join.
- **Weekend behavior**: Both customers_segments and segments have data every day (31 dates), so the job produces output for every date including weekends.
- **Duplicate mappings**: If a customer has the same segment_id multiple times in customers_segments for the same as_of, duplicate rows would appear in the output. Current data does not show this pattern.
- **Append accumulation**: Since writeMode is Append, running the job multiple times for the same effective date would produce duplicate rows. The framework's gap-fill mechanism prevents this by only running for dates not yet succeeded.
- **Unused branches**: The branches table is loaded into shared state via DataSourcing but never referenced in the Transformation SQL.

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [JobExecutor/Jobs/customer_segment_map.json:28] |
| BR-2 | [JobExecutor/Jobs/customer_segment_map.json:28] |
| BR-3 | [JobExecutor/Jobs/customer_segment_map.json:27] |
| BR-4 | [JobExecutor/Jobs/customer_segment_map.json:28] |
| BR-5 | [JobExecutor/Jobs/customer_segment_map.json:34], [curated.customer_segment_map row counts] |
| BR-6 | [JobExecutor/Jobs/customer_segment_map.json:18-22, 27-28] |
| BR-7 | [JobExecutor/Jobs/customer_segment_map.json] |
| BR-8 | [curated.customer_segment_map row counts], [datalake.customers_segments counts] |
| BR-9 | [JobExecutor/Jobs/customer_segment_map.json:28] |

## Open Questions
- **Unused branches sourcing**: The branches table is loaded but never referenced. This appears to be dead configuration. Confidence: HIGH that it is unused.
- **Append idempotency**: If the job were re-run for an already-processed date, it would insert duplicate rows since Append mode does not check for existing data. The executor's gap-fill mechanism prevents this in normal operation. Confidence: HIGH.
