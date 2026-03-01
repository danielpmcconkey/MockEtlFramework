# CommunicationChannelMap -- Business Requirements Document

## Overview
Produces a per-customer communication channel mapping that identifies each customer's preferred marketing channel (Email, SMS, Push, or None) along with their email address and phone number. The output is a flat CSV file used by downstream marketing systems.

## Output Type
CsvFileWriter

## Writer Configuration
- **outputFile:** `Output/curated/communication_channel_map.csv`
- **includeHeader:** true
- **writeMode:** Overwrite
- **lineEnding:** LF
- **trailerFormat:** none

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.customer_preferences | preference_id, customer_id, preference_type, opted_in | Only opted_in = true rows used for channel determination | [communication_channel_map.json:10-11], [CommunicationChannelMapper.cs:67] |
| datalake.customers | id, first_name, last_name | None (all rows in effective date range) | [communication_channel_map.json:15-17] |
| datalake.email_addresses | email_id, customer_id, email_address | None | [communication_channel_map.json:21-23] |
| datalake.phone_numbers | phone_id, customer_id, phone_number | None | [communication_channel_map.json:27-29] |

## Business Rules

BR-1: Each customer receives exactly one row in the output, keyed by customer_id from the customers table.
- Confidence: HIGH
- Evidence: [CommunicationChannelMapper.cs:80-81] -- iterates customers.Rows once per customer

BR-2: Preferred channel is determined by a priority hierarchy: MARKETING_EMAIL > MARKETING_SMS > PUSH_NOTIFICATIONS > None.
- Confidence: HIGH
- Evidence: [CommunicationChannelMapper.cs:89-97] -- if/else-if chain checks in order: MARKETING_EMAIL -> "Email", MARKETING_SMS -> "SMS", PUSH_NOTIFICATIONS -> "Push", else "None"

BR-3: Only opted_in = true preferences are considered when determining preferred channel. Opted-out preferences are ignored.
- Confidence: HIGH
- Evidence: [CommunicationChannelMapper.cs:67-72] -- only adds to prefLookup when optedIn is true

BR-4: If a customer has no email address in email_addresses, the email column is set to "N/A". If a customer has no phone number in phone_numbers, the phone column is set to "" (empty string). This is asymmetric NULL handling.
- Confidence: HIGH
- Evidence: [CommunicationChannelMapper.cs:99-101] -- `"N/A"` for missing email vs `""` for missing phone

BR-5: When a customer has multiple email addresses, the last one encountered wins (dictionary overwrite semantics). Same for phone numbers.
- Confidence: HIGH
- Evidence: [CommunicationChannelMapper.cs:41-43, 51-53] -- `emailLookup[custId] = ...` and `phoneLookup[custId] = ...` overwrite on duplicate customer_id

BR-6: The as_of value is taken from the first row of the customers DataFrame, applied uniformly to all output rows.
- Confidence: HIGH
- Evidence: [CommunicationChannelMapper.cs:76] -- `var asOf = customers.Rows[0]["as_of"]`

BR-7: If the customers DataFrame is null or empty, the output is an empty DataFrame with the output schema columns.
- Confidence: HIGH
- Evidence: [CommunicationChannelMapper.cs:29-33]

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | customers.id | Cast to int via Convert.ToInt32 | [CommunicationChannelMapper.cs:82] |
| first_name | customers.first_name | ToString, null coalesced to "" | [CommunicationChannelMapper.cs:83] |
| last_name | customers.last_name | ToString, null coalesced to "" | [CommunicationChannelMapper.cs:84] |
| preferred_channel | Derived from customer_preferences | Priority: "Email" > "SMS" > "Push" > "None" | [CommunicationChannelMapper.cs:89-97] |
| email | email_addresses.email_address | Last-wins lookup; "N/A" if missing | [CommunicationChannelMapper.cs:100] |
| phone | phone_numbers.phone_number | Last-wins lookup; "" if missing | [CommunicationChannelMapper.cs:101] |
| as_of | customers.Rows[0]["as_of"] | Taken from first customer row | [CommunicationChannelMapper.cs:76] |

## Non-Deterministic Fields
- **email**: When a customer has multiple email addresses, the value depends on row iteration order from DataSourcing, which is database-dependent (no ORDER BY guaranteed).
- **phone**: Same issue as email -- multiple phone numbers for one customer produce non-deterministic results.

## Write Mode Implications
WriteMode is **Overwrite**. Each execution replaces the entire CSV file. For multi-day auto-advance runs, only the last effective date's output persists on disk. Prior days' results are overwritten.

## Edge Cases

1. **Customers with no preferences**: preferred_channel defaults to "None" (no entry in prefLookup means empty HashSet, none of the Contains checks match).
   - Evidence: [CommunicationChannelMapper.cs:86-97]

2. **Customers with multiple opted-in channels**: The priority hierarchy selects the highest priority channel, not all channels.
   - Evidence: [CommunicationChannelMapper.cs:89-97]

3. **NULL handling asymmetry**: Missing email -> "N/A", missing phone -> "" (empty string). This is intentional but unusual.
   - Evidence: [CommunicationChannelMapper.cs:99-101]

4. **Empty customers table**: Returns empty DataFrame with correct schema.
   - Evidence: [CommunicationChannelMapper.cs:29-33]

5. **Preferences across all as_of dates**: The preference lookup iterates ALL rows from all dates in the effective date range, not filtered to a single date. If preferences change across dates, the last-encountered opted_in state wins per customer.
   - Evidence: [CommunicationChannelMapper.cs:59-74] -- no date filtering in the prefs loop

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Channel priority hierarchy | [CommunicationChannelMapper.cs:89-97] |
| Asymmetric NULL handling (email vs phone) | [CommunicationChannelMapper.cs:99-101] |
| Last-wins for duplicate contacts | [CommunicationChannelMapper.cs:41-43, 51-53] |
| Only opted-in preferences count | [CommunicationChannelMapper.cs:67] |
| Output is CSV with header, LF line endings | [communication_channel_map.json:39-44] |
| WriteMode Overwrite | [communication_channel_map.json:43] |
| as_of from first customer row | [CommunicationChannelMapper.cs:76] |

## Open Questions

1. **Non-deterministic contact info**: When a customer has multiple email addresses or phone numbers, the output depends on database row ordering. Is a specific ordering intended (e.g., most recent, primary)?
   - Confidence: MEDIUM -- the code has no explicit ORDER BY or priority logic for duplicates

2. **Cross-date preference accumulation**: Preferences are read across the full effective date range but the lookup doesn't filter by date. If a customer opts in on day 1 and opts out on day 2, the outcome depends on iteration order. Is this intended?
   - Confidence: MEDIUM -- the prefs loop at [CommunicationChannelMapper.cs:59-74] has no date filtering
