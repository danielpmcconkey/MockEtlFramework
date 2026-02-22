# TopBranches -- Business Requirements Document

## Overview

This job ranks branches by their total visit count for the current effective date and produces a ranked list with branch names. The output is written in Overwrite mode, so only the latest effective date's ranking persists.

## Source Tables

### datalake.branch_visits
- **Columns used**: `branch_id`, `as_of` (via WHERE filter)
- **Column sourced but unused**: `visit_id` (see AP-4)
- **Filter**: `as_of >= '2024-10-01'` in the SQL, but this is redundant because DataSourcing already filters to the current effective date
- **Evidence**: [JobExecutor/Jobs/top_branches.json:22] SQL CTE with `WHERE bv.as_of >= '2024-10-01'`

### datalake.branches
- **Columns used**: `branch_id`, `branch_name`, `as_of`
- **Join logic**: Joined to visit totals via `branch_id` to get branch names
- **Evidence**: [JobExecutor/Jobs/top_branches.json:22] `JOIN branches b ON vt.branch_id = b.branch_id`

## Business Rules

BR-1: Visits are counted per branch_id for the current effective date.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/top_branches.json:22] `COUNT(*) AS total_visits FROM branch_visits bv ... GROUP BY bv.branch_id`
- Evidence: [curated.top_branches] For as_of = 2024-10-31, branch_id 27 has total_visits = 4. Direct query: `SELECT COUNT(*) FROM datalake.branch_visits WHERE branch_id = 27 AND as_of = '2024-10-31'` yields 4.

BR-2: Branches are ranked by total_visits in descending order using the RANK() window function.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/top_branches.json:22] `RANK() OVER (ORDER BY vt.total_visits DESC) AS rank`
- Evidence: [curated.top_branches] Branches with tied visit counts share the same rank (e.g., branches 5, 17, 22 all have rank=2 with total_visits=3)

BR-3: Each output row includes the branch_name from the branches table.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/top_branches.json:22] `JOIN branches b ON vt.branch_id = b.branch_id` and `SELECT ... b.branch_name`

BR-4: The as_of column in the output comes from the branches table, not branch_visits.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/top_branches.json:22] `SELECT ... b.as_of FROM visit_totals vt JOIN branches b`

BR-5: Only branches that have at least one visit are included (the GROUP BY in the CTE excludes branches with zero visits).
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/top_branches.json:22] CTE aggregates branch_visits, so only branches present in branch_visits appear
- Evidence: [curated.top_branches] 16 distinct branches vs 40 total branches in datalake.branches -- 24 branches with no visits on Oct 31 are excluded

BR-6: Results are ordered by rank (ascending), then by branch_id (ascending) as tiebreaker.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/top_branches.json:22] `ORDER BY rank, vt.branch_id`

BR-7: The output uses Overwrite mode.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/top_branches.json:28] `"writeMode": "Overwrite"`

## Output Schema

| Column | Source | Transformation |
|--------|--------|----------------|
| branch_id | datalake.branch_visits.branch_id (via CTE) | GROUP BY key |
| branch_name | datalake.branches.branch_name | Joined via branch_id |
| total_visits | Derived: COUNT(*) of branch_visits per branch_id | Aggregate count |
| rank | Derived: RANK() OVER (ORDER BY total_visits DESC) | Window function |
| as_of | datalake.branches.as_of | From the branches table row |

## Edge Cases

- **Tied visit counts**: Branches with the same total_visits share the same rank (RANK function, not DENSE_RANK). The next rank after a tie is offset (e.g., 1, 2, 2, 2, 5 not 1, 2, 2, 2, 3).
- **Branches with zero visits**: Excluded from output (only branches present in branch_visits appear in the CTE).
- **Overwrite mode**: Only the last effective date's ranking persists.
- **Hardcoded date filter**: The `WHERE bv.as_of >= '2024-10-01'` is redundant since DataSourcing filters to one effective date. It has no practical effect.

## Anti-Patterns Identified

- **AP-1: Redundant Data Sourcing** -- Note: Unlike other jobs, both DataSourcing modules (branch_visits and branches) are actually used by the Transformation SQL. No redundant sourcing here.

- **AP-2: Duplicated Transformation Logic** -- This job has a declared SameDay dependency on `BranchVisitSummary`, which already computes per-branch visit counts and writes them to `curated.branch_visit_summary` (columns: branch_id, branch_name, as_of, visit_count). TopBranches re-derives visit counts from raw `datalake.branch_visits` instead of reading from the upstream curated table. The only additional logic TopBranches needs beyond BranchVisitSummary is the RANK() window function. V2 approach: Read from `curated.branch_visit_summary` and apply RANK() directly, instead of re-deriving visit counts from datalake.

- **AP-4: Unused Columns Sourced** -- The branch_visits DataSourcing includes `visit_id`, which is never referenced in the Transformation SQL. The SQL only uses `branch_id` and `as_of` from branch_visits. V2 approach: Remove `visit_id` from the branch_visits DataSourcing columns. If fixing AP-2, the branch_visits DataSourcing module is removed entirely.

- **AP-7: Hardcoded Magic Values** -- The date `'2024-10-01'` is hardcoded in the SQL WHERE clause. Since DataSourcing handles effective date filtering, this is redundant and misleading. V2 approach: Remove the hardcoded date filter.

- **AP-8: Overly Complex SQL** -- The SQL uses a CTE (`WITH visit_totals AS (...)`) to compute visit counts, then selects from it with a JOIN and window function. While the CTE provides some logical separation, the query could be written as a single query with a subquery or even directly if reading from the upstream curated table (AP-2 fix). V2 approach: Simplify SQL.

- **AP-10: Missing Dependency Declarations** -- A dependency on BranchVisitSummary IS declared. However, the job does not actually read from `curated.branch_visit_summary`. If the V2 fixes AP-2 by reading from the upstream curated table, this dependency becomes meaningful and correct.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [JobExecutor/Jobs/top_branches.json:22] `COUNT(*) ... GROUP BY bv.branch_id` |
| BR-2 | [JobExecutor/Jobs/top_branches.json:22] `RANK() OVER (ORDER BY vt.total_visits DESC)` |
| BR-3 | [JobExecutor/Jobs/top_branches.json:22] `JOIN branches b` and `b.branch_name` |
| BR-4 | [JobExecutor/Jobs/top_branches.json:22] `b.as_of` in SELECT |
| BR-5 | [JobExecutor/Jobs/top_branches.json:22] CTE only includes branches with visits |
| BR-6 | [JobExecutor/Jobs/top_branches.json:22] `ORDER BY rank, vt.branch_id` |
| BR-7 | [JobExecutor/Jobs/top_branches.json:28] `"writeMode": "Overwrite"` |

## Open Questions

- None. All business rules are directly observable with HIGH confidence.
