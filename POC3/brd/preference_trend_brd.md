# PreferenceTrend -- Business Requirements Document

## Overview
Tracks the trend of preference opt-in and opt-out counts over time by preference type and date. Each row represents one preference type on one date, showing how many customers opted in vs. opted out. Output is CSV in Append mode, building a cumulative historical record across runs.

## Output Type
CsvFileWriter

## Writer Configuration
- **outputFile:** `Output/curated/preference_trend.csv`
- **includeHeader:** true
- **writeMode:** Append
- **lineEnding:** LF
- **trailerFormat:** none

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.customer_preferences | preference_id, customer_id, preference_type, opted_in | All rows processed; grouped by preference_type and as_of | [preference_trend.json:9-11], SQL at line 15 |

## Business Rules

BR-1: For each (preference_type, as_of) group, opted_in_count is the number of rows with opted_in = 1 (true).
- Confidence: HIGH
- Evidence: [preference_trend.json:15] -- `SUM(CASE WHEN cp.opted_in = 1 THEN 1 ELSE 0 END) AS opted_in_count`

BR-2: For each (preference_type, as_of) group, opted_out_count is the number of rows with opted_in = 0 (false).
- Confidence: HIGH
- Evidence: [preference_trend.json:15] -- `SUM(CASE WHEN cp.opted_in = 0 THEN 1 ELSE 0 END) AS opted_out_count`

BR-3: Results are grouped by preference_type and as_of. One row per preference type per date.
- Confidence: HIGH
- Evidence: [preference_trend.json:15] -- `GROUP BY cp.preference_type, cp.as_of`

BR-4: No ordering is specified in the SQL. Output row order is determined by SQLite's GROUP BY implementation.
- Confidence: HIGH
- Evidence: [preference_trend.json:15] -- SQL has no ORDER BY clause

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| preference_type | customer_preferences.preference_type | GROUP BY key, direct pass-through | [preference_trend.json:15] |
| opted_in_count | customer_preferences.opted_in | SUM of CASE WHEN opted_in = 1 | [preference_trend.json:15] |
| opted_out_count | customer_preferences.opted_in | SUM of CASE WHEN opted_in = 0 | [preference_trend.json:15] |
| as_of | customer_preferences.as_of | GROUP BY key, direct pass-through | [preference_trend.json:15] |

## Non-Deterministic Fields
None identified. The GROUP BY with SUM aggregations is fully deterministic.

## Write Mode Implications
WriteMode is **Append**. Each execution appends new data to the existing CSV file WITHOUT removing prior data. This has significant implications:

1. **Header on first write only**: CsvFileWriter suppresses the header when appending to an existing file. The header is written only on the first execution (when the file does not yet exist). Subsequent appends add data rows only.
   - Evidence: [CsvFileWriter.cs:47] -- `if (_includeHeader && !append)` -- header is only written when NOT in append mode (i.e., file does not exist yet)

2. **Data duplication**: If the same effective date range is re-run, the same data rows will be appended again, creating duplicates.

3. **Cumulative growth**: The file grows with each execution. Over time, it builds a complete historical record of preference trends across all processed dates.

## Edge Cases

1. **Header suppressed on append**: CsvFileWriter suppresses the header when appending to an existing file (`if (_includeHeader && !append)`). The header only appears once at the top of the file, written during the first execution when the file is created.
   - Evidence: [CsvFileWriter.cs:42, 47] -- `var append = _writeMode == WriteMode.Append && File.Exists(resolvedPath)` and `if (_includeHeader && !append)`

2. **Multi-date range in single run**: If the effective date range spans multiple dates, the GROUP BY as_of produces one row per preference type per date within the range. All are appended in a single write.

3. **Re-run duplication**: No deduplication mechanism exists. Re-running for a previously processed date range appends duplicate rows.

4. **No trailer**: Unlike some other jobs, this one has no trailer line.
   - Evidence: [preference_trend.json:17-22] -- no trailerFormat specified

5. **All preference types included**: Unlike EmailOptInRate (which filters to MARKETING_EMAIL only), this job includes all preference types: E_STATEMENTS, MARKETING_EMAIL, MARKETING_SMS, PAPER_STATEMENTS, PUSH_NOTIFICATIONS.
   - Evidence: [preference_trend.json:15] -- no WHERE clause filtering preference_type

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| SUM for opted_in_count | [preference_trend.json:15] |
| SUM for opted_out_count | [preference_trend.json:15] |
| GROUP BY preference_type, as_of | [preference_trend.json:15] |
| Append write mode | [preference_trend.json:22] |
| Include header | [preference_trend.json:20] |
| LF line endings | [preference_trend.json:21] |
| No trailer | [preference_trend.json:17-22] -- absent |

## Open Questions

1. **No ORDER BY**: The SQL lacks an ORDER BY clause. Row order within the appended data depends on SQLite's GROUP BY implementation. Should the output be ordered (e.g., by as_of, preference_type)?
   - Confidence: LOW -- may or may not matter depending on downstream consumers
