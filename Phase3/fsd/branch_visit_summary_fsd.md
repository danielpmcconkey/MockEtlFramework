# BranchVisitSummary -- Functional Specification Document

## Design Approach

SQL-first. The original already uses a SQL Transformation with a CTE. The V2 simplifies by removing the unnecessary CTE and using a direct GROUP BY with JOIN.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | N                   | N/A                | N/A |
| AP-2    | N                   | N/A                | N/A |
| AP-3    | N                   | N/A                | Already uses SQL Transformation |
| AP-4    | Y                   | Y                  | Removed unused columns (visit_id, customer_id, visit_purpose) from branch_visits DataSourcing |
| AP-5    | N                   | N/A                | N/A |
| AP-6    | N                   | N/A                | N/A |
| AP-7    | N                   | N/A                | N/A |
| AP-8    | Y                   | Y                  | Removed unnecessary CTE; simplified to direct GROUP BY with JOIN |
| AP-9    | N                   | N/A                | N/A |
| AP-10   | Y                   | Y                  | Original declares unnecessary dependency on BranchDirectory (reads from datalake.branches, not curated.branch_directory). V2 does not declare this dependency. |

## V2 Pipeline Design

1. **DataSourcing** (`branch_visits`): Read from `datalake.branch_visits` with only the column actually used: branch_id. The framework automatically appends as_of.

2. **DataSourcing** (`branches`): Read from `datalake.branches` with columns: branch_id, branch_name.

3. **Transformation** (`visit_summary`): Direct GROUP BY with INNER JOIN to branches. No CTE needed.

4. **DataFrameWriter**: Write `visit_summary` to `branch_visit_summary` in `double_secret_curated` schema with Append mode.

## SQL Transformation Logic

```sql
SELECT
    bv.branch_id,
    b.branch_name,
    bv.as_of,
    COUNT(*) AS visit_count
FROM branch_visits bv
JOIN branches b ON bv.branch_id = b.branch_id AND bv.as_of = b.as_of
GROUP BY bv.branch_id, b.branch_name, bv.as_of
ORDER BY bv.as_of, bv.branch_id
```

The original SQL used a CTE (`visit_counts`) to first aggregate by branch, then joined with branches. This is unnecessary -- the JOIN and GROUP BY can be combined into a single query. The INNER JOIN ensures only branches with visits appear (matching original behavior). The ORDER BY matches the original ordering.

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1            | GROUP BY bv.branch_id, bv.as_of with COUNT(*) |
| BR-2            | JOIN branches b ON bv.branch_id = b.branch_id AND bv.as_of = b.as_of; b.branch_name in SELECT |
| BR-3            | SELECT produces exactly 4 columns: branch_id, branch_name, as_of, visit_count |
| BR-4            | ORDER BY bv.as_of, bv.branch_id |
| BR-5            | DataFrameWriter writeMode is "Append" |
| BR-6            | INNER JOIN excludes branches with zero visits |
| BR-7            | JOIN condition includes both branch_id AND as_of |
