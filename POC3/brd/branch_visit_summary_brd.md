# BranchVisitSummary — Business Requirements Document

## Overview
Produces a per-branch, per-date summary of total visit counts by aggregating branch_visits and joining with branch names.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `visit_summary`
- **outputFile**: `Output/curated/branch_visit_summary.csv`
- **includeHeader**: true
- **trailerFormat**: `TRAILER|{row_count}|{date}`
- **writeMode**: Append
- **lineEnding**: LF

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.branch_visits | visit_id, customer_id, branch_id, visit_purpose | Effective date range (injected by executor) | [branch_visit_summary.json:8-10] |
| datalake.branches | branch_id, branch_name | Effective date range (injected by executor) | [branch_visit_summary.json:14-16] |

### Schema Details

**branch_visits**: visit_id (integer), customer_id (integer), branch_id (integer), visit_timestamp (timestamp), visit_purpose (varchar), as_of (date)

**branches**: branch_id (integer), branch_name (varchar), address_line1 (varchar), city (varchar), state_province (varchar), postal_code (varchar), country (char), as_of (date)

## Business Rules

BR-1: Visits are counted per branch per date using `COUNT(*)` grouped by branch_id and as_of.
- Confidence: HIGH
- Evidence: [branch_visit_summary.json:22] `COUNT(*) AS visit_count ... GROUP BY bv.branch_id, bv.as_of`

BR-2: Branches are LEFT JOINed on both branch_id AND as_of for date-aligned snapshots, ensuring all visit counts are retained even if the branch record is missing.
- Confidence: HIGH
- Evidence: [branch_visit_summary.json:22] `LEFT JOIN branches b ON vc.branch_id = b.branch_id AND vc.as_of = b.as_of`

BR-3: Output is ordered by as_of, then branch_id.
- Confidence: HIGH
- Evidence: [branch_visit_summary.json:22] `ORDER BY vc.as_of, vc.branch_id`

BR-4: The `customer_id` and `visit_purpose` columns are sourced from branch_visits but not used in the transformation SQL.
- Confidence: HIGH
- Evidence: [branch_visit_summary.json:10] sourced; [branch_visit_summary.json:22] not in SQL

BR-5: The `visit_id` column is sourced but not used (COUNT(*) is used).
- Confidence: HIGH
- Evidence: [branch_visit_summary.json:10] sourced; [branch_visit_summary.json:22] COUNT(*) used

BR-6: The trailer format includes both row_count and date tokens. The {date} token is replaced with the `__maxEffectiveDate` from shared state.
- Confidence: HIGH
- Evidence: [branch_visit_summary.json:29] `"trailerFormat": "TRAILER|{row_count}|{date}"`; [Architecture.md:241] trailer token documentation

BR-7: Branches with zero visits on a given date are excluded (GROUP BY only produces rows with visits). Branches whose branch_id exists in visits but not in the branches table will appear with NULL branch_name (LEFT JOIN).
- Confidence: HIGH
- Evidence: [branch_visit_summary.json:22] CTE groups branch_visits, LEFT JOIN with branches

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| branch_id | branch_visits.branch_id | Direct, grouped | [branch_visit_summary.json:22] |
| branch_name | branches.branch_name | Lookup via date-aligned join | [branch_visit_summary.json:22] |
| as_of | branch_visits.as_of | Direct, grouped | [branch_visit_summary.json:22] |
| visit_count | branch_visits | COUNT(*) per branch per date | [branch_visit_summary.json:22] |

## Non-Deterministic Fields
None identified. However, the trailer's `{date}` token reflects `__maxEffectiveDate`, which varies by execution context.

## Write Mode Implications
**Append mode**: Each execution appends data and a trailer line to `Output/curated/branch_visit_summary.csv`. Multiple runs accumulate data. Re-running the same date produces duplicate data rows and multiple trailer lines interspersed in the file.

## Edge Cases

- **No visits on a date**: Zero data rows for that date; trailer `TRAILER|0|{date}` appended.
- **Multiple trailers**: Append mode produces a trailer after each run's data block, resulting in multiple TRAILER lines throughout the file.
- **Branch not in branches table**: Visits referencing a branch_id absent from branches for that as_of will have NULL branch_name (preserved by LEFT JOIN).
- **LF line endings**: Unix-style line endings (contrast with some other branch jobs using CRLF).

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: COUNT per branch per date | [branch_visit_summary.json:22] |
| BR-2: Date-aligned branch join | [branch_visit_summary.json:22] |
| BR-3: ORDER BY as_of, branch_id | [branch_visit_summary.json:22] |
| BR-4: customer_id/visit_purpose unused | [branch_visit_summary.json:10, 22] |
| BR-5: visit_id unused | [branch_visit_summary.json:10, 22] |
| BR-6: Trailer with row_count + date | [branch_visit_summary.json:29], [Architecture.md:241] |
| BR-7: No zero-visit branches | [branch_visit_summary.json:22] |

## Open Questions
None.
