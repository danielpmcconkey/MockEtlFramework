# BranchVisitLog -- Functional Specification Document

## Design Approach

SQL-first. The original External module (BranchVisitEnricher) performs two dictionary-based lookups to enrich branch visits with branch names and customer names. This is expressible as SQL LEFT JOINs. Special care is needed to replicate the weekend behavior: the original returns empty output when the customers DataFrame is empty (weekends), which is achieved in SQL via a `WHERE EXISTS (SELECT 1 FROM customers)` guard.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed unused `addresses` DataSourcing module entirely |
| AP-2    | N                   | N/A                | N/A |
| AP-3    | Y                   | Y                  | Replaced External module with SQL Transformation (LEFT JOINs) |
| AP-4    | Y                   | Y                  | Removed unused columns from branches (address_line1, city, state_province, postal_code, country) |
| AP-5    | Y                   | N                  | Asymmetric NULL handling preserved for output equivalence: branch_name defaults to empty string on miss, customer names default to NULL on miss. Documented as known inconsistency. |
| AP-6    | Y                   | Y                  | Replaced row-by-row dictionary lookups with SQL LEFT JOINs |
| AP-7    | N                   | N/A                | N/A |
| AP-8    | N                   | N/A                | N/A |
| AP-9    | N                   | N/A                | N/A |
| AP-10   | N                   | N/A                | N/A |

## V2 Pipeline Design

1. **DataSourcing** (`branch_visits`): Read from `datalake.branch_visits` with all 5 columns: visit_id, customer_id, branch_id, visit_timestamp, visit_purpose.

2. **DataSourcing** (`branches`): Read from `datalake.branches` with only 2 columns actually used: branch_id, branch_name.

3. **DataSourcing** (`customers`): Read from `datalake.customers` with columns: id, first_name, last_name.

4. **Transformation** (`visit_log_result`): LEFT JOIN branch_visits with branches (on branch_id + as_of) and LEFT JOIN with customers (on customer_id = id + as_of). COALESCE branch_name to empty string for missing branches. Leave customer names as NULL for missing customers. Include `WHERE EXISTS (SELECT 1 FROM customers)` guard to produce zero rows when customers has no data (weekends).

5. **DataFrameWriter**: Write `visit_log_result` to `branch_visit_log` in `double_secret_curated` schema with Append mode.

## SQL Transformation Logic

```sql
SELECT
    bv.visit_id,
    bv.customer_id,
    c.first_name,
    c.last_name,
    bv.branch_id,
    COALESCE(b.branch_name, '') AS branch_name,
    bv.visit_timestamp,
    bv.visit_purpose,
    bv.as_of
FROM branch_visits bv
LEFT JOIN branches b ON bv.branch_id = b.branch_id AND bv.as_of = b.as_of
LEFT JOIN customers c ON bv.customer_id = c.id AND bv.as_of = c.as_of
WHERE EXISTS (SELECT 1 FROM customers)
```

### Key design decisions:

1. **Weekend guard**: `WHERE EXISTS (SELECT 1 FROM customers)` ensures zero output when customers DataFrame is empty. This replicates the original External module's behavior where it checks `customers == null || customers.Count == 0` first and returns empty.

2. **Asymmetric NULL handling (AP-5)**: Branch names use `COALESCE(b.branch_name, '')` to default to empty string on LEFT JOIN miss. Customer names (`c.first_name`, `c.last_name`) are left as NULL on LEFT JOIN miss. This matches the original: `branchNames.GetValueOrDefault(branchId, "")` vs `customerNames.GetValueOrDefault(customerId, (null!, null!))`.

3. **Customer name lookup**: The original builds the customer name dictionary with `?.ToString() ?? ""` which converts NULL database values to empty strings. However, datalake.customers has NOT NULL constraints on first_name and last_name, so the defensive conversion never triggers. The LEFT JOIN naturally returns the actual column values (never NULL since the column itself is NOT NULL). On a LEFT JOIN miss (no matching customer), the result is NULL, which matches the original `(null!, null!)` default.

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1            | LEFT JOIN with branches on branch_id + as_of; COALESCE to empty string |
| BR-2            | LEFT JOIN with customers on customer_id = id + as_of; names from customer table |
| BR-3            | COALESCE(b.branch_name, '') defaults to empty string for missing branches |
| BR-4            | Customer names left as NULL on LEFT JOIN miss (no COALESCE) |
| BR-5            | SELECT lists exactly 9 columns in correct order |
| BR-6            | DataFrameWriter writeMode is "Append" |
| BR-7            | WHERE EXISTS (SELECT 1 FROM customers) produces zero rows when customers is empty |
| BR-8            | Same WHERE EXISTS guard handles both cases |
| BR-9            | No WHERE filter on visits; all visits included (subject to EXISTS guard) |
