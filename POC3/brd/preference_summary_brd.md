# PreferenceSummary -- Business Requirements Document

## Overview
Produces an aggregate summary of customer preferences by preference type, showing counts of opted-in customers, opted-out customers, and total customers for each preference type. Output is CSV with a trailer line containing the row count and date.

## Output Type
CsvFileWriter

## Writer Configuration
- **outputFile:** `Output/curated/preference_summary.csv`
- **includeHeader:** true
- **trailerFormat:** `TRAILER|{row_count}|{date}`
- **writeMode:** Overwrite
- **lineEnding:** LF

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.customer_preferences | preference_id, customer_id, preference_type, opted_in, updated_date | updated_date sourced but NEVER used (dead column) | [preference_summary.json:9-11], [PreferenceSummaryCounter.cs:28-42] |
| datalake.customers | id, first_name, last_name | Sourced but NEVER used (dead-end data source) | [preference_summary.json:16-18]; PreferenceSummaryCounter.cs does not reference customers |

## Business Rules

BR-1: Preferences are grouped by preference_type. For each type, opted_in_count and opted_out_count are tallied.
- Confidence: HIGH
- Evidence: [PreferenceSummaryCounter.cs:28-42] -- iterates prefs rows, increments optedIn or optedOut per prefType

BR-2: total_customers is calculated as opted_in_count + opted_out_count for each preference type.
- Confidence: HIGH
- Evidence: [PreferenceSummaryCounter.cs:52] -- `["total_customers"] = kvp.Value.optedIn + kvp.Value.optedOut`

BR-3: The as_of value is taken from the first row of the customer_preferences DataFrame, applied uniformly to all output rows.
- Confidence: HIGH
- Evidence: [PreferenceSummaryCounter.cs:25] -- `var asOf = prefs.Rows[0]["as_of"]`

BR-4: The customers table is sourced but never used in any logic. Dead-end data source.
- Confidence: HIGH
- Evidence: [preference_summary.json:16-18] sources customers; PreferenceSummaryCounter.cs does not reference any "customers" DataFrame

BR-5: The updated_date column is sourced from customer_preferences but never used.
- Confidence: HIGH
- Evidence: [preference_summary.json:11] includes updated_date; PreferenceSummaryCounter.cs does not reference it

BR-6: If customer_preferences is null or empty, output is an empty DataFrame.
- Confidence: HIGH
- Evidence: [PreferenceSummaryCounter.cs:19-23]

BR-7: The trailer row_count reflects the actual number of output data rows (number of distinct preference types). This is the standard CsvFileWriter trailer behavior using the framework's `{row_count}` token.
- Confidence: HIGH
- Evidence: [preference_summary.json:28] -- trailerFormat uses `{row_count}`; CsvFileWriter handles substitution per Architecture.md

BR-8: Output row order depends on Dictionary iteration order in C#, which is insertion order for Dictionary<string, ...>. Rows appear in the order preference types are first encountered in the input.
- Confidence: MEDIUM
- Evidence: [PreferenceSummaryCounter.cs:44-55] -- iterates over `counts` dictionary; .NET Dictionary iterates in insertion order when no deletions occur

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| preference_type | customer_preferences.preference_type | ToString, null coalesced to "" | [PreferenceSummaryCounter.cs:31] |
| opted_in_count | customer_preferences.opted_in | Count of rows where opted_in = true per preference_type | [PreferenceSummaryCounter.cs:38-39] |
| opted_out_count | customer_preferences.opted_in | Count of rows where opted_in = false per preference_type | [PreferenceSummaryCounter.cs:40-41] |
| total_customers | Derived | opted_in_count + opted_out_count | [PreferenceSummaryCounter.cs:52] |
| as_of | customer_preferences.Rows[0]["as_of"] | From first row of prefs DataFrame | [PreferenceSummaryCounter.cs:25] |

## Non-Deterministic Fields
None identified. The aggregation is deterministic. Row order depends on input order but the values are deterministic.

## Write Mode Implications
WriteMode is **Overwrite**. Each execution replaces the entire CSV file. For multi-day auto-advance runs, only the last effective date's output persists on disk.

## Edge Cases

1. **Cross-date accumulation**: Since no date filter is applied, if the effective date range spans multiple days, opted_in_count and opted_out_count accumulate across all dates. A preference type with 100 customers per day across 5 days would show total_customers = 500, not 100.
   - Evidence: [PreferenceSummaryCounter.cs:28-42] -- no date filtering

2. **Customers table wasted**: The customers DataFrame is loaded but the External module only uses customer_preferences.
   - Evidence: [preference_summary.json:16-18] vs PreferenceSummaryCounter.cs

3. **Preference types in output**: Based on DB data, the distinct preference types are: E_STATEMENTS, MARKETING_EMAIL, MARKETING_SMS, PAPER_STATEMENTS, PUSH_NOTIFICATIONS. Each gets one row in the output.
   - Evidence: DB query `SELECT DISTINCT preference_type FROM datalake.customer_preferences`

4. **as_of from first row**: The as_of value comes from the first row of the DataFrame. In a multi-date range, this would be the earliest date (min effective date), since DataSourcing fetches data ordered by the date range.
   - Evidence: [PreferenceSummaryCounter.cs:25]

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Group by preference_type | [PreferenceSummaryCounter.cs:28-42] |
| total_customers = opted_in + opted_out | [PreferenceSummaryCounter.cs:52] |
| as_of from first prefs row | [PreferenceSummaryCounter.cs:25] |
| Customers table unused | [preference_summary.json:16-18] vs code |
| updated_date unused | [preference_summary.json:11] vs code |
| Trailer with row_count and date | [preference_summary.json:28] |
| Overwrite write mode | [preference_summary.json:29] |

## Open Questions

1. **Cross-date accumulation vs single-date**: The counts accumulate across all dates in the effective range. If the intention is a snapshot summary for a single date, this produces inflated counts. Is the multi-date accumulation intentional?
   - Confidence: MEDIUM -- no date filtering is applied, which could be an oversight or intentional design for cumulative reporting

2. **Why source customers?** The customers table is loaded but never used. Possible leftover from a prior design iteration.
   - Confidence: HIGH -- the External module code does not reference it
