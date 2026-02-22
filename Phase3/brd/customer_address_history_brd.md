# BRD: CustomerAddressHistory

## Overview
This job produces a historical log of customer addresses for each effective date, filtering out addresses with null customer_id. It appends a daily snapshot of customer addresses to the curated table, building an accumulating history over time.

## Source Tables

| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| addresses | datalake | address_id, customer_id, address_line1, city, state_province, postal_code, country | Sourced via DataSourcing for effective date range | [customer_address_history.json:7-12] |
| branches | datalake | branch_id, branch_name | Sourced via DataSourcing but NOT USED in the SQL transformation | [customer_address_history.json:14-18] |

## Business Rules

BR-1: Only addresses with a non-null customer_id are included.
- Confidence: HIGH
- Evidence: [customer_address_history.json:22] SQL contains `WHERE a.customer_id IS NOT NULL`

BR-2: The output includes address fields from the addresses table and the as_of date for temporal tracking.
- Confidence: HIGH
- Evidence: [customer_address_history.json:22] SQL selects: `customer_id, address_line1, city, state_province, postal_code, country, as_of`

BR-3: Output is ordered by customer_id ascending.
- Confidence: HIGH
- Evidence: [customer_address_history.json:22] SQL contains `ORDER BY sub.customer_id`

BR-4: Data is written in Append mode -- each date's address snapshot accumulates in the curated table.
- Confidence: HIGH
- Evidence: [customer_address_history.json:28] `"writeMode": "Append"`
- Evidence: [curated.customer_address_history] Has rows for all 31 dates with counts matching source

BR-5: The branches DataSourcing module is declared in the job config but NOT used in the SQL transformation.
- Confidence: HIGH
- Evidence: [customer_address_history.json:14-18] branches is sourced; SQL at line 22 only references `addresses` alias `a` -- no reference to branches

BR-6: The SQL uses a subquery pattern (SELECT from subquery) but the subquery does not add any filtering beyond customer_id IS NOT NULL.
- Confidence: HIGH
- Evidence: [customer_address_history.json:22] `SELECT sub.* FROM (SELECT a.* FROM addresses a WHERE a.customer_id IS NOT NULL) sub ORDER BY sub.customer_id`

BR-7: The address_id column is sourced but NOT included in the output -- only customer_id and address fields are output.
- Confidence: HIGH
- Evidence: [customer_address_history.json:22] The SQL selects `a.customer_id, a.address_line1, a.city, a.state_province, a.postal_code, a.country, a.as_of` -- no address_id
- Evidence: [curated.customer_address_history schema] Columns are: customer_id, address_line1, city, state_province, postal_code, country, as_of -- no address_id

BR-8: This is a Transformation-based pipeline (no External module) using the framework's SQLite SQL engine.
- Confidence: HIGH
- Evidence: [customer_address_history.json:20-23] Module type is "Transformation" with inline SQL

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | addresses.customer_id | Pass-through (NOT NULL filtered) | [customer_address_history.json:22] |
| address_line1 | addresses.address_line1 | Pass-through | [customer_address_history.json:22] |
| city | addresses.city | Pass-through | [customer_address_history.json:22] |
| state_province | addresses.state_province | Pass-through | [customer_address_history.json:22] |
| postal_code | addresses.postal_code | Pass-through | [customer_address_history.json:22] |
| country | addresses.country | Pass-through | [customer_address_history.json:22] |
| as_of | addresses.as_of (from DataSourcing) | Pass-through | [customer_address_history.json:22] |

## Edge Cases

- **NULL handling**: Addresses with null customer_id are explicitly excluded. Other null fields (address_line1, city, etc.) pass through without filtering.
  - Evidence: [customer_address_history.json:22] `WHERE a.customer_id IS NOT NULL`
- **Weekend/date fallback**: Addresses have data for all 31 days including weekends. Output row counts match source row counts (223-225 per date).
  - Evidence: [datalake.addresses] All 31 dates; [curated.customer_address_history] Matching row counts per date
- **Zero-row behavior**: If no addresses exist for a date (or all have null customer_id), the Transformation produces an empty DataFrame. In Append mode, no rows are written for that date.
  - Evidence: Framework behavior for empty Transformation results
- **Branches not used**: The branches table is loaded into shared state but the SQL transformation never references it. It occupies memory but has no effect on output.
  - Evidence: [customer_address_history.json:14-18,22]

## Traceability Matrix

| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [customer_address_history.json:22] |
| BR-2 | [customer_address_history.json:22] |
| BR-3 | [customer_address_history.json:22] |
| BR-4 | [customer_address_history.json:28], [curated.customer_address_history row counts] |
| BR-5 | [customer_address_history.json:14-18,22] |
| BR-6 | [customer_address_history.json:22] |
| BR-7 | [customer_address_history.json:22], [curated.customer_address_history schema] |
| BR-8 | [customer_address_history.json:20-23] |

## Open Questions

- **Branches sourced but unused**: Same pattern as multiple other jobs. Confidence: HIGH that unused.
- **Subquery pattern**: The SQL wraps the query in a subquery for no apparent reason -- `SELECT sub.* FROM (...) sub` could be simplified. This may be a coding style preference or artifact. Confidence: HIGH (no functional impact).
- **No deduplication**: If multiple address records exist for the same customer_id on the same as_of, all are included. This is appropriate for an address history table. Confidence: HIGH.
