# SecuritiesDirectory — Business Requirements Document

## Overview
Produces a directory listing of all securities with their attributes, ordered by security_id. Uses a SQL Transformation module rather than an External module. The holdings table is sourced but not used in the transformation SQL — only securities data appears in the output.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `securities_dir`
- **outputFile**: `Output/curated/securities_directory.csv`
- **includeHeader**: true
- **writeMode**: Overwrite
- **lineEnding**: LF
- **trailerFormat**: not specified (no trailer)

Evidence: [securities_directory.json:24-31]

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.securities | security_id, ticker, security_name, security_type, sector, exchange | All columns used in the Transformation SQL | [securities_directory.json:6-11] |
| datalake.holdings | holding_id, investment_id, security_id, customer_id, quantity, current_value | Sourced but NOT referenced in the Transformation SQL | [securities_directory.json:13-18] |

### Table Schemas (from database)

**securities**: security_id (integer), ticker (varchar), security_name (varchar), security_type (varchar), sector (varchar), exchange (varchar), as_of (date)

**holdings**: holding_id (integer), investment_id (integer), security_id (integer), customer_id (integer), quantity (numeric), cost_basis (numeric), current_value (numeric), as_of (date)

## Business Rules

BR-1: The Transformation SQL selects all securities columns plus `as_of`, ordered by `security_id`.
- Confidence: HIGH
- Evidence: [securities_directory.json:22] — `SELECT s.security_id, s.ticker, s.security_name, s.security_type, s.sector, s.exchange, s.as_of FROM securities s ORDER BY s.security_id`

BR-2: No filtering is applied — all securities rows for the effective date range are included in the output.
- Confidence: HIGH
- Evidence: [securities_directory.json:22] — SQL has no WHERE clause

BR-3: Holdings data is sourced but not referenced in the SQL. It is registered as a SQLite table but never queried.
- Confidence: HIGH
- Evidence: [securities_directory.json:13-18] sources holdings; SQL only references `securities s`

BR-4: The result is stored in shared state as `securities_dir` (not `output`), and the CsvFileWriter reads from `securities_dir`.
- Confidence: HIGH
- Evidence: [securities_directory.json:21] — `"resultName": "securities_dir"`; [securities_directory.json:26] — `"source": "securities_dir"`

BR-5: Data is sourced for the effective date range injected by the executor (no explicit date filters in the job config).
- Confidence: HIGH
- Evidence: [securities_directory.json] — no minEffectiveDate/maxEffectiveDate in DataSourcing modules

BR-6: The `as_of` column is included in the output, sourced from the securities table. Unlike External module jobs, this preserves the per-row `as_of` from the source data.
- Confidence: HIGH
- Evidence: [securities_directory.json:22] — `s.as_of` in SELECT list

BR-7: Output is ordered by `security_id` ascending (default SQL ORDER BY direction).
- Confidence: HIGH
- Evidence: [securities_directory.json:22] — `ORDER BY s.security_id`

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| security_id | securities.security_id | Pass-through via SQL | [securities_directory.json:22] |
| ticker | securities.ticker | Pass-through via SQL | [securities_directory.json:22] |
| security_name | securities.security_name | Pass-through via SQL | [securities_directory.json:22] |
| security_type | securities.security_type | Pass-through via SQL | [securities_directory.json:22] |
| sector | securities.sector | Pass-through via SQL | [securities_directory.json:22] |
| exchange | securities.exchange | Pass-through via SQL | [securities_directory.json:22] |
| as_of | securities.as_of | Pass-through via SQL | [securities_directory.json:22] |

## Non-Deterministic Fields
None identified. All values are pass-throughs from source data with a deterministic ORDER BY.

## Write Mode Implications
- **Overwrite** mode: Each run replaces the entire CSV output file.
- For multi-day auto-advance runs, only the final effective date's output persists. Prior dates are overwritten.
- Since there is no date filtering in the SQL and all rows in the effective date range are included, the output for a multi-day range would contain rows for multiple as_of dates (one row per security per date).

## Edge Cases

1. **Holdings sourced but unused**: The holdings DataSourcing module runs but the SQL never queries the holdings table. This adds unnecessary database overhead. (HIGH confidence — SQL only references `securities s`)

2. **Multi-day effective date range**: With 50 securities per day and no WHERE clause, a 92-day range would produce 4,600 rows in the output. The ORDER BY is by security_id only, so rows for the same security across different dates would be interleaved or grouped by their security_id. (HIGH confidence — SQL analysis)

3. **Securities table has weekend data**: Unlike holdings/investments/customers, the securities table has data for all calendar days (weekends included). This means a weekend effective date would still produce output. (MEDIUM confidence — database observation shows 92 dates including weekends for securities vs 66 weekday-only dates for holdings)

4. **NULL values in securities**: Any NULL values in securities columns are passed through as-is. The CsvFileWriter would render them according to its NULL handling (typically empty string in CSV). (MEDIUM confidence — framework behavior)

5. **RFC 4180 quoting**: The CsvFileWriter handles quoting per the framework's implementation. Values containing commas or quotes would be properly escaped. This is safer than the direct-write External modules. (HIGH confidence — Architecture.md documents RFC 4180 quoting for CsvFileWriter)

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| SQL SELECT all securities columns | [securities_directory.json:22] |
| ORDER BY security_id | [securities_directory.json:22] |
| No WHERE clause (all rows) | [securities_directory.json:22] |
| Holdings sourced but unused | [securities_directory.json:13-18] vs SQL |
| Result stored as securities_dir | [securities_directory.json:21] |
| CsvFileWriter reads securities_dir | [securities_directory.json:26] |
| CSV output with header, no trailer | [securities_directory.json:24-31] |

## Open Questions

1. **Why are holdings sourced?**: The job config sources the holdings table but the SQL never references it. This may be a leftover from a design that intended to join holdings with securities (e.g., to show which securities are held). (HIGH confidence — clear from SQL analysis)

2. **Duplicate rows across dates**: For multi-day runs, the same security appears once per date. It's unclear whether downstream consumers expect a single snapshot or a multi-date listing. (MEDIUM confidence)
