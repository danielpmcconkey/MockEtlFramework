# DoNotContactList -- Business Requirements Document

## Overview
Produces a list of customers who have opted out of ALL their preferences (every preference row has opted_in = false). These customers should not be contacted through any channel. Output is CSV with a trailer line, and the job skips processing entirely on Sundays.

## Output Type
CsvFileWriter

## Writer Configuration
- **outputFile:** `Output/curated/do_not_contact_list.csv`
- **includeHeader:** true
- **trailerFormat:** `TRAILER|{row_count}|{date}`
- **writeMode:** Overwrite
- **lineEnding:** LF

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.customer_preferences | preference_id, customer_id, preference_type, opted_in | All rows processed; customer included only if ALL their preferences have opted_in = false | [do_not_contact_list.json:9-11], [DoNotContactProcessor.cs:48-62] |
| datalake.customers | id, first_name, last_name | Used for name lookup; customer must exist in this table | [do_not_contact_list.json:15-17], [DoNotContactProcessor.cs:40-45] |

## Business Rules

BR-1: A customer appears on the do-not-contact list only if they have opted out of ALL preferences (every preference row has opted_in = false, and they have at least one preference row).
- Confidence: HIGH
- Evidence: [DoNotContactProcessor.cs:70] -- `kvp.Value.total > 0 && kvp.Value.total == kvp.Value.optedOut`

BR-2: Sunday skip -- if the effective date (maxEffectiveDate) falls on a Sunday, the processor returns an empty DataFrame immediately. No data is processed.
- Confidence: HIGH
- Evidence: [DoNotContactProcessor.cs:20-24] -- checks `maxDate.DayOfWeek == DayOfWeek.Sunday`, returns empty output

BR-3: The customer must exist in the customers table to be included (customerLookup.ContainsKey check).
- Confidence: HIGH
- Evidence: [DoNotContactProcessor.cs:70] -- `customerLookup.ContainsKey(kvp.Key)`

BR-4: The as_of value is taken from the first row of the customer_preferences DataFrame, applied uniformly to all output rows.
- Confidence: HIGH
- Evidence: [DoNotContactProcessor.cs:64] -- `var asOf = prefs.Rows[0]["as_of"]`

BR-5: The trailer line format is `TRAILER|{row_count}|{date}` where row_count is the number of data rows and date is the max effective date from shared state.
- Confidence: HIGH
- Evidence: [do_not_contact_list.json:29] -- trailerFormat definition; CsvFileWriter handles token substitution per Architecture.md

BR-6: Preferences are not filtered by date -- all rows across the entire effective date range are counted for each customer's opt-out determination.
- Confidence: HIGH
- Evidence: [DoNotContactProcessor.cs:49-62] -- no date filter in the prefs iteration loop

BR-7: If customer_preferences or customers are null/empty, output is an empty DataFrame.
- Confidence: HIGH
- Evidence: [DoNotContactProcessor.cs:33-37]

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | customer_preferences iteration | Int key from customerPrefs dictionary | [DoNotContactProcessor.cs:73] |
| first_name | customers.first_name | ToString, null coalesced to "" | [DoNotContactProcessor.cs:72] |
| last_name | customers.last_name | ToString, null coalesced to "" | [DoNotContactProcessor.cs:72] |
| as_of | customer_preferences.Rows[0]["as_of"] | Taken from first preferences row | [DoNotContactProcessor.cs:64] |

## Non-Deterministic Fields
- **as_of**: Technically deterministic (first row's as_of), but the value depends on which date's data appears first in the DataFrame, which is the min effective date in the range.

## Write Mode Implications
WriteMode is **Overwrite**. Each execution replaces the entire CSV file. On Sundays, this means the previous file is replaced with headers-only plus a trailer with row_count=0. For multi-day auto-advance, only the last effective date's output persists.

## Edge Cases

1. **Sunday execution**: Returns an empty DataFrame. The CsvFileWriter will write a header row and a trailer with row_count=0. Previous day's results are overwritten.
   - Evidence: [DoNotContactProcessor.cs:20-24]

2. **Saturday execution**: Normal processing occurs (no special handling). Only Sunday is skipped.
   - Evidence: [DoNotContactProcessor.cs:20] -- only checks for Sunday

3. **Customer with mixed preferences**: If a customer has 5 preferences and opts out of 4 but stays opted in to 1, they are NOT on the do-not-contact list (total != optedOut).
   - Evidence: [DoNotContactProcessor.cs:70]

4. **Customer with zero preferences**: Not included. The check requires `total > 0`.
   - Evidence: [DoNotContactProcessor.cs:70]

5. **Customer in preferences but not in customers table**: Excluded by the customerLookup check.
   - Evidence: [DoNotContactProcessor.cs:70]

6. **Multi-date accumulation**: Across multiple as_of dates, a customer's preference counts accumulate. If a customer has 3 preferences per date and the range covers 5 dates, they have 15 total rows. All 15 must have opted_in = false to qualify. This means a customer who opts out on all dates except one will NOT appear.
   - Evidence: [DoNotContactProcessor.cs:49-62] -- no date partitioning

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| All preferences must be opted out | [DoNotContactProcessor.cs:70] |
| Sunday skip | [DoNotContactProcessor.cs:20-24] |
| Customer must exist in customers table | [DoNotContactProcessor.cs:70] |
| Trailer with row count and date | [do_not_contact_list.json:29] |
| as_of from first prefs row | [DoNotContactProcessor.cs:64] |
| Overwrite write mode | [do_not_contact_list.json:30] |
| LF line endings | [do_not_contact_list.json:31] |

## Open Questions

1. **Multi-date preference counting**: Since preferences span the full date range without partitioning, the "all opted out" check aggregates across dates. A customer who was fully opted out on day 1 but re-opted-in on day 2 would not appear. Is this cross-date aggregation intentional?
   - Confidence: MEDIUM -- the code has no date partitioning, but logically a "do not contact" list might be intended to reflect current-day status only

2. **Sunday overwrite**: On Sundays, the Overwrite mode replaces any existing file with an empty result. Should Sunday runs be skipped entirely (no file write) instead of writing an empty file?
   - Confidence: LOW -- the code returns before any processing, but the CsvFileWriter still runs and writes the empty DataFrame
