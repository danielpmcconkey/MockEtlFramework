# TopBranches — Business Requirements Document

## Overview
Produces a ranked list of branches by total visit count across all dates since 2024-10-01, using RANK() to assign position. Includes a control trailer with date, row count, and timestamp.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `top_branches`
- **outputFile**: `Output/curated/top_branches.csv`
- **includeHeader**: true
- **trailerFormat**: `CONTROL|{date}|{row_count}|{timestamp}`
- **writeMode**: Overwrite
- **lineEnding**: LF

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.branch_visits | visit_id, branch_id | Effective date range (injected by executor) | [top_branches.json:8-10] |
| datalake.branches | branch_id, branch_name | Effective date range (injected by executor) | [top_branches.json:14-16] |

### Schema Details

**branch_visits**: visit_id (integer), customer_id (integer), branch_id (integer), visit_timestamp (timestamp), visit_purpose (varchar), as_of (date)

**branches**: branch_id (integer), branch_name (varchar), address_line1 (varchar), city (varchar), state_province (varchar), postal_code (varchar), country (char), as_of (date)

## Business Rules

BR-1: The visit count CTE applies a hardcoded date filter `WHERE bv.as_of >= '2024-10-01'`, which counts visits across ALL dates from 2024-10-01 onward that are in the effective range.
- Confidence: HIGH
- Evidence: [top_branches.json:22] `WHERE bv.as_of >= '2024-10-01'`

BR-2: Visit counts are aggregated across ALL dates (no per-date grouping). Each branch gets a single total_visits count.
- Confidence: HIGH
- Evidence: [top_branches.json:22] `GROUP BY bv.branch_id` — no as_of in GROUP BY

BR-3: RANK() window function assigns ranking based on descending total_visits. Branches with equal visit counts receive the same rank (with gaps in subsequent ranks).
- Confidence: HIGH
- Evidence: [top_branches.json:22] `RANK() OVER (ORDER BY vt.total_visits DESC) AS rank`

BR-4: Output is ordered by rank ascending, then branch_id ascending as tie-breaker.
- Confidence: HIGH
- Evidence: [top_branches.json:22] `ORDER BY rank, vt.branch_id`

BR-5: The JOIN to branches is NOT date-aligned — `vt.branch_id = b.branch_id` without an as_of condition. Since branches exist for multiple dates, this creates a cross-join effect: each branch's total visits row is duplicated once per as_of date in the branches DataFrame.
- Confidence: HIGH
- Evidence: [top_branches.json:22] `JOIN branches b ON vt.branch_id = b.branch_id` — no as_of condition; branches has 40 rows per date

BR-6: The `b.as_of` column from branches is included in the output, meaning the same branch appears multiple times with different as_of values (one per date the branch exists).
- Confidence: HIGH
- Evidence: [top_branches.json:22] `SELECT ... b.as_of FROM visit_totals vt JOIN branches b`

BR-7: The trailer includes three tokens: {date} (__maxEffectiveDate), {row_count} (data rows), and {timestamp} (UTC ISO 8601).
- Confidence: HIGH
- Evidence: [top_branches.json:29] `"trailerFormat": "CONTROL|{date}|{row_count}|{timestamp}"`; [Architecture.md:241]

BR-8: The `visit_id` column is sourced from branch_visits but not directly used (COUNT(*) counts all rows).
- Confidence: HIGH
- Evidence: [top_branches.json:10] sourced; [top_branches.json:22] COUNT(*) used

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| branch_id | branch_visits.branch_id / branches.branch_id | Grouped and joined | [top_branches.json:22] |
| branch_name | branches.branch_name | Direct from join | [top_branches.json:22] |
| total_visits | branch_visits | COUNT(*) across all dates | [top_branches.json:22] |
| rank | Computed | RANK() OVER (ORDER BY total_visits DESC) | [top_branches.json:22] |
| as_of | branches.as_of | From branches join (duplicates rows) | [top_branches.json:22] |

## Non-Deterministic Fields

- **{timestamp}** in the trailer line: UTC timestamp at time of writing, varies per execution.

## Write Mode Implications
**Overwrite mode**: Each execution replaces the entire `Output/curated/top_branches.csv` file. Since the query aggregates across all dates, each run produces a complete snapshot of the ranking.

## Edge Cases

- **Branch duplication via join**: Because the branches join lacks an as_of condition, each branch appears N times (once per date in the effective range). If the range spans 10 days and there are 40 branches, the output has 400 rows. The rank and total_visits are identical across duplicates; only as_of differs.
- **Tied ranks**: RANK() produces gaps — if two branches tie at rank 1, the next branch is rank 3.
- **Hardcoded date filter**: The `WHERE bv.as_of >= '2024-10-01'` is hardcoded in the SQL, not parameterized. This means the date floor is independent of the effective date range injected by the executor.
- **Branch with no visits**: Branches that appear in the branches table but have zero visits are excluded (inner join on visit_totals CTE).
- **Trailer timestamp**: The {timestamp} token makes the trailer line non-deterministic across runs.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Hardcoded date filter | [top_branches.json:22] |
| BR-2: Cross-date aggregation | [top_branches.json:22] |
| BR-3: RANK() window function | [top_branches.json:22] |
| BR-4: ORDER BY rank, branch_id | [top_branches.json:22] |
| BR-5: Non-date-aligned join | [top_branches.json:22] |
| BR-6: as_of from branches (duplicates) | [top_branches.json:22] |
| BR-7: Control trailer | [top_branches.json:29], [Architecture.md:241] |
| BR-8: visit_id unused | [top_branches.json:10, 22] |

## Open Questions

OQ-1: Is the branch row duplication (from the non-date-aligned join) intentional? The same branch ranking info is repeated N times with different as_of values from the branches table. This significantly inflates output row count.
- Confidence: HIGH — likely a bug; the join should probably include `AND vt... = b.as_of` or use a deduplicated branch reference

OQ-2: Is the hardcoded `'2024-10-01'` date filter intentional, or should it derive from the effective date range?
- Confidence: MEDIUM — hardcoded date matches firstEffectiveDate but won't adapt if data history changes
