# HoldingsBySector — Business Requirements Document

## Overview
Produces a summary of holdings aggregated by sector, showing the count of holdings and total value per sector. The External module writes CSV output directly to disk (bypassing CsvFileWriter) and includes a trailer line with an inflated row count based on input holdings rows rather than output grouped rows.

## Output Type
Direct file I/O via External module (not CsvFileWriter). The `HoldingsBySectorWriter` writes a CSV file directly using `StreamWriter`.

## Writer Configuration
- **Output file**: `Output/curated/holdings_by_sector.csv` (hardcoded in External module)
- **Encoding**: UTF-8 (StreamWriter default)
- **Line ending**: LF (`\n` in code)
- **Header**: Yes (first line contains column names)
- **Trailer**: Yes, format `TRAILER|{input_count}|{date}` (uses INPUT row count — BUG — see BR-7)
- **No framework writer module**: The job config has no CsvFileWriter or ParquetFileWriter module. The External module is the last module in the pipeline.

Evidence: [holdings_by_sector.json:1-26], [HoldingsBySectorWriter.cs:50-68]

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.holdings | holding_id, investment_id, security_id, customer_id, quantity, cost_basis, current_value | None beyond effective date range | [holdings_by_sector.json:6-11] |
| datalake.securities | security_id, ticker, security_name, security_type, sector, exchange | Used for sector lookup via security_id | [holdings_by_sector.json:13-18] |

### Table Schemas (from database)

**holdings**: holding_id (integer), investment_id (integer), security_id (integer), customer_id (integer), quantity (numeric), cost_basis (numeric), current_value (numeric), as_of (date)

**securities**: security_id (integer), ticker (varchar), security_name (varchar), security_type (varchar), sector (varchar), exchange (varchar), as_of (date)

## Business Rules

BR-1: Holdings are aggregated by sector. Each holding's sector is looked up from the securities table via security_id.
- Confidence: HIGH
- Evidence: [HoldingsBySectorWriter.cs:28-33,36-48]

BR-2: If a holding's security_id has no match in the securities table, its sector defaults to `"Unknown"`.
- Confidence: HIGH
- Evidence: [HoldingsBySectorWriter.cs:40] — `sectorLookup.GetValueOrDefault(secId, "Unknown")`

BR-3: If holdings or securities data is null or empty, an empty DataFrame is stored in shared state and the method returns early. No CSV file is written.
- Confidence: HIGH
- Evidence: [HoldingsBySectorWriter.cs:15-19]

BR-4: `total_value` is rounded to 2 decimal places per sector group.
- Confidence: HIGH
- Evidence: [HoldingsBySectorWriter.cs:63] — `Math.Round(totalValue, 2)`

BR-5: Output rows are ordered alphabetically by sector name.
- Confidence: HIGH
- Evidence: [HoldingsBySectorWriter.cs:59] — `.OrderBy(k => k.Key)`

BR-6: The `as_of` column in data rows uses the formatted `maxDate` string (yyyy-MM-dd format).
- Confidence: HIGH
- Evidence: [HoldingsBySectorWriter.cs:24-25,63]

BR-7: **BUG — Inflated trailer row count**: The trailer uses `inputCount` (the number of raw holdings rows BEFORE grouping) instead of the number of output rows (grouped sectors). For example, if 1303 holdings produce 8 sector groups, the trailer says `TRAILER|1303|{date}` instead of `TRAILER|8|{date}`.
- Confidence: HIGH
- Evidence: [HoldingsBySectorWriter.cs:22,67] — `var inputCount = holdings.Count;` then `writer.Write($"TRAILER|{inputCount}|{dateStr}\n");` with comment "W7: Count INPUT rows before any grouping (inflated count for trailer)"

BR-8: The External module writes directly to disk using StreamWriter, bypassing the framework's CsvFileWriter module. An empty DataFrame is stored as `output` in shared state.
- Confidence: HIGH
- Evidence: [HoldingsBySectorWriter.cs:50-68,70-71]

BR-9: Null sector values in the securities table default to `"Unknown"`.
- Confidence: HIGH
- Evidence: [HoldingsBySectorWriter.cs:32] — `secRow["sector"]?.ToString() ?? "Unknown"`

BR-10: Data is sourced for the effective date range injected by the executor (no explicit date filters in the job config).
- Confidence: HIGH
- Evidence: [holdings_by_sector.json] — no minEffectiveDate/maxEffectiveDate in DataSourcing modules

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| sector | securities.sector | Null coalesced to "Unknown"; grouped as aggregation key | [HoldingsBySectorWriter.cs:32,40] |
| holding_count | COUNT of holdings rows per sector | Integer count | [HoldingsBySectorWriter.cs:47] |
| total_value | SUM(holdings.current_value) per sector | Rounded to 2 decimal places | [HoldingsBySectorWriter.cs:47,63] |
| as_of | __maxEffectiveDate from shared state | Formatted as yyyy-MM-dd string | [HoldingsBySectorWriter.cs:24-25,63] |

### Trailer Row
Format: `TRAILER|{input_holdings_count}|{date}` (inflated count — BUG)

## Non-Deterministic Fields
None identified. All values are deterministic given the same input data and effective date.

## Write Mode Implications
- The External module opens the output file with `append: false`, meaning it always overwrites.
- For multi-day auto-advance runs, only the final effective date's output persists. Prior dates are overwritten.
- Since the writer is in the External module (not the framework), there is no framework-level writeMode control.

## Edge Cases

1. **No holdings or securities data**: If either is null/empty, the method returns early. No CSV file is written. (HIGH confidence — [HoldingsBySectorWriter.cs:15-19])

2. **Holdings with unknown security_id**: Holdings whose security_id has no match in securities are grouped under `"Unknown"` sector. (HIGH confidence — [HoldingsBySectorWriter.cs:40])

3. **Inflated trailer count**: The trailer reports the number of input holdings rows (e.g., 1303) rather than the number of output sector groups (e.g., 8). This is a known bug. (HIGH confidence — [HoldingsBySectorWriter.cs:22,67])

4. **Cross-date aggregation**: When the effective date range spans multiple days, all holdings and securities rows are processed together. The sector lookup dictionary overwrites per security_id, keeping only the last-seen mapping. (MEDIUM confidence — [HoldingsBySectorWriter.cs:30-33])

5. **NULL current_value in holdings**: `Convert.ToDecimal(null)` would throw an exception. No null guard exists. (MEDIUM confidence — [HoldingsBySectorWriter.cs:41])

6. **No RFC 4180 quoting**: Direct CSV writing does not apply RFC 4180 quoting rules. If any sector name contained commas or quotes (e.g., "Real Estate" is safe but a hypothetical "A,B" would break), the CSV would be malformed. Current data appears safe (observed sectors: Consumer, Energy, Finance, Healthcare, Industrial, Real Estate, Technology, Utilities). (LOW confidence — data observation)

7. **Output directory creation**: The module creates the output directory if it doesn't exist. (HIGH confidence — [HoldingsBySectorWriter.cs:53])

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Group by sector | [HoldingsBySectorWriter.cs:36-48] |
| Sector lookup with Unknown default | [HoldingsBySectorWriter.cs:28-33,40] |
| Empty output on null/empty input | [HoldingsBySectorWriter.cs:15-19] |
| Total value rounding | [HoldingsBySectorWriter.cs:63] |
| Alphabetical ordering | [HoldingsBySectorWriter.cs:59] |
| Inflated trailer count bug | [HoldingsBySectorWriter.cs:22,67] |
| Direct file I/O (bypass framework) | [HoldingsBySectorWriter.cs:50-68] |
| as_of from maxDate | [HoldingsBySectorWriter.cs:24-25,63] |
| Null sector default | [HoldingsBySectorWriter.cs:32] |

## Open Questions

1. **Inflated trailer intentionality**: The comment "W7" marks this as known. The trailer count is clearly wrong for a grouped result set, but it needs confirmation whether downstream consumers depend on this inflated count. (HIGH confidence it's a bug)

2. **Why bypass CsvFileWriter?**: Like FundAllocationBreakdown, this External module writes CSV directly instead of using the framework's CsvFileWriter. This bypasses RFC 4180 quoting and framework-level controls. (MEDIUM confidence — likely a design pattern for jobs that combine aggregation with output)

3. **"Real Estate" sector in CSV**: The sector name "Real Estate" contains a space but no commas, so it is safe in unquoted CSV. However, lack of RFC 4180 quoting means any future sector with special characters would break the format. (LOW confidence — speculative edge case)
