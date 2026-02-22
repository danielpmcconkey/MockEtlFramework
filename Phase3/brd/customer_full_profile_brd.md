# CustomerFullProfile — Business Requirements Document

## Overview

The CustomerFullProfile job produces an enriched customer profile combining demographics (age, age bracket), primary contact information (phone and email), and a comma-separated list of segment names for each customer. The output table uses Overwrite mode, retaining only the most recent effective date's data.

## Source Tables

| Table | Alias in Config | Columns Sourced | Purpose |
|-------|----------------|-----------------|---------|
| `datalake.customers` | customers | id, first_name, last_name, birthdate | Core customer data; iterated to produce one output row per customer |
| `datalake.phone_numbers` | phone_numbers | phone_id, customer_id, phone_type, phone_number | Used to find primary (first-encountered) phone number per customer |
| `datalake.email_addresses` | email_addresses | email_id, customer_id, email_address, email_type | Used to find primary (first-encountered) email address per customer |
| `datalake.customers_segments` | customers_segments | customer_id, segment_id | Maps customers to their segment memberships |
| `datalake.segments` | segments | segment_id, segment_name, segment_code | Segment reference data for name lookup |

- Join logic: Phone and email are joined to customers via dictionary lookup (first match wins). Segments are joined through a two-step process: customers_segments maps customer_id to segment_id, then segments provides segment_name. All joins are performed procedurally in C# via hash maps.
- Evidence: [ExternalModules/FullProfileAssembler.cs:29-82] builds four dictionaries for phone, email, segment names, and customer-segment mappings.

## Business Rules

BR-1: One output row is produced per customer per effective date.
- Confidence: HIGH
- Evidence: [ExternalModules/FullProfileAssembler.cs:85-130] iterates `customers.Rows` and creates one output row per customer.
- Evidence: [curated.customer_full_profile] Row count = 223 per as_of date, matching customer count.

BR-2: Age is calculated as the difference between the as_of date and birthdate, adjusting for whether the birthday has occurred yet.
- Confidence: HIGH
- Evidence: [ExternalModules/FullProfileAssembler.cs:91-95] `age = asOfDate.Year - birthdate.Year; if (birthdate > asOfDate.AddYears(-age)) age--;`

BR-3: Age bracket assignment uses the same ranges as CustomerDemographics: 18-25, 26-35, 36-45, 46-55, 56-65, 65+.
- Confidence: HIGH
- Evidence: [ExternalModules/FullProfileAssembler.cs:97-105] Identical switch expression to CustomerDemographicsBuilder.

BR-4: Primary phone is the first phone number encountered per customer. Empty string if none.
- Confidence: HIGH
- Evidence: [ExternalModules/FullProfileAssembler.cs:33-41,107]

BR-5: Primary email is the first email address encountered per customer. Empty string if none.
- Confidence: HIGH
- Evidence: [ExternalModules/FullProfileAssembler.cs:46-55,108]

BR-6: Segments are resolved by joining customers_segments to segments via segment_id, then concatenated as a comma-separated string of segment names (no spaces after commas).
- Confidence: HIGH
- Evidence: [ExternalModules/FullProfileAssembler.cs:111-116] `string.Join(",", segNamesList)` with no space separator.
- Evidence: [curated.customer_full_profile] Sample output shows `"US retail banking,Premium banking"` format.

BR-7: Customers with no segments get an empty string for the segments column.
- Confidence: HIGH
- Evidence: [ExternalModules/FullProfileAssembler.cs:111] `GetValueOrDefault(customerId, new List<int>())` returns empty list, resulting in `string.Join(",", [])` = "".
- Evidence: [curated.customer_full_profile] 0 rows have segments = '' (all 223 customers have at least one segment).

BR-8: Segment names are included only when the segment_id exists in both the customers_segments mapping AND the segments reference table.
- Confidence: HIGH
- Evidence: [ExternalModules/FullProfileAssembler.cs:112-113] `.Where(segId => segmentNames.ContainsKey(segId))` filters out segment_ids not in the segments table.

BR-9: Output uses Overwrite write mode.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_full_profile.json:49] `"writeMode": "Overwrite"`
- Evidence: [curated.customer_full_profile] Only has data for as_of = 2024-10-31.

BR-10: If no customers exist for the effective date, an empty DataFrame is written.
- Confidence: HIGH
- Evidence: [ExternalModules/FullProfileAssembler.cs:17-21]

## Output Schema

| Column | Type | Source | Transformation |
|--------|------|--------|---------------|
| customer_id | integer | `customers.id` | Renamed from `id` via Convert.ToInt32 |
| first_name | varchar(100) | `customers.first_name` | ToString with empty string fallback |
| last_name | varchar(100) | `customers.last_name` | ToString with empty string fallback |
| age | integer | Calculated | Date arithmetic: asOfDate.Year - birthdate.Year with adjustment |
| age_bracket | varchar(10) | Calculated | CASE-style switch on age value |
| primary_phone | varchar(20) | `phone_numbers.phone_number` | First phone per customer; empty string default |
| primary_email | varchar(255) | `email_addresses.email_address` | First email per customer; empty string default |
| segments | text | `segments.segment_name` via `customers_segments` | Comma-separated list of segment names |
| as_of | date | `customers.as_of` | Passthrough |

## Edge Cases

- **Weekend/holiday dates:** `datalake.customers` has no weekend data. When run for a weekend date, the empty guard triggers and writes an empty DataFrame (Overwrite clears previous data). Phone, email, segments, and customers_segments tables DO have weekend data, but they are only processed if customers has rows.
- **Multiple segments per customer:** Customers with multiple segment memberships get all segment names comma-separated. Order depends on iteration order of the customers_segments data. Evidence: [curated.customer_full_profile] shows `"US retail banking,Premium banking"` for multi-segment customers.
- **Segment_code column sourced but unused:** The `segment_code` column is sourced from segments [JobExecutor/Jobs/customer_full_profile.json:38] but never referenced in the External module; only `segment_name` is used.
- **Birthdate not in output:** Unlike CustomerDemographics, this job does NOT include the birthdate column in its output schema.

## Anti-Patterns Identified

- **AP-2: Duplicated Transformation Logic** — This job re-derives age, age_bracket, primary_phone, and primary_email using identical logic to CustomerDemographics [ExternalModules/FullProfileAssembler.cs:91-108 vs. ExternalModules/CustomerDemographicsBuilder.cs:65-79]. Instead of reading from `curated.customer_demographics` and adding segments, it goes back to raw datalake tables and repeats all the same computations. V2 approach: Read from `curated.customer_demographics` (or the V2 equivalent) for age/bracket/phone/email, then only add the segment enrichment. This requires declaring a dependency on CustomerDemographics.

- **AP-3: Unnecessary External Module** — The entire logic (age calculation, age bracket, first phone/email, segment concatenation) is expressible in SQL using date arithmetic, CASE, window functions (ROW_NUMBER for first phone/email), JOINs, and GROUP_CONCAT/string aggregation. V2 approach: Replace with SQL Transformation.

- **AP-4: Unused Columns Sourced** — `phone_id` and `phone_type` from phone_numbers, `email_id` and `email_type` from email_addresses, and `segment_code` from segments are sourced but never used in the External module. V2 approach: Remove unused columns from DataSourcing configs.

- **AP-6: Row-by-Row Iteration in External Module** — Four separate foreach loops build dictionaries [ExternalModules/FullProfileAssembler.cs:33,46,61,72], then a fifth foreach iterates customers [line 85]. All set-based operations. V2 approach: Replace with SQL JOINs and aggregations.

- **AP-7: Hardcoded Magic Values** — Same age bracket boundaries as CustomerDemographics (26, 35, 45, 55, 65) without documentation. V2 approach: Add SQL comments explaining brackets.

- **AP-10: Missing Dependency Declarations** — If V2 reads from curated.customer_demographics, it must declare a SameDay dependency on CustomerDemographics. The original has no declared dependencies. V2 approach: Declare dependency in control.job_dependencies.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [ExternalModules/FullProfileAssembler.cs:85-130], [curated.customer_full_profile] 223 rows |
| BR-2 | [ExternalModules/FullProfileAssembler.cs:91-95] |
| BR-3 | [ExternalModules/FullProfileAssembler.cs:97-105] |
| BR-4 | [ExternalModules/FullProfileAssembler.cs:33-41,107] |
| BR-5 | [ExternalModules/FullProfileAssembler.cs:46-55,108] |
| BR-6 | [ExternalModules/FullProfileAssembler.cs:111-116], curated sample data |
| BR-7 | [ExternalModules/FullProfileAssembler.cs:111] |
| BR-8 | [ExternalModules/FullProfileAssembler.cs:112-113] |
| BR-9 | [JobExecutor/Jobs/customer_full_profile.json:49], curated output single-date |
| BR-10 | [ExternalModules/FullProfileAssembler.cs:17-21] |

## Open Questions

- **Segment ordering:** The order of segment names in the comma-separated list depends on the iteration order of customers_segments rows. This order is not explicitly sorted — it depends on database row order (likely by the `id` primary key column in customers_segments). The business may want alphabetical or priority-based ordering.
  - Confidence: MEDIUM — code behavior is clear but business intent for ordering is ambiguous.

- **Relationship to CustomerDemographics:** The duplicated logic (age, bracket, phone, email) strongly suggests CustomerFullProfile should depend on CustomerDemographics. However, the original has no declared dependency and re-derives everything from scratch. The V2 design should evaluate whether to introduce this dependency (AP-2 fix) or keep the logic self-contained.
  - Confidence: HIGH — duplication is factual; architectural decision is for design phase.
