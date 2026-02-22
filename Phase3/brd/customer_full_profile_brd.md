# BRD: CustomerFullProfile

## Overview
This job produces a comprehensive customer profile that combines demographic information (age, age bracket), primary contact details (phone, email), and a comma-separated list of segment names for each customer. It writes one row per customer to `curated.customer_full_profile` using Overwrite mode.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| customers | datalake | id, first_name, last_name, birthdate | Driver table; iterated to produce one output row per customer | [JobExecutor/Jobs/customer_full_profile.json:7-11] |
| phone_numbers | datalake | phone_id, customer_id, phone_type, phone_number | Looked up by customer_id; first phone number encountered is used | [JobExecutor/Jobs/customer_full_profile.json:13-17] |
| email_addresses | datalake | email_id, customer_id, email_address, email_type | Looked up by customer_id; first email address encountered is used | [JobExecutor/Jobs/customer_full_profile.json:19-23] |
| customers_segments | datalake | customer_id, segment_id | Join table mapping customers to segments; used to look up segment_ids per customer | [JobExecutor/Jobs/customer_full_profile.json:25-29] |
| segments | datalake | segment_id, segment_name, segment_code | Segment reference table; segment_name is resolved from segment_id. segment_code is sourced but not used. | [JobExecutor/Jobs/customer_full_profile.json:31-36] |

## Business Rules
BR-1: One output row is produced per customer (driven by the customers table).
- Confidence: HIGH
- Evidence: [ExternalModules/FullProfileAssembler.cs:84] `foreach (var custRow in customers.Rows)`
- Evidence: [curated.customer_full_profile] 223 rows for as_of = 2024-10-31

BR-2: Age is computed identically to CustomerDemographics: asOfDate.Year - birthdate.Year with birthday adjustment.
- Confidence: HIGH
- Evidence: [ExternalModules/FullProfileAssembler.cs:94-95] `age = asOfDate.Year - birthdate.Year; if (birthdate > asOfDate.AddYears(-age)) age--;`

BR-3: Age bracket classification uses the same ranges as CustomerDemographics.
- Confidence: HIGH
- Evidence: [ExternalModules/FullProfileAssembler.cs:97-105] Same switch expression: <26 => "18-25", <=35 => "26-35", <=45 => "36-45", <=55 => "46-55", <=65 => "56-65", _ => "65+"

BR-4: Primary phone is the first phone number found for a customer (not filtered by phone_type).
- Confidence: HIGH
- Evidence: [ExternalModules/FullProfileAssembler.cs:33-40] `if (!phoneByCustomer.ContainsKey(custId))` — takes first only

BR-5: Primary email is the first email address found for a customer (not filtered by email_type).
- Confidence: HIGH
- Evidence: [ExternalModules/FullProfileAssembler.cs:46-53] `if (!emailByCustomer.ContainsKey(custId))` — takes first only

BR-6: Segments are resolved by joining customers_segments to segments on segment_id, producing a comma-separated string of segment names.
- Confidence: HIGH
- Evidence: [ExternalModules/FullProfileAssembler.cs:68-82] Builds `customerSegmentIds` dictionary, then [lines 111-117] resolves segment_ids to segment_names and joins with `string.Join(",", segNamesList)`
- Evidence: [curated.customer_full_profile] e.g., customer 1024 segments = "US retail banking"

BR-7: If a customer has no segments, the segments field is an empty string.
- Confidence: HIGH
- Evidence: [ExternalModules/FullProfileAssembler.cs:111] `GetValueOrDefault(customerId, new List<int>())` — empty list -> empty Join result

BR-8: If a segment_id in customers_segments has no matching segment in the segments table, it is silently excluded from the comma-separated list.
- Confidence: HIGH
- Evidence: [ExternalModules/FullProfileAssembler.cs:113] `.Where(segId => segmentNames.ContainsKey(segId))` — filters out unmatched segment_ids

BR-9: Segment names are joined WITHOUT spaces after commas (just comma delimiter).
- Confidence: HIGH
- Evidence: [ExternalModules/FullProfileAssembler.cs:116] `string.Join(",", segNamesList)` — no space after comma
- Evidence: [curated.customer_full_profile] Customers with multiple segments would show e.g., "US retail banking,Canadian retail banking"

BR-10: If the customers DataFrame is null or empty, the output is an empty DataFrame.
- Confidence: HIGH
- Evidence: [ExternalModules/FullProfileAssembler.cs:18-22] Guard clause

BR-11: The output is written using Overwrite mode.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_full_profile.json:48] `"writeMode": "Overwrite"`
- Evidence: [curated.customer_full_profile] Only as_of = 2024-10-31

BR-12: segment_code is sourced from the segments table but not used in the output.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_full_profile.json:35] `"segment_code"` in columns list
- Evidence: [ExternalModules/FullProfileAssembler.cs:63] Only `segment_name` is accessed from segment rows

BR-13: The segment_name lookup dictionary is keyed by segment_id and may overwrite if duplicates exist (last segment_name wins per segment_id).
- Confidence: HIGH
- Evidence: [ExternalModules/FullProfileAssembler.cs:61-65] `segmentNames[segId] = segRow["segment_name"]` — dictionary assignment overwrites

BR-14: Name fields are null-coalesced to empty string; phone/email default to empty string if not found.
- Confidence: HIGH
- Evidence: [ExternalModules/FullProfileAssembler.cs:89-90, 107-108] `?? ""` for names, `GetValueOrDefault(customerId, "")` for phone/email

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | customers.id | Direct mapping | [ExternalModules/FullProfileAssembler.cs:120] |
| first_name | customers.first_name | Null-coalesced to empty string | [ExternalModules/FullProfileAssembler.cs:89] |
| last_name | customers.last_name | Null-coalesced to empty string | [ExternalModules/FullProfileAssembler.cs:90] |
| age | Computed | asOfDate.Year - birthdate.Year with birthday adjustment | [ExternalModules/FullProfileAssembler.cs:94-95] |
| age_bracket | Computed from age | Switch expression | [ExternalModules/FullProfileAssembler.cs:97-105] |
| primary_phone | phone_numbers.phone_number | First phone per customer; empty string if none | [ExternalModules/FullProfileAssembler.cs:107] |
| primary_email | email_addresses.email_address | First email per customer; empty string if none | [ExternalModules/FullProfileAssembler.cs:108] |
| segments | customers_segments + segments | Comma-separated segment names | [ExternalModules/FullProfileAssembler.cs:111-116] |
| as_of | customers.as_of | Passed through | [ExternalModules/FullProfileAssembler.cs:128] |

## Edge Cases
- **No segments**: Customers with no entries in customers_segments get an empty string for segments. [ExternalModules/FullProfileAssembler.cs:111]
- **Multiple segments**: Customers with multiple segments get a comma-separated list (no spaces). Customer 1015 has 4 segments. [datalake.customers_segments query]
- **Unmatched segment_ids**: If a segment_id appears in customers_segments but not in the segments table, it is silently dropped. [ExternalModules/FullProfileAssembler.cs:113]
- **No phone/email**: Empty string (not NULL). [ExternalModules/FullProfileAssembler.cs:107-108]
- **Weekend behavior**: Customers table is weekday-only. With Overwrite mode, weekend effective dates would produce empty output and truncate the table.
- **NULL birthdate**: Would cause an exception in ToDateOnly if birthdate is null. The code does not guard against this. [ExternalModules/FullProfileAssembler.cs:91]
- **Segment ordering**: The order of segments in the comma-separated string depends on the order of rows in customers_segments as returned by DataSourcing, which orders by as_of. Within a single as_of, order is database natural order. [ExternalModules/FullProfileAssembler.cs:74-81]

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [ExternalModules/FullProfileAssembler.cs:84], [curated.customer_full_profile count] |
| BR-2 | [ExternalModules/FullProfileAssembler.cs:94-95] |
| BR-3 | [ExternalModules/FullProfileAssembler.cs:97-105] |
| BR-4 | [ExternalModules/FullProfileAssembler.cs:33-40] |
| BR-5 | [ExternalModules/FullProfileAssembler.cs:46-53] |
| BR-6 | [ExternalModules/FullProfileAssembler.cs:68-82, 111-117] |
| BR-7 | [ExternalModules/FullProfileAssembler.cs:111] |
| BR-8 | [ExternalModules/FullProfileAssembler.cs:113] |
| BR-9 | [ExternalModules/FullProfileAssembler.cs:116], [curated.customer_full_profile sample] |
| BR-10 | [ExternalModules/FullProfileAssembler.cs:18-22] |
| BR-11 | [JobExecutor/Jobs/customer_full_profile.json:48] |
| BR-12 | [JobExecutor/Jobs/customer_full_profile.json:35], [ExternalModules/FullProfileAssembler.cs:63] |
| BR-13 | [ExternalModules/FullProfileAssembler.cs:61-65] |
| BR-14 | [ExternalModules/FullProfileAssembler.cs:89-90, 107-108] |

## Open Questions
- **Segment order in comma-separated string**: The order of segment names in the output depends on the iteration order of `customers_segments` rows. This is not explicitly sorted; the order is determined by the DataSourcing query (`ORDER BY as_of`, then natural PostgreSQL row order within a date). Confidence: MEDIUM that this ordering is deterministic across runs.
- **NULL birthdate safety**: The code does not handle NULL birthdates. If a customer has a NULL birthdate, `ToDateOnly` would throw. Current data appears to have no NULL birthdates, but this is not guarded. Confidence: HIGH that no NULL birthdates exist in current data.
- **Phone/email ordering**: Same non-deterministic "first found" behavior as CustomerDemographics. Confidence: MEDIUM that ordering is consistent.
