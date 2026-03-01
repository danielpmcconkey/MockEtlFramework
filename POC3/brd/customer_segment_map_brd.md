# CustomerSegmentMap -- Business Requirements Document

## Overview
Produces a mapping of customers to their assigned segments, enriching the customer-segment association with segment name and code. Uses a SQL Transformation (not an External module) to join the customers_segments table with the segments reference table. Output is a CSV in Append mode, accumulating segment assignments over time.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `seg_map`
- **outputFile**: `Output/curated/customer_segment_map.csv`
- **includeHeader**: true
- **writeMode**: Append
- **lineEnding**: LF
- **trailerFormat**: (not configured -- no trailer)

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.customers_segments | customer_id, segment_id | Effective date range (injected by executor) | [customer_segment_map.json:8-10] |
| datalake.segments | segment_id, segment_name, segment_code | Effective date range (injected by executor) | [customer_segment_map.json:14-17] |
| datalake.branches | branch_id, branch_name, city, state_province | Effective date range (injected by executor) | [customer_segment_map.json:20-23] |

## Business Rules

BR-1: Customer-segment associations are joined with the segments reference table on both segment_id and as_of date. This ensures the segment name/code is from the same effective date as the association.
- Confidence: HIGH
- Evidence: [customer_segment_map.json:29] -- SQL JOIN: `JOIN segments s ON cs.segment_id = s.segment_id AND cs.as_of = s.as_of`.

BR-2: Only customers with segment associations that have a matching segment record (for the same as_of date) appear in the output. This is an INNER JOIN.
- Confidence: HIGH
- Evidence: [customer_segment_map.json:29] -- `JOIN` (not LEFT JOIN).

BR-3: Output is ordered by customer_id ascending, then segment_id ascending.
- Confidence: HIGH
- Evidence: [customer_segment_map.json:29] -- `ORDER BY cs.customer_id, cs.segment_id`.

BR-4: The as_of column from the customers_segments table is included in the output.
- Confidence: HIGH
- Evidence: [customer_segment_map.json:29] -- SQL SELECT includes `cs.as_of`.

BR-5: The branches table is sourced but NOT referenced in the SQL transformation.
- Confidence: HIGH
- Evidence: [customer_segment_map.json:20-23,29] -- branches loaded as "branches" but SQL only references `customers_segments cs` and `segments s`.

BR-6: The writer reads from "seg_map" (the Transformation resultName), not "output".
- Confidence: HIGH
- Evidence: [customer_segment_map.json:33] -- `"source": "seg_map"`.

BR-7: No External module is used. This job uses only DataSourcing, Transformation, and CsvFileWriter.
- Confidence: HIGH
- Evidence: [customer_segment_map.json] -- module list contains only DataSourcing, Transformation, CsvFileWriter.

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | customers_segments.customer_id | Pass-through | [customer_segment_map.json:29] |
| segment_id | customers_segments.segment_id | Pass-through | [customer_segment_map.json:29] |
| segment_name | segments.segment_name | Joined on segment_id + as_of | [customer_segment_map.json:29] |
| segment_code | segments.segment_code | Joined on segment_id + as_of | [customer_segment_map.json:29] |
| as_of | customers_segments.as_of | Pass-through | [customer_segment_map.json:29] |

## Non-Deterministic Fields
None identified.

## Write Mode Implications
- **Append**: Each effective date's segment mappings are appended to the existing CSV file. Over multi-day auto-advance runs, the output accumulates all daily segment assignments. The header is written only once (on first creation); subsequent appends add data rows only. Duplicate segment assignments across dates will result in multiple rows per customer-segment pair with different as_of values.

## Edge Cases
- **Customer with no segments**: Excluded from output (INNER JOIN on customers_segments).
- **Segment not in segments table for that as_of**: Excluded from output (INNER JOIN requires matching segment for same date).
- **Branches table unused**: Loaded but never referenced in SQL.
- **Append mode with header**: On first write, header is included. Subsequent appends should not re-add the header (CsvFileWriter behavior for Append mode).
- **A customer can have multiple segments**: Each customer-segment pair produces a separate row.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| INNER JOIN on segment_id + as_of | [customer_segment_map.json:29] |
| ORDER BY customer_id, segment_id | [customer_segment_map.json:29] |
| as_of included in output | [customer_segment_map.json:29] |
| Branches sourced but unused | [customer_segment_map.json:20-23] |
| Writer reads from seg_map | [customer_segment_map.json:33] |
| Append write mode | [customer_segment_map.json:36] |
| LF line endings | [customer_segment_map.json:37] |

## Open Questions
- OQ-1: The branches table is sourced but unused. Whether this is intentional or dead configuration is unclear. Confidence: MEDIUM.
- OQ-2: The join requires matching as_of dates. If the segments table has gaps in dates that customers_segments does not, some valid associations would be silently dropped. Whether this is by design is unclear. Confidence: LOW.
