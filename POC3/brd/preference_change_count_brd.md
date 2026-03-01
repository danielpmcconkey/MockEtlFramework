# PreferenceChangeCount -- Business Requirements Document

## Overview
Produces a per-customer preference summary showing the total number of preference records, and flags indicating whether the customer has opted in to email marketing and SMS marketing. Despite the name "change count", the job counts total preference rows per customer rather than tracking actual changes over time. Output is Parquet.

## Output Type
ParquetFileWriter

## Writer Configuration
- **outputDirectory:** `Output/curated/preference_change_count/`
- **numParts:** 1
- **writeMode:** Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.customer_preferences | preference_id, customer_id, preference_type, opted_in, updated_date | updated_date sourced but NEVER used in SQL (dead column) | [preference_change_count.json:9-11] |
| datalake.customers | id, prefix, first_name, last_name | prefix sourced but NEVER used in SQL (dead column); id, first_name, last_name also sourced but the SQL only references customer_preferences | [preference_change_count.json:16-18] |

## Business Rules

BR-1: The SQL uses a CTE (all_prefs) that applies RANK() OVER (PARTITION BY customer_id, preference_type ORDER BY preference_id) but the rank value is never used in subsequent logic. It is a dead computation.
- Confidence: HIGH
- Evidence: [preference_change_count.json:22] -- `RANK() OVER (...) AS rnk` is computed in the all_prefs CTE but never referenced in the summary CTE or the final SELECT

BR-2: preference_count is COUNT(*) of all preference rows per customer per as_of date.
- Confidence: HIGH
- Evidence: [preference_change_count.json:22] -- `COUNT(*) AS preference_count` in summary CTE, grouped by customer_id and as_of

BR-3: has_email_opt_in is 1 if the customer has ANY preference row with preference_type = 'MARKETING_EMAIL' AND opted_in = 1, otherwise 0.
- Confidence: HIGH
- Evidence: [preference_change_count.json:22] -- `MAX(CASE WHEN preference_type = 'MARKETING_EMAIL' AND opted_in = 1 THEN 1 ELSE 0 END) AS has_email_opt_in`

BR-4: has_sms_opt_in is 1 if the customer has ANY preference row with preference_type = 'MARKETING_SMS' AND opted_in = 1, otherwise 0.
- Confidence: HIGH
- Evidence: [preference_change_count.json:22] -- `MAX(CASE WHEN preference_type = 'MARKETING_SMS' AND opted_in = 1 THEN 1 ELSE 0 END) AS has_sms_opt_in`

BR-5: Results are grouped by customer_id and as_of. One row per customer per effective date.
- Confidence: HIGH
- Evidence: [preference_change_count.json:22] -- `GROUP BY customer_id, as_of`

BR-6: The customers table is sourced but NEVER referenced in the SQL transformation. Dead-end data source.
- Confidence: HIGH
- Evidence: [preference_change_count.json:16-18] sources customers; the SQL at line 22 only references customer_preferences (aliased as cp in the all_prefs CTE)

BR-7: The updated_date column is sourced from customer_preferences but never used in the SQL.
- Confidence: HIGH
- Evidence: [preference_change_count.json:11] includes updated_date; SQL at line 22 does not reference it

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | customer_preferences.customer_id | GROUP BY key | [preference_change_count.json:22] |
| preference_count | customer_preferences | COUNT(*) per customer per date | [preference_change_count.json:22] |
| has_email_opt_in | customer_preferences.preference_type + opted_in | MAX(CASE) flag: 1 if any MARKETING_EMAIL with opted_in=1 | [preference_change_count.json:22] |
| has_sms_opt_in | customer_preferences.preference_type + opted_in | MAX(CASE) flag: 1 if any MARKETING_SMS with opted_in=1 | [preference_change_count.json:22] |
| as_of | customer_preferences.as_of | GROUP BY key, pass-through | [preference_change_count.json:22] |

## Non-Deterministic Fields
None identified. The SQL is fully deterministic with GROUP BY and aggregate functions.

## Write Mode Implications
WriteMode is **Overwrite**. Each execution replaces the entire Parquet directory. For multi-day auto-advance, only the last effective date's output persists. However, since GROUP BY includes as_of, a single run covering multiple dates produces rows for each date.

## Edge Cases

1. **Dead RANK computation**: The RANK() window function is computed but never used. It adds processing overhead without contributing to the output.
   - Evidence: [preference_change_count.json:22] -- rnk computed in all_prefs CTE, never selected

2. **Customers table unused**: The customers DataFrame is loaded into shared state and registered as a SQLite table but the SQL only references customer_preferences. Dead-end data source.
   - Evidence: [preference_change_count.json:16-18] vs SQL

3. **Misleading name**: "PreferenceChangeCount" suggests tracking changes over time, but the job actually counts total preference rows (which are snapshot rows, not change events). The preference_count column counts ALL preference rows per customer, not changes.
   - Evidence: [preference_change_count.json:22] -- COUNT(*) counts all rows, no change detection logic

4. **Multiple opt-in flags**: has_email_opt_in and has_sms_opt_in use MAX(CASE...) so if a customer has even one MARKETING_EMAIL preference with opted_in=1, the flag is 1, even if they have another MARKETING_EMAIL preference with opted_in=0 on the same date.
   - Evidence: [preference_change_count.json:22] -- MAX() returns the highest value across all matching rows

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| COUNT(*) as preference_count | [preference_change_count.json:22] |
| MAX(CASE) for email opt-in flag | [preference_change_count.json:22] |
| MAX(CASE) for SMS opt-in flag | [preference_change_count.json:22] |
| GROUP BY customer_id, as_of | [preference_change_count.json:22] |
| Dead RANK computation | [preference_change_count.json:22] rnk never referenced |
| Customers table unused | [preference_change_count.json:16-18] vs SQL |
| Overwrite write mode | [preference_change_count.json:29] |

## Open Questions

1. **What is the RANK for?** The RANK() window function is computed but never used. Was it intended for a different version of the query (e.g., to detect first vs. subsequent preference records)?
   - Confidence: HIGH -- clearly unused in the current SQL

2. **Why source customers?** The customers table is loaded but never referenced. Possible leftover from a prior design.
   - Confidence: HIGH -- SQL does not reference it

3. **Is this really "change count"?** The name implies tracking changes over time, but the job counts total preference rows per snapshot date. No actual change detection (comparing day-over-day) is performed.
   - Confidence: HIGH -- the SQL performs no temporal comparison
