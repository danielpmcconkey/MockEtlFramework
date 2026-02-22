# BRD: BranchVisitSummary

## Overview
Produces a summary of total branch visit counts per branch per effective date, joining with branches to include branch_name. Uses Append mode to accumulate results across all effective dates.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| branch_visits | datalake | visit_id, customer_id, branch_id, visit_purpose | Filtered by effective date range. Grouped by (branch_id, as_of) to count visits. | [JobExecutor/Jobs/branch_visit_summary.json:6-10] |
| branches | datalake | branch_id, branch_name | Filtered by effective date range. Joined to visit counts via branch_id AND as_of. | [JobExecutor/Jobs/branch_visit_summary.json:12-16] |

## Business Rules
BR-1: Branch visits are counted by grouping on (branch_id, as_of) to produce a total visit_count per branch per date.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_summary.json:22] SQL CTE: `SELECT bv.branch_id, COUNT(*) AS visit_count, bv.as_of FROM branch_visits bv GROUP BY bv.branch_id, bv.as_of`

BR-2: The visit counts are joined to branches using INNER JOIN on both branch_id AND as_of to get the branch_name.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_summary.json:22] SQL: `JOIN branches b ON vc.branch_id = b.branch_id AND vc.as_of = b.as_of`

BR-3: The INNER JOIN means only visits to branches that exist in the branches table for the same as_of date are included. Visits to non-existent branch_ids are dropped.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_summary.json:22] INNER JOIN semantics

BR-4: Write mode is Append -- each effective date's summary is added to the curated table.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_summary.json:28] `"writeMode": "Append"`
- Evidence: [curated.branch_visit_summary] 31 as_of dates present (all calendar days in October 2024)

BR-5: The output is ordered by (as_of, branch_id).
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_summary.json:22] SQL: `ORDER BY vc.as_of, vc.branch_id`

BR-6: The job processes ALL calendar days including weekends, since both branch_visits and branches have weekend data.
- Confidence: HIGH
- Evidence: [curated.branch_visit_summary] 31 dates including weekends
- Evidence: [datalake.branch_visits] Has weekend as_of dates
- Evidence: [datalake.branches] Has weekend as_of dates

BR-7: This job has a SameDay dependency on BranchDirectory.
- Confidence: HIGH
- Evidence: [control.job_dependencies] BranchVisitSummary depends on BranchDirectory with dependency_type = SameDay
- Note: Like BranchVisitPurposeBreakdown, this job reads from datalake.branches directly, not curated.branch_directory.

BR-8: Only branches with at least one visit for a given as_of appear in the output (due to the GROUP BY on branch_visits driving the rows, then INNER JOIN limiting to existing branches).
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_summary.json:22] SQL logic: CTE groups branch_visits, then INNER JOIN with branches
- Evidence: [curated.branch_visit_summary] Varying row counts per date (20, 21, 15, 17...) indicate not all 40 branches have visits every day

BR-9: This is a simpler version of BranchVisitPurposeBreakdown -- it aggregates total visits per branch instead of per (branch, purpose).
- Confidence: HIGH
- Evidence: Compare SQL: BranchVisitSummary groups by (branch_id, as_of) while BranchVisitPurposeBreakdown groups by (branch_id, visit_purpose, as_of)

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| branch_id | datalake.branch_visits.branch_id | Grouping key | [branch_visit_summary.json:22] |
| branch_name | datalake.branches.branch_name | Joined via branch_id AND as_of | [branch_visit_summary.json:22] |
| as_of | datalake.branch_visits.as_of | Grouping key | [branch_visit_summary.json:22] |
| visit_count | Computed | COUNT(*) of all visits per (branch_id, as_of) regardless of purpose | [branch_visit_summary.json:22] |

## Edge Cases
- **NULL handling**: No explicit NULL handling in the SQL. NULLs in grouping columns would be grouped as their own category.
- **Empty branch_visits**: If no visits for a date, the GROUP BY produces zero rows, and the INNER JOIN produces nothing.
- **Branch not found**: If a branch_id in branch_visits has no match in branches for the same as_of, the INNER JOIN drops those visits.
- **Weekend data**: Both branch_visits and branches have weekend data, so the job produces output for all 31 calendar days.
- **No visit_purpose in output**: Unlike BranchVisitPurposeBreakdown, this job does not break down by purpose -- it shows total visits per branch.

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [branch_visit_summary.json:22] SQL GROUP BY |
| BR-2 | [branch_visit_summary.json:22] SQL JOIN ON |
| BR-3 | [branch_visit_summary.json:22] INNER JOIN |
| BR-4 | [branch_visit_summary.json:28], [curated output dates] |
| BR-5 | [branch_visit_summary.json:22] SQL ORDER BY |
| BR-6 | [curated output 31 dates], [datalake dates] |
| BR-7 | [control.job_dependencies] |
| BR-8 | [branch_visit_summary.json:22] SQL logic, [curated row counts vary] |
| BR-9 | [branch_visit_summary.json:22] vs [branch_visit_purpose_breakdown.json:29] |

## Open Questions
- **SameDay dependency on BranchDirectory**: The job reads from datalake.branches, not curated.branch_directory. The dependency ensures BranchDirectory has run for the same date, but the data flow is not directly linked. Confidence: MEDIUM that this is an orchestration requirement rather than a data dependency.
