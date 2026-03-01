# EmailOptInRate -- Business Requirements Document

## Overview
Calculates the email marketing opt-in rate per customer segment, showing how many customers in each segment have opted in to MARKETING_EMAIL preferences versus the total count. Output is Parquet with one part file, overwritten each run.

## Output Type
ParquetFileWriter

## Writer Configuration
- **outputDirectory:** `Output/curated/email_opt_in_rate/`
- **numParts:** 1
- **writeMode:** Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.customer_preferences | preference_id, customer_id, preference_type, opted_in | WHERE preference_type = 'MARKETING_EMAIL' | [email_opt_in_rate.json:9-11], SQL WHERE clause at line 36 |
| datalake.customers_segments | customer_id, segment_id | JOIN key between preferences and segments | [email_opt_in_rate.json:15-17] |
| datalake.segments | segment_id, segment_name | JOIN to resolve segment_id to segment_name | [email_opt_in_rate.json:21-23] |
| datalake.phone_numbers | phone_id, customer_id, phone_type, phone_number | Sourced but NEVER used in transformation SQL (dead-end data source) | [email_opt_in_rate.json:27-30] |

## Business Rules

BR-1: Only MARKETING_EMAIL preferences are included in the calculation. Other preference types are filtered out.
- Confidence: HIGH
- Evidence: [email_opt_in_rate.json:36] -- `WHERE cp.preference_type = 'MARKETING_EMAIL'`

BR-2: Opt-in count is the number of MARKETING_EMAIL preference rows where opted_in = 1 (true), per segment.
- Confidence: HIGH
- Evidence: [email_opt_in_rate.json:36] -- `SUM(CASE WHEN cp.opted_in = 1 THEN 1 ELSE 0 END) AS opted_in_count`

BR-3: Total count is the total number of MARKETING_EMAIL preference rows per segment (opted_in = 1 or 0).
- Confidence: HIGH
- Evidence: [email_opt_in_rate.json:36] -- `COUNT(*) AS total_count`

BR-4: Opt-in rate is calculated as integer division: `CAST(opted_in_count AS INTEGER) / CAST(total_count AS INTEGER)`. This produces integer division (truncating), so the rate will be 0 unless 100% of customers opted in, in which case it will be 1.
- Confidence: HIGH
- Evidence: [email_opt_in_rate.json:36] -- `CAST(SUM(...) AS INTEGER) / CAST(COUNT(*) AS INTEGER) AS opt_in_rate`

BR-5: Results are grouped by segment_name and as_of date.
- Confidence: HIGH
- Evidence: [email_opt_in_rate.json:36] -- `GROUP BY s.segment_name, cp.as_of`

BR-6: Customers are linked to segments via the customers_segments junction table. A customer without a segment mapping is excluded (INNER JOINs).
- Confidence: HIGH
- Evidence: [email_opt_in_rate.json:36] -- `JOIN customers_segments cs ON cp.customer_id = cs.customer_id JOIN segments s ON cs.segment_id = s.segment_id`

BR-7: The phone_numbers table is sourced but never referenced in the SQL. Dead-end data source.
- Confidence: HIGH
- Evidence: [email_opt_in_rate.json:27-30] sources phone_numbers; SQL at line 36 does not reference it

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| segment_name | segments.segment_name | Via JOIN through customers_segments | [email_opt_in_rate.json:36] |
| opted_in_count | customer_preferences.opted_in | SUM of CASE WHEN opted_in = 1 | [email_opt_in_rate.json:36] |
| total_count | customer_preferences | COUNT(*) of MARKETING_EMAIL rows per segment | [email_opt_in_rate.json:36] |
| opt_in_rate | Derived | INTEGER division: opted_in_count / total_count (always 0 or 1) | [email_opt_in_rate.json:36] |
| as_of | customer_preferences.as_of | GROUP BY key, pass-through | [email_opt_in_rate.json:36] |

## Non-Deterministic Fields
None identified. The SQL is fully deterministic given the GROUP BY and aggregate functions.

## Write Mode Implications
WriteMode is **Overwrite**. Each execution replaces the entire Parquet directory. For multi-day auto-advance runs, only the last effective date's output persists on disk. However, since the GROUP BY includes as_of, a single run that covers multiple dates will produce one row per segment per date.

## Edge Cases

1. **Integer division produces 0 or 1 only**: The opt_in_rate uses integer division (`CAST AS INTEGER / CAST AS INTEGER`), which truncates the decimal. Unless 100% of customers in a segment opted in, the rate will be 0. This is almost certainly a bug -- the intended calculation likely uses float/decimal division.
   - Evidence: [email_opt_in_rate.json:36] -- `CAST(...AS INTEGER) / CAST(...AS INTEGER)`

2. **Customer in multiple segments**: If a customer appears in customers_segments multiple times (different segment_ids), their preference counts toward each segment independently.
   - Evidence: JOIN logic -- each customers_segments row creates a separate match

3. **No MARKETING_EMAIL preferences**: If no customer has a MARKETING_EMAIL preference, the WHERE clause filters all rows and the result is empty.

4. **Phone numbers sourced but unused**: The phone_numbers DataFrame is loaded into shared state and registered as a SQLite table but never queried in the SQL.
   - Evidence: [email_opt_in_rate.json:27-30]

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Filter to MARKETING_EMAIL only | [email_opt_in_rate.json:36] WHERE clause |
| Integer division for rate | [email_opt_in_rate.json:36] CAST expressions |
| GROUP BY segment_name and as_of | [email_opt_in_rate.json:36] |
| INNER JOIN through customers_segments | [email_opt_in_rate.json:36] |
| Phone numbers unused | [email_opt_in_rate.json:27-30] vs SQL |
| Overwrite write mode | [email_opt_in_rate.json:43] |

## Open Questions

1. **Integer division bug**: The opt_in_rate will always be 0 or 1 due to integer division. This is almost certainly unintended -- a real opt-in rate should be a decimal (e.g., 0.65). Should this use `CAST(... AS REAL)` or `CAST(... AS FLOAT)` instead?
   - Confidence: HIGH -- integer division in a "rate" calculation is a classic bug

2. **Why is phone_numbers sourced?** It has no role in the email opt-in rate calculation. Possible copy-paste from another job config.
   - Confidence: HIGH -- the SQL does not reference phone_numbers
