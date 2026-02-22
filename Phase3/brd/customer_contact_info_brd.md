# BRD: CustomerContactInfo

## Overview
This job consolidates phone numbers and email addresses into a unified contact information table, normalizing both contact types into a common schema with contact_type, contact_subtype, and contact_value columns. It produces an accumulating daily snapshot of all customer contact information.

## Source Tables

| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| phone_numbers | datalake | phone_id, customer_id, phone_type, phone_number | Sourced via DataSourcing for effective date range | [customer_contact_info.json:7-11] |
| email_addresses | datalake | email_id, customer_id, email_address, email_type | Sourced via DataSourcing for effective date range | [customer_contact_info.json:13-18] |
| segments | datalake | segment_id, segment_name | Sourced via DataSourcing but NOT USED in the SQL transformation | [customer_contact_info.json:20-24] |

## Business Rules

BR-1: Phone numbers are mapped to contact records with contact_type = 'Phone', contact_subtype = phone_type, contact_value = phone_number.
- Confidence: HIGH
- Evidence: [customer_contact_info.json:29] SQL: `SELECT customer_id, 'Phone' AS contact_type, phone_type AS contact_subtype, phone_number AS contact_value, as_of FROM phone_numbers`

BR-2: Email addresses are mapped to contact records with contact_type = 'Email', contact_subtype = email_type, contact_value = email_address.
- Confidence: HIGH
- Evidence: [customer_contact_info.json:29] SQL: `SELECT customer_id, 'Email' AS contact_type, email_type AS contact_subtype, email_address AS contact_value, as_of FROM email_addresses`

BR-3: Phone and email records are combined using UNION ALL (preserving duplicates).
- Confidence: HIGH
- Evidence: [customer_contact_info.json:29] SQL: `UNION ALL` between the phone and email SELECT statements

BR-4: Output is ordered by customer_id, then contact_type, then contact_subtype.
- Confidence: HIGH
- Evidence: [customer_contact_info.json:29] SQL: `ORDER BY customer_id, contact_type, contact_subtype`

BR-5: Data is written in Append mode -- each effective date's contact snapshot accumulates.
- Confidence: HIGH
- Evidence: [customer_contact_info.json:35] `"writeMode": "Append"`
- Evidence: [curated.customer_contact_info] 750 rows per date across all 31 dates

BR-6: The segments DataSourcing module is declared in the job config but NOT used in the SQL transformation.
- Confidence: HIGH
- Evidence: [customer_contact_info.json:20-24] segments is sourced; SQL at line 29 only references phone_numbers and email_addresses

BR-7: This is a Transformation-based pipeline (no External module) using the framework's SQLite SQL engine.
- Confidence: HIGH
- Evidence: [customer_contact_info.json:26-30] Module type is "Transformation"

BR-8: The phone_id and email_id columns are sourced from the datalake but NOT included in the output.
- Confidence: HIGH
- Evidence: [customer_contact_info.json:8,15] phone_id and email_id are in DataSourcing columns
- Evidence: [customer_contact_info.json:29] SQL does not select phone_id or email_id
- Evidence: [curated.customer_contact_info schema] No phone_id or email_id columns

BR-9: All phone and email records for the date are included without any filtering (no NULL checks, no status filters).
- Confidence: HIGH
- Evidence: [customer_contact_info.json:29] SQL has no WHERE clause

BR-10: Contact data has entries for all 31 days including weekends.
- Confidence: HIGH
- Evidence: [datalake.phone_numbers] All 31 dates; [datalake.email_addresses] All 31 dates (inferred from customer_contact_info output having 31 dates)
- Evidence: [curated.customer_contact_info] 750 rows per date for all dates

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | phone_numbers.customer_id / email_addresses.customer_id | Pass-through | [customer_contact_info.json:29] |
| contact_type | Computed | 'Phone' for phone records, 'Email' for email records | [customer_contact_info.json:29] |
| contact_subtype | phone_numbers.phone_type / email_addresses.email_type | Mapped from type fields | [customer_contact_info.json:29] |
| contact_value | phone_numbers.phone_number / email_addresses.email_address | Mapped from value fields | [customer_contact_info.json:29] |
| as_of | DataSourcing injected | Pass-through from source tables | [customer_contact_info.json:29] |

## Edge Cases

- **NULL handling**: No explicit NULL filtering. If customer_id, phone_type, email_type, phone_number, or email_address is null, the null passes through to the output.
  - Evidence: [customer_contact_info.json:29] No WHERE clause or NULL checks
- **Weekend/date fallback**: Both phone_numbers and email_addresses have data for all 31 days including weekends, so output is produced every day.
  - Evidence: [datalake.phone_numbers] 31 dates; [curated.customer_contact_info] 750 rows per date
- **Zero-row behavior**: If no phone or email records exist for a date, the UNION ALL produces empty output. In Append mode, no rows are written.
  - Evidence: Framework behavior for empty Transformation results
- **Duplicate contacts**: UNION ALL is used, so if a customer has the same contact info in both tables (unlikely but possible), both records appear. Within a single table, duplicate records also pass through.
  - Evidence: [customer_contact_info.json:29] `UNION ALL` (not UNION)

## Traceability Matrix

| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [customer_contact_info.json:29] |
| BR-2 | [customer_contact_info.json:29] |
| BR-3 | [customer_contact_info.json:29] |
| BR-4 | [customer_contact_info.json:29] |
| BR-5 | [customer_contact_info.json:35], [curated.customer_contact_info row counts] |
| BR-6 | [customer_contact_info.json:20-24,29] |
| BR-7 | [customer_contact_info.json:26-30] |
| BR-8 | [customer_contact_info.json:8,15,29], [curated.customer_contact_info schema] |
| BR-9 | [customer_contact_info.json:29] |
| BR-10 | [datalake.phone_numbers dates], [curated.customer_contact_info dates] |

## Open Questions

- **Segments sourced but unused**: Same pattern as other jobs. The segments table is loaded into shared state by DataSourcing but the SQL transformation never references it. Confidence: HIGH that unused.
- **No deduplication**: UNION ALL preserves potential duplicates. This appears intentional for a contact log/snapshot. Confidence: HIGH.
- **phone_id and email_id sourced but dropped**: These IDs are fetched from the datalake but not carried into the output. This may indicate they were once used or are needed for DataSourcing but not for the business output. Confidence: HIGH on behavior; MEDIUM on intent.
