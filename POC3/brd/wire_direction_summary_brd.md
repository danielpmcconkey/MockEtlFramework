# WireDirectionSummary — Business Requirements Document

## Overview
Aggregates wire transfer activity by direction (Inbound/Outbound), producing counts, totals, and averages. Output is written directly to CSV by the External module (bypassing the framework's CsvFileWriter), including a trailer line with an inflated row count based on input rows rather than output rows.

## Output Type
Direct file I/O via External module (NOT CsvFileWriter)

## Writer Configuration
The External module (`WireDirectionSummaryWriter`) writes the CSV file directly using `StreamWriter`. There is no framework writer module in the job config pipeline.

- **Output path**: `Output/curated/wire_direction_summary.csv` (resolved relative to solution root)
- **Header**: Yes (column names joined by comma)
- **Line ending**: LF (`writer.NewLine = "\n"`)
- **Write mode**: Overwrite (FileStream with `append: false`)
- **Trailer**: Yes — format: `TRAILER|{inputCount}|{date}`
  - `inputCount` = number of INPUT wire_transfers rows (before grouping), NOT output row count
  - `date` = `__maxEffectiveDate` formatted as `yyyy-MM-dd`

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.wire_transfers | wire_id, customer_id, direction, amount, status | Effective date range (injected by executor) | [wire_direction_summary.json:8-12] |

### Table Schema (from database)

**wire_transfers**: wire_id (integer), customer_id (integer), account_id (integer), direction (varchar: Inbound/Outbound), amount (numeric, range ~1012-49959), counterparty_name (varchar), counterparty_bank (varchar), status (varchar: Completed/Pending/Rejected), wire_timestamp (timestamp), as_of (date). ~35-62 rows per as_of date.

## Business Rules

BR-1: Wire transfers are grouped by `direction` (Inbound, Outbound). All statuses (Completed, Pending, Rejected) are included — no status filter.
- Confidence: HIGH
- Evidence: [WireDirectionSummaryWriter.cs:30-41] — groups by direction, no status check

BR-2: Per-direction aggregations:
  - `wire_count` = count of wires in that direction
  - `total_amount` = SUM(amount), rounded to 2 decimal places
  - `avg_amount` = total_amount / wire_count, rounded to 2 decimal places
- Confidence: HIGH
- Evidence: [WireDirectionSummaryWriter.cs:46-49]

BR-3: The trailer line uses the count of INPUT rows (all wire_transfers rows before grouping), NOT the count of OUTPUT rows (which is typically 2: one per direction).
- Confidence: HIGH
- Evidence: [WireDirectionSummaryWriter.cs:26, 62, 104] — `var inputCount = wireTransfers.Count;` saved before grouping; comment "W7: trailer uses inputCount (inflated)"

BR-4: The `as_of` column in the output is taken from the FIRST row of the wire_transfers DataFrame, not from `__maxEffectiveDate`.
- Confidence: HIGH
- Evidence: [WireDirectionSummaryWriter.cs:43] — `var asOf = wireTransfers.Rows[0]["as_of"];`

BR-5: The CSV is written directly by the External module using `StreamWriter`, bypassing the framework's `CsvFileWriter`. No RFC 4180 quoting is applied — values are joined by comma with `.ToString()`.
- Confidence: HIGH
- Evidence: [WireDirectionSummaryWriter.cs:79-106] — `WriteDirectCsv` method uses StreamWriter

BR-6: The output DataFrame is also stored in shared state as "output", but since there is no writer module in the pipeline after External, this DataFrame is unused by the framework.
- Confidence: HIGH
- Evidence: [WireDirectionSummaryWriter.cs:64] — sets `sharedState["output"]`; [wire_direction_summary.json] — no writer module follows

BR-7: Empty file behavior: If wire_transfers is null or empty, `WriteDirectCsv` is called with empty rows and inputCount=0, producing a file with just the header and a `TRAILER|0|{date}` line.
- Confidence: HIGH
- Evidence: [WireDirectionSummaryWriter.cs:20-23] — calls `WriteDirectCsv(new List<Row>(), outputColumns, 0, sharedState)`

BR-8: Output directory is created if it doesn't exist.
- Confidence: HIGH
- Evidence: [WireDirectionSummaryWriter.cs:84-86] — `Directory.CreateDirectory`

BR-9: The `__maxEffectiveDate` is used for the trailer date. If not present in shared state, falls back to `DateOnly.FromDateTime(DateTime.Today)`.
- Confidence: HIGH
- Evidence: [WireDirectionSummaryWriter.cs:88-89] — conditional read with fallback

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| direction | wire_transfers.direction | Group key | [WireDirectionSummaryWriter.cs:33] |
| wire_count | Computed | COUNT per direction | [WireDirectionSummaryWriter.cs:47] |
| total_amount | Computed | SUM(amount), ROUND 2dp | [WireDirectionSummaryWriter.cs:48] |
| avg_amount | Computed | total/count, ROUND 2dp | [WireDirectionSummaryWriter.cs:49] |
| as_of | wire_transfers.Rows[0]["as_of"] | From first input row | [WireDirectionSummaryWriter.cs:43] |

### Trailer Line
Format: `TRAILER|{inputCount}|{maxEffectiveDate:yyyy-MM-dd}`

## Non-Deterministic Fields
- The `as_of` column is taken from the first row of the input DataFrame. If input row ordering is non-deterministic, the as_of value could vary between runs with multi-date ranges.
- The trailer date depends on `__maxEffectiveDate` from shared state, which is deterministic per executor run.

## Write Mode Implications
The External module uses `append: false` in the StreamWriter constructor, meaning each run **overwrites** the CSV file entirely. In multi-day gap-fill scenarios, only the last day's output survives.

## Edge Cases

1. **Dictionary iteration order**: Groups are iterated via `foreach` on a `Dictionary<string, (int, decimal)>`. Dictionary iteration order in .NET is not guaranteed to be stable across different runtime versions, though it is typically insertion order within a single run.
   - Evidence: [WireDirectionSummaryWriter.cs:45] — `foreach (var kvp in groups)`

2. **No RFC 4180 quoting**: Values are converted via `.ToString()` and joined with commas. If any value contains a comma, newline, or quote, the CSV will be malformed.
   - Evidence: [WireDirectionSummaryWriter.cs:100] — `string.Join(",", values)`

3. **Inflated trailer count**: The trailer reports input row count (e.g., 40-60 per day) instead of output row count (typically 2). Downstream consumers expecting a row count validation will see a mismatch.
   - Evidence: [WireDirectionSummaryWriter.cs:104] — explicit use of `inputCount`

4. **Single direction only**: If all wires are one direction, output has only 1 data row. The trailer still shows the full input count.
   - Evidence: [WireDirectionSummaryWriter.cs:30-41] — grouping logic handles any number of distinct directions

5. **No job config writer module**: Unlike other jobs, this job has no writer module in the JSON config. The External module handles all file output internally.
   - Evidence: [wire_direction_summary.json] — only DataSourcing and External modules, no writer

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Group by direction, no status filter | [WireDirectionSummaryWriter.cs:30-41] |
| BR-2: Count, sum, avg aggregations | [WireDirectionSummaryWriter.cs:46-49] |
| BR-3: Trailer uses input count | [WireDirectionSummaryWriter.cs:26, 62, 104] |
| BR-4: as_of from first input row | [WireDirectionSummaryWriter.cs:43] |
| BR-5: Direct CSV via StreamWriter | [WireDirectionSummaryWriter.cs:79-106] |
| BR-6: Output DataFrame unused | [wire_direction_summary.json] — no writer module |
| BR-7: Empty file with trailer | [WireDirectionSummaryWriter.cs:20-23] |
| BR-8: Directory auto-creation | [WireDirectionSummaryWriter.cs:84-86] |
| BR-9: Trailer date fallback | [WireDirectionSummaryWriter.cs:88-89] |

## Open Questions

1. **Inflated trailer count**: The trailer reports input rows instead of output rows. Is this a bug (should report output rows) or intentional (reporting source volume)?
   - Confidence: HIGH — code comment "W7: trailer uses inputCount (inflated)" suggests this is a known behavior/quirk

2. **Why bypass CsvFileWriter?** The framework provides a CsvFileWriter with trailer support. Using direct file I/O duplicates that functionality and loses RFC 4180 quoting. Was this done to control the trailer row count specifically?
   - Confidence: MEDIUM — the inflated trailer count would not be possible via CsvFileWriter (which uses output row count via `{row_count}` token)
