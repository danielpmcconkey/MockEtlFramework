# CustomerBranchActivity -- Functional Specification Document

## Design Approach

**SQL-equivalent logic in External module with empty-DataFrame guard.** The original External module counts branch visits per customer and enriches with customer names. This is a simple GROUP BY + LEFT JOIN in SQL. However, the framework's Transformation module does not register empty DataFrames as SQLite tables. On weekends, `customers` has no data (while `branch_visits` does), so the original returns empty output. A pure SQL approach would crash because the `customers` table wouldn't exist in SQLite.

The V2 uses a clean External module that:
1. Returns empty output when customers DataFrame is empty (weekend guard)
2. Returns empty output when branch_visits DataFrame is empty
3. Uses LINQ-based aggregation to count visits and enrich with names

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed unused `branches` DataSourcing module |
| AP-2    | N                   | N/A                | No duplicated logic |
| AP-3    | Y                   | Partial            | External module retained for empty-DataFrame guard; internal logic simplified with LINQ |
| AP-4    | Y                   | Y                  | Removed `visit_id`, `branch_id`, `visit_purpose` from DataSourcing (only `customer_id` needed) |
| AP-5    | N                   | N/A                | No asymmetric NULL handling (NULL names are intentional for missing customers) |
| AP-6    | Y                   | Y                  | Manual dictionary-building loops replaced with LINQ GroupBy and ToDictionary |
| AP-7    | N                   | N/A                | No magic values |
| AP-8    | N                   | N/A                | No overly complex SQL |
| AP-9    | N                   | N/A                | Name accurately describes the job |
| AP-10   | N                   | N/A                | No inter-job dependencies needed |

## V2 Pipeline Design

1. **DataSourcing** - `branch_visits` from `datalake.branch_visits` with columns: `customer_id`
2. **DataSourcing** - `customers` from `datalake.customers` with columns: `id`, `first_name`, `last_name`
3. **External** - `CustomerBranchActivityBuilderV2`: empty-check guard + LINQ-based aggregation
4. **DataFrameWriter** - Write to `customer_branch_activity` in `double_secret_curated` schema, Append mode

## External Module Design

```
IF customers is empty OR branch_visits is empty:
    Return empty DataFrame
ELSE:
    1. Build customer name lookup from customers DataFrame
    2. Group branch_visits by customer_id, count visits per customer (LINQ GroupBy)
    3. Get as_of from first branch_visit row
    4. For each customer group:
       - Look up name (NULL if not found)
       - Emit row: customer_id, first_name, last_name, as_of, visit_count
```

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1 | LINQ GroupBy customer_id produces one row per customer with visits |
| BR-2 | Count() on each group gives visit_count |
| BR-3 | Customer name lookup with NULL fallback when customer not found |
| BR-4 | as_of from first row of branch_visits DataFrame |
| BR-5 | Empty-check guard for both DataFrames |
| BR-6 | DataFrameWriter configured with writeMode: Append |
| BR-7 | Empty output on weekends (customers empty) |
