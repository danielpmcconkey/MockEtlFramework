# DailyWireVolume — Business Requirements Document

## Overview
Aggregates wire transfer activity by date, producing a daily count and total dollar amount of all wire transfers. Output is appended to a CSV file covering the fixed date range 2024-10-01 through 2024-12-31.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `daily_vol`
- **outputFile**: `Output/curated/daily_wire_volume.csv`
- **includeHeader**: true
- **writeMode**: Append
- **lineEnding**: LF
- **trailerFormat**: not specified (no trailer)

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.wire_transfers | wire_id, customer_id, direction, amount, status, wire_timestamp | Hard-coded date range: minEffectiveDate=2024-10-01, maxEffectiveDate=2024-12-31; SQL WHERE: as_of >= '2024-10-01' AND as_of <= '2024-12-31' | [daily_wire_volume.json:9-12] |

### Table Schema (from database)

**wire_transfers**: wire_id (integer), customer_id (integer), account_id (integer), direction (varchar: Inbound/Outbound), amount (numeric, range ~1012-49959), counterparty_name (varchar), counterparty_bank (varchar), status (varchar: Completed/Pending/Rejected), wire_timestamp (timestamp), as_of (date). ~35-62 rows per as_of date.

## Business Rules

BR-1: DataSourcing uses **hard-coded** effective dates (minEffectiveDate=2024-10-01, maxEffectiveDate=2024-12-31) rather than executor-injected dates.
- Confidence: HIGH
- Evidence: [daily_wire_volume.json:11-12] — explicit `minEffectiveDate` and `maxEffectiveDate` in the DataSourcing config

BR-2: The SQL transformation additionally filters by `as_of >= '2024-10-01' AND as_of <= '2024-12-31'` — this is redundant with the DataSourcing date filter.
- Confidence: HIGH
- Evidence: [daily_wire_volume.json:17] — SQL WHERE clause duplicates the DataSourcing date range

BR-3: Wire transfers are grouped by `as_of` date (aliased as `wire_date`) with aggregations: COUNT(*) as wire_count, ROUND(SUM(amount), 2) as total_amount.
- Confidence: HIGH
- Evidence: [daily_wire_volume.json:17] — SQL GROUP BY as_of

BR-4: All wire transfers are included regardless of status (Completed, Pending, Rejected) or direction (Inbound, Outbound). No filtering on these fields.
- Confidence: HIGH
- Evidence: [daily_wire_volume.json:17] — SQL has no WHERE clause on status or direction

BR-5: Results are ordered by `as_of` ascending.
- Confidence: HIGH
- Evidence: [daily_wire_volume.json:17] — `ORDER BY as_of`

BR-6: The SQL SELECT includes `as_of` as both `wire_date` (aliased) AND as a standalone column, resulting in a duplicate date column in the output.
- Confidence: HIGH
- Evidence: [daily_wire_volume.json:17] — `SELECT as_of AS wire_date, COUNT(*) AS wire_count, ROUND(SUM(amount), 2) AS total_amount, as_of`

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| wire_date | wire_transfers.as_of | Aliased from as_of, grouped | [daily_wire_volume.json:17] |
| wire_count | Computed | COUNT(*) per as_of date | [daily_wire_volume.json:17] |
| total_amount | Computed | ROUND(SUM(amount), 2) per as_of date | [daily_wire_volume.json:17] |
| as_of | wire_transfers.as_of | Direct pass-through (duplicate of wire_date) | [daily_wire_volume.json:17] |

## Non-Deterministic Fields
None identified. Aggregations are deterministic given the same input data.

## Write Mode Implications
**Append** mode: Each execution appends rows to the existing CSV file. If the job is run multiple times for overlapping date ranges, duplicate rows will accumulate. The header row is included per the config, but with Append mode, subsequent runs may produce a file with header only at the top (first write) — CsvFileWriter behavior for Append with includeHeader=true would write the header on the first write and data on subsequent writes, OR it may write header every time depending on implementation.

Given the hard-coded date range covering Q4 2024, every run produces the same full set of daily aggregates for the entire quarter. Re-running appends a second copy of the same data.

## Edge Cases

1. **Hard-coded dates override executor injection**: Because `minEffectiveDate`/`maxEffectiveDate` are explicitly set in the DataSourcing config, the executor's injected effective dates are ignored. The job always processes the full 2024-10-01 to 2024-12-31 range.
   - Evidence: [daily_wire_volume.json:11-12]; Architecture.md states explicit dates take precedence over shared-state injection

2. **Weekends/holidays with no wire data**: Dates with zero wire transfers produce no output row (GROUP BY produces no group for absent dates). The output may have date gaps.
   - Evidence: [Database query] — wire_transfers data shows varying counts (35-62 per day), suggesting business-day-only patterns

3. **Redundant WHERE clause**: The SQL WHERE clause is technically redundant since DataSourcing already filters the date range. Both operate on the same `as_of` column boundaries.
   - Evidence: [daily_wire_volume.json:11-12, 17]

4. **Duplicate as_of column**: The output includes both `wire_date` (aliased from as_of) and `as_of` as separate columns with identical values.
   - Evidence: [daily_wire_volume.json:17]

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Hard-coded dates | [daily_wire_volume.json:11-12] |
| BR-2: Redundant SQL filter | [daily_wire_volume.json:17] |
| BR-3: GROUP BY as_of | [daily_wire_volume.json:17] |
| BR-4: No status/direction filter | [daily_wire_volume.json:17] |
| BR-5: ORDER BY as_of | [daily_wire_volume.json:17] |
| BR-6: Duplicate as_of column | [daily_wire_volume.json:17] |
| Output: CSV, Append, LF, header | [daily_wire_volume.json:21-27] |

## Open Questions

1. **Append mode with static date range**: Since the date range is hard-coded, every run produces identical output. Append mode will create duplicate data across runs. Is this intentional (accumulation log) or a misconfiguration?
   - Confidence: LOW — behavior is clear from config, but intent is ambiguous

2. **All statuses included**: Pending and Rejected wires are counted alongside Completed wires. Should volume metrics only include Completed transfers?
   - Confidence: MEDIUM — no filter exists, business intent unclear
