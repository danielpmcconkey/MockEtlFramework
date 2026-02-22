# CustomerContactInfo — Business Requirements Document

## Overview

Produces a unified contact information table by combining phone numbers and email addresses for each customer. Each row represents one contact method (phone or email) with its type and value. Output is appended daily.

## Source Tables

| Table | Schema | Columns Used | Purpose |
|-------|--------|-------------|---------|
| `datalake.phone_numbers` | datalake | phone_id, customer_id, phone_type, phone_number | Source phone contact records |
| `datalake.email_addresses` | datalake | email_id, customer_id, email_address, email_type | Source email contact records |
| `datalake.segments` | datalake | segment_id, segment_name | **SOURCED BUT NEVER USED** — not referenced in the Transformation SQL |

## Business Rules

BR-1: Phone numbers and email addresses are combined into a single output via UNION ALL.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_contact_info.json:29] SQL uses `UNION ALL` to combine phone and email records

BR-2: Phone records are mapped as: contact_type = 'Phone', contact_subtype = phone_type, contact_value = phone_number.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_contact_info.json:29] SQL: `SELECT customer_id, 'Phone' AS contact_type, phone_type AS contact_subtype, phone_number AS contact_value, as_of FROM phone_numbers`

BR-3: Email records are mapped as: contact_type = 'Email', contact_subtype = email_type, contact_value = email_address.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_contact_info.json:29] SQL: `SELECT customer_id, 'Email' AS contact_type, email_type AS contact_subtype, email_address AS contact_value, as_of FROM email_addresses`

BR-4: Output is ordered by customer_id, contact_type, contact_subtype.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_contact_info.json:29] `ORDER BY customer_id, contact_type, contact_subtype`
- Evidence: [curated.customer_contact_info] Data is ordered by customer_id, then Email before Phone (alphabetically), then subtypes

BR-5: Output uses Append mode — each daily run appends rows.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_contact_info.json:33] `"writeMode": "Append"`

BR-6: All phone and email records are included — no filtering applied.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_contact_info.json:29] No WHERE clause in either branch of the UNION ALL

BR-7: The UNION ALL preserves duplicate records (if any exist). No deduplication is performed.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_contact_info.json:29] `UNION ALL` (not UNION) preserves all rows including duplicates

## Output Schema

| Column | Source | Transformation |
|--------|--------|----------------|
| customer_id | phone_numbers.customer_id / email_addresses.customer_id | Direct |
| contact_type | Computed | 'Phone' or 'Email' (string literal) |
| contact_subtype | phone_numbers.phone_type / email_addresses.email_type | Direct |
| contact_value | phone_numbers.phone_number / email_addresses.email_address | Direct |
| as_of | Framework-injected effective date | Via DataSourcing |

## Edge Cases

- **Weekend data**: Both phone_numbers and email_addresses have data every day including weekends (verified: 429 and 321 rows for Oct 5-6). So output is produced daily.
- **Consistent row count**: 750 rows per day (429 phones + 321 emails) for all observed dates.
- **No filtering**: Every phone and email record is included; no NULL checks or status filters.

## Anti-Patterns Identified

- **AP-1: Redundant Data Sourcing** — The `segments` table is sourced via DataSourcing (segment_id, segment_name) but never referenced in the Transformation SQL. The SQL only references `phone_numbers` and `email_addresses`. Evidence: [JobExecutor/Jobs/customer_contact_info.json:20-24] segments sourced; [line 29] SQL does not reference segments. V2 approach: Remove the segments DataSourcing module.

- **AP-4: Unused Columns Sourced** — From phone_numbers: `phone_id` is sourced but not in the output. From email_addresses: `email_id` is sourced but not in the output. Evidence: [JobExecutor/Jobs/customer_contact_info.json:11,17] include phone_id and email_id; [line 29] SQL does not SELECT these columns. V2 approach: Remove phone_id and email_id from DataSourcing columns.

- **AP-8: Overly Complex SQL** — The SQL wraps the UNION ALL in a CTE (`WITH all_contacts AS (...)`) and then simply `SELECT ... FROM all_contacts`. The CTE adds no value — the UNION ALL can be the main query directly with an ORDER BY. Evidence: [JobExecutor/Jobs/customer_contact_info.json:29] CTE wraps a straightforward UNION ALL. V2 approach: Use UNION ALL directly with ORDER BY, no CTE.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [JobExecutor/Jobs/customer_contact_info.json:29] |
| BR-2 | [JobExecutor/Jobs/customer_contact_info.json:29] |
| BR-3 | [JobExecutor/Jobs/customer_contact_info.json:29] |
| BR-4 | [JobExecutor/Jobs/customer_contact_info.json:29] |
| BR-5 | [JobExecutor/Jobs/customer_contact_info.json:33] |
| BR-6 | [JobExecutor/Jobs/customer_contact_info.json:29] |
| BR-7 | [JobExecutor/Jobs/customer_contact_info.json:29] |

## Open Questions

None. This job is straightforward UNION ALL with clear mapping.
