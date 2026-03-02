# ComplianceEventSummary — Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | Group by (event_type, status) with COUNT aggregation |
| TC-02   | BR-2           | Sunday skip — zero data rows on Sundays |
| TC-03   | BR-3           | Empty/null compliance_events input produces zero data rows |
| TC-04   | BR-4           | Dead-end accounts source eliminated in V2 (AP1) |
| TC-05   | BR-5           | as_of value taken from input rows (uniform in single-day runs) |
| TC-06   | BR-6           | NULL event_type coalesced to empty string |
| TC-07   | BR-6           | NULL status coalesced to empty string |
| TC-08   | BR-7           | All five event types flow through without filtering |
| TC-09   | BR-8           | All three status values flow through without filtering |
| TC-10   | BR-9           | Trailer format: TRAILER\|{row_count}\|{date} |
| TC-11   | Writer Config  | Output format: CSV, header, Overwrite, LF, trailer |
| TC-12   | BR-2, Edge     | Saturday is NOT skipped — only Sunday triggers empty output |
| TC-13   | BR-1, Edge     | Single (event_type, status) combination produces count of 1 |
| TC-14   | BR-1, Edge     | Multiple rows for same (event_type, status) produce correct aggregate count |
| TC-15   | FSD Proofmark  | Proofmark config correctness |
| TC-16   | FSD SQL        | Column order matches V1 output schema exactly |
| TC-17   | BR-2, BR-9     | Sunday skip: trailer row_count is 0, date is the Sunday date |
| TC-18   | Edge Case      | Zero-row output file structure (header + trailer, no data rows) |
| TC-19   | FSD Anti-Pattern | AP3 elimination — no External module in V2 |
| TC-20   | FSD Anti-Pattern | AP4 elimination — event_id, customer_id not sourced in V2 |
| TC-21   | Edge Case      | Boundary date: first effective date (2024-10-01) |
| TC-22   | Edge Case      | Boundary date: last effective date (2024-12-31) |
| TC-23   | Non-Deterministic | Row order: V1 dictionary order vs V2 ORDER BY |

## Test Cases

### TC-01: Group By Aggregation
- **Traces to:** BR-1
- **Input conditions:** compliance_events data for a weekday effective date with multiple rows across different (event_type, status) combinations.
- **Expected output:** Each unique (event_type, status) pair appears exactly once in the output. The event_count column reflects the number of input rows matching that pair. For example, if 10 rows have (AML_FLAG, Open) and 5 rows have (AML_FLAG, Cleared), the output should contain two rows: (AML_FLAG, Open, 10) and (AML_FLAG, Cleared, 5).
- **Verification method:** For a given effective date, query `SELECT event_type, status, COUNT(*) FROM datalake.compliance_events WHERE as_of = '{date}' GROUP BY event_type, status` and compare against V2 output. Cross-validate with V1 output via Proofmark.

### TC-02: Sunday Skip
- **Traces to:** BR-2
- **Input conditions:** Run the job for a Sunday effective date. 2024-10-06 is the first Sunday in the date range.
- **Expected output:** The output CSV contains a header row and a trailer row, but zero data rows. The trailer should show `TRAILER|0|2024-10-06`.
- **Verification method:** Run for 2024-10-06 (Sunday). Verify the output file has exactly 2 lines: the header and the trailer. The trailer's row_count token should resolve to 0. Compare against V1 output.

### TC-03: Empty Input Handling
- **Traces to:** BR-3
- **Input conditions:** compliance_events DataFrame has zero rows for the effective date (either no data exists in datalake or the table is empty for that date).
- **Expected output:** The output CSV contains a header row and a trailer row, but zero data rows — same structure as the Sunday skip case.
- **Verification method:** If such a date exists in the test range, run for it and verify output. If all dates have data, this is verified by code inspection: the FSD notes that an empty DataSourcing result may cause a SQLite table-not-found error (Transformation.cs:46 skips empty DataFrames). This is a known risk documented in the FSD Risk Register — monitor during Phase D. If the SQL error occurs, escalation to Tier 2 is needed.

### TC-04: Dead-End Accounts Source Eliminated (AP1)
- **Traces to:** BR-4
- **Input conditions:** V2 job config JSON.
- **Expected output:** The V2 job config does NOT contain a DataSourcing module for the `accounts` table. V1 sourced accounts but never used them (dead-end). V2 eliminates this anti-pattern.
- **Verification method:** Read `JobExecutor/Jobs/compliance_event_summary_v2.json` and confirm only one DataSourcing entry exists (for `compliance_events`). No reference to `accounts` table.

### TC-05: as_of Value From Input Rows
- **Traces to:** BR-5
- **Input conditions:** Single-day executor run (min == max effective date) for a weekday.
- **Expected output:** Every output row's `as_of` column matches the effective date. Since single-day runs produce rows with a uniform `as_of`, V2's `GROUP BY ... as_of` produces the same result as V1's "take as_of from first row" approach.
- **Verification method:** Run for a specific weekday date (e.g., 2024-10-01). Verify all rows in the output have `as_of = 2024-10-01`. Compare against V1 output.

### TC-06: NULL event_type Coalesced to Empty String
- **Traces to:** BR-6
- **Input conditions:** A compliance_events row with NULL event_type (if such data exists in datalake, or verified by code path analysis).
- **Expected output:** The output row's event_type column is an empty string `""`, not NULL or "NULL". The row is still counted in its group — it groups as ("", status).
- **Verification method:** Query `SELECT COUNT(*) FROM datalake.compliance_events WHERE event_type IS NULL` to check if NULLs exist in test data. Per BRD BR-6, the current data has zero NULLs (verified by analyst), but the COALESCE is retained for defensive correctness. Confirm V2 SQL includes `COALESCE(event_type, '')`. If test data with NULLs can be injected (it cannot — datalake is read-only), this is verified by code inspection.

### TC-07: NULL Status Coalesced to Empty String
- **Traces to:** BR-6
- **Input conditions:** A compliance_events row with NULL status.
- **Expected output:** The output row's status column is an empty string `""`, not NULL. The row groups as (event_type, "").
- **Verification method:** Same approach as TC-06 but for the `status` column. Query `SELECT COUNT(*) FROM datalake.compliance_events WHERE status IS NULL`. Confirm V2 SQL includes `COALESCE(status, '')`.

### TC-08: All Event Types Flow Through
- **Traces to:** BR-7
- **Input conditions:** compliance_events data containing all five event types: AML_FLAG, ID_VERIFICATION, KYC_REVIEW, PEP_CHECK, SANCTIONS_SCREEN.
- **Expected output:** All five event types appear in the output (assuming each has at least one row in the effective date's data). No event types are filtered out.
- **Verification method:** Run for a weekday date with full event data. Extract distinct event_type values from output. Verify all five appear. Cross-reference with `SELECT DISTINCT event_type FROM datalake.compliance_events WHERE as_of = '{date}'`.

### TC-09: All Status Values Flow Through
- **Traces to:** BR-8
- **Input conditions:** compliance_events data containing all three statuses: Cleared, Escalated, Open.
- **Expected output:** All three status values appear in the output (assuming each has at least one row). No statuses are filtered out.
- **Verification method:** Run for a weekday date with full status data. Extract distinct status values from output. Verify all three appear. Cross-reference with `SELECT DISTINCT status FROM datalake.compliance_events WHERE as_of = '{date}'`.

### TC-10: Trailer Format
- **Traces to:** BR-9
- **Input conditions:** Standard weekday run producing N data rows.
- **Expected output:** The last line of the output CSV is `TRAILER|N|{effective_date}`, where N is the count of data rows (excluding header and trailer) and {effective_date} is the `__maxEffectiveDate` value (e.g., `TRAILER|15|2024-10-01`).
- **Verification method:** Read the last line of the output CSV. Parse the pipe-delimited tokens. Verify:
  - Token 1 = "TRAILER"
  - Token 2 = count of data rows in the file (total lines minus 2: header and trailer)
  - Token 3 = effective date in yyyy-MM-dd format matching `__maxEffectiveDate`

### TC-11: Output Format Verification
- **Traces to:** Writer Configuration (BRD)
- **Input conditions:** Standard job run producing output.
- **Expected output:**
  - File format: CSV
  - File location: `Output/double_secret_curated/compliance_event_summary.csv`
  - First line is a header row: `event_type,status,event_count,as_of`
  - Last line is a trailer row: `TRAILER|{row_count}|{date}`
  - Line endings are LF (Unix-style, `\n`), NOT CRLF
  - Write mode is Overwrite — running the job twice produces a file containing only the second run's data
  - Encoding: UTF-8, no BOM
- **Verification method:**
  - Verify file exists at expected path
  - Read first line and confirm header columns match expected schema
  - Read last line and confirm it starts with "TRAILER|"
  - Check line endings with `xxd` or `od` — no `\r\n` sequences
  - Run job twice for different dates and confirm the file only contains the second run's output

### TC-12: Saturday Is NOT Skipped
- **Traces to:** BR-2, BRD Edge Case 5
- **Input conditions:** Run for a Saturday effective date. 2024-10-05 is the first Saturday in the date range.
- **Expected output:** The output contains data rows — Saturday is processed normally. The Sunday skip logic in V1 (and replicated in V2) applies ONLY to Sundays.
- **Verification method:** Run for 2024-10-05. Verify the output file contains data rows (not just header + trailer). The data should reflect the normal GROUP BY aggregation for that date's compliance_events. Compare against V1 output.

### TC-13: Single Row Per Group
- **Traces to:** BR-1
- **Input conditions:** An effective date where at least one (event_type, status) combination has exactly one matching row.
- **Expected output:** That group's event_count = 1 in the output.
- **Verification method:** Query datalake for groups with COUNT(*) = 1 for the chosen date. Verify the corresponding output row shows event_count = 1.

### TC-14: Multiple Rows Per Group
- **Traces to:** BR-1
- **Input conditions:** An effective date where at least one (event_type, status) combination has many matching rows (e.g., > 10).
- **Expected output:** That group's event_count matches the actual count from the source data.
- **Verification method:** Query `SELECT event_type, status, COUNT(*) as cnt FROM datalake.compliance_events WHERE as_of = '{date}' GROUP BY event_type, status ORDER BY cnt DESC LIMIT 1` to find the largest group. Verify the output row for that group matches the expected count.

### TC-15: Proofmark Configuration Correctness
- **Traces to:** FSD Section 8
- **Input conditions:** Proofmark config for this job.
- **Expected output:** Config specifies:
  - `comparison_target: "compliance_event_summary"`
  - `reader: csv`
  - `threshold: 100.0`
  - `header_rows: 1` (under csv section)
  - `trailer_rows: 1` (under csv section)
  - No EXCLUDED columns
  - No FUZZY columns
- **Verification method:** Read the Proofmark YAML config at `POC3/proofmark_configs/compliance_event_summary.yaml` and verify all fields match the FSD's Proofmark config design section.

### TC-16: Column Order Matches V1
- **Traces to:** FSD Section 4 (Output Schema)
- **Input conditions:** Standard job run.
- **Expected output:** Output CSV columns appear in this exact order:
  1. event_type
  2. status
  3. event_count
  4. as_of
- **Verification method:** Read the header line of the output CSV. Verify column names and their order match exactly. Compare V2 header against V1 header.

### TC-17: Sunday Trailer Content
- **Traces to:** BR-2, BR-9
- **Input conditions:** Run for a Sunday effective date (e.g., 2024-10-06).
- **Expected output:** The trailer line reads `TRAILER|0|2024-10-06`. The row_count is 0 because zero data rows are produced. The date is the Sunday date itself (not a fallback Friday date — this job does Sunday skip, not weekend fallback).
- **Verification method:** Run for 2024-10-06. Read the trailer line. Verify the row count token resolves to 0 and the date token resolves to the Sunday date. Compare against V1 output.

### TC-18: Zero-Row Output File Structure
- **Traces to:** Edge Case (BRD Edge Cases 1 & 2)
- **Input conditions:** Any scenario producing zero data rows (Sunday skip or empty input).
- **Expected output:** The output file still exists and contains:
  - Line 1: Header row (`event_type,status,event_count,as_of`)
  - Line 2: Trailer row (`TRAILER|0|{date}`)
  - No blank lines between header and trailer
  - Total file is exactly 2 lines
- **Verification method:** Run for a Sunday. Count total lines in the output file. Verify exactly 2 lines exist with expected content.

### TC-19: AP3 Elimination — No External Module
- **Traces to:** FSD Section 1, Section 3
- **Input conditions:** V2 job config JSON.
- **Expected output:** The V2 job config uses a Transformation module (SQL) instead of an External module. No reference to `ComplianceEventSummaryBuilder` or any External module type in the config.
- **Verification method:** Read `JobExecutor/Jobs/compliance_event_summary_v2.json`. Confirm module types are: DataSourcing, Transformation, CsvFileWriter. No module with `"type": "External"` exists.

### TC-20: AP4 Elimination — Unused Columns Removed
- **Traces to:** FSD Section 3
- **Input conditions:** V2 job config JSON DataSourcing for compliance_events.
- **Expected output:** The V2 DataSourcing for compliance_events sources only `event_type` and `status` (plus auto-appended `as_of`). The columns `event_id` and `customer_id` are NOT sourced because they are not used in the GROUP BY or output.
- **Verification method:** Read the V2 job config's DataSourcing module for compliance_events. Confirm the `columns` array contains only `["event_type", "status"]`.

### TC-21: Boundary Date — First Effective Date
- **Traces to:** Edge Case
- **Input conditions:** Run for 2024-10-01 (first effective date, a Tuesday).
- **Expected output:** Normal output with GROUP BY aggregation. The as_of column shows 2024-10-01. The trailer date shows 2024-10-01.
- **Verification method:** Run for 2024-10-01. Verify output contains data rows with as_of = 2024-10-01. Verify trailer date matches. Compare against V1 output.

### TC-22: Boundary Date — Last Effective Date
- **Traces to:** Edge Case
- **Input conditions:** Run for 2024-12-31 (last effective date, a Tuesday).
- **Expected output:** Normal output with GROUP BY aggregation. The as_of column shows 2024-12-31. The trailer date shows 2024-12-31.
- **Verification method:** Run for 2024-12-31. Verify output contains data rows with as_of = 2024-12-31. Verify trailer date matches. Compare against V1 output.

### TC-23: Row Order — Non-Deterministic vs Deterministic
- **Traces to:** BRD Non-Deterministic Fields, FSD Section 5 Note 4
- **Input conditions:** Standard weekday run.
- **Expected output:** V1 output has non-deterministic row order (Dictionary enumeration). V2 output uses `ORDER BY event_type, status` for deterministic order. Row content should be identical (same groups, same counts), but row order may differ.
- **Verification method:** This is a known discrepancy documented in the FSD. Proofmark comparison should handle this if it does set-based (order-independent) matching. If Proofmark fails due to row order, the Proofmark config or V2 SQL ORDER BY may need adjustment. Verify by comparing sorted V1 output against sorted V2 output — they should be identical.
