# WireTransferDaily -- Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | Wire transfers grouped by as_of date with no filtering on status/direction |
| TC-02   | BR-2           | Per-date aggregations: wire_count, total_amount (rounded 2dp), avg_amount (rounded 2dp) |
| TC-03   | BR-3           | wire_date and as_of both equal the group key (as_of date) for daily rows |
| TC-04   | BR-4, BR-5     | MONTHLY_TOTAL summary row appended on month-end dates with correct values |
| TC-05   | BR-6           | Accounts table not sourced in V2 (AP1 dead-end eliminated) |
| TC-06   | BR-7           | Empty input produces zero-row output with no MONTHLY_TOTAL row |
| TC-07   | BR-8           | NULL as_of values silently excluded from aggregation |
| TC-08   | BR-9           | Effective dates injected by executor, no hard-coded dates in config |
| TC-09   | Writer Config   | Parquet output: 1 part, Overwrite mode, correct output directory |
| TC-10   | AP3             | V2 uses no External module -- Tier 1 framework-only chain |
| TC-11   | AP4             | Only wire_id and amount sourced (plus auto-appended as_of) |
| TC-12   | AP6             | SQL GROUP BY replaces row-by-row Dictionary iteration |
| TC-13   | Edge Case       | MONTHLY_TOTAL wire_date is string "MONTHLY_TOTAL" (mixed-type column) |
| TC-14   | Edge Case       | Non-month-end dates produce no MONTHLY_TOTAL row |
| TC-15   | Edge Case       | Overwrite mode: multi-day gap-fill retains only last date's output |
| TC-16   | W3b             | Month-end detection handles variable-length months (Oct 31, Nov 30, Dec 31) |
| TC-17   | W5 (contingency)| Banker's rounding vs arithmetic rounding monitored for avg_amount/total_amount |
| TC-18   | Proofmark       | Proofmark comparison passes: parquet reader, 100% threshold, zero exclusions |

## Test Cases

### TC-01: Wire transfers grouped by as_of with no status/direction filtering
- **Traces to:** BR-1
- **Input conditions:** Run V2 job for a single weekday (e.g., 2024-10-01) where wire_transfers data exists with mixed statuses (Completed, Pending, Rejected) and directions (Inbound, Outbound).
- **Expected output:** All wire transfers for that date are included in the aggregation regardless of status or direction. The output contains one daily row with wire_count equal to the total number of wire_transfers rows for that as_of date.
- **Verification method:** Compare V2 output wire_count against `SELECT COUNT(*) FROM datalake.wire_transfers WHERE as_of = '2024-10-01'`. All rows must be counted, no filtering applied. Evidence: [WireTransferDailyProcessor.cs:31-44] groups by as_of with no status/direction check; [FSD Section 4, note 1].

### TC-02: Per-date aggregations computed correctly
- **Traces to:** BR-2
- **Input conditions:** Run V2 job for a single weekday (e.g., 2024-10-15).
- **Expected output:** Output contains exactly one daily row with:
  - `wire_count` = COUNT of all wire_transfers rows for that date
  - `total_amount` = SUM(amount) rounded to 2 decimal places
  - `avg_amount` = total_amount / wire_count rounded to 2 decimal places
- **Verification method:** Query `SELECT COUNT(*), ROUND(SUM(amount)::numeric, 2), ROUND((SUM(amount) / COUNT(*))::numeric, 2) FROM datalake.wire_transfers WHERE as_of = '2024-10-15'` and compare values against V2 Parquet output. Evidence: [FSD Section 4]; [BRD BR-2].

### TC-03: wire_date and as_of both equal group key for daily rows
- **Traces to:** BR-3
- **Input conditions:** Run V2 job for a single weekday (e.g., 2024-10-01).
- **Expected output:** In the daily output row, `wire_date` equals the as_of date and `as_of` equals the same as_of date. Both columns contain the identical value.
- **Verification method:** Read V2 Parquet output and verify `wire_date == as_of` for all daily rows. The SQL aliases `as_of AS wire_date` and also selects `as_of` directly [FSD Section 4, design note 6].

### TC-04: MONTHLY_TOTAL row appended on month-end dates
- **Traces to:** BR-4, BR-5
- **Input conditions:** Run V2 job for month-end dates: 2024-10-31, 2024-11-30, 2024-12-31.
- **Expected output:** Each run produces two rows:
  1. A daily aggregation row (wire_date = the date, normal aggregates)
  2. A MONTHLY_TOTAL row with:
     - `wire_date` = "MONTHLY_TOTAL" (string literal)
     - `wire_count` = COUNT(*) of all wires in the effective range
     - `total_amount` = ROUND(SUM(amount), 2)
     - `avg_amount` = ROUND(SUM(amount) / COUNT(*), 2)
     - `as_of` = MAX(as_of) = the month-end date
- **Verification method:** Run V2 for each month-end date. Verify output contains exactly 2 rows. Verify MONTHLY_TOTAL row values match aggregate query results. Evidence: [FSD Section 4, UNION ALL structure]; [BRD BR-4, BR-5]; W3b replication in [FSD Section 6].

### TC-05: Accounts table not sourced (AP1 elimination)
- **Traces to:** BR-6
- **Input conditions:** Inspect V2 job config JSON.
- **Expected output:** The V2 config contains exactly one DataSourcing module entry for `wire_transfers`. There is NO DataSourcing entry for `accounts`.
- **Verification method:** Read `wire_transfer_daily_v2.json` and verify the modules array contains only one DataSourcing entry with `table: "wire_transfers"`. V1 sourced `datalake.accounts` (columns: account_id, customer_id, account_type) but the External module never referenced it. Evidence: [FSD Section 3]; [BRD BR-6]; [wire_transfer_daily.json:14-18].

### TC-06: Empty input produces zero-row output with no MONTHLY_TOTAL
- **Traces to:** BR-7
- **Input conditions:** Run V2 job for a date with no wire_transfers data (e.g., a weekend date if no weekend data exists, or a date outside the data range).
- **Expected output:** Output Parquet file contains zero data rows. No MONTHLY_TOTAL row is produced (the HAVING clause with `COUNT(*) > 0` prevents it). Output directory exists with the correct schema but no data.
- **Verification method:** Run V2 for a zero-data date. Read Parquet output and confirm 0 rows. Verify no MONTHLY_TOTAL row exists. Evidence: [FSD Section 4, HAVING clause]; [BRD BR-7]; V1 returns before monthly check when input is empty [WireTransferDailyProcessor.cs:21-25].

### TC-07: NULL as_of values silently excluded
- **Traces to:** BR-8
- **Input conditions:** If wire_transfers contains any rows with NULL as_of (or test with synthetic data containing NULL as_of).
- **Expected output:** Rows with NULL as_of are excluded from both the daily aggregation and the MONTHLY_TOTAL calculation. No error is raised.
- **Verification method:** Verify the V2 SQL contains `WHERE as_of IS NOT NULL` in both SELECT clauses. This replicates V1's `if (asOf == null) continue;` behavior. Note: DataSourcing's PostgreSQL-level filter already excludes NULLs, so this is a defensive measure. Evidence: [FSD Section 4, design note 4]; [BRD BR-8].

### TC-08: Effective dates injected by executor
- **Traces to:** BR-9
- **Input conditions:** Inspect V2 job config JSON.
- **Expected output:** No `minEffectiveDate` or `maxEffectiveDate` fields appear in the DataSourcing config. The `firstEffectiveDate` is set to "2024-10-01" at the job level. Effective dates are injected at runtime by the executor.
- **Verification method:** Read `wire_transfer_daily_v2.json` and confirm DataSourcing has no date fields. Evidence: [FSD Section 3]; executor injects dates via `__minEffectiveDate` / `__maxEffectiveDate` shared state keys.

### TC-09: Writer config: Parquet, 1 part, Overwrite mode
- **Traces to:** BRD Writer Configuration
- **Input conditions:** Inspect V2 job config and run V2 for a single date.
- **Expected output:** Config specifies: type=ParquetFileWriter, source="output", outputDirectory="Output/double_secret_curated/wire_transfer_daily/", numParts=1, writeMode="Overwrite". Output directory contains exactly 1 part file after a run.
- **Verification method:** Read `wire_transfer_daily_v2.json` and verify ParquetFileWriter config matches. Run V2 and count part files in the output directory. Evidence: [FSD Section 5]; [BRD Writer Configuration]; V1 uses identical writer params except output path [wire_transfer_daily.json:25-29].

### TC-10: No External module in V2 (AP3 elimination)
- **Traces to:** AP3
- **Input conditions:** Inspect V2 job config JSON.
- **Expected output:** The V2 modules array contains exactly 3 modules: DataSourcing, Transformation, ParquetFileWriter. No "External" type module exists. The entire pipeline is Tier 1 framework-only.
- **Verification method:** Read `wire_transfer_daily_v2.json` and verify no module has `type: "External"`. V1 used `ExternalModules.WireTransferDailyProcessor` for GROUP BY aggregation and conditional MONTHLY_TOTAL logic, both of which are now expressed in SQL. Evidence: [FSD Section 1, Tier justification]; [FSD Section 7, AP3].

### TC-11: Only wire_id and amount sourced (AP4 elimination)
- **Traces to:** AP4
- **Input conditions:** Inspect V2 job config JSON.
- **Expected output:** DataSourcing columns are exactly `["wire_id", "amount"]`. V1 sourced 7 columns (wire_id, customer_id, account_id, direction, amount, wire_timestamp, status) but only used as_of (auto-appended) and amount. Five columns eliminated: customer_id, account_id, direction, wire_timestamp, status.
- **Verification method:** Read `wire_transfer_daily_v2.json` and verify the columns array. Evidence: [FSD Section 2, Column reduction]; [BRD Source Tables]; [WireTransferDailyProcessor.cs:34-35] accesses only row["as_of"] and row["amount"].

### TC-12: SQL GROUP BY replaces row-by-row iteration (AP6 elimination)
- **Traces to:** AP6
- **Input conditions:** Inspect V2 job config and Transformation SQL.
- **Expected output:** V2 uses `GROUP BY as_of` with aggregate functions (COUNT, SUM, ROUND) instead of V1's foreach loop over DataTable.Rows with a Dictionary accumulator. The SQL performs set-based aggregation.
- **Verification method:** Verify the Transformation module's SQL contains `GROUP BY as_of` with `COUNT(*)`, `SUM(amount)`, and `ROUND(...)`. No procedural iteration exists in V2. Evidence: [FSD Section 4]; V1's foreach loop at [WireTransferDailyProcessor.cs:31-44].

### TC-13: MONTHLY_TOTAL wire_date is string (mixed-type column)
- **Traces to:** BRD Edge Case 2
- **Input conditions:** Run V2 for a month-end date (e.g., 2024-10-31).
- **Expected output:** The daily row has `wire_date` as a date value (2024-10-31). The MONTHLY_TOTAL row has `wire_date` as the string literal "MONTHLY_TOTAL". This produces a mixed-type column in Parquet, matching V1 behavior.
- **Verification method:** Read V2 Parquet output for a month-end run. Verify the MONTHLY_TOTAL row's wire_date field is the string "MONTHLY_TOTAL", not a date. Evidence: [FSD Section 4, design note 7]; [BRD Edge Case 2]; [WireTransferDailyProcessor.cs:72].

### TC-14: Non-month-end dates produce no MONTHLY_TOTAL row
- **Traces to:** BR-4 (inverse)
- **Input conditions:** Run V2 for mid-month dates (e.g., 2024-10-15, 2024-11-15).
- **Expected output:** Output contains exactly 1 row (the daily aggregation). No MONTHLY_TOTAL row is present. The HAVING clause `strftime('%d', MAX(as_of), '+1 day') = '01'` evaluates to false, so the UNION ALL's second SELECT produces zero rows.
- **Verification method:** Run V2 for a mid-month date. Verify output has exactly 1 row. Verify no row with wire_date = "MONTHLY_TOTAL" exists. Evidence: [FSD Section 4, HAVING clause]; [FSD Section 6, W3b].

### TC-15: Overwrite mode retains only last date's output
- **Traces to:** BRD Write Mode Implications
- **Input conditions:** Run V2 for 2024-10-01, then run again for 2024-10-02.
- **Expected output:** After the second run, the output directory contains only 2024-10-02 data. The 2024-10-01 data is gone (overwritten). Only one daily row exists from the final run.
- **Verification method:** Run V2 for two consecutive dates. After the second run, read output and verify only the second date's data exists. Confirm row count is 1 (or 2 if month-end). Evidence: [FSD Section 5, Write mode implications]; [BRD Write Mode Implications].

### TC-16: Month-end detection handles variable-length months (W3b)
- **Traces to:** W3b
- **Input conditions:** Run V2 for all three month-end dates in the data range: 2024-10-31 (31 days), 2024-11-30 (30 days), 2024-12-31 (31 days).
- **Expected output:** Each run produces a MONTHLY_TOTAL row. The SQLite expression `strftime('%d', MAX(as_of), '+1 day') = '01'` correctly identifies the last day of each month regardless of month length.
- **Verification method:** Run V2 for each month-end date. Verify MONTHLY_TOTAL row is present in each output. Also run for one day before each month-end (Oct 30, Nov 29, Dec 30) and verify no MONTHLY_TOTAL row appears. Evidence: [FSD Section 6, W3b]; V1 uses `DateTime.DaysInMonth` [WireTransferDailyProcessor.cs:65].

### TC-17: Rounding mode divergence monitoring (W5 contingency)
- **Traces to:** W5
- **Input conditions:** Run V2 for the full date range (2024-10-01 through 2024-12-31) and compare total_amount and avg_amount against V1 output for every date.
- **Expected output:** Values match exactly in the common case. If a midpoint value (exactly x.xx5000...) occurs, V1's banker's rounding (MidpointRounding.ToEven) and V2's SQLite arithmetic rounding (round half away from zero) may produce a 0.01 difference.
- **Verification method:** Run Proofmark comparison with strict matching first. If any date shows a mismatch on total_amount or avg_amount, inspect whether the difference is exactly 0.01 and whether the pre-rounded value was an exact midpoint. If so, add fuzzy tolerance per FSD Section 8 contingency plan. Evidence: [FSD Section 4, design note 5]; [FSD Section 9, Open Question 1].

### TC-18: Proofmark comparison passes
- **Traces to:** Proofmark
- **Input conditions:** Run both V1 and V2 for the full effective date range. Configure Proofmark with: `comparison_target: "wire_transfer_daily"`, `reader: parquet`, `threshold: 100.0`, zero excluded columns, zero fuzzy columns.
- **Expected output:** Proofmark reports 100% match across all effective dates. Row counts match. Column values match. Column ordering matches (wire_date, wire_count, total_amount, avg_amount, as_of).
- **Verification method:** Run Proofmark comparison tool. Verify threshold met. If comparison fails on rounding (W5), escalate to fuzzy tolerance per TC-17. Evidence: [FSD Section 8, Proofmark Config].
