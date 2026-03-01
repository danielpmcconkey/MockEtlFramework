# MarketingEligibleCustomers -- Business Requirements Document

## Overview
Identifies customers who are eligible for marketing across BOTH required marketing channels (MARKETING_EMAIL and MARKETING_SMS). Only customers who have opted in to both channels are included. Implements weekend fallback logic and outputs customer details with their email address to CSV.

## Output Type
CsvFileWriter

## Writer Configuration
- **outputFile:** `Output/curated/marketing_eligible_customers.csv`
- **includeHeader:** true
- **writeMode:** Overwrite
- **lineEnding:** LF
- **trailerFormat:** none

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.customer_preferences | preference_id, customer_id, preference_type, opted_in | Only opted_in = true AND preference_type IN ('MARKETING_EMAIL', 'MARKETING_SMS') | [MarketingEligibleProcessor.cs:62-63, 81] |
| datalake.customers | id, prefix, first_name, last_name, suffix, birthdate | prefix, suffix, birthdate sourced but never used (dead columns) | [marketing_eligible_customers.json:15-17], [MarketingEligibleProcessor.cs:34 comment AP4] |
| datalake.email_addresses | email_id, customer_id, email_address, email_type | email_type sourced but never used (dead column) | [marketing_eligible_customers.json:21-23], [MarketingEligibleProcessor.cs:34 comment AP4] |

## Business Rules

BR-1: A customer is marketing-eligible only if they have opted in (opted_in = true) to BOTH required channels: MARKETING_EMAIL and MARKETING_SMS.
- Confidence: HIGH
- Evidence: [MarketingEligibleProcessor.cs:62-63] defines requiredTypes set; [MarketingEligibleProcessor.cs:92] checks `kvp.Value.Count == requiredTypes.Count`

BR-2: Weekend fallback -- on Saturday, use Friday's data (maxDate - 1 day). On Sunday, use Friday's data (maxDate - 2 days). Weekday processing uses the actual effective date.
- Confidence: HIGH
- Evidence: [MarketingEligibleProcessor.cs:20-22]

BR-3: When weekend fallback is active (targetDate != maxDate), only preference rows matching the fallback targetDate are considered. On weekdays, all rows in the effective date range are processed.
- Confidence: HIGH
- Evidence: [MarketingEligibleProcessor.cs:71-75] -- conditional date filter

BR-4: The customer must exist in the customers table to be included.
- Confidence: HIGH
- Evidence: [MarketingEligibleProcessor.cs:92] -- `customerLookup.ContainsKey(kvp.Key)`

BR-5: If a customer has no email address, the email_address column defaults to "" (empty string).
- Confidence: HIGH
- Evidence: [MarketingEligibleProcessor.cs:95] -- `emailLookup.GetValueOrDefault(kvp.Key, "")`

BR-6: The as_of column in the output is set to the targetDate (which may be the Friday fallback date).
- Confidence: HIGH
- Evidence: [MarketingEligibleProcessor.cs:103] -- `["as_of"] = targetDate`

BR-7: The prefix, suffix, and birthdate columns are sourced from customers but never used. The email_type column is sourced from email_addresses but never used. All dead columns.
- Confidence: HIGH
- Evidence: [MarketingEligibleProcessor.cs:34] comment AP4; code at lines 46-47 only extracts first_name and last_name

BR-8: When a customer has multiple email addresses, the last one encountered wins (dictionary overwrite).
- Confidence: HIGH
- Evidence: [MarketingEligibleProcessor.cs:56] -- `emailLookup[custId] = ...` overwrites

BR-9: If customer_preferences or customers are null/empty, output is an empty DataFrame.
- Confidence: HIGH
- Evidence: [MarketingEligibleProcessor.cs:36-39]

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | customerOptIns dictionary key | Int from iteration | [MarketingEligibleProcessor.cs:97] |
| first_name | customers.first_name | ToString, null coalesced to "" | [MarketingEligibleProcessor.cs:94] |
| last_name | customers.last_name | ToString, null coalesced to "" | [MarketingEligibleProcessor.cs:94] |
| email_address | email_addresses.email_address | Last-wins lookup; "" if missing | [MarketingEligibleProcessor.cs:95] |
| as_of | Derived | Set to targetDate (Friday fallback on weekends) | [MarketingEligibleProcessor.cs:103] |

## Non-Deterministic Fields
- **email_address**: When a customer has multiple email addresses, the value depends on database row ordering.
- **Row order**: Output iterates over a dictionary, which has no guaranteed order in .NET.

## Write Mode Implications
WriteMode is **Overwrite**. Each execution replaces the entire CSV file. For multi-day auto-advance runs, only the last effective date's output persists on disk.

## Edge Cases

1. **Customer opted in to 1 of 2 channels**: NOT included. Must opt in to both: MARKETING_EMAIL AND MARKETING_SMS.
   - Evidence: [MarketingEligibleProcessor.cs:92]

2. **Saturday/Sunday execution**: Uses Friday's preference data via weekend fallback.
   - Evidence: [MarketingEligibleProcessor.cs:20-22]

3. **Customer with email opt-in but no email on file**: Still included in output, but email_address is "" (empty string). No email existence validation is performed.
   - Evidence: [MarketingEligibleProcessor.cs:95]

4. **Weekday with multi-date range**: Same pattern as CustomerContactability -- no date filtering on weekdays. All preference rows in the range are considered. A customer who opts in to all 3 channels on any date within the range qualifies, even if they opt out on later dates (the HashSet accumulates opt-ins).
   - Evidence: [MarketingEligibleProcessor.cs:71-75, 82-86]

5. **Preference types not in required set**: PUSH_NOTIFICATIONS, E_STATEMENTS and PAPER_STATEMENTS are ignored (not in the requiredTypes set).
   - Evidence: [MarketingEligibleProcessor.cs:62-63, 81]

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Both marketing channels required | [MarketingEligibleProcessor.cs:62-63, 92] |
| Weekend fallback to Friday | [MarketingEligibleProcessor.cs:20-22] |
| Conditional date filtering | [MarketingEligibleProcessor.cs:71-75] |
| Empty string for missing email | [MarketingEligibleProcessor.cs:95] |
| Unused columns (prefix, suffix, birthdate, email_type) | [MarketingEligibleProcessor.cs:34] |
| Overwrite write mode | [marketing_eligible_customers.json:35] |

## Open Questions

1. **Weekday multi-date accumulation**: On weekdays, opt-ins accumulate across dates without date filtering. A customer who opts in to MARKETING_EMAIL on day 1 and MARKETING_SMS on day 2 would qualify even though they were never opted in to both on the same day. Is this intended?
   - Confidence: MEDIUM -- the HashSet accumulation at [MarketingEligibleProcessor.cs:82-86] never removes entries

2. **No phone requirement**: Unlike CustomerContactability, this job does not require the customer to have a phone number on file. Only customer existence is checked. Is this intentional?
   - Confidence: LOW -- different jobs may have different requirements, but the contrast is notable
