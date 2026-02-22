# CustomerDemographics — Business Requirements Document

## Overview

The CustomerDemographics job produces an enriched customer profile with calculated age, age bracket, and primary contact information (phone and email) for each customer as of each effective date. The output table uses Overwrite mode, retaining only the most recent date's data.

## Source Tables

| Table | Alias in Config | Columns Sourced | Purpose |
|-------|----------------|-----------------|---------|
| `datalake.customers` | customers | id, prefix, first_name, last_name, sort_name, suffix, birthdate | Core customer data; iterated to produce one output row per customer |
| `datalake.phone_numbers` | phone_numbers | phone_id, customer_id, phone_type, phone_number | Used to find primary (first-encountered) phone number per customer |
| `datalake.email_addresses` | email_addresses | email_id, customer_id, email_address, email_type | Used to find primary (first-encountered) email address per customer |
| `datalake.segments` | segments | segment_id, segment_name | **NOT USED** — sourced but never referenced by the External module |

- Join logic: Phone and email are joined to customers via `customer_id` in a dictionary lookup (first match per customer wins). No explicit JOIN; the External module builds hash maps.
- Evidence: [ExternalModules/CustomerDemographicsBuilder.cs:28-53] builds `phoneByCustomer` and `emailByCustomer` dictionaries; segments DataFrame is never accessed.
- Date filtering: All DataSourcing modules get effective dates injected automatically by the framework. Customers, accounts, and loan_accounts tables have weekday-only data (no Sat/Sun as_of dates). Phone, email, and segment tables have data for all 7 days.

## Business Rules

BR-1: One output row is produced per customer per effective date.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerDemographicsBuilder.cs:56-93] iterates `customers.Rows` and creates one output row per customer row.
- Evidence: [curated.customer_demographics] Row count = 223 per as_of date, matching `datalake.customers` count of 223.

BR-2: Age is calculated as the difference between the effective date (as_of) and the customer's birthdate, adjusting for whether the birthday has occurred yet in that year.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerDemographicsBuilder.cs:65-66] `age = asOfDate.Year - birthdate.Year; if (birthdate > asOfDate.AddYears(-age)) age--;`

BR-3: Age bracket is assigned using the following ranges: 18-25 (age < 26), 26-35 (age 26-35), 36-45 (age 36-45), 46-55 (age 46-55), 56-65 (age 56-65), 65+ (age > 65).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerDemographicsBuilder.cs:68-76] C# switch expression defines exact brackets.

BR-4: Primary phone is the first phone number encountered for a customer (based on row order from DataSourcing). If no phone exists, an empty string is used.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerDemographicsBuilder.cs:31-38] `if (!phoneByCustomer.ContainsKey(custId))` ensures only first phone is kept.
- Evidence: [ExternalModules/CustomerDemographicsBuilder.cs:78] `GetValueOrDefault(customerId, "")` provides empty string fallback.

BR-5: Primary email is the first email address encountered for a customer (based on row order from DataSourcing). If no email exists, an empty string is used.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerDemographicsBuilder.cs:44-52] Same first-match pattern as phone.
- Evidence: [ExternalModules/CustomerDemographicsBuilder.cs:79] `GetValueOrDefault(customerId, "")` provides empty string fallback.

BR-6: Output uses Overwrite write mode — the curated table retains only the most recent effective date's data.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_demographics.json:42] `"writeMode": "Overwrite"`
- Evidence: [curated.customer_demographics] Only has data for as_of = 2024-10-31 (single date, 223 rows).

BR-7: If no customers exist for the effective date, an empty DataFrame with the correct schema is written.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerDemographicsBuilder.cs:18-21] Empty guard produces empty DataFrame with output columns.

BR-8: The birthdate column is passed through unchanged from the source.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerDemographicsBuilder.cs:86] `["birthdate"] = custRow["birthdate"]` (raw value passthrough).

## Output Schema

| Column | Type | Source | Transformation |
|--------|------|--------|---------------|
| customer_id | integer | `customers.id` | Renamed from `id` via `Convert.ToInt32` |
| first_name | varchar(100) | `customers.first_name` | ToString with empty string fallback |
| last_name | varchar(100) | `customers.last_name` | ToString with empty string fallback |
| birthdate | date | `customers.birthdate` | Passthrough |
| age | integer | Calculated | `asOfDate.Year - birthdate.Year` with birthday adjustment |
| age_bracket | varchar(10) | Calculated | Switch expression on age value |
| primary_phone | varchar(20) | `phone_numbers.phone_number` | First phone per customer; empty string if none |
| primary_email | varchar(255) | `email_addresses.email_address` | First email per customer; empty string if none |
| as_of | date | `customers.as_of` | Passthrough from source row |

## Edge Cases

- **Weekend/holiday dates:** The `datalake.customers` table has no data for weekends (Sat/Sun). When the framework tries to run for a weekend effective date, DataSourcing returns zero rows, triggering the empty DataFrame guard at line 18-21. The output will be an empty write (Overwrite clears previous data). However, phone_numbers and email_addresses DO have weekend data — this only matters if customers has rows.
- **Missing phone/email:** Customers without phone numbers or email addresses get empty strings ("") rather than NULL. Evidence: [ExternalModules/CustomerDemographicsBuilder.cs:78-79].
- **Multiple phones/emails per customer:** Only the first encountered phone/email is used. Order depends on DataSourcing row order (database result order, which is by phone_id/email_id unless otherwise specified).
- **Birthdate edge case:** The age calculation handles leap year birthdays implicitly via `AddYears(-age)` comparison.

## Anti-Patterns Identified

- **AP-1: Redundant Data Sourcing** — The `segments` table is sourced in the job config [JobExecutor/Jobs/customer_demographics.json:28-33] but the External module `CustomerDemographicsBuilder` never accesses `sharedState["segments"]`. The segments DataFrame is loaded into memory and never used. V2 approach: Remove the segments DataSourcing module entirely.

- **AP-3: Unnecessary External Module** — The External module performs: (a) dictionary lookups for first phone/email per customer, (b) age calculation from birthdate, (c) age bracket assignment. All of these are expressible in SQL: age via date arithmetic, age bracket via CASE, primary phone/email via window functions (ROW_NUMBER partitioned by customer_id). V2 approach: Replace with SQL Transformation + DataFrameWriter pipeline.

- **AP-4: Unused Columns Sourced** — The `prefix`, `sort_name`, and `suffix` columns are sourced from `datalake.customers` [JobExecutor/Jobs/customer_demographics.json:10] but never referenced in the External module. The module accesses only `id`, `first_name`, `last_name`, `birthdate`, and `as_of`. Similarly, `phone_id` and `phone_type` are sourced from phone_numbers but unused; `email_id` and `email_type` are sourced from email_addresses but unused. V2 approach: Source only the columns actually needed.

- **AP-6: Row-by-Row Iteration in External Module** — The module iterates over phone_numbers, email_addresses, and customers row by row using foreach loops [ExternalModules/CustomerDemographicsBuilder.cs:31,44,56] to build lookups and produce output. This is set-based logic (join + first-match aggregation) that SQL handles natively. V2 approach: Replace with SQL using JOINs and window functions.

- **AP-7: Hardcoded Magic Values** — Age bracket boundaries (26, 35, 45, 55, 65) appear as literal values [ExternalModules/CustomerDemographicsBuilder.cs:68-76] without documentation of their business meaning (e.g., generational cohorts, regulatory age bands). V2 approach: Keep the values but add SQL comments explaining each bracket.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [ExternalModules/CustomerDemographicsBuilder.cs:56-93], [curated.customer_demographics] 223 rows/date |
| BR-2 | [ExternalModules/CustomerDemographicsBuilder.cs:65-66] |
| BR-3 | [ExternalModules/CustomerDemographicsBuilder.cs:68-76] |
| BR-4 | [ExternalModules/CustomerDemographicsBuilder.cs:31-38,78] |
| BR-5 | [ExternalModules/CustomerDemographicsBuilder.cs:44-52,79] |
| BR-6 | [JobExecutor/Jobs/customer_demographics.json:42], curated output single-date |
| BR-7 | [ExternalModules/CustomerDemographicsBuilder.cs:18-21] |
| BR-8 | [ExternalModules/CustomerDemographicsBuilder.cs:86] |

## Open Questions

- **Phone/email ordering:** The "primary" phone and email depend on row iteration order from DataSourcing, which reflects database query order (likely by primary key: phone_id, email_id). This is deterministic but not explicitly documented as a business rule. If the desired behavior is "lowest phone_id" vs. "Mobile type first", the current logic may not match the intent.
  - Confidence: MEDIUM — the code is clear about "first encountered" but business intent for "primary" is ambiguous.
