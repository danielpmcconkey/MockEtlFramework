# ComplianceResolutionTime -- Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | Only Cleared events with non-null review_date are included |
| TC-02   | BR-2           | days_to_resolve computed via julianday difference, cast to INTEGER (truncated) |
| TC-03   | BR-3, W4       | avg_resolution_days uses integer division (truncation, not rounding) |
| TC-04   | BR-4           | Results grouped by event_type and as_of |
| TC-05   | BR-5, BR-6     | Cross join on 1=1 is preserved, inflating resolved_count and total_days |
| TC-06   | BR-6           | avg_resolution_days is mathematically correct despite cross-join inflation |
| TC-07   | BR-7, AP8      | Unused ROW_NUMBER() window function removed in V2 SQL |
| TC-08   | Writer Config  | CsvFileWriter with header, LF line endings, trailer, Overwrite mode |
| TC-09   | Writer Config  | Trailer format is TRAILER\|{row_count}\|{date} |
| TC-10   | W9             | Overwrite mode: multi-day run retains only last effective date's output |
| TC-11   | AP4            | Unused columns (event_id, customer_id) removed from V2 DataSourcing |
| TC-12   | Edge Case      | No cleared events produces empty output (zero data rows, header + trailer only) |
| TC-13   | Edge Case      | All cleared events have NULL review_date produces empty output |
| TC-14   | Edge Case      | Negative resolution time (review_date < event_date) is included without guard |
| TC-15   | Edge Case      | Weekend/non-existent as_of date produces zero-row output |
| TC-16   | Edge Case      | NULL event_type passes through (not coalesced) |
| TC-17   | Edge Case      | Single as_of date: inflation factor equals total compliance_events row count |
| TC-18   | Edge Case      | Boundary date 2024-10-01 (first effective date) produces valid output |
| TC-19   | Edge Case      | Boundary date 2024-12-31 (last effective date) produces valid output |
| TC-20   | FSD: Tier 1    | V2 uses framework-only pipeline (no External module) |
| TC-21   | Proofmark      | Proofmark comparison passes with zero exclusions and zero fuzzy columns |
| TC-22   | Output Format  | Output columns in exact order: event_type, resolved_count, total_days, avg_resolution_days, as_of |

## Test Cases

### TC-01: Only Cleared events with non-null review_date are included
- **Traces to:** BR-1
- **Input conditions:** Run V2 job for a single weekday (e.g., 2024-10-01) where compliance_events contains rows with various statuses (Cleared, Open, Pending, etc.) and some Cleared events have NULL review_date.
- **Expected output:** Only rows where `status = 'Cleared' AND review_date IS NOT NULL` contribute to the resolved CTE. Events with other statuses or NULL review_date are excluded from the aggregation.
- **Verification method:** Query `datalake.compliance_events` for the same date, count rows matching the filter vs total rows. Compare the event_type groups in V2 output against the filtered source data. The V2 SQL WHERE clause `status = 'Cleared' AND review_date IS NOT NULL` must match V1 behavior [FSD Section 5].

### TC-02: days_to_resolve uses julianday difference cast to INTEGER
- **Traces to:** BR-2
- **Input conditions:** Run V2 job for a date with known Cleared events. Manually compute `julianday(review_date) - julianday(event_date)` for each qualifying event using the source data.
- **Expected output:** The `total_days` column (which is `SUM(days_to_resolve)` inflated by cross join) should be consistent with the sum of truncated julianday differences multiplied by the inflation factor. For example, if an event has event_date=2024-09-01 and review_date=2024-09-08, days_to_resolve = 7 (truncated from julianday difference).
- **Verification method:** Query source data for Cleared events with non-null review_date. Compute expected days_to_resolve per event using `CAST(julianday(review_date) - julianday(event_date) AS INTEGER)` in a standalone SQLite query. Verify the V2 output's total_days (after accounting for cross-join inflation) matches the expected sum. Truncation (not rounding) must be confirmed [FSD Section 5, BR-2].

### TC-03: avg_resolution_days uses integer division (W4)
- **Traces to:** BR-3, W4
- **Input conditions:** Run V2 job for a date where the average resolution days would have a fractional component (e.g., total_days_raw = 50, count_raw = 3 yields 50/3 = 16 truncated, not 17 rounded).
- **Expected output:** `avg_resolution_days` equals `CAST(SUM(days_to_resolve) AS INTEGER) / CAST(COUNT(*) AS INTEGER)`, which truncates toward zero. The cross-join inflation cancels: `(sum*M) / (N*M) = sum/N` where M is the inflation factor.
- **Verification method:** Compute the expected average manually from source data: sum all days_to_resolve for an event_type, divide by the count of resolved events (pre-inflation values). Verify the V2 output matches the truncated integer division result. Confirm this is truncation, not rounding (e.g., 16.67 becomes 16, not 17) [FSD Section 3, W4].

### TC-04: Results grouped by event_type and as_of
- **Traces to:** BR-4
- **Input conditions:** Run V2 job for a date where multiple event_types exist among Cleared events with non-null review_date.
- **Expected output:** Each unique (event_type, as_of) combination produces exactly one output row. The output contains one row per event_type per as_of date.
- **Verification method:** Query source data to identify distinct event_types among qualifying events. Verify V2 output has one row per event_type for the given as_of date. Confirm no duplicate (event_type, as_of) pairs exist in the output [FSD Section 5].

### TC-05: Cross join on 1=1 inflates resolved_count and total_days
- **Traces to:** BR-5, BR-6
- **Input conditions:** Run V2 job for a single date. Determine the total number of rows in the compliance_events DataFrame (all statuses, not just Cleared) for that date. This is the inflation factor M.
- **Expected output:** For each event_type group: `resolved_count = raw_count * M` and `total_days = raw_total_days * M`, where raw_count and raw_total_days are the values that would result without the cross join, and M is the total row count of the compliance_events DataFrame for that date (e.g., 115 rows per BRD evidence).
- **Verification method:** Compute raw_count and raw_total_days per event_type from source data (Cleared events with non-null review_date only). Multiply each by M (total compliance_events rows for that date). Compare against V2 output values. The cross join `JOIN compliance_events ON 1=1` must be present in V2 SQL for output equivalence [FSD Section 3, Cross Join Preservation].

### TC-06: avg_resolution_days correct despite inflation
- **Traces to:** BR-6
- **Input conditions:** Same as TC-05. For each event_type, compute both the inflated and non-inflated average.
- **Expected output:** `avg_resolution_days = CAST(raw_total_days * M AS INTEGER) / CAST(raw_count * M AS INTEGER)` which equals `CAST(raw_total_days AS INTEGER) / CAST(raw_count AS INTEGER)` because M cancels. The average is correct despite the inflated counts.
- **Verification method:** For each event_type, verify that `avg_resolution_days` in V2 output equals `raw_total_days / raw_count` (integer division). This confirms the mathematical invariant documented in BR-6 and FSD Section 3 [FSD Section 3, Cross Join Preservation].

### TC-07: Unused ROW_NUMBER() removed in V2
- **Traces to:** BR-7, AP8
- **Input conditions:** Inspect the V2 job config JSON and the Transformation SQL.
- **Expected output:** The V2 SQL does NOT contain `ROW_NUMBER()` or `PARTITION BY event_type ORDER BY event_date`. The CTE SELECT list contains only `event_type` and `days_to_resolve`. The removal of the unused window function does not change output.
- **Verification method:** Read `compliance_resolution_time_v2.json` and verify the SQL string does not contain `ROW_NUMBER`. Compare V1 and V2 SQL: the only structural differences should be removal of `ROW_NUMBER()`, `event_date`, `review_date`, and `rn` from the CTE [FSD Section 5, Changes from V1 to V2].

### TC-08: CsvFileWriter configuration matches V1
- **Traces to:** BRD Writer Configuration
- **Input conditions:** Inspect the V2 job config JSON CsvFileWriter module.
- **Expected output:** The writer is configured with: `includeHeader: true`, `writeMode: "Overwrite"`, `lineEnding: "LF"`, `trailerFormat: "TRAILER|{row_count}|{date}"`, and `source: "resolution_stats"`. Output path is `Output/double_secret_curated/compliance_resolution_time.csv`.
- **Verification method:** Read `compliance_resolution_time_v2.json` and verify each CsvFileWriter property matches the BRD writer configuration exactly (except the output path which uses the V2 directory) [FSD Section 6, Section 7].

### TC-09: Trailer format correct
- **Traces to:** BRD Writer Configuration
- **Input conditions:** Run V2 job for a single date (e.g., 2024-12-31, the last effective date with Overwrite mode).
- **Expected output:** The last line of the CSV file is a trailer in the format `TRAILER|{row_count}|{date}` where `{row_count}` is the number of data rows (excluding header and trailer) and `{date}` is the max effective date (e.g., `2024-12-31`). The trailer uses pipe delimiters, not commas.
- **Verification method:** Read the raw CSV file. Verify the last line matches `TRAILER|<N>|<date>` where N equals the number of data rows between the header and trailer. The `{row_count}` token is resolved by CsvFileWriter to the output DataFrame row count, and `{date}` to `__maxEffectiveDate` [FSD Section 2, Architecture.md CsvFileWriter docs].

### TC-10: Overwrite mode retains only last effective date's output (W9)
- **Traces to:** W9
- **Input conditions:** Run V2 job for 2024-10-01, then run again for 2024-10-02. Check the file after each run.
- **Expected output:** After the first run, the file contains results for 2024-10-01. After the second run, the file contains ONLY results for 2024-10-02. The 2024-10-01 data is completely overwritten. The trailer date is `2024-10-02`.
- **Verification method:** Read the CSV file after each run. Verify only one date's data is present after the second run. Check the `as_of` column values and trailer date. This confirms Overwrite mode behavior documented in W9 [FSD Section 3, W9].

### TC-11: Unused columns removed from V2 DataSourcing (AP4)
- **Traces to:** AP4
- **Input conditions:** Inspect the V2 job config JSON DataSourcing module.
- **Expected output:** The DataSourcing `columns` array contains exactly `["event_type", "event_date", "status", "review_date"]`. The columns `event_id` and `customer_id` are absent (they were sourced by V1 but never used in the SQL).
- **Verification method:** Read `compliance_resolution_time_v2.json` and verify the columns list. Compare against V1 config which includes `event_id` and `customer_id`. Confirm removal does not affect output by running the full comparison [FSD Section 6, AP4 analysis].

### TC-12: No cleared events produces empty output
- **Traces to:** Edge Case 1 (BRD)
- **Input conditions:** Run V2 job for a hypothetical date where no compliance_events have `status = 'Cleared'`. (If no such date exists in the data, this test validates the SQL logic conceptually: the CTE would return zero rows, the cross join would produce zero rows, and the GROUP BY would produce zero output rows.)
- **Expected output:** The CSV file contains only the header row and a trailer row with row_count = 0. No data rows between them. Format: header line, then `TRAILER|0|<date>`.
- **Verification method:** Verify the output file has exactly 2 lines (header + trailer) or verify through SQL analysis that the CTE returns zero rows when no events match the filter [FSD Section 5, BRD Edge Case 1].

### TC-13: All cleared events have NULL review_date produces empty output
- **Traces to:** Edge Case 1 (BRD), BR-1
- **Input conditions:** Similar to TC-12 but specifically testing the `review_date IS NOT NULL` filter. If all Cleared events have NULL review_date, the resolved CTE returns zero rows.
- **Expected output:** Same as TC-12: header + trailer only, with row_count = 0.
- **Verification method:** Same as TC-12. The `AND review_date IS NOT NULL` clause in the WHERE filters out any Cleared event with a missing review date [FSD Section 5].

### TC-14: Negative resolution time included without guard
- **Traces to:** Edge Case 4 (BRD)
- **Input conditions:** If any Cleared events exist where `review_date < event_date` (review happened before the event date, possibly a data error), run V2 for that date.
- **Expected output:** The `days_to_resolve` for such events is negative (e.g., if review_date is 2 days before event_date, days_to_resolve = -2). This negative value participates in SUM and COUNT normally. The V2 SQL has no guard against negative values, matching V1 behavior.
- **Verification method:** Query source data for events where `review_date < event_date`. If found, verify V2 output includes the negative days in the aggregation. If no such data exists, verify the V2 SQL contains no `WHERE days_to_resolve >= 0` or similar guard clause [BRD Edge Case 4].

### TC-15: Weekend/non-existent as_of date produces zero-row output
- **Traces to:** Edge Case (weekend dates)
- **Input conditions:** Run V2 job for a Saturday (e.g., 2024-10-05) or Sunday (e.g., 2024-10-06) where no compliance_events data exists.
- **Expected output:** DataSourcing returns zero rows. The Transformation SQL has no data to process, but since the CTE filters on `status = 'Cleared'`, even an empty base table yields zero resolved rows. The cross join with zero rows on either side yields zero output rows. The CSV contains header + trailer with row_count = 0.
- **Verification method:** Verify the output file has exactly 2 lines (header + trailer) after running for a weekend date. Verify no error is thrown [FSD Section 5].

### TC-16: NULL event_type is not coalesced
- **Traces to:** Edge Case (NULL handling)
- **Input conditions:** Check if any Cleared events with non-null review_date have NULL event_type in the source data.
- **Expected output:** If NULL event_type exists among qualifying events, it appears as-is in the output (empty string or NULL representation in CSV). The V2 SQL does NOT use COALESCE on event_type -- it groups directly on the raw value.
- **Verification method:** Query source data for Cleared events with non-null review_date and NULL event_type. If found, check V2 output for the corresponding group. The SQL uses `resolved.event_type` directly without COALESCE [FSD Section 5, V2 SQL].

### TC-17: Single as_of date inflation factor
- **Traces to:** BR-5, BR-6
- **Input conditions:** Run V2 job for a single date. Query `SELECT COUNT(*) FROM datalake.compliance_events WHERE as_of = '<date>'` to get M (the total rows in the DataFrame for that date, e.g., 115 per BRD evidence).
- **Expected output:** For each event_type: `resolved_count = raw_event_count * M`. For example, if 20 events of type "SAR" are Cleared with non-null review_date, and M = 115, then resolved_count for SAR = 20 * 115 = 2300.
- **Verification method:** Compute raw counts per event_type from source data. Multiply by M. Compare against V2 output resolved_count values. This quantitatively validates the cross-join inflation [BRD Edge Case 2, FSD Section 3].

### TC-18: First effective date boundary (2024-10-01)
- **Traces to:** Edge Case (boundary dates)
- **Input conditions:** Run V2 job for exactly 2024-10-01 (the firstEffectiveDate from the job config).
- **Expected output:** Normal output with valid resolution statistics. The as_of column shows `2024-10-01`. No special behavior at the first date boundary.
- **Verification method:** Verify output contains data rows with as_of = 2024-10-01. Verify row count and values are consistent with source data for that date [FSD Section 6, firstEffectiveDate].

### TC-19: Last effective date boundary (2024-12-31)
- **Traces to:** Edge Case (boundary dates)
- **Input conditions:** Run V2 job for 2024-12-31 (the last date in the data range).
- **Expected output:** Normal output with valid resolution statistics. Since this is Overwrite mode, this is the final output state after a full date range run.
- **Verification method:** Verify output contains data rows with as_of = 2024-12-31. Verify the trailer date is `2024-12-31`. This is the "final state" that Proofmark will compare [FSD Section 3, W9].

### TC-20: V2 uses Tier 1 framework-only pipeline
- **Traces to:** FSD Tier Justification
- **Input conditions:** Inspect the V2 job config JSON.
- **Expected output:** The module chain is exactly: DataSourcing -> Transformation -> CsvFileWriter. No External module is present. This is a Tier 1 implementation.
- **Verification method:** Read `compliance_resolution_time_v2.json` and verify the modules array contains exactly 3 entries: one DataSourcing, one Transformation, one CsvFileWriter. No External module type should be present [FSD Section 1, Tier Justification].

### TC-21: Proofmark comparison passes with zero exclusions/fuzzy
- **Traces to:** FSD Proofmark Config Design
- **Input conditions:** Run both V1 and V2 jobs for the full date range (2024-10-01 through 2024-12-31). Run Proofmark with config: `reader: csv`, `header_rows: 1`, `trailer_rows: 1`, `threshold: 100.0`, no exclusions, no fuzzy columns.
- **Expected output:** Proofmark exits with code 0 (PASS). The V2 CSV file is byte-identical to V1 (same header, same data rows, same trailer).
- **Verification method:** Execute Proofmark comparison between `Output/curated/compliance_resolution_time.csv` and `Output/double_secret_curated/compliance_resolution_time.csv`. Verify exit code 0 and zero mismatches in the report. This validates that AP8 removal (ROW_NUMBER) and AP4 removal (unused columns) do not affect output [FSD Section 8].

### TC-22: Output column order matches V1
- **Traces to:** BRD Output Schema
- **Input conditions:** Run V2 job for any valid date. Read the CSV header row.
- **Expected output:** The header row is exactly: `event_type,resolved_count,total_days,avg_resolution_days,as_of`. Columns appear in this exact order, comma-separated, with no trailing comma or extra whitespace.
- **Verification method:** Read the first line of the V2 CSV output file and compare against the expected header string. The column order is defined in the SQL SELECT clause [FSD Section 4, Section 5].
