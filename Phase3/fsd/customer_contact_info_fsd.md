# CustomerContactInfo -- Functional Specification Document

## Design Approach

**SQL-first.** The original job already uses a Transformation module with a UNION ALL query. The V2 removes the unnecessary CTE wrapper, removes the unused `segments` DataSourcing module, and removes unused columns (`phone_id`, `email_id`) from the DataSourcing configurations.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed unused `segments` DataSourcing module |
| AP-2    | N                   | N/A                | No duplicated logic |
| AP-3    | N                   | N/A                | Original already uses SQL Transformation |
| AP-4    | Y                   | Y                  | Removed `phone_id` and `email_id` from DataSourcing columns |
| AP-5    | N                   | N/A                | No NULL/default asymmetry |
| AP-6    | N                   | N/A                | No row-by-row iteration |
| AP-7    | N                   | N/A                | No magic values (string literals 'Phone'/'Email' are descriptive, not magic) |
| AP-8    | Y                   | Y                  | Removed unnecessary CTE wrapper; UNION ALL with ORDER BY directly |
| AP-9    | N                   | N/A                | Name accurately describes the job |
| AP-10   | N                   | N/A                | No inter-job dependencies needed |

## V2 Pipeline Design

1. **DataSourcing** - `phone_numbers` from `datalake.phone_numbers` with columns: `customer_id`, `phone_type`, `phone_number`
2. **DataSourcing** - `email_addresses` from `datalake.email_addresses` with columns: `customer_id`, `email_address`, `email_type`
3. **Transformation** - UNION ALL of phone and email records with ORDER BY
4. **DataFrameWriter** - Write to `customer_contact_info` in `double_secret_curated` schema, Append mode

## SQL Transformation Logic

```sql
SELECT customer_id, 'Phone' AS contact_type, phone_type AS contact_subtype, phone_number AS contact_value, as_of
FROM phone_numbers
UNION ALL
SELECT customer_id, 'Email' AS contact_type, email_type AS contact_subtype, email_address AS contact_value, as_of
FROM email_addresses
ORDER BY customer_id, contact_type, contact_subtype
```

This eliminates the unnecessary CTE from the original:
`WITH all_contacts AS (...) SELECT ... FROM all_contacts ORDER BY ...`

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1 | UNION ALL combines phone and email records |
| BR-2 | Phone SELECT maps phone_type to contact_subtype, phone_number to contact_value, literal 'Phone' to contact_type |
| BR-3 | Email SELECT maps email_type to contact_subtype, email_address to contact_value, literal 'Email' to contact_type |
| BR-4 | ORDER BY customer_id, contact_type, contact_subtype |
| BR-5 | DataFrameWriter configured with writeMode: Append |
| BR-6 | No WHERE clause -- all records included |
| BR-7 | UNION ALL preserves all rows including duplicates |
