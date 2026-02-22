# BranchDirectory -- Functional Specification Document

## Design Approach

SQL-first. The original already uses a SQL Transformation, but with an unnecessarily complex CTE and ROW_NUMBER deduplication. Since the source data has no duplicate branch_ids per as_of date, the V2 simplifies to a plain SELECT with ORDER BY.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | N                   | N/A                | N/A |
| AP-2    | N                   | N/A                | N/A |
| AP-3    | N                   | N/A                | Already uses SQL Transformation |
| AP-4    | N                   | N/A                | All sourced columns are used in output |
| AP-5    | N                   | N/A                | N/A |
| AP-6    | N                   | N/A                | N/A |
| AP-7    | N                   | N/A                | N/A |
| AP-8    | Y                   | Y                  | Removed unnecessary CTE with ROW_NUMBER deduplication; replaced with simple SELECT since no duplicate branch_ids exist |
| AP-9    | N                   | N/A                | N/A |
| AP-10   | N                   | N/A                | N/A |

## V2 Pipeline Design

1. **DataSourcing** (`branches`): Read from `datalake.branches` with all 7 columns: branch_id, branch_name, address_line1, city, state_province, postal_code, country. The framework automatically appends as_of.

2. **Transformation** (`branch_dir`): Simple SELECT of all 8 columns (7 sourced + as_of) ordered by branch_id.

3. **DataFrameWriter**: Write `branch_dir` to `branch_directory` in `double_secret_curated` schema with Overwrite mode.

## SQL Transformation Logic

```sql
SELECT
    branch_id,
    branch_name,
    address_line1,
    city,
    state_province,
    postal_code,
    country,
    as_of
FROM branches
ORDER BY branch_id
```

The original SQL wrapped this in a CTE with `ROW_NUMBER() OVER (PARTITION BY branch_id ORDER BY branch_id) AS rn` and filtered `WHERE rn = 1`. Since datalake.branches has no duplicate branch_ids per as_of date (verified: `SELECT branch_id, COUNT(*) ... HAVING COUNT(*) > 1` returns 0 rows), the ROW_NUMBER dedup is unnecessary and produces identical output to a simple SELECT.

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1            | No WHERE clause; all branches included |
| BR-2            | Original ROW_NUMBER dedup removed since no duplicates exist; output is identical |
| BR-3            | SELECT lists exactly 8 columns matching output schema |
| BR-4            | ORDER BY branch_id produces ascending order |
| BR-5            | DataFrameWriter writeMode is "Overwrite" |
