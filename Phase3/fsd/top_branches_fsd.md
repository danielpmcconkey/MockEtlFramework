# TopBranches -- Functional Specification Document

## Design Approach

SQL-first with AP-2 dependency fix. The original job re-derives per-branch visit counts from raw `datalake.branch_visits`, duplicating logic already computed by the upstream BranchVisitSummary job and written to `curated.branch_visit_summary`. The V2 reads from the upstream curated table (which already contains branch_id, branch_name, visit_count, and as_of), then applies RANK() to produce the ranked output. This eliminates duplicated computation and properly leverages the declared dependency.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | N                   | N/A                | Both original DataSourcing modules were used |
| AP-2    | Y                   | Y                  | V2 reads from curated.branch_visit_summary instead of re-deriving visit counts from datalake |
| AP-3    | N                   | N/A                | Original already uses Transformation (not External module) |
| AP-4    | Y                   | Y                  | Original sourced visit_id from branch_visits which was unused; V2 does not source raw branch_visits at all |
| AP-5    | N                   | N/A                | No NULL/default handling involved |
| AP-6    | N                   | N/A                | No External module with row-by-row iteration |
| AP-7    | Y                   | Y                  | Removed hardcoded date '2024-10-01' from WHERE clause (DataSourcing handles date filtering) |
| AP-8    | Y                   | Y                  | Removed CTE; simplified to a direct SELECT with RANK() from upstream table |
| AP-9    | N                   | N/A                | Job name accurately describes what it does |
| AP-10   | N                   | N/A                | Dependency on BranchVisitSummary already declared; V2 now actually uses upstream output |

## V2 Pipeline Design

1. **DataSourcing** `branch_summary`: Read `branch_id`, `branch_name`, `visit_count` from `curated.branch_visit_summary`
2. **Transformation** `top_branches`: Apply RANK() window function and rename visit_count to total_visits
3. **DataFrameWriter**: Write `top_branches` to `double_secret_curated.top_branches` in Overwrite mode

## SQL Transformation Logic

```sql
SELECT
    branch_id,
    branch_name,
    visit_count AS total_visits,
    RANK() OVER (ORDER BY visit_count DESC) AS rank,
    as_of
FROM branch_summary
ORDER BY rank, branch_id
```

**Key design notes:**
- Reads pre-computed visit counts from curated.branch_visit_summary (populated by upstream BranchVisitSummary job)
- Column renaming: visit_count -> total_visits
- RANK() (not DENSE_RANK) produces gaps in ranking after ties (e.g., 1, 2, 2, 2, 5) matching original behavior (BR-2)
- Only branches with visits appear in branch_visit_summary, so branches with zero visits are naturally excluded (BR-5)
- ORDER BY rank, branch_id for consistent ordering with tiebreaker (BR-6)
- Depends on BranchVisitSummary running first (SameDay dependency already declared)

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1 | visit_count from upstream = pre-computed COUNT(*) of visits per branch |
| BR-2 | RANK() OVER (ORDER BY visit_count DESC) |
| BR-3 | branch_name from upstream table (originally from branches table, pre-joined by BranchVisitSummary) |
| BR-4 | as_of from upstream table (same effective date) |
| BR-5 | Only branches with visits exist in branch_visit_summary (implicit filter) |
| BR-6 | ORDER BY rank, branch_id |
| BR-7 | DataFrameWriter writeMode: "Overwrite" |
