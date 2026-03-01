# CustomerAddressHistory -- Business Requirements Document

## Overview
Produces a historical record of all customer addresses across the effective date range. Uses a SQL Transformation (not an External module) to select and order address data. The branches table is sourced but not used in the transformation. Output is Parquet with 2 part files in Append mode, accumulating address history over time.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `addr_history`
- **outputDirectory**: `Output/curated/customer_address_history/`
- **numParts**: 2
- **writeMode**: Append

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.addresses | address_id, customer_id, address_line1, city, state_province, postal_code, country | Effective date range (injected by executor); SQL filter: `customer_id IS NOT NULL` | [customer_address_history.json:8-11, 20-22] |
| datalake.branches | branch_id, branch_name | Effective date range (injected by executor) | [customer_address_history.json:14-17] |

## Business Rules

BR-1: Address records are filtered to exclude rows where `customer_id IS NULL` via the SQL transformation.
- Confidence: HIGH
- Evidence: [customer_address_history.json:22] -- SQL WHERE clause: `WHERE a.customer_id IS NOT NULL`.

BR-2: Output is ordered by `customer_id` ascending.
- Confidence: HIGH
- Evidence: [customer_address_history.json:22] -- SQL ORDER BY: `ORDER BY sub.customer_id`.

BR-3: The `as_of` column from the addresses table is included in the output, preserving the snapshot date for each address record.
- Confidence: HIGH
- Evidence: [customer_address_history.json:22] -- SQL SELECT includes `a.as_of`.

BR-4: The branches table is sourced but NOT referenced in the SQL transformation. It is loaded into shared state but unused.
- Confidence: HIGH
- Evidence: [customer_address_history.json:14-17,22] -- branches is sourced as "branches" but the SQL only references `addresses a`.

BR-5: The Transformation module registers all DataFrames in shared state as SQLite tables, then executes the SQL. The result is stored under the name "addr_history".
- Confidence: HIGH
- Evidence: [customer_address_history.json:20] -- `"resultName": "addr_history"`.

BR-6: The ParquetFileWriter reads from "addr_history" (not "output").
- Confidence: HIGH
- Evidence: [customer_address_history.json:25] -- `"source": "addr_history"`.

BR-7: address_id is NOT included in the output -- the SQL only selects customer_id, address_line1, city, state_province, postal_code, country, as_of.
- Confidence: HIGH
- Evidence: [customer_address_history.json:22] -- SELECT list does not include address_id.

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | addresses.customer_id | Pass-through, filtered NOT NULL | [customer_address_history.json:22] |
| address_line1 | addresses.address_line1 | Pass-through | [customer_address_history.json:22] |
| city | addresses.city | Pass-through | [customer_address_history.json:22] |
| state_province | addresses.state_province | Pass-through | [customer_address_history.json:22] |
| postal_code | addresses.postal_code | Pass-through | [customer_address_history.json:22] |
| country | addresses.country | Pass-through | [customer_address_history.json:22] |
| as_of | addresses.as_of | Pass-through | [customer_address_history.json:22] |

## Non-Deterministic Fields
None identified.

## Write Mode Implications
- **Append**: Each effective date's address records are appended to the existing Parquet output. Over multi-day auto-advance runs, the output accumulates the full address history. Combined with the as_of column, this produces a complete temporal record. Duplicate rows may appear if the same address data exists across multiple effective dates.

## Edge Cases
- **NULL customer_id**: Excluded by the SQL WHERE clause.
- **Branches table unused**: Loaded into shared state but not referenced in SQL.
- **Append mode accumulation**: Running across multiple effective dates accumulates address snapshots. The same address may appear multiple times with different as_of values.
- **address_id excluded**: The primary key of the addresses table is NOT in the output. Deduplication would need to use the composite of all output fields.
- **No External module**: This job uses only DataSourcing, Transformation, and ParquetFileWriter -- no custom C# logic.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| NULL customer_id filter | [customer_address_history.json:22] |
| ORDER BY customer_id | [customer_address_history.json:22] |
| as_of included in output | [customer_address_history.json:22] |
| Branches sourced but unused | [customer_address_history.json:14-17] |
| Writer reads from addr_history | [customer_address_history.json:25] |
| Append write mode | [customer_address_history.json:29] |
| 2 part files | [customer_address_history.json:28] |

## Open Questions
- OQ-1: The branches table is sourced but unused. This may be intentional (loaded for potential future use or framework artifact) or dead configuration. Confidence: MEDIUM.
- OQ-2: address_id is excluded from the output. Whether this is intentional (to focus on address content rather than identity) or an oversight is unclear. Confidence: LOW -- the SELECT is explicit.
