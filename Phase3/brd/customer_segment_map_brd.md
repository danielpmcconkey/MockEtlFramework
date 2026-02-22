# CustomerSegmentMap — Business Requirements Document

## Overview

The CustomerSegmentMap job joins customer-segment membership data with the segment reference table to produce a mapping of customers to their segments, including segment name and code. The output uses Append mode, accumulating data across all effective dates.

## Source Tables

| Table | Alias in Config | Columns Sourced | Purpose |
|-------|----------------|-----------------|---------|
| `datalake.customers_segments` | customers_segments | customer_id, segment_id | Customer-to-segment membership mapping |
| `datalake.segments` | segments | segment_id, segment_name, segment_code | Segment reference data for name and code lookup |
| `datalake.branches` | branches | branch_id, branch_name, city, state_province | **NOT USED** — sourced but never referenced in the Transformation SQL |

- Join logic: `customers_segments` is inner-joined to `segments` on `segment_id` AND `as_of` (same-date join).
- Evidence: [JobExecutor/Jobs/customer_segment_map.json:29] `JOIN segments s ON cs.segment_id = s.segment_id AND cs.as_of = s.as_of`

## Business Rules

BR-1: Each customer-segment pair produces one output row per effective date, enriched with segment_name and segment_code from the segments reference table.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_segment_map.json:29] SQL joins customers_segments to segments on segment_id and as_of.
- Evidence: [curated.customer_segment_map] 291 rows per as_of, matching datalake.customers_segments count of 291.

BR-2: The join between customers_segments and segments uses an inner join with both segment_id and as_of equality, ensuring only matching-date segment definitions are used.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_segment_map.json:29] `JOIN segments s ON cs.segment_id = s.segment_id AND cs.as_of = s.as_of`

BR-3: Output is ordered by customer_id, then segment_id.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_segment_map.json:29] `ORDER BY cs.customer_id, cs.segment_id`

BR-4: Output uses Append write mode, accumulating rows across all effective dates.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_segment_map.json:35] `"writeMode": "Append"`
- Evidence: [curated.customer_segment_map] Has 291 rows for each of 31 dates (Oct 1-31), including weekends.

BR-5: Weekend dates are included because both source tables (customers_segments and segments) have data for all 7 days of the week.
- Confidence: HIGH
- Evidence: [datalake.customers_segments] and [datalake.segments] both have as_of dates for Oct 5 (Sat) and Oct 6 (Sun).
- Evidence: [curated.customer_segment_map] Has rows for 2024-10-05 and 2024-10-06.

BR-6: Customers whose segment_id does not exist in the segments reference table for that date are excluded (inner join behavior).
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_segment_map.json:29] INNER JOIN filters out non-matching rows.
- Evidence: [datalake.segments] Segment_id 5 ("Student banking") exists in segments table. [datalake.customers_segments] segment_id 5 has 0 customer mappings for 2024-10-01. All mapped segment_ids do exist in segments.

## Output Schema

| Column | Type | Source | Transformation |
|--------|------|--------|---------------|
| customer_id | integer | `customers_segments.customer_id` | Passthrough |
| segment_id | integer | `customers_segments.segment_id` | Passthrough |
| segment_name | varchar(100) | `segments.segment_name` | Joined via segment_id + as_of |
| segment_code | varchar(10) | `segments.segment_code` | Joined via segment_id + as_of |
| as_of | date | `customers_segments.as_of` | Passthrough |

## Edge Cases

- **Weekend dates:** Both source tables have data for all 7 days, so this job produces output for weekends. This contrasts with jobs that source from `customers` or `accounts` tables, which have weekday-only data.
- **Missing segment reference:** If a segment_id in customers_segments has no matching row in the segments table for the same as_of, that row is excluded (inner join). Based on database inspection, all currently-mapped segment_ids exist in segments, so this is a theoretical edge case.
- **Stable row counts:** The customers_segments table has exactly 291 rows for every as_of date inspected, and the output consistently has 291 rows per date. The data appears stable (no membership changes across the month).

## Anti-Patterns Identified

- **AP-1: Redundant Data Sourcing** — The `branches` table is sourced [JobExecutor/Jobs/customer_segment_map.json:22-26] with columns `branch_id, branch_name, city, state_province`, but the Transformation SQL only references `customers_segments` (aliased `cs`) and `segments` (aliased `s`). The branches DataFrame is loaded into shared state and registered as a SQLite table but never queried. V2 approach: Remove the branches DataSourcing module entirely.

- **AP-4: Unused Columns Sourced** — Even for the tables that are used, no columns are unused — all sourced columns from customers_segments and segments appear in the output or join condition. This anti-pattern applies only to the branches table (covered by AP-1).

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [JobExecutor/Jobs/customer_segment_map.json:29], [curated.customer_segment_map] 291 rows/date |
| BR-2 | [JobExecutor/Jobs/customer_segment_map.json:29] |
| BR-3 | [JobExecutor/Jobs/customer_segment_map.json:29] |
| BR-4 | [JobExecutor/Jobs/customer_segment_map.json:35], curated output 31 dates |
| BR-5 | [datalake.customers_segments] and [datalake.segments] weekend data, curated weekend data |
| BR-6 | [JobExecutor/Jobs/customer_segment_map.json:29] INNER JOIN |

## Open Questions

None — this job's logic is straightforward and fully observable in the SQL Transformation. All business rules are HIGH confidence.
