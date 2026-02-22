# BRD: TopBranches

## Overview
This job ranks branches by their total number of visits (across all as_of dates loaded for the current effective date) and produces a ranked list. The output is written to `curated.top_branches` in Overwrite mode.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| branch_visits | datalake | visit_id, branch_id | Aggregated by branch_id: COUNT(*) as total_visits; filtered by as_of >= '2024-10-01' (redundant) | [JobExecutor/Jobs/top_branches.json:5-10] DataSourcing config; [top_branches.json:21-22] SQL |
| branches | datalake | branch_id, branch_name | Joined to visit_totals on branch_id to get branch_name; also provides as_of for output | [top_branches.json:12-16] DataSourcing config; [top_branches.json:21-22] SQL JOIN |

## Business Rules

BR-1: Branch visits are aggregated by branch_id to compute total_visits (COUNT(*)) for the effective date.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/top_branches.json:22] SQL CTE: `SELECT bv.branch_id, COUNT(*) AS total_visits FROM branch_visits bv WHERE bv.as_of >= '2024-10-01' GROUP BY bv.branch_id`
- Evidence: Since DataSourcing loads only one day's data, this counts visits for that single day

BR-2: The SQL includes `WHERE bv.as_of >= '2024-10-01'` but since DataSourcing loads only the current effective date's data, this filter is redundant for dates on or after 2024-10-01.
- Confidence: HIGH
- Evidence: [top_branches.json:22] SQL WHERE clause
- Evidence: [Lib/Control/JobExecutorService.cs:100-101] Single date injection into DataSourcing

BR-3: Branches are ranked using RANK() window function ordered by total_visits DESC. Ties receive the same rank, and the next rank is skipped (standard RANK behavior).
- Confidence: HIGH
- Evidence: [top_branches.json:22] `RANK() OVER (ORDER BY vt.total_visits DESC) AS rank`
- Evidence: [curated.top_branches] Multiple branches with rank=2 (3 visits each), next rank is 5

BR-4: The visit_totals CTE is joined to the branches table on branch_id. This is an INNER JOIN, so only branches that have at least one visit appear in the output.
- Confidence: HIGH
- Evidence: [top_branches.json:22] `FROM visit_totals vt JOIN branches b ON vt.branch_id = b.branch_id` — JOIN without LEFT means INNER
- Evidence: [curated.top_branches] 16 rows, less than total branch count, confirms branches without visits are excluded

BR-5: The as_of column in the output comes from the branches table (b.as_of), not from branch_visits.
- Confidence: HIGH
- Evidence: [top_branches.json:22] `b.as_of` in SELECT clause
- Evidence: [curated.top_branches] All rows have as_of = 2024-10-31

BR-6: Output is ordered by rank ASC, then branch_id ASC.
- Confidence: HIGH
- Evidence: [top_branches.json:22] `ORDER BY rank, vt.branch_id`

BR-7: Output is written in Overwrite mode — each run truncates the entire table before writing.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/top_branches.json:28] `"writeMode": "Overwrite"`
- Evidence: [curated.top_branches] Only one as_of date (2024-10-31) present

BR-8: The job has a SameDay dependency on BranchVisitSummary.
- Confidence: HIGH
- Evidence: [control.job_dependencies] Query shows TopBranches depends on BranchVisitSummary with dependency_type = 'SameDay'

BR-9: Branch_visits data exists for all 31 days (including weekends), while branches data also exists for all 31 days. So the JOIN can produce output for any effective date.
- Confidence: HIGH
- Evidence: [datalake.branch_visits] 31 distinct as_of dates
- Evidence: [datalake.branches] 31 distinct as_of dates

BR-10: The join between visit_totals (which has no as_of — it was removed by GROUP BY) and branches (which has as_of) means each visit_totals row is replicated once per branch row. Since DataSourcing loads only one day's branches data, there is exactly one branches row per branch_id, so the join is effectively 1:1.
- Confidence: HIGH
- Evidence: [top_branches.json:22] visit_totals CTE groups away as_of; branches table retains as_of
- Evidence: [Lib/Control/JobExecutorService.cs:100-101] Single-day DataSourcing
- Evidence: [curated.top_branches] No duplicate branch_ids in output

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| branch_id | visit_totals.branch_id (from branch_visits) | GROUP BY key | [top_branches.json:22] |
| branch_name | branches.branch_name | Joined lookup | [top_branches.json:22] |
| total_visits | COUNT(*) of branch_visits | Integer count per branch | [top_branches.json:22] |
| rank | RANK() window function | Rank by total_visits DESC | [top_branches.json:22] |
| as_of | branches.as_of | From branches table join | [top_branches.json:22] |

## Edge Cases
- **NULL handling**: No explicit NULL handling. branch_id is expected to be non-null in both tables.
- **Weekend/date fallback**: Both branch_visits and branches have data on weekends, so the job can produce output for all 31 days.
- **Zero-row behavior**: If no branch visits exist for the effective date, the CTE produces zero rows, the JOIN produces zero rows, and an empty DataFrame is written after truncate.
- **Branches with no visits**: Excluded by INNER JOIN behavior (BR-4). Only visited branches appear in output.
- **RANK vs DENSE_RANK**: RANK is used, meaning gaps appear after ties. For example, if 3 branches tie at rank 2, the next rank is 5, not 3.
- **SQLite RANK**: The Transformation runs in SQLite which supports RANK() window function.

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [top_branches.json:22], [curated data verification] |
| BR-2 | [top_branches.json:22], [JobExecutorService.cs:100-101] |
| BR-3 | [top_branches.json:22], [curated data verification] |
| BR-4 | [top_branches.json:22], [curated data count analysis] |
| BR-5 | [top_branches.json:22], [curated data observation] |
| BR-6 | [top_branches.json:22] |
| BR-7 | [top_branches.json:28], [curated data observation] |
| BR-8 | [control.job_dependencies query] |
| BR-9 | [datalake date analysis] |
| BR-10 | [top_branches.json:22], [JobExecutorService.cs:100-101], [curated data observation] |

## Open Questions
- The name "TopBranches" implies there should be a LIMIT or TOP N, but the SQL does not include a LIMIT clause. All branches with visits are ranked and included. Confidence: HIGH that this is the intended behavior — the ranking allows consumers to filter as needed.
