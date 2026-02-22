# CustomerAddressHistory — Business Requirements Document

## Overview

Produces a historical record of customer addresses by selecting address records for each effective date, filtering out rows where customer_id is NULL, and appending daily. Output captures the address state for each customer on each date.

## Source Tables

| Table | Schema | Columns Used | Purpose |
|-------|--------|-------------|---------|
| `datalake.addresses` | datalake | address_id, customer_id, address_line1, city, state_province, postal_code, country | Source address records |
| `datalake.branches` | datalake | branch_id, branch_name | **SOURCED BUT NEVER USED** — not referenced in the Transformation SQL |

## Business Rules

BR-1: Address records are selected from the datalake for the effective date, filtering out rows where customer_id IS NULL.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_address_history.json:22] SQL: `WHERE a.customer_id IS NOT NULL`
- Evidence: [curated.customer_address_history] All rows have non-null customer_id

BR-2: Output columns are: customer_id, address_line1, city, state_province, postal_code, country, as_of.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_address_history.json:22] SQL SELECT clause lists these exact columns
- Evidence: [curated.customer_address_history] Schema matches

BR-3: Output is ordered by customer_id ascending.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_address_history.json:22] `ORDER BY sub.customer_id`

BR-4: Output uses Append mode — each daily run appends its rows.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_address_history.json:27] `"writeMode": "Append"`

BR-5: The address_id column is NOT included in the output despite being sourced.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_address_history.json:12] address_id in DataSourcing columns
- Evidence: [JobExecutor/Jobs/customer_address_history.json:22] SQL SELECT does not include address_id
- Evidence: [curated.customer_address_history] No address_id column in output

BR-6: Addresses are present every day including weekends (datalake.addresses has data for all dates).
- Confidence: HIGH
- Evidence: [datalake.addresses] Row counts present for all dates Oct 1-31 including weekends

## Output Schema

| Column | Source | Transformation |
|--------|--------|----------------|
| customer_id | addresses.customer_id | Direct |
| address_line1 | addresses.address_line1 | Direct |
| city | addresses.city | Direct |
| state_province | addresses.state_province | Direct |
| postal_code | addresses.postal_code | Direct |
| country | addresses.country | Direct |
| as_of | Framework-injected effective date | Via DataSourcing |

## Edge Cases

- **NULL customer_id**: Rows are filtered out (BR-1). In practice, customer_id is NOT NULL in the addresses table schema, so this filter has no effect on current data.
- **Addresses with data every day**: Unlike tables that skip weekends (customers, accounts), addresses have data for all dates, so this job produces output for every day including weekends.
- **Row count growth**: Oct 1 has 223 rows, Oct 2 onwards has 224-225 as new addresses appear.

## Anti-Patterns Identified

- **AP-1: Redundant Data Sourcing** — The `branches` table is sourced via DataSourcing (branch_id, branch_name) but is never referenced in the Transformation SQL. The SQL only references the `addresses` table. Evidence: [JobExecutor/Jobs/customer_address_history.json:16-19] branches sourced; [line 22] SQL only uses `addresses a`. V2 approach: Remove the branches DataSourcing module entirely.

- **AP-4: Unused Columns Sourced** — The `address_id` column is sourced from addresses but not included in the output SQL. Evidence: [JobExecutor/Jobs/customer_address_history.json:12] includes address_id; [line 22] SQL does not SELECT address_id. V2 approach: Remove address_id from DataSourcing columns.

- **AP-8: Overly Complex SQL** — The SQL wraps a simple query in an unnecessary subquery: `SELECT sub.* FROM (SELECT ... FROM addresses a WHERE ...) sub ORDER BY sub.customer_id`. The subquery adds no value — the same result is achieved with `SELECT ... FROM addresses a WHERE ... ORDER BY a.customer_id`. Evidence: [JobExecutor/Jobs/customer_address_history.json:22] Unnecessary subquery wrapper. V2 approach: Simplify to a direct SELECT with WHERE and ORDER BY.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [JobExecutor/Jobs/customer_address_history.json:22] |
| BR-2 | [JobExecutor/Jobs/customer_address_history.json:22], [curated.customer_address_history schema] |
| BR-3 | [JobExecutor/Jobs/customer_address_history.json:22] |
| BR-4 | [JobExecutor/Jobs/customer_address_history.json:27] |
| BR-5 | [JobExecutor/Jobs/customer_address_history.json:12,22] |
| BR-6 | [datalake.addresses row counts per as_of] |

## Open Questions

- **NULL customer_id filter**: The datalake.addresses table has `customer_id INTEGER NOT NULL` constraint, making the `WHERE a.customer_id IS NOT NULL` filter redundant for current data. It may be a defensive measure. Confidence: HIGH that the filter is functionally redundant but harmless.
