# CustomerDemographics -- Business Requirements Document

## Overview
Produces a per-customer demographics record including personal details, computed age and age bracket, and primary contact information (phone and email). Ages are calculated relative to the effective date. Output is a CSV with CRLF line endings.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/customer_demographics.csv`
- **includeHeader**: true
- **writeMode**: Overwrite
- **lineEnding**: CRLF
- **trailerFormat**: (not configured -- no trailer)

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.customers | id, prefix, first_name, last_name, sort_name, suffix, birthdate | Effective date range (injected by executor) | [customer_demographics.json:8-11] |
| datalake.phone_numbers | phone_id, customer_id, phone_type, phone_number | Effective date range (injected by executor) | [customer_demographics.json:14-17] |
| datalake.email_addresses | email_id, customer_id, email_address, email_type | Effective date range (injected by executor) | [customer_demographics.json:20-23] |
| datalake.segments | segment_id, segment_name | Effective date range (injected by executor) | [customer_demographics.json:26-29] |

## Business Rules

BR-1: Age is calculated as the difference in years between the customer's birthdate and the as_of date from the customer row, with a birthday adjustment (subtract 1 if birthday hasn't occurred yet in the as_of year).
- Confidence: HIGH
- Evidence: [CustomerDemographicsBuilder.cs:65-66] -- `var age = asOfDate.Year - birthdate.Year; if (birthdate > asOfDate.AddYears(-age)) age--;`.

BR-2: Age bracket is assigned using a switch expression with the following ranges:
- < 26: "18-25"
- 26-35: "26-35"
- 36-45: "36-45"
- 46-55: "46-55"
- 56-65: "56-65"
- > 65: "65+"
- Confidence: HIGH
- Evidence: [CustomerDemographicsBuilder.cs:68-76] -- switch expression on `age`.

BR-3: Primary phone is the FIRST phone number encountered for each customer in the phone_numbers DataFrame. The selection is not filtered by phone_type.
- Confidence: HIGH
- Evidence: [CustomerDemographicsBuilder.cs:31-38] -- `if (!phoneByCustomer.ContainsKey(custId))` only takes the first entry.

BR-4: Primary email is the FIRST email address encountered for each customer in the email_addresses DataFrame. The selection is not filtered by email_type.
- Confidence: HIGH
- Evidence: [CustomerDemographicsBuilder.cs:44-51] -- `if (!emailByCustomer.ContainsKey(custId))` only takes the first entry.

BR-5: Customers with no phone number get an empty string ("") for primary_phone. Same for customers with no email.
- Confidence: HIGH
- Evidence: [CustomerDemographicsBuilder.cs:78-79] -- `GetValueOrDefault(customerId, "")`.

BR-6: When customers DataFrame is null or empty, the output is an empty DataFrame with correct schema.
- Confidence: HIGH
- Evidence: [CustomerDemographicsBuilder.cs:17-20] -- null/empty guard.

BR-7: The `as_of` column is taken directly from the customer row's as_of value.
- Confidence: HIGH
- Evidence: [CustomerDemographicsBuilder.cs:91] -- `custRow["as_of"]`.

BR-8: The `birthdate` column is passed through as the raw value from the customer row (not formatted).
- Confidence: HIGH
- Evidence: [CustomerDemographicsBuilder.cs:86] -- `custRow["birthdate"]` passed directly.

BR-9: Some sourced columns (prefix, sort_name, suffix, phone_id, phone_type, email_id, email_type) are loaded by DataSourcing but NOT used in the output.
- Confidence: HIGH
- Evidence: [CustomerDemographicsBuilder.cs:10-14] -- output columns do not include prefix, sort_name, suffix, phone_type, etc.

BR-10: The segments table is sourced but NOT used by the External module.
- Confidence: HIGH
- Evidence: [CustomerDemographicsBuilder.cs:16-24] -- only customers, phone_numbers, and email_addresses are accessed from shared state.

BR-11: The birthdate-to-DateOnly conversion handles DateOnly, DateTime, and string formats via a helper method.
- Confidence: HIGH
- Evidence: [CustomerDemographicsBuilder.cs:99-105] -- `ToDateOnly` method with pattern matching.

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | customers.id | Cast to int via Convert.ToInt32 | [CustomerDemographicsBuilder.cs:58] |
| first_name | customers.first_name | ToString with null coalesce to "" | [CustomerDemographicsBuilder.cs:59] |
| last_name | customers.last_name | ToString with null coalesce to "" | [CustomerDemographicsBuilder.cs:60] |
| birthdate | customers.birthdate | Pass-through (raw value) | [CustomerDemographicsBuilder.cs:86] |
| age | Computed | Years between birthdate and as_of, with birthday adjustment | [CustomerDemographicsBuilder.cs:65-66] |
| age_bracket | Computed from age | Categorical: "18-25", "26-35", "36-45", "46-55", "56-65", "65+" | [CustomerDemographicsBuilder.cs:68-76] |
| primary_phone | phone_numbers.phone_number | First phone number found for customer, or "" | [CustomerDemographicsBuilder.cs:35,78] |
| primary_email | email_addresses.email_address | First email address found for customer, or "" | [CustomerDemographicsBuilder.cs:48,79] |
| as_of | customers.as_of | Pass-through | [CustomerDemographicsBuilder.cs:91] |

## Non-Deterministic Fields
None identified. However, the "first phone/email" logic is order-dependent -- the order comes from DataSourcing, which returns rows in database query order. This is deterministic for a given data state but not explicitly ordered.

## Write Mode Implications
- **Overwrite**: Each execution replaces the entire output file. For multi-day auto-advance runs, only the last effective date's output survives on disk.

## Edge Cases
- **Customer under 18**: The age bracket for ages < 18 would be "18-25" because the switch uses `< 26` as the first case with no lower bound check.
- **Null birthdate**: Would cause an exception in `ToDateOnly` since there is no null guard before the age calculation.
- **Customer with no phone or email**: Gets empty string "" for both fields.
- **Multiple phones/emails**: Only the first encountered is used. The selection order depends on database/DataSourcing row ordering.
- **Unused sourced columns**: prefix, sort_name, suffix, phone_type, email_type, phone_id, email_id are loaded but not in output.
- **Segments table unused**: Loaded but never referenced.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Age calculation with birthday adjustment | [CustomerDemographicsBuilder.cs:65-66] |
| Age bracket assignment | [CustomerDemographicsBuilder.cs:68-76] |
| First phone selection | [CustomerDemographicsBuilder.cs:31-38] |
| First email selection | [CustomerDemographicsBuilder.cs:44-51] |
| Default empty string for missing contact | [CustomerDemographicsBuilder.cs:78-79] |
| Empty guard on customers | [CustomerDemographicsBuilder.cs:17-20] |
| Segments unused | [CustomerDemographicsBuilder.cs:16-24] |
| CRLF line endings | [customer_demographics.json:44] |
| Overwrite mode | [customer_demographics.json:43] |
| birthdate pass-through | [CustomerDemographicsBuilder.cs:86] |

## Open Questions
- OQ-1: Primary phone/email selection uses "first encountered" without filtering by type (e.g., "Mobile" or "Personal"). Whether the ordering from the database produces a reliable "primary" designation is unclear. Confidence: MEDIUM -- the code makes no effort to prioritize by type.
- OQ-2: The segments table is sourced but unused. Confidence: MEDIUM.
- OQ-3: Several sourced columns (prefix, sort_name, suffix) are loaded but not used. Whether this is intentional or a missing feature is unclear. Confidence: LOW.
