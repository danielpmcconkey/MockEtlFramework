# CustomerContactability -- Business Requirements Document

## Overview
Identifies customers who are contactable for marketing purposes -- those who have opted in to MARKETING_EMAIL and have both a valid email address and phone number on file. Implements weekend fallback logic (Saturday/Sunday use Friday's preference data) and outputs to Parquet.

## Output Type
ParquetFileWriter

## Writer Configuration
- **outputDirectory:** `Output/curated/customer_contactability/`
- **numParts:** 1
- **writeMode:** Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.customer_preferences | preference_id, customer_id, preference_type, opted_in | Only preference_type = 'MARKETING_EMAIL' AND opted_in = true | [CustomerContactabilityProcessor.cs:90] |
| datalake.customers | id, prefix, first_name, last_name, suffix | prefix and suffix sourced but never used (dead columns) | [customer_contactability.json:16-17], [CustomerContactabilityProcessor.cs:37-38 comment AP4] |
| datalake.email_addresses | email_id, customer_id, email_address | Must exist for customer to be included | [CustomerContactabilityProcessor.cs:99] |
| datalake.phone_numbers | phone_id, customer_id, phone_number | Must exist for customer to be included | [CustomerContactabilityProcessor.cs:100] |
| datalake.segments | segment_id, segment_name | Sourced but NEVER used (dead-end data source) | [customer_contactability.json:38-40], [CustomerContactabilityProcessor.cs:37 comment AP1] |

## Business Rules

BR-1: A customer is contactable if they have opted_in = true for preference_type = 'MARKETING_EMAIL'.
- Confidence: HIGH
- Evidence: [CustomerContactabilityProcessor.cs:90] -- `if (optedIn && prefType == "MARKETING_EMAIL") marketingOptIn.Add(custId)`

BR-2: A contactable customer must also have a valid email address AND phone number on file (entries in both email_addresses and phone_numbers tables).
- Confidence: HIGH
- Evidence: [CustomerContactabilityProcessor.cs:98-100] -- three continue checks: customerLookup, emailLookup, phoneLookup must all contain the customer

BR-3: Weekend fallback -- on Saturday, use Friday's data (maxDate - 1 day). On Sunday, use Friday's data (maxDate - 2 days). Weekday processing uses the actual effective date.
- Confidence: HIGH
- Evidence: [CustomerContactabilityProcessor.cs:20-22] -- Saturday subtracts 1, Sunday subtracts 2

BR-4: When weekend fallback is active (targetDate != maxDate), only preference rows matching the fallback targetDate are considered. When not on a weekend, all preference rows in the effective date range are processed.
- Confidence: HIGH
- Evidence: [CustomerContactabilityProcessor.cs:80-84] -- `if (targetDate != maxDate)` then filters by `rowDate != targetDate`

BR-5: The as_of column in the output is set to the targetDate (which may be the Friday fallback date, not the actual effective date).
- Confidence: HIGH
- Evidence: [CustomerContactabilityProcessor.cs:111] -- `["as_of"] = targetDate`

BR-6: The prefix and suffix columns are sourced from the customers table but never used in the output or any logic (dead columns).
- Confidence: HIGH
- Evidence: [customer_contactability.json:17] sources prefix/suffix; [CustomerContactabilityProcessor.cs:51] only extracts first_name and last_name

BR-7: The segments table is sourced but never used in any logic (dead-end data source).
- Confidence: HIGH
- Evidence: [customer_contactability.json:38-40] sources segments; [CustomerContactabilityProcessor.cs:37] confirms with comment "AP1: segments sourced but never used"

BR-8: When a customer has multiple email addresses, the last one encountered wins (dictionary overwrite). Same for phone numbers.
- Confidence: HIGH
- Evidence: [CustomerContactabilityProcessor.cs:60-61, 69-70] -- dictionary assignment overwrites duplicates

BR-9: If customer_preferences or customers DataFrames are null or empty, output is an empty DataFrame.
- Confidence: HIGH
- Evidence: [CustomerContactabilityProcessor.cs:40-44]

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | customer_preferences iteration | Int from marketingOptIn set | [CustomerContactabilityProcessor.cs:95] |
| first_name | customers.first_name | ToString, null coalesced to "" | [CustomerContactabilityProcessor.cs:102] |
| last_name | customers.last_name | ToString, null coalesced to "" | [CustomerContactabilityProcessor.cs:102] |
| email_address | email_addresses.email_address | Last-wins lookup by customer_id | [CustomerContactabilityProcessor.cs:109] |
| phone_number | phone_numbers.phone_number | Last-wins lookup by customer_id | [CustomerContactabilityProcessor.cs:110] |
| as_of | Derived | Set to targetDate (Friday fallback on weekends) | [CustomerContactabilityProcessor.cs:111] |

## Non-Deterministic Fields
- **email_address**: When a customer has multiple email addresses, the value depends on database row ordering (last-wins dictionary overwrite).
- **phone_number**: Same non-determinism as email_address for customers with multiple phone numbers.
- **Row order**: Output rows iterate over a HashSet<int>, which has no guaranteed order.

## Write Mode Implications
WriteMode is **Overwrite**. Each execution replaces the entire Parquet directory. For multi-day auto-advance runs, only the last effective date's output persists.

## Edge Cases

1. **Saturday execution**: Uses Friday's preference data. The as_of in the output reflects Friday's date, not Saturday.
   - Evidence: [CustomerContactabilityProcessor.cs:21]

2. **Sunday execution**: Uses Friday's preference data (2 days back). Same as_of behavior.
   - Evidence: [CustomerContactabilityProcessor.cs:22]

3. **Customer opted in but missing email or phone**: Excluded from output. Must have entries in all three lookups (customers, emails, phones).
   - Evidence: [CustomerContactabilityProcessor.cs:98-100]

4. **Customer opted in to MARKETING_SMS but not MARKETING_EMAIL**: NOT included. Only MARKETING_EMAIL opt-in qualifies.
   - Evidence: [CustomerContactabilityProcessor.cs:90]

5. **Weekday with multiple as_of dates in range**: When targetDate == maxDate (weekday), the date filter is skipped, so ALL preference rows across the entire effective date range are processed. This means a customer who opts out on a later date might still appear if they were opted in on an earlier date (last-encountered state wins).
   - Evidence: [CustomerContactabilityProcessor.cs:80-84]

6. **customers table has no weekend data**: The customers table skips weekends (confirmed by DB query -- no rows for 2024-10-05, 2024-10-06). This is consistent with the weekend fallback logic.
   - Evidence: DB query on datalake.customers GROUP BY as_of

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| MARKETING_EMAIL opt-in filter | [CustomerContactabilityProcessor.cs:90] |
| Must have email AND phone | [CustomerContactabilityProcessor.cs:98-100] |
| Weekend fallback to Friday | [CustomerContactabilityProcessor.cs:20-22] |
| Date filtering on weekends only | [CustomerContactabilityProcessor.cs:80-84] |
| as_of set to targetDate | [CustomerContactabilityProcessor.cs:111] |
| Unused prefix/suffix columns | [CustomerContactabilityProcessor.cs:37-38] |
| Unused segments table | [CustomerContactabilityProcessor.cs:37] |
| Overwrite write mode | [customer_contactability.json:50] |

## Open Questions

1. **Weekday multi-date behavior**: On weekdays, the processor does not filter preferences by date, processing all rows in the effective date range. If a customer's opt-in status changes across dates, the outcome is non-deterministic (depends on iteration order). Is this intended?
   - Confidence: MEDIUM -- the conditional date filter at [CustomerContactabilityProcessor.cs:80-84] suggests the developer only intended date filtering for weekend fallback scenarios
