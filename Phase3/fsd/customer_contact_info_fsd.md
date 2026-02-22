# FSD: CustomerContactInfoV2

## Overview
CustomerContactInfoV2 replicates the exact Transformation-based logic of CustomerContactInfo, consolidating phone numbers and email addresses into a unified contact schema using UNION ALL. The V2 keeps the same DataSourcing and Transformation steps, replacing DataFrameWriter with a thin External writer that uses DscWriterUtil.

## Design Decisions
- **Pattern B (Transformation + V2 Writer)**: Original uses DataSourcing + Transformation + DataFrameWriter. V2 keeps DataSourcing and Transformation identical, replaces DataFrameWriter with External writer.
- **Write mode**: Append (overwrite=false) to match original.
- **Same SQL**: The Transformation SQL is kept identical to ensure behavioral equivalence.
- **Segments DataSourcing retained**: Original sources segments but SQL never references them. V2 retains this.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | phone_numbers: phone_id, customer_id, phone_type, phone_number |
| 2 | DataSourcing | email_addresses: email_id, customer_id, email_address, email_type |
| 3 | DataSourcing | segments: segment_id, segment_name |
| 4 | Transformation | SQL UNION ALL of phone and email records, ordered. Result: "contact_info" |
| 5 | External | CustomerContactInfoV2Writer - reads contact_info, writes to dsc |

## V2 External Module: CustomerContactInfoV2Writer
- File: ExternalModules/CustomerContactInfoV2Writer.cs
- Processing logic: Reads "contact_info" DataFrame from shared state, writes to dsc
- Output columns: customer_id, contact_type, contact_subtype, contact_value, as_of
- Target table: double_secret_curated.customer_contact_info
- Write mode: Append (overwrite=false)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 (Phone mapping) | SQL: 'Phone' AS contact_type, phone_type AS contact_subtype |
| BR-2 (Email mapping) | SQL: 'Email' AS contact_type, email_type AS contact_subtype |
| BR-3 (UNION ALL) | SQL UNION ALL preserves duplicates |
| BR-4 (Order by customer_id, type, subtype) | SQL ORDER BY clause |
| BR-5 (Append mode) | DscWriterUtil.Write with overwrite=false |
| BR-6 (Segments unused) | Segments DataSourcing retained, SQL doesn't reference |
| BR-7 (Transformation pipeline) | Same SQL as original |
| BR-8 (phone_id, email_id excluded) | SQL doesn't select these IDs |
| BR-9 (No filtering) | No WHERE clause in SQL |
| BR-10 (All 31 days) | Phone and email data available every day |
