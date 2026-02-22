# CustomerAddressHistory -- Functional Specification Document

## Design Approach

**SQL-first.** The original job already uses a Transformation module (no External module). The V2 simplifies the SQL by removing the unnecessary subquery wrapper, removes the unused `branches` DataSourcing module, and removes the unused `address_id` column from the DataSourcing configuration.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed unused `branches` DataSourcing module |
| AP-2    | N                   | N/A                | No duplicated logic |
| AP-3    | N                   | N/A                | Original already uses SQL Transformation |
| AP-4    | Y                   | Y                  | Removed `address_id` from DataSourcing columns (not in output) |
| AP-5    | N                   | N/A                | No NULL/default asymmetry |
| AP-6    | N                   | N/A                | No row-by-row iteration |
| AP-7    | N                   | N/A                | No magic values |
| AP-8    | Y                   | Y                  | Removed unnecessary subquery wrapper; direct SELECT with WHERE and ORDER BY |
| AP-9    | N                   | N/A                | Name accurately describes the job |
| AP-10   | N                   | N/A                | No inter-job dependencies needed |

## V2 Pipeline Design

1. **DataSourcing** - `addresses` from `datalake.addresses` with columns: `customer_id`, `address_line1`, `city`, `state_province`, `postal_code`, `country`
2. **Transformation** - Direct SELECT with WHERE and ORDER BY (no subquery)
3. **DataFrameWriter** - Write to `customer_address_history` in `double_secret_curated` schema, Append mode

## SQL Transformation Logic

```sql
SELECT a.customer_id, a.address_line1, a.city, a.state_province, a.postal_code, a.country, a.as_of
FROM addresses a
WHERE a.customer_id IS NOT NULL
ORDER BY a.customer_id
```

This eliminates the unnecessary subquery from the original:
`SELECT sub.* FROM (SELECT ... FROM addresses a WHERE ...) sub ORDER BY sub.customer_id`

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1 | WHERE clause filters out NULL customer_id rows |
| BR-2 | SELECT clause specifies exact output columns |
| BR-3 | ORDER BY customer_id provides ascending sort |
| BR-4 | DataFrameWriter configured with writeMode: Append |
| BR-5 | address_id removed from DataSourcing columns (not in output) |
| BR-6 | DataSourcing fetches addresses for effective date (data exists every day) |
