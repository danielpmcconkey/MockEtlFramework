# CustomerContactInfo -- Business Requirements Document

## Overview
Produces a unified contact information dataset by combining phone numbers and email addresses into a single denormalized structure. Each row represents one contact method for a customer, tagged by type (Phone/Email) and subtype (Mobile/Home/Work or Personal/Work). Output is Parquet in append mode, building up a historical record across effective dates.

## Output Type
ParquetFileWriter

## Writer Configuration
- **outputDirectory:** `Output/curated/customer_contact_info/`
- **numParts:** 2
- **writeMode:** Append

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.phone_numbers | phone_id, customer_id, phone_type, phone_number | None | [customer_contact_info.json:8-11] |
| datalake.email_addresses | email_id, customer_id, email_address, email_type | None | [customer_contact_info.json:14-17] |
| datalake.segments | segment_id, segment_name | Sourced but NEVER used in transformation SQL (dead-end data source) | [customer_contact_info.json:20-22] |

## Business Rules

BR-1: Phone records and email records are combined via UNION ALL into a single result set. Phone records get contact_type = 'Phone' and email records get contact_type = 'Email'.
- Confidence: HIGH
- Evidence: [customer_contact_info.json:26-29] -- SQL uses UNION ALL with hardcoded 'Phone' and 'Email' literals

BR-2: The phone_type column maps to contact_subtype for phone records (values: Mobile, Home, Work). The email_type column maps to contact_subtype for email records (values: Personal, Work).
- Confidence: HIGH
- Evidence: [customer_contact_info.json:26-29] -- `phone_type AS contact_subtype` and `email_type AS contact_subtype`; DB query shows distinct phone_type = {Mobile, Home, Work}, email_type = {Personal, Work}

BR-3: The phone_number or email_address maps to contact_value in the output.
- Confidence: HIGH
- Evidence: [customer_contact_info.json:26-29] -- `phone_number AS contact_value` and `email_address AS contact_value`

BR-4: Output is ordered by customer_id, contact_type, contact_subtype.
- Confidence: HIGH
- Evidence: [customer_contact_info.json:29] -- `ORDER BY customer_id, contact_type, contact_subtype`

BR-5: The segments table is sourced but never referenced in the SQL transformation. It is a dead-end data source that wastes a database query.
- Confidence: HIGH
- Evidence: [customer_contact_info.json:20-22] sources segments; the SQL at line 26-29 only references phone_numbers and email_addresses

BR-6: The as_of column is included in the output, carried through from the source tables.
- Confidence: HIGH
- Evidence: [customer_contact_info.json:26-29] -- `as_of` is selected in both halves of the UNION ALL

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | phone_numbers.customer_id / email_addresses.customer_id | Direct pass-through | [customer_contact_info.json:26-29] |
| contact_type | Literal 'Phone' or 'Email' | Hardcoded based on source table | [customer_contact_info.json:26-29] |
| contact_subtype | phone_numbers.phone_type / email_addresses.email_type | Aliased as contact_subtype | [customer_contact_info.json:26-29] |
| contact_value | phone_numbers.phone_number / email_addresses.email_address | Aliased as contact_value | [customer_contact_info.json:26-29] |
| as_of | phone_numbers.as_of / email_addresses.as_of | Direct pass-through | [customer_contact_info.json:26-29] |

## Non-Deterministic Fields
None identified. The output is deterministic given the ORDER BY clause and the data. Row order within tied sort keys may vary but Parquet is unordered by nature.

## Write Mode Implications
WriteMode is **Append**. Each execution adds new part files to the output directory without removing prior ones. Over multi-day auto-advance runs, each effective date's data is appended, building a cumulative historical record. This means duplicate data will accumulate if the job is re-run for the same effective date range. The numParts=2 setting means each run produces two part files.

## Edge Cases

1. **No phone or email data**: If both source tables are empty, the UNION ALL produces zero rows, and an empty Parquet file is written.

2. **Customer with only phone or only email**: The UNION ALL ensures partial records are included -- a customer with only a phone number still appears with contact_type='Phone'.

3. **Multiple contact methods per customer**: A customer with 3 phone numbers and 2 emails produces 5 rows in the output.

4. **Segments table wasted**: The segments DataFrame is loaded into shared state and registered as a SQLite table but never queried. No functional impact, just unnecessary I/O.
   - Evidence: [customer_contact_info.json:20-22]

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| UNION ALL combines phone and email | [customer_contact_info.json:26-29] |
| contact_type literals Phone/Email | [customer_contact_info.json:26-29] |
| ORDER BY customer_id, contact_type, contact_subtype | [customer_contact_info.json:29] |
| Segments sourced but unused | [customer_contact_info.json:20-22] vs SQL at line 26-29 |
| Append write mode | [customer_contact_info.json:36] |
| 2 part files | [customer_contact_info.json:35] |

## Open Questions

1. **Why is the segments table sourced?** It is loaded but never used in the transformation SQL. Possible leftover from a prior design or intended for future use.
   - Confidence: HIGH -- the SQL clearly does not reference it
