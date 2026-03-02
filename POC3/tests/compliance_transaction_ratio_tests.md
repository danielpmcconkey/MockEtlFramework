# ComplianceTransactionRatio -- Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | Compliance events grouped by event_type with count per type |
| TC-02   | BR-2           | txn_count is total count of ALL transactions (unfiltered) |
| TC-03   | BR-3, W4       | events_per_1000_txns uses integer division (truncation) |
| TC-04   | BR-4           | Division-by-zero guard: events_per_1000_txns = 0 when txn_count = 0 |
| TC-05   | BR-5           | Output rows ordered alphabetically by event_type |
| TC-06   | BR-6, W7       | Trailer row count uses inflated input count (compliance + transaction rows) |
| TC-07   | BR-7           | External module writes CSV directly, bypassing CsvFileWriter |
| TC-08   | BR-8           | NULL event_type coalesced to "Unknown" |
| TC-09   | BR-9           | as_of date formatted as yyyy-MM-dd from __maxEffectiveDate |
| TC-10   | BR-10          | No framework writer module in job config |
| TC-11   | Writer Config  | Header row written manually, Overwrite mode, LF line endings |
| TC-12   | W9             | Overwrite mode: multi-day run retains only last effective date's output |
| TC-13   | AP3 (partial)  | Grouping logic moved to SQL Transformation (eliminated row-by-row C# loop) |
| TC-14   | AP6            | Row-by-row foreach replaced by SQL GROUP BY |
| TC-15   | AP4            | V1 unused columns retained in V2 DataSourcing for structural safety |
| TC-16   | Edge Case      | Empty compliance_events: no CSV file written |
| TC-17   | Edge Case      | Zero transactions: all events_per_1000_txns values are 0 |
| TC-18   | Edge Case      | Integer truncation for small ratios (e.g., 1 event / 4263 txns = 0) |
| TC-19   | Edge Case      | Inflated trailer count dwarfs actual output row count |
| TC-20   | Edge Case      | Weekend/non-existent as_of date: DataSourcing returns zero rows |
| TC-21   | Edge Case      | Boundary date 2024-10-01 (first effective date) produces valid output |
| TC-22   | Edge Case      | Boundary date 2024-12-31 (last effective date) produces valid output |
| TC-23   | FSD: Tier 2    | V2 uses DataSourcing -> Transformation -> External (minimal) pipeline |
| TC-24   | Proofmark      | Proofmark comparison passes with zero exclusions and zero fuzzy columns |
| TC-25   | Output Format  | Output columns in exact order: event_type, event_count, txn_count, events_per_1000_txns, as_of |
| TC-26   | Edge Case      | NULL transactions DataFrame handled gracefully (txn_count = 0) |

## Test Cases

### TC-01: Compliance events grouped by event_type with count
- **Traces to:** BR-1
- **Input conditions:** Run V2 job for a single weekday (e.g., 2024-10-01) where compliance_events contains multiple event types.
- **Expected output:** Each distinct event_type in the source data produces one output row. The `event_count` column equals the count of compliance_events rows with that event_type (after COALESCE of NULLs to "Unknown"). For example, if there are 25 "SAR" events, event_count for SAR = 25.
- **Verification method:** Query `datalake.compliance_events` for the same date, group by event_type (with NULL as "Unknown"), and compare counts against V2 output. The V2 SQL `SELECT COALESCE(event_type, 'Unknown') AS event_type, COUNT(*) AS event_count FROM compliance_events GROUP BY COALESCE(event_type, 'Unknown')` handles this [FSD Section 5].

### TC-02: txn_count is total count of ALL transactions
- **Traces to:** BR-2
- **Input conditions:** Run V2 job for a single date. Query `SELECT COUNT(*) FROM datalake.transactions WHERE as_of = '<date>'` to get the expected transaction count.
- **Expected output:** Every output row has the same `txn_count` value, equal to the total number of transactions for that date (e.g., ~4263 per BRD evidence). No filtering is applied to transactions.
- **Verification method:** Compare the `txn_count` value in every V2 output row against the direct database count. All rows must show the same value. The External module computes `txnCount = transactions?.Count ?? 0` from the full DataFrame [FSD Section 10].

### TC-03: events_per_1000_txns uses integer division (W4)
- **Traces to:** BR-3, W4
- **Input conditions:** Run V2 job for a date where integer truncation is observable. For example, if event_count = 25 and txn_count = 4263: `(25 * 1000) / 4263 = 25000 / 4263 = 5` (truncated from 5.864...).
- **Expected output:** `events_per_1000_txns = (event_count * 1000) / txn_count` using C# integer division. The result is truncated toward zero, not rounded.
- **Verification method:** For each output row, manually compute `(event_count * 1000) / txn_count` using integer arithmetic and verify the V2 output matches. Confirm truncation (e.g., 5.864 becomes 5, not 6). The External module uses `(eventCount * RatePerThousand) / txnCount` with `int` operands [FSD Section 10, W4].

### TC-04: Division-by-zero guard when txn_count = 0
- **Traces to:** BR-4
- **Input conditions:** Run V2 job for a hypothetical date where the transactions DataFrame is empty (zero rows). (If no such date exists in the data, verify the code logic: the External module must use a ternary guard.)
- **Expected output:** All `events_per_1000_txns` values are 0. No division-by-zero exception is thrown. `txn_count` = 0 for all rows.
- **Verification method:** Inspect the External module source code for the guard: `txnCount > 0 ? (eventCount * RatePerThousand) / txnCount : 0`. If a zero-transaction date can be simulated, run the job and verify the output [FSD Section 10, BR-4].

### TC-05: Output rows ordered alphabetically by event_type
- **Traces to:** BR-5
- **Input conditions:** Run V2 job for a date with multiple distinct event types.
- **Expected output:** Rows in the CSV data section (between header and trailer) are sorted alphabetically by the `event_type` column. For example: "AML" before "CTR" before "SAR" before "Unknown".
- **Verification method:** Read the V2 CSV output data rows. Verify event_type values are in strict ascending alphabetical order. The V2 SQL includes `ORDER BY event_type` which the External module preserves by iterating grouped_events rows in order [FSD Section 5, Section 10].

### TC-06: Trailer uses inflated input count (W7)
- **Traces to:** BR-6, W7
- **Input conditions:** Run V2 job for a single date. Determine the source row counts: compliance_events count (e.g., ~115) and transactions count (e.g., ~4263).
- **Expected output:** The trailer line is `TRAILER|{inputCount}|{date}` where `inputCount = compliance_events.Count + transactions.Count` (e.g., 115 + 4263 = 4378). This is NOT the output row count (which would be ~5 for 5 event types). The date is the max effective date.
- **Verification method:** Read the last line of the V2 CSV file. Parse the pipe-delimited trailer. Verify the count field equals the sum of input DataFrame row counts, NOT the number of data rows in the CSV. Compare against V1 trailer to ensure identical inflation behavior [FSD Section 10, W7].

### TC-07: External module writes CSV directly (no CsvFileWriter)
- **Traces to:** BR-7
- **Input conditions:** Inspect the V2 job config JSON.
- **Expected output:** The V2 config contains no CsvFileWriter module. The External module (`ComplianceTransactionRatioV2Processor`) is the last module in the chain and writes the CSV file directly using StreamWriter.
- **Verification method:** Read `compliance_transaction_ratio_v2.json` and verify the modules array ends with an External module. Confirm no CsvFileWriter type appears. The `sharedState["output"]` is set to an empty DataFrame by the External module [FSD Section 6, Section 7, Section 10].

### TC-08: NULL event_type coalesced to "Unknown"
- **Traces to:** BR-8
- **Input conditions:** Check if any compliance_events rows have NULL event_type for a given date. If so, run V2 for that date.
- **Expected output:** Events with NULL event_type appear under a group with event_type = "Unknown". Their count is aggregated into the "Unknown" row. No raw NULL values appear in the output.
- **Verification method:** Query `SELECT COUNT(*) FROM datalake.compliance_events WHERE event_type IS NULL AND as_of = '<date>'`. If count > 0, verify V2 output contains an "Unknown" row with event_count matching this count. The V2 SQL uses `COALESCE(event_type, 'Unknown')` in both SELECT and GROUP BY [FSD Section 5].

### TC-09: as_of date formatted as yyyy-MM-dd
- **Traces to:** BR-9
- **Input conditions:** Run V2 job for a date (e.g., 2024-10-01).
- **Expected output:** Every data row's `as_of` column contains the date in `yyyy-MM-dd` format (e.g., "2024-10-01"). The trailer date also uses this format.
- **Verification method:** Read V2 CSV output and verify all `as_of` values match the expected format. Check the trailer's date component matches. The External module reads `__maxEffectiveDate` from shared state and formats with `ToString("yyyy-MM-dd")` [FSD Section 10, BR-9].

### TC-10: No framework writer module in job config
- **Traces to:** BR-10
- **Input conditions:** Inspect the V2 job config JSON.
- **Expected output:** The modules array contains exactly 4 entries: 2x DataSourcing, 1x Transformation, 1x External. No CsvFileWriter or ParquetFileWriter module is present.
- **Verification method:** Read `compliance_transaction_ratio_v2.json` and verify module types. This matches V1 which also has no framework writer [FSD Section 6, BR-10].

### TC-11: Header row, Overwrite mode, LF line endings
- **Traces to:** BRD Writer Configuration
- **Input conditions:** Run V2 job for a single date. Read the raw output file bytes.
- **Expected output:** The file starts with a header row: `event_type,event_count,txn_count,events_per_1000_txns,as_of`. All line endings are LF (`\n`), not CRLF. The file is overwritten each run (not appended).
- **Verification method:** Read the raw file. Verify the first line is the expected header. Check for `\r\n` sequences (none should exist). Run twice for different dates and confirm only the second date's data remains [FSD Section 10, BRD Writer Configuration].

### TC-12: Overwrite mode retains only last effective date's output (W9)
- **Traces to:** W9
- **Input conditions:** Run V2 job for 2024-10-01, then run again for 2024-10-02.
- **Expected output:** After the first run, the file contains rows with as_of = 2024-10-01. After the second run, the file contains ONLY rows with as_of = 2024-10-02. The 2024-10-01 data is completely overwritten. Trailer date changes to 2024-10-02.
- **Verification method:** Read the CSV file after each run. Verify only one date's data is present after the second run. The External module uses `StreamWriter` with `append: false` [FSD Section 10, BRD Write Mode Implications].

### TC-13: Grouping logic moved to SQL (AP3 partial elimination)
- **Traces to:** AP3 (partial elimination)
- **Input conditions:** Inspect V2 source code and job config.
- **Expected output:** The V2 Transformation SQL performs the GROUP BY and COUNT aggregation. The V1 C# `foreach` loop that built eventGroups dictionary is eliminated. The External module reads pre-grouped data from `grouped_events` DataFrame, not raw compliance_events rows.
- **Verification method:** Read the V2 job config to verify the Transformation SQL contains `GROUP BY COALESCE(event_type, 'Unknown')`. Read the V2 External module to verify it iterates `grouped_events` (the SQL output), not `compliance_events` (the raw source). This confirms AP3/AP6 elimination [FSD Section 2, Section 5, Section 10].

### TC-14: Row-by-row foreach eliminated (AP6)
- **Traces to:** AP6
- **Input conditions:** Inspect V2 External module source code.
- **Expected output:** The V2 External module does NOT contain a `foreach` loop that builds a dictionary of event_type -> count from raw compliance_events rows. The grouping is done in SQL. The External module's iteration over `grouped_events` rows for file writing is NOT an AP6 violation (file I/O inherently requires row iteration).
- **Verification method:** Read `ComplianceTransactionRatioV2Processor.cs` and verify no dictionary-building loop over compliance_events exists. The module reads pre-grouped rows and writes them to the file [FSD Section 10, AP6 analysis].

### TC-15: V1 unused columns retained in V2 DataSourcing
- **Traces to:** AP4 (retained for safety)
- **Input conditions:** Inspect the V2 job config JSON DataSourcing modules.
- **Expected output:** The compliance_events DataSourcing includes `["event_id", "customer_id", "event_type", "status"]` and the transactions DataSourcing includes `["transaction_id", "account_id", "amount"]` -- matching V1's column lists exactly. This is an intentional decision: retaining V1's column lists ensures row count consistency for the W7 inflated trailer.
- **Verification method:** Read `compliance_transaction_ratio_v2.json` and verify both DataSourcing column arrays match V1. The FSD documents this as an intentional AP4 retention for structural safety [FSD Section 3, AP4 analysis].

### TC-16: Empty compliance_events produces no CSV file
- **Traces to:** Edge Case 1 (BRD)
- **Input conditions:** Run V2 job for a hypothetical date where compliance_events is empty (zero rows from DataSourcing). Note: per FSD Section 5, if compliance_events is empty, DataSourcing returns a zero-row DataFrame, Transformation's RegisterTable skips it, and the SQL fails with "no such table."
- **Expected output:** The job fails for that date (Transformation throws). No CSV file is written. If a previous file existed, it is preserved (the External module never executes, so no overwrite occurs). This matches V1 behavior where an empty compliance_events causes the External module to return early without writing.
- **Verification method:** If a zero-row compliance_events date exists, run V2 and verify the job fails without creating/overwriting the CSV file. If no such date exists, verify the V2 code path through the FSD's analysis: empty DataFrame -> Transformation fails -> External never runs -> file untouched [FSD Section 5, SQL Notes].

### TC-17: Zero transactions produces events_per_1000_txns = 0 for all rows
- **Traces to:** Edge Case 2 (BRD)
- **Input conditions:** Run V2 job for a hypothetical date where the transactions table has zero rows for that date.
- **Expected output:** `txn_count = 0` for all output rows. `events_per_1000_txns = 0` for all rows (division-by-zero guard). `event_count` values are still computed normally from compliance_events grouping. The trailer count equals `compliance_events.Count + 0`.
- **Verification method:** If such a date exists, verify output. Otherwise, verify the External module code: `txnCount = transactions?.Count ?? 0` handles null/empty DataFrames, and the ternary guard prevents division by zero [FSD Section 10, BR-4].

### TC-18: Integer truncation for small ratios
- **Traces to:** Edge Case 3 (BRD)
- **Input conditions:** Run V2 job for a date where at least one event_type has a small count relative to total transactions. For example: 1 event of type X with 4263 total transactions yields `(1 * 1000) / 4263 = 0`.
- **Expected output:** `events_per_1000_txns = 0` for that event_type, because C# integer division `1000 / 4263 = 0`. The fractional component (0.234...) is discarded.
- **Verification method:** Identify event types with fewer than `txn_count / 1000` events. Verify their `events_per_1000_txns` is 0 in the output. This is the W4 integer division wrinkle [FSD Section 3, W4].

### TC-19: Inflated trailer count dwarfs actual output row count
- **Traces to:** Edge Case 4 (BRD), W7
- **Input conditions:** Run V2 job for a typical date. Count the number of data rows in the CSV (expected: ~5 for 5 event types). Read the trailer count.
- **Expected output:** The trailer count (e.g., ~4378) is vastly larger than the data row count (e.g., 5). The trailer count = compliance_events.Count + transactions.Count, while the data row count = number of distinct event types.
- **Verification method:** Parse the trailer line. Compare the trailer's count value against the actual number of data rows in the file. Confirm the ratio is approximately `(~115 + ~4263) / ~5 = ~876x`. This is the W7 trailer inflation documented in BRD and FSD [BRD Edge Case 4, FSD Section 3].

### TC-20: Weekend date with no source data
- **Traces to:** Edge Case (weekend dates)
- **Input conditions:** Run V2 job for a Saturday (e.g., 2024-10-05) or Sunday (e.g., 2024-10-06) where no compliance_events or transactions data exists.
- **Expected output:** DataSourcing returns zero rows for compliance_events. Per TC-16 analysis, the Transformation SQL fails because the empty DataFrame is not registered as a SQLite table. The job fails for that date. No CSV file is written or overwritten.
- **Verification method:** Run V2 for a weekend date and verify the job fails gracefully. If a previous CSV file exists, verify it is not overwritten [FSD Section 5, SQL Notes].

### TC-21: First effective date boundary (2024-10-01)
- **Traces to:** Edge Case (boundary dates)
- **Input conditions:** Run V2 job for exactly 2024-10-01 (the firstEffectiveDate from the job config).
- **Expected output:** Normal output with valid grouping, counts, ratios, and trailer. The as_of column shows `2024-10-01`. The trailer date is `2024-10-01`.
- **Verification method:** Verify output contains data rows with as_of = 2024-10-01. Verify all computed fields are consistent with source data for that date [FSD Section 6].

### TC-22: Last effective date boundary (2024-12-31)
- **Traces to:** Edge Case (boundary dates)
- **Input conditions:** Run V2 job for 2024-12-31 (the last date in the data range). Since this is Overwrite mode, this is the final output state after a full date range run.
- **Expected output:** Normal output with as_of = 2024-12-31. The trailer date is `2024-12-31`. This is the file state that Proofmark will compare.
- **Verification method:** Verify output values and trailer date. Compare against V1 output for the same date [FSD Section 10].

### TC-23: V2 uses Tier 2 pipeline (DataSourcing -> Transformation -> External)
- **Traces to:** FSD Tier Justification
- **Input conditions:** Inspect the V2 job config JSON.
- **Expected output:** The module chain is: DataSourcing (compliance_events) -> DataSourcing (transactions) -> Transformation (grouped_events) -> External (ComplianceTransactionRatioV2Processor). This is Tier 2: framework modules handle data access and grouping; a minimal External handles cross-DataFrame computation and file I/O.
- **Verification method:** Read `compliance_transaction_ratio_v2.json` and verify 4 modules in order: 2x DataSourcing, 1x Transformation, 1x External. The tier is justified by the W7 trailer requirement (CsvFileWriter cannot produce inflated trailer) and the empty-table SQLite limitation [FSD Section 1, Tier Justification].

### TC-24: Proofmark comparison passes with zero exclusions/fuzzy
- **Traces to:** FSD Proofmark Config Design
- **Input conditions:** Run both V1 and V2 jobs for the full date range (2024-10-01 through 2024-12-31). Run Proofmark with config: `reader: csv`, `header_rows: 1`, `trailer_rows: 1`, `threshold: 100.0`, no exclusions, no fuzzy columns.
- **Expected output:** Proofmark exits with code 0 (PASS). The V2 CSV file is byte-identical to V1 (same header, same data rows in same order, same trailer with identical inflated count and date).
- **Verification method:** Execute Proofmark comparison between `Output/curated/compliance_transaction_ratio.csv` and `Output/double_secret_curated/compliance_transaction_ratio.csv`. Verify exit code 0 and zero mismatches. This validates that moving grouping to SQL and using a V2 External module preserves output equivalence [FSD Section 8].

### TC-25: Output column order matches V1
- **Traces to:** BRD Output Schema
- **Input conditions:** Run V2 job for any valid date. Read the CSV header row.
- **Expected output:** The header row is exactly: `event_type,event_count,txn_count,events_per_1000_txns,as_of`. Columns appear in this exact order, comma-separated, with no trailing comma or extra whitespace.
- **Verification method:** Read the first line of the V2 CSV output. Compare against the expected header string. The External module writes the header manually: `event_type,event_count,txn_count,events_per_1000_txns,as_of` [FSD Section 10, Section 4].

### TC-26: NULL or missing transactions DataFrame handled gracefully
- **Traces to:** Edge Case (NULL safety)
- **Input conditions:** Run V2 job for a date where the transactions DataFrame might be null in shared state (e.g., if DataSourcing returns null for an empty table).
- **Expected output:** The External module handles this gracefully: `transactions?.Count ?? 0` produces txn_count = 0. The inflated trailer count equals compliance_events.Count + 0. All events_per_1000_txns values are 0.
- **Verification method:** Verify the External module code uses null-safe access (`?.Count ?? 0`) for the transactions DataFrame. If a null-transactions scenario can be triggered, run the job and verify zero txn_count and zero ratios [FSD Section 10, BR-2, BR-4].
