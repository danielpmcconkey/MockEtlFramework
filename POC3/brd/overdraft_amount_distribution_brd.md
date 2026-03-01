# OverdraftAmountDistribution — Business Requirements Document

## Overview
Buckets overdraft events into amount ranges (0-50, 50-100, 100-250, 250-500, 500+) and produces a CSV file with event counts and total amounts per bucket. The External module bypasses the framework's CsvFileWriter and writes the CSV directly to disk, including a trailer line with an inflated row count.

## Output Type
Direct file I/O via External module (bypasses CsvFileWriter). The External module writes CSV directly using `StreamWriter`. Note: the job config has NO writer module — only DataSourcing and External.

## Writer Configuration
- **Output mechanism**: `StreamWriter` in External module (not CsvFileWriter)
- **outputFile**: `Output/curated/overdraft_amount_distribution.csv` (hardcoded in processor)
- **includeHeader**: yes (written by External)
- **lineEnding**: System default (Environment.NewLine via StreamWriter)
- **trailerFormat**: `TRAILER|{inputRowCount}|{maxDate:yyyy-MM-dd}` (hardcoded in processor)
- **writeMode**: Overwrite (StreamWriter with `append: false`)

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.overdraft_events | overdraft_id, account_id, customer_id, overdraft_amount, fee_amount, fee_waived, event_timestamp | Effective date range injected via shared state | [overdraft_amount_distribution.json:4-11] |

### Table Schema (from database)

**overdraft_events**: overdraft_id (integer), account_id (integer), customer_id (integer), overdraft_amount (numeric), fee_amount (numeric), fee_waived (boolean), event_timestamp (timestamp), as_of (date)

## Business Rules

BR-1: Overdraft amounts are bucketed into 5 predefined ranges using `<=` boundary logic:
- `0-50`: amount <= 50
- `50-100`: 50 < amount <= 100
- `100-250`: 100 < amount <= 250
- `250-500`: 250 < amount <= 500
- `500+`: amount > 500
- Confidence: HIGH
- Evidence: [OverdraftAmountDistributionProcessor.cs:56-64] `if (amount <= 50m) bucket = "0-50"; else if (amount <= 100m) ...`

BR-2: Empty buckets (count = 0) are excluded from the output — only buckets with at least one event are written.
- Confidence: HIGH
- Evidence: [OverdraftAmountDistributionProcessor.cs:80-81] `if (kvp.Value.count == 0) continue;`

BR-3: The `total_amount` per bucket uses `decimal` precision (not double).
- Confidence: HIGH
- Evidence: [OverdraftAmountDistributionProcessor.cs:46] `Dictionary<string, (int count, decimal total)>`

BR-4: The trailer line uses the **input row count** (total rows in the source DataFrame), NOT the output bucket count. This produces an inflated trailer count.
- Confidence: HIGH
- Evidence: [OverdraftAmountDistributionProcessor.cs:35,88] `int inputRowCount = overdraftEvents?.Count ?? 0;` then `TRAILER|{inputRowCount}|{maxDate:yyyy-MM-dd}`; Comment: `W7: Count INPUT rows before bucketing for inflated trailer count`

BR-5: The `as_of` column for all output rows is taken from the first row's `as_of` value, not the effective date.
- Confidence: HIGH
- Evidence: [OverdraftAmountDistributionProcessor.cs:43] `var asOf = overdraftEvents.Rows[0]["as_of"]?.ToString()`

BR-6: The External module writes CSV directly to disk, **bypassing the framework's CsvFileWriter entirely**. The job config has no writer module after the External step.
- Confidence: HIGH
- Evidence: [overdraft_amount_distribution.json] Only 2 modules: DataSourcing + External; [OverdraftAmountDistributionProcessor.cs:71-89] Direct `StreamWriter` usage

BR-7: The External module also stores the bucketed results into shared state as `"output"` DataFrame for framework compatibility, but nothing downstream consumes it.
- Confidence: HIGH
- Evidence: [OverdraftAmountDistributionProcessor.cs:107] `sharedState["output"] = new DataFrame(outputRows, outputColumns);`

BR-8: Effective dates are injected by the executor at runtime.
- Confidence: HIGH
- Evidence: [overdraft_amount_distribution.json:4-11] No `minEffectiveDate` / `maxEffectiveDate` in DataSourcing config

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| amount_bucket | Derived | Bucketed label ("0-50", "50-100", "100-250", "250-500", "500+") | [OverdraftAmountDistributionProcessor.cs:56-64] |
| event_count | Derived | Count of events per bucket | [OverdraftAmountDistributionProcessor.cs:66-67] |
| total_amount | overdraft_events.overdraft_amount | Sum of overdraft_amount per bucket (decimal) | [OverdraftAmountDistributionProcessor.cs:67] |
| as_of | overdraft_events.as_of (first row) | String pass-through from first source row | [OverdraftAmountDistributionProcessor.cs:43] |

**Trailer row**: `TRAILER|{inputRowCount}|{maxDate:yyyy-MM-dd}` — uses input row count, not output row count.

## Non-Deterministic Fields
None identified. Output is deterministic given the same source data and effective date.

## Write Mode Implications
- **Overwrite** mode (via StreamWriter `append: false`): Each execution replaces the CSV file entirely. On multi-day auto-advance, only the final effective date's output persists.
- Confidence: HIGH
- Evidence: [OverdraftAmountDistributionProcessor.cs:75] `new StreamWriter(outputPath, false)`

## Edge Cases

EC-1: **Inflated trailer row count** — The trailer reports the count of INPUT rows (all overdraft events for the effective date range), not the count of OUTPUT rows (buckets). For example, 139 input rows bucketed into 5 groups would show `TRAILER|139|...` instead of `TRAILER|5|...`.
- Confidence: HIGH
- Evidence: [OverdraftAmountDistributionProcessor.cs:35,88] Comment: `W7`

EC-2: **Line ending mismatch** — The External module uses `StreamWriter.WriteLine` which uses `Environment.NewLine` (platform default: `\n` on Linux, `\r\n` on Windows). This may differ from the CsvFileWriter's configurable line ending behavior.
- Confidence: HIGH
- Evidence: [OverdraftAmountDistributionProcessor.cs:77-88] `writer.WriteLine(...)` — no explicit line ending control

EC-3: **Empty source data** — If no overdraft events exist, an empty DataFrame is returned and no CSV file is written (the StreamWriter block is skipped).
- Confidence: HIGH
- Evidence: [OverdraftAmountDistributionProcessor.cs:37-41] Early return before StreamWriter block

EC-4: **No RFC 4180 quoting** — The direct CSV output uses simple string interpolation without RFC 4180 quoting rules (unlike the framework's CsvFileWriter).
- Confidence: HIGH
- Evidence: [OverdraftAmountDistributionProcessor.cs:83] `writer.WriteLine($"{kvp.Key},{kvp.Value.count},{kvp.Value.total},{asOf}");`

EC-5: **Bucket order** — Buckets are output in dictionary insertion order: 0-50, 50-100, 100-250, 250-500, 500+.
- Confidence: HIGH
- Evidence: [OverdraftAmountDistributionProcessor.cs:47-53] Dictionary initialized with ordered keys

EC-6: **Overwrite on multi-day runs** — During auto-advance, each run overwrites the file. Only the final day's output survives.
- Confidence: HIGH
- Evidence: [OverdraftAmountDistributionProcessor.cs:75] `new StreamWriter(outputPath, false)`

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Bucket boundaries | [OverdraftAmountDistributionProcessor.cs:56-64] |
| BR-2: Empty bucket exclusion | [OverdraftAmountDistributionProcessor.cs:80-81] |
| BR-3: Decimal precision | [OverdraftAmountDistributionProcessor.cs:46] |
| BR-4: Inflated trailer count | [OverdraftAmountDistributionProcessor.cs:35,88] |
| BR-5: as_of from first row | [OverdraftAmountDistributionProcessor.cs:43] |
| BR-6: Direct file I/O | [overdraft_amount_distribution.json], [OverdraftAmountDistributionProcessor.cs:71-89] |
| BR-7: Shared state output set | [OverdraftAmountDistributionProcessor.cs:107] |
| EC-1: Inflated trailer | [OverdraftAmountDistributionProcessor.cs:35,88] |
| EC-2: Line ending | [OverdraftAmountDistributionProcessor.cs:77-88] |

## Open Questions
1. **Is the inflated trailer intentional?** The trailer uses input row count instead of output row count. The code comment (`W7`) suggests this is a known behavior, but it's unclear if it's intentional or a bug. Confidence: HIGH that this is worth investigating.
2. **Why bypass CsvFileWriter?** The job config has no writer module — the External module handles all output. This circumvents framework features like configurable line endings, RFC 4180 quoting, and standard trailer token substitution. Confidence: MEDIUM that this is intentional for custom trailer behavior.
