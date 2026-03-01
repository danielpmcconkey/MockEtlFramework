# ComplianceResolutionTime — Business Requirements Document

## Overview
Computes resolution time statistics for cleared compliance events, showing the count of resolved events, total days to resolve, and average resolution days grouped by event type. Used for compliance SLA tracking and operational efficiency measurement.

## Output Type
CsvFileWriter

## Writer Configuration
- **outputFile**: `Output/curated/compliance_resolution_time.csv`
- **includeHeader**: true
- **trailerFormat**: `TRAILER|{row_count}|{date}`
- **writeMode**: Overwrite
- **lineEnding**: LF

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.compliance_events | event_id, customer_id, event_type, event_date, status, review_date | Effective date range via executor; SQL filters to status = 'Cleared' AND review_date IS NOT NULL | [compliance_resolution_time.json:4-11] |

### Source Table Schema (from database)

**compliance_events**: event_id (integer), customer_id (integer), event_type (varchar), event_date (date), status (varchar), review_date (date), as_of (date)

## Business Rules

BR-1: Only events with status = 'Cleared' AND review_date IS NOT NULL are included in resolution calculations.
- Confidence: HIGH
- Evidence: [compliance_resolution_time.json:15] — SQL WHERE clause: `status = 'Cleared' AND review_date IS NOT NULL`

BR-2: Days to resolve is computed as `julianday(review_date) - julianday(event_date)`, cast to INTEGER (truncated, not rounded).
- Confidence: HIGH
- Evidence: [compliance_resolution_time.json:15] — `CAST(julianday(review_date) - julianday(event_date) AS INTEGER) AS days_to_resolve`

BR-3: Average resolution days is computed using integer division: `CAST(SUM(days_to_resolve) AS INTEGER) / CAST(COUNT(*) AS INTEGER)`. This truncates rather than rounding.
- Confidence: HIGH
- Evidence: [compliance_resolution_time.json:15] — explicit CAST to INTEGER on both operands produces integer division

BR-4: Results are grouped by event_type and as_of date.
- Confidence: HIGH
- Evidence: [compliance_resolution_time.json:15] — `GROUP BY resolved.event_type, compliance_events.as_of`

BR-5: The SQL uses a cross join (`JOIN compliance_events ON 1=1`) between the resolved CTE and the full compliance_events table to obtain the as_of column. This will produce a Cartesian product, inflating the counts.
- Confidence: HIGH
- Evidence: [compliance_resolution_time.json:15] — `FROM resolved JOIN compliance_events ON 1=1 GROUP BY resolved.event_type, compliance_events.as_of`

BR-6: Due to the cross join (BR-5), the `resolved_count` and `total_days` values will be multiplied by the number of distinct as_of dates in the compliance_events DataFrame. The `avg_resolution_days` integer division may still produce correct values if inflation is uniform, but the count values will be inflated.
- Confidence: HIGH
- Evidence: [compliance_resolution_time.json:15] — `COUNT(*)` and `SUM(days_to_resolve)` operate on the Cartesian product; mathematical analysis: if N resolved events and M as_of dates, COUNT = N*M per group, SUM = sum*M per group, avg = (sum*M)/(N*M) = sum/N (correct). So avg is correct, but counts are inflated by factor M.

BR-7: A ROW_NUMBER() window function is applied within the CTE (`PARTITION BY event_type ORDER BY event_date`) but the result column `rn` is never used in the outer query — it has no effect on the output.
- Confidence: HIGH
- Evidence: [compliance_resolution_time.json:15] — `ROW_NUMBER() OVER (PARTITION BY event_type ORDER BY event_date) AS rn` is computed but not referenced in the SELECT or WHERE of the outer query

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| event_type | compliance_events.event_type | Grouped key | [compliance_resolution_time.json:15] |
| resolved_count | Computed | COUNT(*) of resolved events (inflated by cross join) | [compliance_resolution_time.json:15] |
| total_days | Computed | SUM(days_to_resolve) (inflated by cross join) | [compliance_resolution_time.json:15] |
| avg_resolution_days | Computed | Integer division of total_days / resolved_count | [compliance_resolution_time.json:15] |
| as_of | compliance_events.as_of | Grouped key from cross join | [compliance_resolution_time.json:15] |

## Non-Deterministic Fields
None identified. SQL output order depends on SQLite's GROUP BY behavior, which is deterministic for the same input data.

## Write Mode Implications
- **Overwrite** mode: each run replaces the entire output file. Multi-day runs will only retain the last effective date's output.
- Evidence: [compliance_resolution_time.json:24]

## Edge Cases

1. **No cleared events**: If no events have status = 'Cleared' or all cleared events have NULL review_date, the resolved CTE returns zero rows, producing an empty output.
   - Confidence: HIGH
   - Evidence: [compliance_resolution_time.json:15] — WHERE clause

2. **Cross join inflation**: The `JOIN compliance_events ON 1=1` creates a Cartesian product between resolved events and ALL compliance_events rows. For a single as_of date with 115 compliance events, each resolved event is duplicated 115 times. This inflates `resolved_count` and `total_days` but the integer division for `avg_resolution_days` cancels out the inflation factor.
   - Confidence: HIGH
   - Evidence: [compliance_resolution_time.json:15]; [DB query: compliance_events has 115 rows per as_of date]

3. **Integer truncation**: Both `days_to_resolve` and `avg_resolution_days` use INTEGER casting, which truncates decimal portions rather than rounding.
   - Confidence: HIGH
   - Evidence: [compliance_resolution_time.json:15]

4. **Negative resolution time**: If `review_date < event_date`, `days_to_resolve` would be negative. No guard against this exists.
   - Confidence: MEDIUM
   - Evidence: [compliance_resolution_time.json:15] — no check for review_date >= event_date

5. **Multiple as_of dates in DataFrame**: If DataSourcing returns multiple as_of dates, the cross join produces output rows per (event_type, as_of) combination, with all event_type's resolved events counted against each as_of date.
   - Confidence: HIGH
   - Evidence: [compliance_resolution_time.json:15] — GROUP BY includes `compliance_events.as_of`

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Cleared + non-null review_date filter | [compliance_resolution_time.json:15] |
| BR-2: julianday-based days_to_resolve | [compliance_resolution_time.json:15] |
| BR-3: Integer division for avg | [compliance_resolution_time.json:15] |
| BR-4: Group by event_type, as_of | [compliance_resolution_time.json:15] |
| BR-5: Cross join (1=1) | [compliance_resolution_time.json:15] |
| BR-6: Inflation analysis | [compliance_resolution_time.json:15], [DB query] |
| BR-7: Unused ROW_NUMBER | [compliance_resolution_time.json:15] |

## Open Questions
1. The cross join on `1=1` inflates resolved_count and total_days. Is this intentional (e.g., to normalize per-date) or a bug?
   - Confidence: MEDIUM — the avg_resolution_days is mathematically correct despite the inflation, suggesting this may be unintentional
2. The ROW_NUMBER() computation serves no purpose in the current query. Was it intended for a filter (e.g., latest event per type) that was removed?
   - Confidence: LOW
