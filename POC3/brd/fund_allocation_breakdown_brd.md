# FundAllocationBreakdown — Business Requirements Document

## Overview
Produces a breakdown of holdings aggregated by security type (e.g., Stock, Bond, ETF, Mutual Fund), showing the count of holdings, total value, and average value per type. The External module writes CSV output directly to disk, bypassing the framework's CsvFileWriter module entirely.

## Output Type
Direct file I/O via External module (not CsvFileWriter). The `FundAllocationWriter` writes a CSV file directly using `StreamWriter`.

## Writer Configuration
- **Output file**: `Output/curated/fund_allocation_breakdown.csv` (hardcoded in External module)
- **Encoding**: UTF-8 (StreamWriter default)
- **Line ending**: LF (`\n` in code)
- **Header**: Yes (first line contains column names)
- **Trailer**: Yes, format `TRAILER|{row_count}|2024-10-01` (hardcoded stale date — see BR-7)
- **No framework writer module**: The job config has no CsvFileWriter or ParquetFileWriter module. The External module is the last module in the pipeline.

Evidence: [fund_allocation_breakdown.json:1-32], [FundAllocationWriter.cs:50-72]

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.holdings | holding_id, investment_id, security_id, customer_id, quantity, current_value | None beyond effective date range | [fund_allocation_breakdown.json:6-11] |
| datalake.securities | security_id, ticker, security_name, security_type, sector | Used for security_type lookup | [fund_allocation_breakdown.json:13-18] |
| datalake.investments | investment_id, customer_id, account_type, current_value | Sourced but NOT used by External module | [fund_allocation_breakdown.json:20-25] |

### Table Schemas (from database)

**holdings**: holding_id (integer), investment_id (integer), security_id (integer), customer_id (integer), quantity (numeric), cost_basis (numeric), current_value (numeric), as_of (date)

**securities**: security_id (integer), ticker (varchar), security_name (varchar), security_type (varchar), sector (varchar), exchange (varchar), as_of (date)

**investments**: investment_id (integer), customer_id (integer), account_type (varchar), current_value (numeric), risk_profile (varchar), advisor_id (integer), as_of (date)

## Business Rules

BR-1: Holdings are aggregated by security_type. Each security's type is looked up from the securities table via security_id.
- Confidence: HIGH
- Evidence: [FundAllocationWriter.cs:28-33,36-48]

BR-2: If a holding's security_id has no match in the securities table, its type defaults to `"Unknown"`.
- Confidence: HIGH
- Evidence: [FundAllocationWriter.cs:40] — `typeLookup.GetValueOrDefault(secId, "Unknown")`

BR-3: If holdings or securities data is null or empty, an empty DataFrame is stored in shared state and an empty CSV is NOT written (the method returns early).
- Confidence: HIGH
- Evidence: [FundAllocationWriter.cs:18-22]

BR-4: `total_value` is rounded to 2 decimal places per security_type group.
- Confidence: HIGH
- Evidence: [FundAllocationWriter.cs:66] — `Math.Round(totalValue, 2)`

BR-5: `avg_value` is computed as `total_value / count` per group, rounded to 2 decimal places. If count is 0, avg_value is 0 (division guard).
- Confidence: HIGH
- Evidence: [FundAllocationWriter.cs:64] — `count > 0 ? Math.Round(totalValue / count, 2) : 0m`

BR-6: Output rows are ordered alphabetically by security_type.
- Confidence: HIGH
- Evidence: [FundAllocationWriter.cs:60] — `.OrderBy(k => k.Key)`

BR-7: **BUG — Stale trailer date**: The trailer line hardcodes `2024-10-01` instead of using `maxDate`. The date variable is computed but not used in the trailer.
- Confidence: HIGH
- Evidence: [FundAllocationWriter.cs:71] — `writer.Write($"TRAILER|{rowCount}|2024-10-01\n");` with comment "W8: Trailer stale date"

BR-8: The trailer row_count reflects the number of output data rows (grouped security types), not input holdings rows.
- Confidence: HIGH
- Evidence: [FundAllocationWriter.cs:55,68] — `rowCount` is incremented once per group output

BR-9: The `as_of` column in data rows uses the formatted `maxDate` string (yyyy-MM-dd format).
- Confidence: HIGH
- Evidence: [FundAllocationWriter.cs:25-26,66]

BR-10: Data is sourced for the effective date range injected by the executor (no explicit date filters in the job config).
- Confidence: HIGH
- Evidence: [fund_allocation_breakdown.json] — no minEffectiveDate/maxEffectiveDate in DataSourcing modules

BR-11: The External module writes directly to disk using StreamWriter, completely bypassing the framework's CsvFileWriter module. An empty DataFrame is stored as `output` in shared state.
- Confidence: HIGH
- Evidence: [FundAllocationWriter.cs:50-72,74-75]

BR-12: Null security_type values in the securities table default to `"Unknown"`.
- Confidence: HIGH
- Evidence: [FundAllocationWriter.cs:32] — `secRow["security_type"]?.ToString() ?? "Unknown"`

BR-13: Investments data is sourced by the job config but never referenced by the External module.
- Confidence: HIGH
- Evidence: [fund_allocation_breakdown.json:20-25] sources investments, but [FundAllocationWriter.cs] never accesses `sharedState["investments"]`

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| security_type | securities.security_type | Null coalesced to "Unknown"; grouped as aggregation key | [FundAllocationWriter.cs:32,40] |
| holding_count | COUNT of holdings rows per security_type | Integer count | [FundAllocationWriter.cs:47] |
| total_value | SUM(holdings.current_value) per security_type | Rounded to 2 decimal places | [FundAllocationWriter.cs:47,66] |
| avg_value | total_value / holding_count per security_type | Rounded to 2 decimal places; 0 if count is 0 | [FundAllocationWriter.cs:64] |
| as_of | __maxEffectiveDate from shared state | Formatted as yyyy-MM-dd string | [FundAllocationWriter.cs:25-26,66] |

### Trailer Row
Format: `TRAILER|{row_count}|2024-10-01` (hardcoded date — BUG)

## Non-Deterministic Fields
None identified. All values are deterministic given the same input data and effective date.

## Write Mode Implications
- The External module opens the output file with `append: false`, meaning it always overwrites.
- For multi-day auto-advance runs, only the final effective date's output persists. Prior dates are overwritten.
- Since the writer is in the External module (not the framework), there is no framework-level writeMode control.

## Edge Cases

1. **No holdings or securities data**: If either is null/empty, the External module returns early with an empty DataFrame. No CSV file is written. (HIGH confidence — [FundAllocationWriter.cs:18-22])

2. **Holdings with unknown security_id**: Holdings whose security_id has no match in securities are grouped under `"Unknown"` security_type. (HIGH confidence — [FundAllocationWriter.cs:40])

3. **Stale trailer date**: The trailer always shows `2024-10-01` regardless of the actual effective date. This is a known bug. (HIGH confidence — [FundAllocationWriter.cs:71])

4. **Cross-date aggregation**: When the effective date range spans multiple days, all holdings and securities rows across dates are processed together. The security_type lookup may find multiple entries per security_id (one per date), but the dictionary overwrites so only the last-seen mapping is kept. (MEDIUM confidence — [FundAllocationWriter.cs:30-33])

5. **Investments sourced but unused**: The investments DataSourcing module runs but its data is never consumed. (HIGH confidence — code review)

6. **NULL current_value in holdings**: `Convert.ToDecimal(null)` would throw an exception. No null guard exists. (MEDIUM confidence — [FundAllocationWriter.cs:41])

7. **Output directory creation**: The module creates the output directory if it doesn't exist. (HIGH confidence — [FundAllocationWriter.cs:53])

8. **No RFC 4180 quoting**: The direct CSV writing does not apply RFC 4180 quoting rules. If any security_type contained commas or quotes, the CSV would be malformed. (MEDIUM confidence — [FundAllocationWriter.cs:66])

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Group by security_type | [FundAllocationWriter.cs:36-48] |
| Security type lookup with Unknown default | [FundAllocationWriter.cs:28-33,40] |
| Empty output on null/empty input | [FundAllocationWriter.cs:18-22] |
| Total value rounding | [FundAllocationWriter.cs:66] |
| Average value computation | [FundAllocationWriter.cs:64] |
| Alphabetical ordering | [FundAllocationWriter.cs:60] |
| Stale trailer date bug | [FundAllocationWriter.cs:71] |
| Trailer row count = output rows | [FundAllocationWriter.cs:55,68] |
| Direct file I/O (bypass framework) | [FundAllocationWriter.cs:50-72] |
| Unused investments source | [fund_allocation_breakdown.json:20-25] |
| as_of from maxDate | [FundAllocationWriter.cs:25-26,66] |

## Open Questions

1. **Why bypass CsvFileWriter?**: The External module writes CSV directly instead of using the framework's CsvFileWriter. This bypasses RFC 4180 quoting and framework-level write mode control. Unclear if intentional or a historical artifact. (MEDIUM confidence)

2. **Why are investments sourced?**: The job config sources the investments table, but the External module never references it. May be a leftover from a prior design. (HIGH confidence — clear from code review)

3. **Stale trailer date intentionality**: The hardcoded `2024-10-01` in the trailer is almost certainly a bug (the comment labels it "W8"), but it needs confirmation whether fixing it would break downstream consumers. (HIGH confidence it's a bug)
