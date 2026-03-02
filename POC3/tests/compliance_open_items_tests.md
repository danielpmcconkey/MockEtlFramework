# ComplianceOpenItems — Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | Only Open and Escalated status events included (per V1 source code) |
| TC-02   | BR-2           | Weekend fallback: Saturday maps to Friday (-1 day) |
| TC-03   | BR-2           | Weekend fallback: Sunday maps to Friday (-2 days) |
| TC-04   | BR-2           | Weekday dates used as-is (no fallback) |
| TC-05   | BR-3           | Only compliance_events rows matching target date (after fallback) are included |
| TC-06   | BR-4           | Customer name enrichment via LEFT JOIN by customer_id |
| TC-07   | BR-4           | Missing customer defaults first_name and last_name to empty string |
| TC-08   | BR-5, BR-6     | Unused columns (prefix, suffix, review_date) eliminated in V2 (AP4) |
| TC-09   | BR-7           | Empty/null compliance_events input produces zero-row output |
| TC-10   | BR-8           | Output as_of is the target date (after weekend fallback), not raw __maxEffectiveDate |
| TC-11   | BR-9           | Customer lookup uses all rows without date filter; last-seen wins |
| TC-12   | Writer Config  | Output format: Parquet, 1 part, Overwrite |
| TC-13   | BR-1, FSD      | BRD inconsistency: BR-1 text says "Open" only, but code filters Open + Escalated |
| TC-14   | Edge Case      | Weekend fallback: Friday data missing in datalake produces zero rows |
| TC-15   | Edge Case      | NULL first_name in customers produces empty string in output |
| TC-16   | Edge Case      | NULL last_name in customers produces empty string in output |
| TC-17   | FSD Proofmark  | Proofmark config correctness |
| TC-18   | FSD SQL        | Column order matches V1 output schema exactly |
| TC-19   | FSD Anti-Pattern | AP3 elimination — no External module in V2 |
| TC-20   | FSD Anti-Pattern | AP6 elimination — no row-by-row iteration in V2 |
| TC-21   | Edge Case      | Boundary date: 2024-10-01 (Tuesday, first effective date) |
| TC-22   | Edge Case      | Boundary date: 2024-12-31 (Tuesday, last effective date) |
| TC-23   | BR-2, Edge     | Weekend fallback across month boundary (e.g., Sunday Nov 3 -> Friday Nov 1) |
| TC-24   | Edge Case      | customer_id with no compliance events — no output row generated |
| TC-25   | BR-1           | Events with Cleared status are excluded |
| TC-26   | BR-8, BR-2     | Saturday run: as_of in output is Friday's date, not Saturday's |
| TC-27   | W9             | Overwrite mode: multi-day runs retain only last effective date's output |

## Test Cases

### TC-01: Status Filter — Open and Escalated
- **Traces to:** BR-1, FSD Section 3 (BRD Inconsistency)
- **Input conditions:** compliance_events data for a weekday effective date containing rows with status = 'Open', 'Escalated', and 'Cleared'.
- **Expected output:** Only rows with status 'Open' or 'Escalated' appear in the output. Rows with status 'Cleared' are excluded. This follows the V1 source code ground truth [ComplianceOpenItemsBuilder.cs:49-53] which filters for both, despite the BRD BR-1 text mentioning only 'Open'.
- **Verification method:** Run for a weekday date. Query `SELECT DISTINCT status FROM` the output Parquet file. Confirm only 'Open' and 'Escalated' appear. Query `SELECT COUNT(*) FROM datalake.compliance_events WHERE as_of = '{date}' AND status = 'Cleared'` to confirm Cleared events exist in source but are absent from output. Compare against V1 output via Proofmark.

### TC-02: Weekend Fallback — Saturday
- **Traces to:** BR-2
- **Input conditions:** Run for a Saturday effective date. 2024-10-05 is the first Saturday in the date range.
- **Expected output:** The job computes target_date = 2024-10-04 (Friday, -1 day). Only compliance_events rows with `as_of = 2024-10-04` are included. The output `as_of` column shows 2024-10-04, not 2024-10-05. In practice, since the executor sources only Saturday's data (min == max == Saturday), but the SQL filters for Friday's as_of, zero rows will be produced because Friday's data is not in the sourced set.
- **Verification method:** Run for 2024-10-05 (Saturday). Verify the output is empty (zero data rows) because DataSourcing only pulled Saturday's data but the WHERE clause filters to Friday. Compare against V1 output.

### TC-03: Weekend Fallback — Sunday
- **Traces to:** BR-2
- **Input conditions:** Run for a Sunday effective date. 2024-10-06 is the first Sunday in the date range.
- **Expected output:** The job computes target_date = 2024-10-04 (Friday, -2 days). Only compliance_events rows with `as_of = 2024-10-04` are included. Same as Saturday: zero rows produced because Friday's data is not sourced. Output as_of would be 2024-10-04.
- **Verification method:** Run for 2024-10-06 (Sunday). Verify empty output. Compare against V1 output.

### TC-04: Weekday Date — No Fallback
- **Traces to:** BR-2
- **Input conditions:** Run for a weekday effective date (e.g., 2024-10-01, Tuesday).
- **Expected output:** target_date equals the effective date itself. Compliance events with `as_of = 2024-10-01` and status IN ('Open', 'Escalated') appear in the output with as_of = 2024-10-01.
- **Verification method:** Run for 2024-10-01. Verify output rows have as_of = 2024-10-01. Verify row count matches `SELECT COUNT(*) FROM datalake.compliance_events WHERE as_of = '2024-10-01' AND status IN ('Open', 'Escalated')`. Compare against V1 output.

### TC-05: Target Date Filter
- **Traces to:** BR-3
- **Input conditions:** Single-day weekday run where DataSourcing returns rows with a single as_of date matching the effective date.
- **Expected output:** All compliance_events rows matching the target date (after any weekend fallback) and the status filter appear in the output. No rows from other dates leak through.
- **Verification method:** Verify every output row's as_of matches the target date. Run for multiple dates and confirm each run's output only references its own target date's data.

### TC-06: Customer Name Enrichment
- **Traces to:** BR-4
- **Input conditions:** Compliance events for customers who exist in the customers table. Run for a weekday date.
- **Expected output:** Each output row's first_name and last_name match the corresponding customer record from datalake.customers (by customer_id). The enrichment uses a LEFT JOIN, so all compliance events appear regardless of whether the customer exists.
- **Verification method:** For a sample of output rows, query `SELECT first_name, last_name FROM datalake.customers WHERE id = {customer_id} AND as_of = '{date}'` and confirm the names match. Compare against V1 output.

### TC-07: Missing Customer — Default to Empty String
- **Traces to:** BR-4
- **Input conditions:** A compliance event with a customer_id that does not exist in the customers table for the effective date.
- **Expected output:** The output row still appears (not dropped). The first_name and last_name columns are empty strings, not NULL.
- **Verification method:** Query for compliance event customer_ids that have no matching customer record. If any exist in the test data, verify those output rows have first_name = '' and last_name = ''. If all customer_ids have matches, this is verified by code inspection: V2 SQL uses `LEFT JOIN ... COALESCE(cl.first_name, '') ... COALESCE(cl.last_name, '')`.

### TC-08: Unused Columns Eliminated (AP4)
- **Traces to:** BR-5, BR-6, FSD Section 3
- **Input conditions:** V2 job config JSON.
- **Expected output:**
  - DataSourcing for compliance_events does NOT include `review_date` in columns list.
  - DataSourcing for customers does NOT include `prefix` or `suffix` in columns list.
  - These were sourced in V1 but never used in output (dead-end columns).
- **Verification method:** Read `JobExecutor/Jobs/compliance_open_items_v2.json`. Verify compliance_events columns are `["event_id", "customer_id", "event_type", "event_date", "status"]`. Verify customers columns are `["id", "first_name", "last_name"]`. No `review_date`, `prefix`, or `suffix` present.

### TC-09: Empty Input Produces Empty Output
- **Traces to:** BR-7
- **Input conditions:** compliance_events DataFrame has zero rows for the effective date.
- **Expected output:** The output Parquet file contains zero data rows (just the schema). The FSD notes a risk: if the compliance_events DataFrame is empty, Transformation.cs:46 skips table registration, and the SQL will fail with a table-not-found error.
- **Verification method:** If a date with zero compliance_events exists, run for that date and verify. Otherwise, this is a known risk to monitor during Phase D. If the edge case triggers, escalation to Tier 2 with a guard External module is warranted per FSD Section 5 Key Design Decision #3.

### TC-10: Output as_of Is Target Date
- **Traces to:** BR-8
- **Input conditions:** Run for a weekday effective date (e.g., 2024-10-01).
- **Expected output:** Every output row's `as_of` column equals the target date (which equals the effective date on weekdays). This is the post-fallback date, not the raw `__maxEffectiveDate` (though they coincide on weekdays).
- **Verification method:** Run for 2024-10-01. Verify all output rows have as_of = 2024-10-01. Compare against V1 output.

### TC-11: Customer Lookup — Last-Seen Wins
- **Traces to:** BR-9, FSD Section 3 (Customer Deduplication)
- **Input conditions:** Customers DataFrame containing multiple rows for the same customer_id across different as_of dates (possible in multi-day effective date ranges).
- **Expected output:** For each customer_id, the name values come from the row with the highest as_of date. V2 uses `ROW_NUMBER() OVER (PARTITION BY id ORDER BY as_of DESC)` with `rn = 1` to match V1's dictionary-overwrite behavior where later rows overwrite earlier ones.
- **Verification method:** In practice, single-day executor runs (min == max effective date) produce at most one as_of per customer, making deduplication a no-op. Verify by confirming output names match the customer record for the effective date. The deduplication logic is verified by code inspection of the V2 SQL's `customer_latest` CTE.

### TC-12: Output Format — Parquet
- **Traces to:** Writer Configuration (BRD)
- **Input conditions:** Standard job run producing output.
- **Expected output:**
  - File format: Parquet
  - Output directory: `Output/double_secret_curated/compliance_open_items/`
  - Part file: `part-00000.parquet` (numParts = 1)
  - Write mode: Overwrite — running the job twice produces a directory containing only the second run's part file
  - Schema: event_id (INT), customer_id (INT), first_name (TEXT), last_name (TEXT), event_type (TEXT), event_date (TEXT/date), status (TEXT), as_of (TEXT/date)
- **Verification method:**
  - Verify directory exists at expected path
  - Verify exactly one part file exists (`part-00000.parquet`)
  - Read the Parquet file and verify schema matches expected columns and types
  - Run job twice for different dates and confirm only the second run's data is present

### TC-13: BRD Inconsistency — Open vs Open+Escalated
- **Traces to:** BR-1, FSD Section 3 (BRD Inconsistency)
- **Input conditions:** compliance_events with Escalated status rows for the effective date.
- **Expected output:** Escalated status rows ARE included in the output. The FSD explicitly notes the BRD text inconsistency: BR-1 says "status = 'Open'" but V1 source code filters for both 'Open' and 'Escalated'. V2 follows source code (ground truth).
- **Verification method:** Run for a date with Escalated events. Verify those events appear in output. Query `SELECT COUNT(*) FROM datalake.compliance_events WHERE as_of = '{date}' AND status = 'Escalated'` and confirm the count is reflected in the output. Compare against V1 output.

### TC-14: Weekend Fallback — Friday Data Missing
- **Traces to:** Edge Case (BRD Edge Case 1)
- **Input conditions:** Run for a Saturday or Sunday. The weekend fallback targets Friday's date. But since DataSourcing only pulls the effective date's data (Saturday or Sunday), Friday's as_of rows are not present in the sourced DataFrame.
- **Expected output:** Zero output rows. The WHERE clause `ce.as_of = t.target_date` filters to Friday, but no Friday rows exist in the sourced data (only Saturday/Sunday rows).
- **Verification method:** Run for any Saturday (e.g., 2024-10-05) or Sunday (e.g., 2024-10-06). Verify the output Parquet file contains zero data rows. Compare against V1 output.

### TC-15: NULL first_name Produces Empty String
- **Traces to:** BRD Edge Case 5, BR-4
- **Input conditions:** A customer record with NULL first_name in datalake.customers.
- **Expected output:** The output row for that customer shows first_name as empty string `""`, not NULL.
- **Verification method:** Query `SELECT COUNT(*) FROM datalake.customers WHERE first_name IS NULL`. If NULLs exist, verify the corresponding output rows have first_name = ''. V2 SQL uses `COALESCE(cl.first_name, '')` which handles both LEFT JOIN misses and explicit NULLs.

### TC-16: NULL last_name Produces Empty String
- **Traces to:** BRD Edge Case 5, BR-4
- **Input conditions:** A customer record with NULL last_name in datalake.customers.
- **Expected output:** The output row for that customer shows last_name as empty string `""`, not NULL.
- **Verification method:** Query `SELECT COUNT(*) FROM datalake.customers WHERE last_name IS NULL`. If NULLs exist, verify the corresponding output rows have last_name = ''. V2 SQL uses `COALESCE(cl.last_name, '')`.

### TC-17: Proofmark Configuration Correctness
- **Traces to:** FSD Section 8
- **Input conditions:** Proofmark config for this job.
- **Expected output:** Config specifies:
  - `comparison_target: "compliance_open_items"`
  - `reader: parquet`
  - `threshold: 100.0`
  - No EXCLUDED columns
  - No FUZZY columns
- **Verification method:** Read the Proofmark YAML config at `POC3/proofmark_configs/compliance_open_items.yaml` and verify all fields match the FSD's Proofmark config design section. No column overrides are expected because all output columns are deterministic.

### TC-18: Column Order Matches V1
- **Traces to:** FSD Section 4 (Output Schema)
- **Input conditions:** Standard weekday job run.
- **Expected output:** Output Parquet columns appear in this exact order:
  1. event_id
  2. customer_id
  3. first_name
  4. last_name
  5. event_type
  6. event_date
  7. status
  8. as_of
- **Verification method:** Read the Parquet file schema and verify column names and their order match exactly. Compare V2 schema against V1 Parquet file schema.

### TC-19: AP3 Elimination — No External Module
- **Traces to:** FSD Section 1, Section 10
- **Input conditions:** V2 job config JSON.
- **Expected output:** The V2 config uses a Transformation module (SQL) instead of an External module. No reference to `ComplianceOpenItemsBuilder` or any External module type.
- **Verification method:** Read `JobExecutor/Jobs/compliance_open_items_v2.json`. Confirm module types are: DataSourcing (x2), Transformation, ParquetFileWriter. No module with `"type": "External"` exists.

### TC-20: AP6 Elimination — No Row-by-Row Iteration
- **Traces to:** FSD Section 3
- **Input conditions:** V2 implementation (SQL in job config).
- **Expected output:** V2 uses SQL set-based operations (LEFT JOIN for customer enrichment, WHERE for status filter) instead of V1's row-by-row foreach loop with Dictionary accumulation. No External module C# code exists for this job.
- **Verification method:** Verify V2 is Tier 1 (no External module). All logic is in the SQL string within the Transformation module config. The SQL uses JOIN and WHERE clauses, not procedural iteration.

### TC-21: Boundary Date — First Effective Date
- **Traces to:** Edge Case
- **Input conditions:** Run for 2024-10-01 (Tuesday, first effective date).
- **Expected output:** Normal output with open/escalated compliance events. as_of = 2024-10-01. No weekend fallback applied (Tuesday).
- **Verification method:** Run for 2024-10-01. Verify output rows have as_of = 2024-10-01 and status IN ('Open', 'Escalated'). Compare against V1 output.

### TC-22: Boundary Date — Last Effective Date
- **Traces to:** Edge Case
- **Input conditions:** Run for 2024-12-31 (Tuesday, last effective date).
- **Expected output:** Normal output with open/escalated compliance events. as_of = 2024-12-31.
- **Verification method:** Run for 2024-12-31. Verify output rows have as_of = 2024-12-31. Compare against V1 output.

### TC-23: Weekend Fallback Across Month Boundary
- **Traces to:** BR-2, Edge Case
- **Input conditions:** Run for Sunday 2024-11-03. Weekend fallback targets Friday 2024-11-01 (-2 days). This crosses no month boundary in this case, but verifies the date arithmetic works correctly near month edges. For a cross-month case: Saturday 2024-11-02 targets Friday 2024-11-01 (-1 day).
- **Expected output:** target_date = 2024-11-01 (Friday). Since DataSourcing only pulls the effective date's data (Saturday/Sunday), and the WHERE clause filters to Friday, zero output rows are produced (consistent with TC-14).
- **Verification method:** Run for 2024-11-02 (Saturday) or 2024-11-03 (Sunday). Verify empty output. The date arithmetic should correctly cross the month boundary if applicable (e.g., if running for Saturday 2024-06-01, target would be Friday 2024-05-31 — not in our date range but validates the logic pattern).

### TC-24: Customer With No Compliance Events
- **Traces to:** Edge Case
- **Input conditions:** A customer_id exists in datalake.customers but has no compliance events in datalake.compliance_events for the effective date.
- **Expected output:** No output row is generated for that customer. The job starts from compliance_events and LEFT JOINs customers — not the other way around. Customers without events simply don't appear.
- **Verification method:** This is verified by the SQL structure: the main query iterates compliance_events rows and LEFT JOINs customers. A customer with no events has no rows to join against and produces no output.

### TC-25: Cleared Status Excluded
- **Traces to:** BR-1
- **Input conditions:** compliance_events with status = 'Cleared' for the effective date.
- **Expected output:** Zero rows with status = 'Cleared' appear in the output. Only 'Open' and 'Escalated' pass the filter.
- **Verification method:** Run for a date with Cleared events. Query `SELECT COUNT(*) FROM datalake.compliance_events WHERE as_of = '{date}' AND status = 'Cleared'` to confirm they exist in source. Verify the output Parquet file contains no rows where status = 'Cleared'.

### TC-26: Saturday Run — as_of Is Friday's Date
- **Traces to:** BR-8, BR-2
- **Input conditions:** Run for Saturday 2024-10-05. Weekend fallback computes target_date = 2024-10-04 (Friday).
- **Expected output:** If any output rows existed, their as_of would be 2024-10-04 (Friday), not 2024-10-05 (Saturday). In practice, since DataSourcing only pulls Saturday's data and the WHERE filters to Friday, zero rows are produced. But the principle is validated: the output as_of is the target_date, not the raw effective date.
- **Verification method:** Run for 2024-10-05. If output is empty (expected), this is consistent. If output were non-empty (unexpected), verify as_of = 2024-10-04. The target_date computation is verified by examining the V2 SQL's `target` CTE: `strftime('%w', max_as_of) = '6' THEN date(max_as_of, '-1 day')`.

### TC-27: Overwrite Mode — Multi-Day Retention
- **Traces to:** W9, BRD Write Mode Implications
- **Input conditions:** Run the job for two consecutive weekday dates (e.g., 2024-10-01 then 2024-10-02).
- **Expected output:** After the second run, the output directory contains only the second date's data. The first date's output is overwritten because writeMode = Overwrite.
- **Verification method:** Run for 2024-10-01, note the output. Then run for 2024-10-02. Read the Parquet file and verify all rows have as_of = 2024-10-02. No rows from 2024-10-01 remain. This matches V1's Overwrite behavior (W9 — arguably wrong writeMode, but must be reproduced).
