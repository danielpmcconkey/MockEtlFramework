# PeakTransactionTimes — Business Requirements Document

## Overview
Produces an hourly breakdown of transaction volume and amounts, grouped by hour of day extracted from `txn_timestamp`. The External module writes a CSV file directly (bypassing the framework's CsvFileWriter), with a trailer that uses the **input row count** rather than the output row count. This is a known quirk (W7).

## Output Type
Direct file I/O via External module (`PeakTransactionTimesWriter`). The External module writes CSV directly using `StreamWriter`, not via the framework's CsvFileWriter module.

## Writer Configuration
The External module writes directly to `Output/curated/peak_transaction_times.csv`:
- **Header**: Included (column names joined by comma)
- **Line ending**: LF (`writer.NewLine = "\n"`)
- **Trailer format**: `TRAILER|{inputCount}|{dateStr}` where `inputCount` is the number of **input** transaction rows (not output rows) and `dateStr` is the max effective date
- **Write mode**: Overwrite (`append: false` in StreamWriter constructor)
- **Encoding**: Default StreamWriter encoding (UTF-8 with BOM, unlike framework CsvFileWriter which is UTF-8 no BOM)

Note: There is **no ParquetFileWriter or CsvFileWriter** module in the job config. The External module is the final module in the pipeline.

## Source Tables

| Table | Columns | Filters | Evidence |
|-------|---------|---------|----------|
| datalake.transactions | transaction_id, account_id, txn_timestamp, txn_type, amount, description | Effective date range injected by executor via shared state | [peak_transaction_times.json:6-11] |
| datalake.accounts | account_id, customer_id, account_type, interest_rate | Effective date range injected by executor via shared state | [peak_transaction_times.json:13-18] |

Note: The `accounts` table is sourced but **not used** by the External module. Only the `transactions` DataFrame is read. Columns `transaction_id`, `account_id`, `txn_type`, `description` from transactions are also sourced but not used — only `txn_timestamp` and `amount` are referenced.

## Business Rules

BR-1: Transactions are grouped by **hour of day** (0-23) extracted from `txn_timestamp`.
- Confidence: HIGH
- Evidence: [PeakTransactionTimesWriter.cs:36-39] `hour = dt.Hour` extracts the hour component

BR-2: `txn_count` is the count of transactions per hour.
- Confidence: HIGH
- Evidence: [PeakTransactionTimesWriter.cs:44] `current.count + 1`

BR-3: `total_amount` is the sum of amounts per hour, rounded to 2 decimal places using `decimal` arithmetic.
- Confidence: HIGH
- Evidence: [PeakTransactionTimesWriter.cs:45] `current.total + Convert.ToDecimal(row["amount"])`; [PeakTransactionTimesWriter.cs:55] `Math.Round(kvp.Value.total, 2)`

BR-4: Output rows are ordered by `hour_of_day ASC`.
- Confidence: HIGH
- Evidence: [PeakTransactionTimesWriter.cs:49] `hourlyGroups.OrderBy(k => k.Key)`

BR-5: The trailer uses the **input transaction count** (before hourly bucketing), not the output row count. This is a known quirk (W7) that inflates the trailer count.
- Confidence: HIGH
- Evidence: [PeakTransactionTimesWriter.cs:25] `var inputCount = transactions.Count;`; [PeakTransactionTimesWriter.cs:61] `WriteDirectCsv(outputRows, outputColumns, inputCount, sharedState)`; [PeakTransactionTimesWriter.cs:90] `writer.WriteLine($"TRAILER|{inputCount}|{dateStr}")`; Comment: "W7: External writes CSV directly, trailer uses inputCount (inflated)"

BR-6: The `as_of` column in the output is set to the `__maxEffectiveDate` formatted as `yyyy-MM-dd`.
- Confidence: HIGH
- Evidence: [PeakTransactionTimesWriter.cs:28-29] `var maxDate = (DateOnly)sharedState["__maxEffectiveDate"]; var dateStr = maxDate.ToString("yyyy-MM-dd");`

BR-7: The External module sets `sharedState["output"]` to an **empty DataFrame** after writing CSV. The job config has no subsequent writer module, so this empty DataFrame is not written anywhere.
- Confidence: HIGH
- Evidence: [PeakTransactionTimesWriter.cs:63] `sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);`

BR-8: The CSV file is overwritten on each run (`append: false`).
- Confidence: HIGH
- Evidence: [PeakTransactionTimesWriter.cs:76] `new StreamWriter(outputPath, append: false)`

BR-9: The timestamp is parsed as `DateTime` first; if that fails, it tries `DateTime.TryParse` on the string representation. The hour defaults to 0 if parsing fails.
- Confidence: HIGH
- Evidence: [PeakTransactionTimesWriter.cs:35-39]

BR-10: The CSV uses StreamWriter default encoding, which is UTF-8 **with BOM** — different from the framework's CsvFileWriter which uses UTF-8 without BOM.
- Confidence: MEDIUM
- Evidence: [PeakTransactionTimesWriter.cs:76] `new StreamWriter(outputPath, append: false)` — default StreamWriter constructor uses UTF-8 with BOM. Framework CsvFileWriter explicitly uses `new UTF8Encoding(false)` per Architecture.md.

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| hour_of_day | transactions.txn_timestamp | Hour component (0-23) extracted from DateTime | [PeakTransactionTimesWriter.cs:36-39] |
| txn_count | transactions | Count per hour bucket | [PeakTransactionTimesWriter.cs:44] |
| total_amount | transactions.amount | `Math.Round(SUM(decimal), 2)` per hour | [PeakTransactionTimesWriter.cs:45, 55] |
| as_of | __maxEffectiveDate | Formatted as `yyyy-MM-dd` | [PeakTransactionTimesWriter.cs:56] |

## Non-Deterministic Fields
None identified. All values are deterministic given the same input data and effective date.

## Write Mode Implications
- **Overwrite** (direct file I/O): Each run completely replaces the CSV file. Only the latest effective date's results persist.
- On multi-day auto-advance, each day's run overwrites the previous. Only the final day's data survives.

## Edge Cases

1. **Empty transactions**: When `transactions` is null or has zero rows, `WriteDirectCsv` is called with empty rows and `inputCount = 0`. The output file contains only the header and trailer `TRAILER|0|{date}`. [PeakTransactionTimesWriter.cs:18-21]

2. **Trailer count mismatch (W7)**: If there are 4000 input transactions distributed across 24 hours, the output has up to 24 rows but the trailer says `TRAILER|4000|{date}`. This is intentional behavior per the W7 quirk annotation.

3. **Timestamp parsing fallback**: If `txn_timestamp` is not a `DateTime`, it falls back to `DateTime.TryParse`. If both fail, `hour` defaults to 0, grouping unparseable timestamps into the midnight bucket.

4. **Accounts table unused**: The accounts DataFrame is sourced via DataSourcing but never read by the External module. This is harmless overhead.

5. **UTF-8 BOM difference**: The direct file write uses StreamWriter default encoding (UTF-8 with BOM), unlike the framework's CsvFileWriter. This may cause byte-level differences in file comparisons.

6. **No hours with zero transactions**: Hours that have no transactions do not appear in the output. The output only contains hours that had at least one transaction.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Hour extraction from txn_timestamp | [PeakTransactionTimesWriter.cs:36-39] |
| Count per hour | [PeakTransactionTimesWriter.cs:44] |
| Decimal sum with rounding | [PeakTransactionTimesWriter.cs:45, 55] |
| Order by hour_of_day ASC | [PeakTransactionTimesWriter.cs:49] |
| Trailer uses input count (W7) | [PeakTransactionTimesWriter.cs:25, 61, 90] |
| as_of from __maxEffectiveDate | [PeakTransactionTimesWriter.cs:28-29] |
| Direct CSV write (not framework writer) | [PeakTransactionTimesWriter.cs:67-91] |
| Overwrite mode (append: false) | [PeakTransactionTimesWriter.cs:76] |
| LF line ending | [PeakTransactionTimesWriter.cs:77] |
| Empty output DataFrame returned | [PeakTransactionTimesWriter.cs:63] |
| firstEffectiveDate = 2024-10-01 | [peak_transaction_times.json:3] |
| Unused accounts source | [peak_transaction_times.json:13-18] vs code |

## Open Questions
None.
