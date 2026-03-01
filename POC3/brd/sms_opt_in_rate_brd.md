# SmsOptInRate -- Business Requirements Document

## Overview
Calculates the SMS marketing opt-in rate per customer segment, showing how many customers in each segment have opted in to MARKETING_SMS preferences versus the total count. This is the SMS counterpart to the EmailOptInRate job. Output is Parquet with one part file, overwritten each run.

## Output Type
ParquetFileWriter

## Writer Configuration
- **outputDirectory:** `Output/curated/sms_opt_in_rate/`
- **numParts:** 1
- **writeMode:** Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.customer_preferences | preference_id, customer_id, preference_type, opted_in | WHERE preference_type = 'MARKETING_SMS' | [sms_opt_in_rate.json:9-11], SQL at line 28 |
| datalake.customers_segments | customer_id, segment_id | JOIN key between preferences and segments | [sms_opt_in_rate.json:16-17] |
| datalake.segments | segment_id, segment_name | JOIN to resolve segment_id to segment_name | [sms_opt_in_rate.json:21-23] |

## Business Rules

BR-1: Only MARKETING_SMS preferences are included in the calculation. Other preference types are filtered out.
- Confidence: HIGH
- Evidence: [sms_opt_in_rate.json:28] -- `WHERE cp.preference_type = 'MARKETING_SMS'`

BR-2: Opt-in count is the number of MARKETING_SMS preference rows where opted_in = 1, per segment.
- Confidence: HIGH
- Evidence: [sms_opt_in_rate.json:28] -- `SUM(CASE WHEN cp.opted_in = 1 THEN 1 ELSE 0 END) AS opted_in_count`

BR-3: Total count is the total number of MARKETING_SMS preference rows per segment (opted_in = 1 or 0).
- Confidence: HIGH
- Evidence: [sms_opt_in_rate.json:28] -- `COUNT(*) AS total_count`

BR-4: Opt-in rate is calculated as integer division: `CAST(opted_in_count AS INTEGER) / CAST(total_count AS INTEGER)`. This produces integer division (truncating), so the rate will be 0 unless 100% of customers opted in, in which case it will be 1.
- Confidence: HIGH
- Evidence: [sms_opt_in_rate.json:28] -- `CAST(SUM(...) AS INTEGER) / CAST(COUNT(*) AS INTEGER) AS opt_in_rate`

BR-5: Results are grouped by segment_name and as_of date.
- Confidence: HIGH
- Evidence: [sms_opt_in_rate.json:28] -- `GROUP BY s.segment_name, cp.as_of`

BR-6: Customers are linked to segments via the customers_segments junction table. A customer without a segment mapping is excluded (INNER JOINs).
- Confidence: HIGH
- Evidence: [sms_opt_in_rate.json:28] -- `JOIN customers_segments cs ON cp.customer_id = cs.customer_id JOIN segments s ON cs.segment_id = s.segment_id`

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| segment_name | segments.segment_name | Via JOIN through customers_segments | [sms_opt_in_rate.json:28] |
| opted_in_count | customer_preferences.opted_in | SUM of CASE WHEN opted_in = 1 | [sms_opt_in_rate.json:28] |
| total_count | customer_preferences | COUNT(*) of MARKETING_SMS rows per segment | [sms_opt_in_rate.json:28] |
| opt_in_rate | Derived | INTEGER division: opted_in_count / total_count (always 0 or 1) | [sms_opt_in_rate.json:28] |
| as_of | customer_preferences.as_of | GROUP BY key, pass-through | [sms_opt_in_rate.json:28] |

## Non-Deterministic Fields
None identified. The SQL is fully deterministic given the GROUP BY and aggregate functions.

## Write Mode Implications
WriteMode is **Overwrite**. Each execution replaces the entire Parquet directory. For multi-day auto-advance runs, only the last effective date's output persists on disk. However, since the GROUP BY includes as_of, a single run covering multiple dates produces one row per segment per date.

## Edge Cases

1. **Integer division produces 0 or 1 only**: The opt_in_rate uses integer division (`CAST AS INTEGER / CAST AS INTEGER`), which truncates the decimal. Unless 100% of customers in a segment opted in, the rate will be 0. This is almost certainly a bug -- same issue as EmailOptInRate.
   - Evidence: [sms_opt_in_rate.json:28] -- `CAST(...AS INTEGER) / CAST(...AS INTEGER)`

2. **Customer in multiple segments**: If a customer appears in customers_segments multiple times (different segment_ids), their preference counts toward each segment independently.
   - Evidence: JOIN logic -- each customers_segments row creates a separate match

3. **No MARKETING_SMS preferences**: If no customer has a MARKETING_SMS preference, the WHERE clause filters all rows and the result is empty.

4. **Structural twin of EmailOptInRate**: This job is structurally identical to EmailOptInRate with only the preference_type filter changed from 'MARKETING_EMAIL' to 'MARKETING_SMS'. Same integer division bug, same schema, same join logic.
   - Evidence: Compare [sms_opt_in_rate.json:28] with [email_opt_in_rate.json:36] -- identical SQL structure with different WHERE value

5. **No dead-end data sources**: Unlike EmailOptInRate, this job does NOT source phone_numbers. All three sourced tables are used.
   - Evidence: [sms_opt_in_rate.json] -- only 3 DataSourcing modules, all referenced in SQL

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Filter to MARKETING_SMS only | [sms_opt_in_rate.json:28] WHERE clause |
| Integer division for rate | [sms_opt_in_rate.json:28] CAST expressions |
| GROUP BY segment_name and as_of | [sms_opt_in_rate.json:28] |
| INNER JOIN through customers_segments | [sms_opt_in_rate.json:28] |
| Overwrite write mode | [sms_opt_in_rate.json:36] |

## Open Questions

1. **Integer division bug**: Same as EmailOptInRate -- the opt_in_rate will always be 0 or 1 due to integer division. Should use `CAST(... AS REAL)` or `CAST(... AS FLOAT)` for a meaningful rate calculation.
   - Confidence: HIGH -- integer division in a "rate" calculation is a classic bug; identical to EmailOptInRate
