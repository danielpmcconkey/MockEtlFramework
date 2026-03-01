# BranchVisitPurposeBreakdown — Business Requirements Document

## Overview
Produces a per-branch, per-visit-purpose, per-date breakdown of visit counts, showing how many visits each branch received for each purpose (Account Opening, Deposit, Inquiry, Loan Application, Withdrawal).

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `purpose_breakdown`
- **outputFile**: `Output/curated/branch_visit_purpose_breakdown.csv`
- **includeHeader**: true
- **trailerFormat**: `END|{row_count}`
- **writeMode**: Append
- **lineEnding**: CRLF

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.branch_visits | visit_id, customer_id, branch_id, visit_purpose | Effective date range (injected by executor) | [branch_visit_purpose_breakdown.json:8-10] |
| datalake.branches | branch_id, branch_name | Effective date range (injected by executor) | [branch_visit_purpose_breakdown.json:14-16] |
| datalake.segments | segment_id, segment_name | Effective date range (injected by executor) | [branch_visit_purpose_breakdown.json:20-22] |

### Schema Details

**branch_visits**: visit_id (integer), customer_id (integer), branch_id (integer), visit_timestamp (timestamp), visit_purpose (varchar), as_of (date)

**branches**: branch_id (integer), branch_name (varchar), address_line1 (varchar), city (varchar), state_province (varchar), postal_code (varchar), country (char), as_of (date)

**segments**: segment_id (integer), segment_name (varchar), segment_code (varchar), as_of (date)

## Business Rules

BR-1: Visit counts are grouped by branch_id, visit_purpose, and as_of — one row per branch-purpose-date combination.
- Confidence: HIGH
- Evidence: [branch_visit_purpose_breakdown.json:29] `GROUP BY bv.branch_id, bv.visit_purpose, bv.as_of`

BR-2: A window function computes `total_branch_visits` (total visits per branch per date) via `SUM(COUNT(*)) OVER (PARTITION BY bv.branch_id, bv.as_of)`, but this column is NOT included in the outer SELECT.
- Confidence: HIGH
- Evidence: [branch_visit_purpose_breakdown.json:29] CTE computes total_branch_visits; outer SELECT only picks branch_id, branch_name, visit_purpose, as_of, visit_count

BR-3: Branches are joined on both branch_id AND as_of to ensure date-aligned snapshots.
- Confidence: HIGH
- Evidence: [branch_visit_purpose_breakdown.json:29] `JOIN branches b ON pc.branch_id = b.branch_id AND pc.as_of = b.as_of`

BR-4: Output is ordered by as_of, then branch_id, then visit_purpose.
- Confidence: HIGH
- Evidence: [branch_visit_purpose_breakdown.json:29] `ORDER BY pc.as_of, pc.branch_id, pc.visit_purpose`

BR-5: The `segments` table is sourced but never referenced in the transformation SQL.
- Confidence: HIGH
- Evidence: [branch_visit_purpose_breakdown.json:20-22] DataSourcing; [branch_visit_purpose_breakdown.json:29] SQL does not mention segments

BR-6: The `customer_id` column is sourced from branch_visits but not used in the transformation.
- Confidence: HIGH
- Evidence: [branch_visit_purpose_breakdown.json:10] sourced; [branch_visit_purpose_breakdown.json:29] not in SQL

BR-7: The `visit_id` column is sourced from branch_visits but not used in the transformation (COUNT(*) is used, not COUNT(visit_id)).
- Confidence: HIGH
- Evidence: [branch_visit_purpose_breakdown.json:10] sourced; [branch_visit_purpose_breakdown.json:29] COUNT(*) used

BR-8: The trailer line format is `END|{row_count}` — the {row_count} token is replaced with the number of data rows.
- Confidence: HIGH
- Evidence: [branch_visit_purpose_breakdown.json:36] `"trailerFormat": "END|{row_count}"`

BR-9: Branches with no visits for a given purpose on a given date will not appear (inner join, no outer join or purpose enumeration).
- Confidence: HIGH
- Evidence: [branch_visit_purpose_breakdown.json:29] GROUP BY only produces rows where visits exist

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| branch_id | branch_visits.branch_id | Direct, grouped | [branch_visit_purpose_breakdown.json:29] |
| branch_name | branches.branch_name | Lookup via date-aligned join | [branch_visit_purpose_breakdown.json:29] |
| visit_purpose | branch_visits.visit_purpose | Direct, grouped | [branch_visit_purpose_breakdown.json:29] |
| as_of | branch_visits.as_of | Direct, grouped | [branch_visit_purpose_breakdown.json:29] |
| visit_count | branch_visits | COUNT(*) per group | [branch_visit_purpose_breakdown.json:29] |

## Non-Deterministic Fields
None identified.

## Write Mode Implications
**Append mode**: Each execution appends data to `Output/curated/branch_visit_purpose_breakdown.csv`. Each new effective date adds rows. Re-running the same date produces duplicate rows. The trailer is appended after each run's data, so the file may contain multiple trailer lines interspersed with data from different runs.

## Edge Cases

- **No visits for a branch on a date**: That branch will not appear in the output for that date.
- **No visits at all for a date**: Zero data rows; trailer line `END|0` would be appended.
- **Multiple trailers**: Append mode means each execution adds a trailer. Multiple runs produce multiple `END|N` lines throughout the file.
- **Branch not in branches table**: If branch_visits references a branch_id that doesn't exist in branches for that as_of, the inner join drops those visits.
- **Five known visit purposes**: Account Opening, Deposit, Inquiry, Loan Application, Withdrawal (per DB observation).
- **CRLF line endings**: Windows-style line endings.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Group by branch+purpose+date | [branch_visit_purpose_breakdown.json:29] |
| BR-2: total_branch_visits computed but not output | [branch_visit_purpose_breakdown.json:29] |
| BR-3: Date-aligned branch join | [branch_visit_purpose_breakdown.json:29] |
| BR-4: ORDER BY as_of, branch_id, purpose | [branch_visit_purpose_breakdown.json:29] |
| BR-5: segments unused | [branch_visit_purpose_breakdown.json:20-22, 29] |
| BR-6: customer_id unused | [branch_visit_purpose_breakdown.json:10, 29] |
| BR-7: visit_id unused | [branch_visit_purpose_breakdown.json:10, 29] |
| BR-8: Trailer format | [branch_visit_purpose_breakdown.json:36] |
| BR-9: No outer join | [branch_visit_purpose_breakdown.json:29] |

## Open Questions

OQ-1: Why is `total_branch_visits` computed in the CTE but excluded from the output SELECT? Possible future use for percentage calculation or oversight.
- Confidence: MEDIUM — clearly computed but not output

OQ-2: Why is the `segments` table sourced but never used?
- Confidence: MEDIUM — vestigial data source
