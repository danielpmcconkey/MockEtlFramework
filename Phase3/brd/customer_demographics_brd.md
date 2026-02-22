# BRD: CustomerDemographics

## Overview
This job produces a customer demographics profile that enriches customer data with computed age, age bracket classification, and primary contact information (first phone number and first email address found). It writes one row per customer to `curated.customer_demographics` using Overwrite mode.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| customers | datalake | id, prefix, first_name, last_name, sort_name, suffix, birthdate | Driver table; iterated to produce one output row per customer. prefix, sort_name, suffix are sourced but NOT used by the External module. | [JobExecutor/Jobs/customer_demographics.json:7-11] |
| phone_numbers | datalake | phone_id, customer_id, phone_type, phone_number | Looked up by customer_id; first phone number encountered is used as primary_phone | [JobExecutor/Jobs/customer_demographics.json:13-17] |
| email_addresses | datalake | email_id, customer_id, email_address, email_type | Looked up by customer_id; first email address encountered is used as primary_email | [JobExecutor/Jobs/customer_demographics.json:19-23] |
| segments | datalake | segment_id, segment_name | Sourced into shared state but NOT used by the External module | [JobExecutor/Jobs/customer_demographics.json:25-29]; [ExternalModules/CustomerDemographicsBuilder.cs] no reference to "segments" |

## Business Rules
BR-1: One output row is produced per customer (driven by the customers table).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerDemographicsBuilder.cs:56] `foreach (var custRow in customers.Rows)`
- Evidence: [curated.customer_demographics] 223 rows for as_of = 2024-10-31, matching customer count

BR-2: Age is computed as the difference between the as_of date and birthdate, with birthday adjustment.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerDemographicsBuilder.cs:65-66] `age = asOfDate.Year - birthdate.Year; if (birthdate > asOfDate.AddYears(-age)) age--;`
- Evidence: This correctly handles leap years and partial years (reduces age by 1 if the customer has not yet had their birthday as of the as_of date)

BR-3: Age bracket classification uses the following ranges:
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerDemographicsBuilder.cs:68-76]
  - `< 26` => "18-25"
  - `<= 35` => "26-35"
  - `<= 45` => "36-45"
  - `<= 55` => "46-55"
  - `<= 65` => "56-65"
  - `> 65` => "65+"

BR-4: Primary phone is the first phone number found for a customer (first row encountered in the data, not filtered by phone_type).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerDemographicsBuilder.cs:31-38] `if (!phoneByCustomer.ContainsKey(custId))` — only takes the first phone found per customer
- Evidence: The phone_type column is sourced but not used for filtering or prioritization

BR-5: Primary email is the first email address found for a customer (first row encountered in the data, not filtered by email_type).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerDemographicsBuilder.cs:44-51] `if (!emailByCustomer.ContainsKey(custId))` — only takes the first email found per customer

BR-6: If a customer has no phone numbers, primary_phone is set to empty string.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerDemographicsBuilder.cs:78] `phoneByCustomer.GetValueOrDefault(customerId, "")`

BR-7: If a customer has no email addresses, primary_email is set to empty string.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerDemographicsBuilder.cs:79] `emailByCustomer.GetValueOrDefault(customerId, "")`

BR-8: The birthdate is passed through to the output unchanged.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerDemographicsBuilder.cs:86] `["birthdate"] = custRow["birthdate"]`

BR-9: If the customers DataFrame is null or empty, the output is an empty DataFrame.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerDemographicsBuilder.cs:18-22] Guard clause

BR-10: The segments and partially-used customer columns (prefix, sort_name, suffix) are sourced but not used.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerDemographicsBuilder.cs] No reference to `segments`, `prefix`, `sort_name`, or `suffix`
- Evidence: [JobExecutor/Jobs/customer_demographics.json:10] columns include prefix, sort_name, suffix but output schema does not

BR-11: The output is written using Overwrite mode.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_demographics.json:42] `"writeMode": "Overwrite"`
- Evidence: [curated.customer_demographics] Only contains data for `as_of = 2024-10-31`

BR-12: Name fields (first_name, last_name) are null-coalesced to empty string.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerDemographicsBuilder.cs:59-60] `?? ""`

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | customers.id | Direct mapping via Convert.ToInt32 | [ExternalModules/CustomerDemographicsBuilder.cs:58] |
| first_name | customers.first_name | Null-coalesced to empty string | [ExternalModules/CustomerDemographicsBuilder.cs:59] |
| last_name | customers.last_name | Null-coalesced to empty string | [ExternalModules/CustomerDemographicsBuilder.cs:60] |
| birthdate | customers.birthdate | Passed through unchanged | [ExternalModules/CustomerDemographicsBuilder.cs:86] |
| age | Computed | asOfDate.Year - birthdate.Year, adjusted for birthday | [ExternalModules/CustomerDemographicsBuilder.cs:65-66] |
| age_bracket | Computed from age | Switch expression mapping age to bracket string | [ExternalModules/CustomerDemographicsBuilder.cs:68-76] |
| primary_phone | phone_numbers.phone_number | First phone found for customer; empty string if none | [ExternalModules/CustomerDemographicsBuilder.cs:78] |
| primary_email | email_addresses.email_address | First email found for customer; empty string if none | [ExternalModules/CustomerDemographicsBuilder.cs:79] |
| as_of | customers.as_of | Passed through from customers row | [ExternalModules/CustomerDemographicsBuilder.cs:91] |

## Edge Cases
- **NULL names**: first_name and last_name are null-coalesced to empty string. [ExternalModules/CustomerDemographicsBuilder.cs:59-60]
- **No phone/email**: Customers without phone numbers or email addresses get empty strings (not NULL). [ExternalModules/CustomerDemographicsBuilder.cs:78-79]
- **Multiple phones/emails**: Only the first encountered row is used. The ordering depends on the database query order from DataSourcing (which uses `ORDER BY as_of`). For a single as_of date, the order is the natural row order from PostgreSQL. [ExternalModules/CustomerDemographicsBuilder.cs:34, 48]
- **Weekend/holiday behavior**: Customers table is weekday-only (23 dates). On weekends, no customer data exists, so the guard clause produces an empty output. With Overwrite mode, this would truncate the table. The executor gap-fills dates, so weekend dates would be processed but produce empty output.
- **Date parsing**: The ToDateOnly helper handles DateOnly, DateTime, and string types. [ExternalModules/CustomerDemographicsBuilder.cs:99-105]
- **Age calculation edge case**: The birthday adjustment `if (birthdate > asOfDate.AddYears(-age)) age--` correctly handles the case where a customer's birthday has not yet occurred in the as_of year.
- **Unused sourced data**: prefix, sort_name, suffix from customers and the entire segments table are loaded but never used in the output.

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [ExternalModules/CustomerDemographicsBuilder.cs:56], [curated.customer_demographics row count] |
| BR-2 | [ExternalModules/CustomerDemographicsBuilder.cs:65-66] |
| BR-3 | [ExternalModules/CustomerDemographicsBuilder.cs:68-76] |
| BR-4 | [ExternalModules/CustomerDemographicsBuilder.cs:31-38] |
| BR-5 | [ExternalModules/CustomerDemographicsBuilder.cs:44-51] |
| BR-6 | [ExternalModules/CustomerDemographicsBuilder.cs:78] |
| BR-7 | [ExternalModules/CustomerDemographicsBuilder.cs:79] |
| BR-8 | [ExternalModules/CustomerDemographicsBuilder.cs:86] |
| BR-9 | [ExternalModules/CustomerDemographicsBuilder.cs:18-22] |
| BR-10 | [ExternalModules/CustomerDemographicsBuilder.cs], [JobExecutor/Jobs/customer_demographics.json:10] |
| BR-11 | [JobExecutor/Jobs/customer_demographics.json:42], [curated.customer_demographics dates] |
| BR-12 | [ExternalModules/CustomerDemographicsBuilder.cs:59-60] |

## Open Questions
- **Phone/email ordering**: The "first phone" and "first email" depends on the row order returned by PostgreSQL/DataSourcing. The DataSourcing module orders by `as_of`, but within a single as_of date, the order depends on the natural storage order. This means the "primary" designation is effectively arbitrary if a customer has multiple phones or emails. Confidence: MEDIUM that this is intentional (the code does not attempt to sort or prioritize by phone_type/email_type).
- **Unused segments sourcing**: Same as CustomerCreditSummary — the segments table is loaded but never referenced. Confidence: HIGH that this is dead code in the config.
- **Unused customer columns**: prefix, sort_name, suffix are sourced but not used. Confidence: HIGH that they are not needed for this job's output.
