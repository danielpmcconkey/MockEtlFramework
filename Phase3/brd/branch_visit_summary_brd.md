# BranchVisitSummary — Business Requirements Document

## Overview

This job produces a daily summary of total visit counts per branch, joining branch visit counts with branch names. The result is written to `curated.branch_visit_summary` using Append mode, accumulating daily summaries over time.

## Source Tables

### datalake.branch_visits
- **Columns sourced:** visit_id, customer_id, branch_id, visit_purpose
- **Columns actually used:** branch_id (group key), as_of (group key). COUNT(*) counts all rows.
- **Evidence:** [JobExecutor/Jobs/branch_visit_summary.json:22] SQL groups by `bv.branch_id, bv.as_of` and counts.

### datalake.branches
- **Columns sourced:** branch_id, branch_name
- **Columns actually used:** Both (branch_id as join key, branch_name in output)
- **Evidence:** [JobExecutor/Jobs/branch_visit_summary.json:22] SQL joins on `vc.branch_id = b.branch_id AND vc.as_of = b.as_of` and selects `b.branch_name`.

## Business Rules

BR-1: Branch visits are grouped by branch_id and as_of, and the total count of visits per branch per day is computed.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_summary.json:22] `SELECT bv.branch_id, COUNT(*) AS visit_count, bv.as_of FROM branch_visits bv GROUP BY bv.branch_id, bv.as_of`.
- Evidence: [curated.branch_visit_summary] For as_of 2024-10-01, branch 7 (Denver CO Branch) has visit_count 4, matching the 4 visit records in branch_visit_purpose_breakdown.

BR-2: Each output row includes the branch_name, looked up by joining with branches on branch_id and as_of.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_summary.json:22] `JOIN branches b ON vc.branch_id = b.branch_id AND vc.as_of = b.as_of` and `b.branch_name` in SELECT.

BR-3: The output contains 4 columns: branch_id, branch_name, as_of, visit_count.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_summary.json:22] SELECT lists these 4 columns.
- Evidence: [curated.branch_visit_summary] Schema confirms these 4 columns.

BR-4: Results are ordered by as_of then branch_id.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_summary.json:22] `ORDER BY vc.as_of, vc.branch_id`.

BR-5: Data is written in Append mode, accumulating daily summaries.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_summary.json:27] `"writeMode": "Append"`.
- Evidence: [curated.branch_visit_summary] Contains multiple as_of dates with varying counts.

BR-6: Only branches with at least one visit for the effective date appear in the output (INNER JOIN behavior).
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_summary.json:22] Uses `JOIN` (inner join). Branches with zero visits are excluded from the visit_counts CTE, and thus from the output.

BR-7: The join between visit_counts and branches matches on both branch_id AND as_of.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_visit_summary.json:22] `ON vc.branch_id = b.branch_id AND vc.as_of = b.as_of`.

## Output Schema

| Column | Source | Transformation |
|--------|--------|----------------|
| branch_id | datalake.branch_visits.branch_id | Group key |
| branch_name | datalake.branches.branch_name | Joined via branch_id + as_of |
| as_of | datalake.branch_visits.as_of | Group key |
| visit_count | Computed | COUNT(*) of visits per (branch_id, as_of) |

## Edge Cases

- **Weekend dates:** branch_visits AND branches both have weekend data. This job uses SQL Transformation (no External module empty guards), so weekend data IS processed and output. Confirmed by curated output containing weekend dates (e.g., 2024-10-05, 2024-10-06).
- **Branch with no visits:** Not included in output (INNER JOIN, BR-6).
- **Empty source data:** If no visits exist for an as_of date, the GROUP BY produces no rows.
- **Visit with no branch record:** Excluded by INNER JOIN (unlikely in practice).

## Anti-Patterns Identified

- **AP-4: Unused Columns Sourced** — The branch_visits DataSourcing fetches visit_id, customer_id, and visit_purpose, but the SQL only uses branch_id and as_of (the COUNT(*) counts rows, not specific columns).
  - Evidence: [JobExecutor/Jobs/branch_visit_summary.json:10] columns include visit_id, customer_id, visit_purpose; [JobExecutor/Jobs/branch_visit_summary.json:22] SQL only references bv.branch_id and bv.as_of.
  - V2 approach: Only source branch_id from branch_visits.

- **AP-8: Overly Complex SQL** — The SQL uses a CTE (`visit_counts`) to first aggregate visits by branch, then joins with branches. This could be simplified to a single query with GROUP BY and JOIN combined.
  - Evidence: [JobExecutor/Jobs/branch_visit_summary.json:22] CTE wraps a simple GROUP BY.
  - V2 approach: Simplify to `SELECT bv.branch_id, b.branch_name, bv.as_of, COUNT(*) AS visit_count FROM branch_visits bv JOIN branches b ON bv.branch_id = b.branch_id AND bv.as_of = b.as_of GROUP BY bv.branch_id, b.branch_name, bv.as_of ORDER BY bv.as_of, bv.branch_id`.

- **AP-10: Missing Dependency Declarations** — BranchVisitSummary (job_id=24) has a declared dependency on BranchDirectory (job_id=22) via `control.job_dependencies`. However, BranchVisitSummary reads from `datalake.branches` directly (not `curated.branch_directory`), so this dependency is unnecessary — it doesn't actually consume the output of BranchDirectory.
  - Evidence: [control.job_dependencies] row: job_id=24, depends_on_job_id=22, SameDay; [JobExecutor/Jobs/branch_visit_summary.json:22] SQL reads from the `branches` DataFrame (sourced from datalake.branches, not curated).
  - V2 approach: The V2 job should not declare a dependency on BranchDirectory since it reads directly from datalake, not from curated.branch_directory. However, if the V2 is redesigned to read from curated.branch_directory (fixing AP-2-like redundancy), then the dependency would be appropriate. For simplicity and output equivalence, the V2 should read from datalake.branches and remove the unnecessary dependency.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [JobExecutor/Jobs/branch_visit_summary.json:22] GROUP BY + COUNT |
| BR-2 | [JobExecutor/Jobs/branch_visit_summary.json:22] JOIN branches |
| BR-3 | [JobExecutor/Jobs/branch_visit_summary.json:22], [curated.branch_visit_summary] schema |
| BR-4 | [JobExecutor/Jobs/branch_visit_summary.json:22] ORDER BY |
| BR-5 | [JobExecutor/Jobs/branch_visit_summary.json:27] |
| BR-6 | [JobExecutor/Jobs/branch_visit_summary.json:22] INNER JOIN |
| BR-7 | [JobExecutor/Jobs/branch_visit_summary.json:22] ON clause |

## Open Questions

- **Q1:** The existing dependency on BranchDirectory (job_id=22) in `control.job_dependencies` appears unnecessary since BranchVisitSummary reads from datalake.branches, not curated.branch_directory. This may have been intentional (to ensure branches data is "validated" first) or accidental. The V2 should evaluate whether reading from curated.branch_directory would be beneficial. Confidence: MEDIUM that the dependency is unnecessary for correctness.
