# BranchVisitPurposeBreakdown — Business Requirements Document

## Overview

This job breaks down branch visits by purpose (Deposit, Withdrawal, Inquiry, Loan Application, Account Opening), counting visits per branch per purpose per effective date. The result is written to `curated.branch_visit_purpose_breakdown` using Append mode.

## Source Tables

### datalake.branch_visits
- **Columns sourced:** visit_id, customer_id, branch_id, visit_purpose
- **Columns actually used:** branch_id (group key + join key), visit_purpose (group key), as_of (group key), visit_id (counted via COUNT(*))
- **Evidence:** [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] SQL references bv.branch_id, bv.visit_purpose, bv.as_of in GROUP BY.

### datalake.branches
- **Columns sourced:** branch_id, branch_name
- **Columns actually used:** Both (branch_id as join key, branch_name in output)
- **Evidence:** [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] SQL joins on `pc.branch_id = b.branch_id AND pc.as_of = b.as_of` and selects `b.branch_name`.

### datalake.segments
- **Columns sourced:** segment_id, segment_name
- **Usage:** NONE — loaded into shared state as "segments" but never referenced in the Transformation SQL.
- **Evidence:** [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] SQL only references `branch_visits` and `branches` tables. The "segments" table is not used in any FROM, JOIN, or subquery.

## Business Rules

BR-1: Branch visits are grouped by branch_id, visit_purpose, and as_of, and the count of visits per group is computed.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] `GROUP BY bv.branch_id, bv.visit_purpose, bv.as_of` and `COUNT(*) AS visit_count`.
- Evidence: [curated.branch_visit_purpose_breakdown] For as_of 2024-10-01, branch 7 (Denver CO Branch) has 4 separate purpose entries summing to 4 visits.

BR-2: Each output row includes the branch_name, looked up by joining with branches on branch_id and as_of.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] `JOIN branches b ON pc.branch_id = b.branch_id AND pc.as_of = b.as_of`.

BR-3: The SQL computes `total_branch_visits` using a window function (`SUM(COUNT(*)) OVER (PARTITION BY bv.branch_id, bv.as_of)`) but this value is NOT included in the final output.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] The CTE includes `total_branch_visits` but the outer SELECT only picks `pc.branch_id, b.branch_name, pc.visit_purpose, pc.as_of, pc.visit_count` — `total_branch_visits` is computed but discarded.
- Evidence: [curated.branch_visit_purpose_breakdown] Output schema has no `total_branch_visits` column.

BR-4: The output contains 5 columns: branch_id, branch_name, visit_purpose, as_of, visit_count.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] Outer SELECT lists these 5 columns.
- Evidence: [curated.branch_visit_purpose_breakdown] Schema confirms these 5 columns.

BR-5: Results are ordered by as_of, then branch_id, then visit_purpose.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] `ORDER BY pc.as_of, pc.branch_id, pc.visit_purpose`.

BR-6: Data is written in Append mode, accumulating purpose breakdowns across effective dates.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:36] `"writeMode": "Append"`.
- Evidence: [curated.branch_visit_purpose_breakdown] Contains multiple as_of dates.

BR-7: Only branches with at least one visit appear in the output (INNER JOIN behavior).
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] Uses `JOIN` (inner join) between purpose_counts CTE and branches. Branches with zero visits don't appear in the CTE, so they're excluded.

BR-8: The join between purpose_counts and branches matches on both branch_id AND as_of.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] `ON pc.branch_id = b.branch_id AND pc.as_of = b.as_of`.

## Output Schema

| Column | Source | Transformation |
|--------|--------|----------------|
| branch_id | datalake.branch_visits.branch_id | Group key |
| branch_name | datalake.branches.branch_name | Joined via branch_id + as_of |
| visit_purpose | datalake.branch_visits.visit_purpose | Group key |
| as_of | datalake.branch_visits.as_of | Group key |
| visit_count | Computed | COUNT(*) of visits per (branch_id, visit_purpose, as_of) |

## Edge Cases

- **Weekend dates:** branch_visits AND branches both have weekend data. This job uses SQL Transformation (no External module empty guards), so weekend data IS processed and output. This is confirmed by the curated output containing weekend as_of dates (e.g., 2024-10-05, 2024-10-06).
- **Branch with no visits:** Not included in output (INNER JOIN behavior, BR-7).
- **Visit with no branch record:** Would be excluded by INNER JOIN if the branch_id doesn't exist in branches for that as_of. In practice, all branch_ids in visits have corresponding branch records.
- **Empty source data:** If branch_visits is empty for an as_of date, the GROUP BY produces no rows, so no output is written for that date.

## Anti-Patterns Identified

- **AP-1: Redundant Data Sourcing** — The `segments` DataSourcing module fetches segment_id and segment_name from `datalake.segments`, but the Transformation SQL never references the "segments" table.
  - Evidence: [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:20-24] segments DataSourcing defined; [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] SQL only uses branch_visits and branches.
  - V2 approach: Remove the segments DataSourcing module entirely.

- **AP-4: Unused Columns Sourced** — The branch_visits DataSourcing fetches customer_id but it's never used in the Transformation SQL (the GROUP BY and COUNT only use branch_id, visit_purpose, as_of).
  - Evidence: [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:10] columns include customer_id; [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] SQL only uses branch_id, visit_purpose, as_of from branch_visits.
  - V2 approach: Remove customer_id from the branch_visits DataSourcing columns.

- **AP-8: Overly Complex SQL** — The SQL uses a CTE (`purpose_counts`) with an unnecessary window function (`SUM(COUNT(*)) OVER (...)`) that computes `total_branch_visits` which is never used in the output. The entire CTE could be replaced with a simpler direct query joining branch_visits with branches and grouping.
  - Evidence: [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] `total_branch_visits` is computed in CTE but not selected in outer query.
  - V2 approach: Simplify to a direct GROUP BY with JOIN: `SELECT bv.branch_id, b.branch_name, bv.visit_purpose, bv.as_of, COUNT(*) AS visit_count FROM branch_visits bv JOIN branches b ON bv.branch_id = b.branch_id AND bv.as_of = b.as_of GROUP BY bv.branch_id, b.branch_name, bv.visit_purpose, bv.as_of ORDER BY bv.as_of, bv.branch_id, bv.visit_purpose`.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] GROUP BY + COUNT(*) |
| BR-2 | [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] JOIN branches |
| BR-3 | [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] total_branch_visits computed but not selected |
| BR-4 | [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29], [curated.branch_visit_purpose_breakdown] schema |
| BR-5 | [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] ORDER BY |
| BR-6 | [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:36] |
| BR-7 | [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] INNER JOIN |
| BR-8 | [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] ON clause |

## Open Questions

- **Q1:** The `visit_id` column is sourced from branch_visits but not used in the SQL. This is a minor unused column issue (already flagged under AP-4 for customer_id). Both visit_id and customer_id should be removed from sourcing. Confidence: HIGH.
