# BranchVisitPurposeBreakdown -- Functional Specification Document

## Design Approach

SQL-first. The original already uses a SQL Transformation, but with an unnecessarily complex CTE that computes a `total_branch_visits` window function value that is never used in the output. The V2 simplifies to a direct GROUP BY with JOIN.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed unused `segments` DataSourcing module entirely |
| AP-2    | N                   | N/A                | N/A |
| AP-3    | N                   | N/A                | Already uses SQL Transformation |
| AP-4    | Y                   | Y                  | Removed unused columns (visit_id, customer_id) from branch_visits DataSourcing |
| AP-5    | N                   | N/A                | N/A |
| AP-6    | N                   | N/A                | N/A |
| AP-7    | N                   | N/A                | N/A |
| AP-8    | Y                   | Y                  | Removed unnecessary CTE with unused total_branch_visits window function; simplified to direct GROUP BY with JOIN |
| AP-9    | N                   | N/A                | N/A |
| AP-10   | N                   | N/A                | N/A |

## V2 Pipeline Design

1. **DataSourcing** (`branch_visits`): Read from `datalake.branch_visits` with only columns actually used: branch_id, visit_purpose. The framework automatically appends as_of.

2. **DataSourcing** (`branches`): Read from `datalake.branches` with columns: branch_id, branch_name.

3. **Transformation** (`purpose_breakdown`): Direct GROUP BY with INNER JOIN to branches. No CTE, no window functions.

4. **DataFrameWriter**: Write `purpose_breakdown` to `branch_visit_purpose_breakdown` in `double_secret_curated` schema with Append mode.

## SQL Transformation Logic

```sql
SELECT
    bv.branch_id,
    b.branch_name,
    bv.visit_purpose,
    bv.as_of,
    COUNT(*) AS visit_count
FROM branch_visits bv
JOIN branches b ON bv.branch_id = b.branch_id AND bv.as_of = b.as_of
GROUP BY bv.branch_id, b.branch_name, bv.visit_purpose, bv.as_of
ORDER BY bv.as_of, bv.branch_id, bv.visit_purpose
```

The original SQL used a CTE (`purpose_counts`) that computed `SUM(COUNT(*)) OVER (PARTITION BY bv.branch_id, bv.as_of) AS total_branch_visits`. This window function result was never selected in the outer query. The V2 eliminates this unnecessary computation.

The INNER JOIN ensures only branches with at least one visit appear in the output (matching the original behavior). The ORDER BY matches the original ordering.

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1            | GROUP BY bv.branch_id, bv.visit_purpose, bv.as_of with COUNT(*) |
| BR-2            | JOIN branches b ON bv.branch_id = b.branch_id AND bv.as_of = b.as_of; b.branch_name in SELECT |
| BR-3            | total_branch_visits not computed (was unused in original output); simplification eliminates dead computation |
| BR-4            | SELECT produces exactly 5 columns: branch_id, branch_name, visit_purpose, as_of, visit_count |
| BR-5            | ORDER BY bv.as_of, bv.branch_id, bv.visit_purpose |
| BR-6            | DataFrameWriter writeMode is "Append" |
| BR-7            | INNER JOIN excludes branches with zero visits |
| BR-8            | JOIN condition includes both branch_id AND as_of |
