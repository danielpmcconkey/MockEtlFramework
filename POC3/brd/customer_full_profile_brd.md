# CustomerFullProfile — Business Requirements Document

## Overview
Assembles a comprehensive customer profile by joining customer demographics with their primary phone number, primary email address, and segment memberships. Computes age and age bracket from birthdate. Output is written to Parquet with Overwrite mode.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `output`
- **outputDirectory**: `Output/curated/customer_full_profile/`
- **numParts**: 2
- **writeMode**: Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.customers | id, first_name, last_name, birthdate | Effective date range (injected) | [customer_full_profile.json:8-10] |
| datalake.phone_numbers | phone_id, customer_id, phone_type, phone_number | Effective date range (injected) | [customer_full_profile.json:13-16] |
| datalake.email_addresses | email_id, customer_id, email_address, email_type | Effective date range (injected) | [customer_full_profile.json:19-22] |
| datalake.customers_segments | customer_id, segment_id | Effective date range (injected) | [customer_full_profile.json:25-27] |
| datalake.segments | segment_id, segment_name, segment_code | Effective date range (injected) | [customer_full_profile.json:30-32] |

## Business Rules

BR-1: Primary phone is the FIRST phone number encountered for each customer_id in the phone_numbers DataFrame. "First" is determined by DataFrame iteration order (not by phone_type or phone_id). Only one phone is kept per customer.
- Confidence: HIGH
- Evidence: [FullProfileAssembler.cs:31-41] — `if (!phoneByCustomer.ContainsKey(custId))` keeps only the first

BR-2: Primary email is the FIRST email address encountered for each customer_id in the email_addresses DataFrame. Same first-encountered logic as phone.
- Confidence: HIGH
- Evidence: [FullProfileAssembler.cs:44-54] — `if (!emailByCustomer.ContainsKey(custId))` keeps only the first

BR-3: Age is calculated as `asOfDate.Year - birthdate.Year`, decremented by 1 if the customer hasn't had their birthday yet as of the as_of date.
- Confidence: HIGH
- Evidence: [FullProfileAssembler.cs:94-95] — `age = asOfDate.Year - birthdate.Year; if (birthdate > asOfDate.AddYears(-age)) age--;`

BR-4: Age bracket categorization follows these ranges: 18-25 (age < 26), 26-35, 36-45, 46-55, 56-65, 65+ (age > 65).
- Confidence: HIGH
- Evidence: [FullProfileAssembler.cs:97-105] — switch expression with explicit ranges

BR-5: Segments are a comma-separated string of segment_names resolved through a two-step join: customers_segments maps customer_id to segment_id, then segments maps segment_id to segment_name.
- Confidence: HIGH
- Evidence: [FullProfileAssembler.cs:57-82,110-116] — customerSegmentIds dictionary + segmentNames lookup + string.Join

BR-6: If a customer has no phone, email, or segments, empty strings are used for those fields.
- Confidence: HIGH
- Evidence: [FullProfileAssembler.cs:107-108] — `GetValueOrDefault(customerId, "")` for phone/email; [FullProfileAssembler.cs:116] — empty Join result for no segments

BR-7: The as_of value comes from the customer row itself (custRow["as_of"]).
- Confidence: HIGH
- Evidence: [FullProfileAssembler.cs:128] — `["as_of"] = custRow["as_of"]`

BR-8: Only segment_name is used from the segments table; segment_code is sourced but not included in the output.
- Confidence: HIGH
- Evidence: [FullProfileAssembler.cs:63] — only segment_name extracted from segments rows

BR-9: If customers is null or empty, an empty output DataFrame is produced immediately.
- Confidence: HIGH
- Evidence: [FullProfileAssembler.cs:18-22]

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | customers.id | Convert.ToInt32 | [FullProfileAssembler.cs:87] |
| first_name | customers.first_name | ToString, empty string default | [FullProfileAssembler.cs:88] |
| last_name | customers.last_name | ToString, empty string default | [FullProfileAssembler.cs:89] |
| age | Computed from customers.birthdate + as_of | Year difference with birthday adjustment | [FullProfileAssembler.cs:94-95] |
| age_bracket | Computed from age | Categorized into 6 brackets | [FullProfileAssembler.cs:97-105] |
| primary_phone | phone_numbers.phone_number | First phone for customer; empty if none | [FullProfileAssembler.cs:107] |
| primary_email | email_addresses.email_address | First email for customer; empty if none | [FullProfileAssembler.cs:108] |
| segments | Computed from customers_segments + segments | Comma-separated segment names | [FullProfileAssembler.cs:110-116] |
| as_of | customers.as_of | Passthrough | [FullProfileAssembler.cs:128] |

## Non-Deterministic Fields
- **primary_phone**: "First encountered" depends on DataFrame iteration order, which depends on database query order. If multiple phones exist per customer, the selection is not deterministically specified (no ORDER BY in DataSourcing).
- **primary_email**: Same non-determinism as primary_phone.
- **segments**: The order of segment names in the comma-separated string depends on iteration order of customers_segments rows.
- Confidence: MEDIUM — DataSourcing may return rows in a consistent order within a single run, but order is not formally guaranteed.

## Write Mode Implications
- **Overwrite mode**: Each run replaces the entire Parquet directory. Only the latest effective date's profiles persist.
- Multi-day gap-fill: only the final day's data survives.

## Edge Cases
- **Weekend dates**: Customers table is weekday-only. Phone_numbers, email_addresses, and customers_segments appear to have data on all days including weekends. On a weekday run, all source tables have data; on a weekend, customers will be empty, producing an empty output.
- **Age calculation edge case**: If birthdate is exactly on as_of date, age is correctly computed (birthday has occurred). If birthdate is the day after as_of, age is decremented.
- **Multiple phones/emails per customer**: Only the first is kept; remaining are silently discarded.
- **Segment with unknown segment_id**: If customers_segments references a segment_id not in the segments table, that segment is filtered out (Where clause in FullProfileAssembler.cs:113).

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| First phone as primary | FullProfileAssembler.cs:31-41 |
| First email as primary | FullProfileAssembler.cs:44-54 |
| Age calculation from birthdate | FullProfileAssembler.cs:94-95 |
| Age brackets (6 ranges) | FullProfileAssembler.cs:97-105 |
| Segments as comma-separated names | FullProfileAssembler.cs:110-116 |
| Empty defaults for missing data | FullProfileAssembler.cs:107-108 |
| Overwrite write mode | customer_full_profile.json:50 |
| 2 Parquet part files | customer_full_profile.json:49 |
| First effective date 2024-10-01 | customer_full_profile.json:3 |

## Open Questions
1. The "primary" phone/email selection is based on iteration order, not on phone_type/email_type. Is there a business definition of "primary" that should be applied? (Confidence: MEDIUM)
2. Segment_code is sourced from the segments table but never used. Is it needed? (Confidence: LOW)
