# BRD: BranchVisitPurposeBreakdown

## Overview
Produces a breakdown of branch visits by purpose for each branch and effective date, showing the count of visits per (branch_id, visit_purpose, as_of) combination. Joins with branches to include branch_name. Uses Append mode to accumulate results across all effective dates.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| branch_visits | datalake | visit_id, customer_id, branch_id, visit_purpose | Filtered by effective date range. Grouped by (branch_id, visit_purpose, as_of). | [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:6-10] |
| branches | datalake | branch_id, branch_name | Filtered by effective date range. Joined to visit counts via branch_id AND as_of. | [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:12-16] |
| segments | datalake | segment_id, segment_name | Filtered by effective date range. Sourced but NOT used in the SQL. | [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:18-22] |

## Business Rules
BR-1: Branch visits are counted by grouping on (branch_id, visit_purpose, as_of).
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] SQL: `GROUP BY bv.branch_id, bv.visit_purpose, bv.as_of` with `COUNT(*) AS visit_count`

BR-2: The SQL computes a `total_branch_visits` window function (`SUM(COUNT(*)) OVER (PARTITION BY bv.branch_id, bv.as_of)`) in the CTE, but this column is NOT included in the final output SELECT.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] CTE includes `SUM(COUNT(*)) OVER (PARTITION BY bv.branch_id, bv.as_of) AS total_branch_visits`
- Evidence: [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] Final SELECT is `pc.branch_id, b.branch_name, pc.visit_purpose, pc.as_of, pc.visit_count` -- no total_branch_visits

BR-3: The purpose counts are joined to branches using INNER JOIN on both branch_id AND as_of to get the branch_name.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] SQL: `JOIN branches b ON pc.branch_id = b.branch_id AND pc.as_of = b.as_of`

BR-4: The INNER JOIN means only visits to branches that exist in the branches table for the same as_of date are included. Visits to non-existent branch_ids would be dropped.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] INNER JOIN semantics

BR-5: Write mode is Append -- each effective date's breakdown is added to the curated table.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:35] `"writeMode": "Append"`
- Evidence: [curated.branch_visit_purpose_breakdown] 31 as_of dates present (all calendar days in October 2024)

BR-6: The output is ordered by (as_of, branch_id, visit_purpose).
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] SQL: `ORDER BY pc.as_of, pc.branch_id, pc.visit_purpose`

BR-7: The segments table is sourced but never used in the SQL transformation.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] SQL does not reference the `segments` table at all

BR-8: The job processes ALL calendar days including weekends, since both branch_visits and branches have weekend data.
- Confidence: HIGH
- Evidence: [curated.branch_visit_purpose_breakdown] 31 dates including weekends (Oct 5, Oct 6, Oct 12, Oct 13, etc.)
- Evidence: [datalake.branch_visits] Has weekend as_of dates
- Evidence: [datalake.branches] Has weekend as_of dates

BR-9: This job has a SameDay dependency on BranchDirectory.
- Confidence: HIGH
- Evidence: [control.job_dependencies] BranchVisitPurposeBreakdown depends on BranchDirectory with dependency_type = SameDay
- Note: However, this job reads directly from datalake.branches, not from curated.branch_directory. The dependency may be for scheduling correctness rather than data flow.

BR-10: Only branches with at least one visit for a given as_of appear in the output (due to the GROUP BY on branch_visits and INNER JOIN to branches).
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_purpose_breakdown.json:29] GROUP BY on branch_visits drives the rows, then INNER JOIN limits to existing branches

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| branch_id | datalake.branch_visits.branch_id | Grouping key | [branch_visit_purpose_breakdown.json:29] |
| branch_name | datalake.branches.branch_name | Joined via branch_id AND as_of | [branch_visit_purpose_breakdown.json:29] |
| visit_purpose | datalake.branch_visits.visit_purpose | Grouping key | [branch_visit_purpose_breakdown.json:29] |
| as_of | datalake.branch_visits.as_of | Grouping key | [branch_visit_purpose_breakdown.json:29] |
| visit_count | Computed | COUNT(*) of visits per (branch_id, visit_purpose, as_of) | [branch_visit_purpose_breakdown.json:29] |

## Edge Cases
- **NULL handling**: No explicit NULL handling. If visit_purpose is NULL, it would be grouped as its own category.
- **Empty branch_visits**: If no visits for a date, the GROUP BY produces zero rows. The INNER JOIN would also produce nothing.
- **Branch not found**: If a branch_id in branch_visits has no matching row in branches for the same as_of, the INNER JOIN drops those visits from the output.
- **Weekend data**: Both branch_visits and branches have weekend data, so the job produces output for all 31 calendar days in October 2024.
- **Computed but unused column**: The total_branch_visits window function is computed but excluded from the output. This may be remnant code from when a percentage column was planned or previously existed.

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [branch_visit_purpose_breakdown.json:29] SQL GROUP BY |
| BR-2 | [branch_visit_purpose_breakdown.json:29] SQL CTE and final SELECT |
| BR-3 | [branch_visit_purpose_breakdown.json:29] SQL JOIN ON |
| BR-4 | [branch_visit_purpose_breakdown.json:29] INNER JOIN |
| BR-5 | [branch_visit_purpose_breakdown.json:35], [curated output dates] |
| BR-6 | [branch_visit_purpose_breakdown.json:29] SQL ORDER BY |
| BR-7 | [branch_visit_purpose_breakdown.json:29] SQL, [branch_visit_purpose_breakdown.json:18-22] segments sourced |
| BR-8 | [curated output 31 dates], [datalake.branch_visits dates], [datalake.branches dates] |
| BR-9 | [control.job_dependencies] |
| BR-10 | [branch_visit_purpose_breakdown.json:29] SQL logic |

## Open Questions
- **Why is segments sourced but unused?** The segments DataSourcing module is configured but the SQL never references the segments table. Confidence: MEDIUM that it is dead code.
- **Why is total_branch_visits computed but not output?** The CTE computes `SUM(COUNT(*)) OVER (PARTITION BY bv.branch_id, bv.as_of) AS total_branch_visits` but the final SELECT does not include this column. This may be a remnant of a previously planned percentage calculation. Confidence: MEDIUM.
- **SameDay dependency on BranchDirectory**: The job reads from datalake.branches, not curated.branch_directory. The dependency ensures BranchDirectory has run for the same date, but the data flow is not directly linked. This may be an orchestration requirement rather than a data dependency. Confidence: MEDIUM.
