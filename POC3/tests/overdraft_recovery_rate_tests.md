# OverdraftRecoveryRate — V2 Test Plan

## Job Info
- **V2 Config**: `overdraft_recovery_rate_v2.json`
- **Tier**: 1 (Framework Only)
- **External Module**: None (V1 External module `OverdraftRecoveryRateProcessor` eliminated via AP3)

## Pre-Conditions

1. PostgreSQL database is accessible at `172.18.0.1` with `datalake.overdraft_events` table populated.
2. V1 baseline output exists at `Output/curated/overdraft_recovery_rate.csv` for comparison.
3. V2 job config `overdraft_recovery_rate_v2.json` is deployed to `JobExecutor/Jobs/`.
4. Effective date range covers 2024-10-01 through 2024-12-31 (92 days).
5. The `datalake.overdraft_events` table contains rows with both `fee_waived = true` and `fee_waived = false` on at least some dates, and at least 23 dates have zero overdraft events (per FSD OQ-1).

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-1    | Output Schema  | Output columns match V1 schema exactly |
| TC-2    | BR-4           | Single summary row — row count equivalence |
| TC-3    | BR-1, BR-2     | Data content equivalence (event counts, recovery rate) |
| TC-4    | BR-6, Writer   | CSV writer config: header, trailer, Overwrite, LF |
| TC-5    | AP3, AP4, AP6  | Anti-pattern elimination verification |
| TC-6    | EC-1 thru EC-6 | Edge cases: empty data, division by zero, overwrite behavior |
| TC-7    | FSD Section 8  | Proofmark config correctness |
| TC-W4   | W4 (BR-2)      | Integer division produces recovery_rate = 0 |
| TC-W5   | W5 (BR-3)      | Banker's rounding is a no-op on zero |

## Test Cases

### TC-1: Output Schema Validation

- **Traces to:** BRD Output Schema
- **Input conditions:** Standard job run for the full effective date range (2024-10-01 to 2024-12-31).
- **Expected output:** The output CSV header contains exactly these columns in this order:
  1. `total_events`
  2. `charged_count`
  3. `waived_count`
  4. `recovery_rate`
  5. `as_of`
- **Verification method:** Read the first line (header) of the V2 output CSV at `Output/double_secret_curated/overdraft_recovery_rate.csv`. Confirm it contains exactly 5 columns in the order listed above. Compare against V1's header line at `Output/curated/overdraft_recovery_rate.csv` to confirm they are identical.

### TC-2: Row Count Equivalence

- **Traces to:** BR-4
- **Input conditions:** Full auto-advance run across the entire effective date range.
- **Expected output:** The final output file contains exactly 1 data row (plus 1 header row and 1 trailer row, for a total of 3 lines). The trailer's `{row_count}` token should resolve to `1`.
- **Verification method:** Count data rows in both V1 and V2 output files (excluding header and trailer). Both should have exactly 1 data row. Verify the trailer line contains `TRAILER|1|2024-12-31`.

### TC-3: Data Content Equivalence

- **Traces to:** BR-1, BR-2, BR-5
- **Input conditions:** Full auto-advance run.
- **Expected output:** The single data row in V2 matches V1 exactly:
  - `total_events` = count of all overdraft events across the full date range
  - `charged_count` = count where `fee_waived = false`
  - `waived_count` = count where `fee_waived = true`
  - `recovery_rate` = 0 (due to W4 integer division)
  - `as_of` = `2024-12-31` (the max effective date, formatted `yyyy-MM-dd`)
- **Verification method:** Run Proofmark comparison between V1 and V2 output. Additionally, manually verify counts by querying: `SELECT COUNT(*) AS total, SUM(CASE WHEN NOT fee_waived THEN 1 ELSE 0 END) AS charged, SUM(CASE WHEN fee_waived THEN 1 ELSE 0 END) AS waived FROM datalake.overdraft_events WHERE as_of BETWEEN '2024-10-01' AND '2024-12-31'`. Confirm `charged_count + waived_count = total_events`.
- **W-code note:** `recovery_rate` is always 0 due to W4. See TC-W4 for dedicated verification.

### TC-4: Writer Configuration

- **Traces to:** BR-6, BRD Writer Configuration
- **Input conditions:** Standard job run producing output.
- **Expected output:**
  - File format: CSV
  - File location: `Output/double_secret_curated/overdraft_recovery_rate.csv`
  - First line is a header row with column names
  - Last line is a trailer row matching `TRAILER|{row_count}|{date}` format
  - Trailer resolves to `TRAILER|1|2024-12-31` for the full date range
  - Line endings are LF (Unix-style `\n`), NOT CRLF
  - Write mode is Overwrite -- running the job twice produces a file containing only the second run's data
- **Verification method:**
  - Verify file exists at expected path
  - Read first line and confirm it matches expected column headers
  - Read last line and confirm it matches `TRAILER|1|2024-12-31`
  - Check line endings with `xxd` or `od -c` -- no `\r\n` sequences should exist
  - Run job for two different single dates and confirm the file only contains the second run's output (Overwrite behavior confirmed)

### TC-5: Anti-Pattern Elimination Verification

- **Traces to:** FSD Section 7
- **Input conditions:** Inspect V2 job config and module chain.
- **Expected output:**
  - **AP3 (Unnecessary External Module):** V2 config has NO External module entry. The module chain is `DataSourcing -> Transformation -> CsvFileWriter`. The V1 `OverdraftRecoveryRateProcessor` is not referenced.
  - **AP4 (Unused Columns):** V2 DataSourcing sources only `["fee_waived"]`. V1 sourced 7 columns (`overdraft_id`, `account_id`, `customer_id`, `overdraft_amount`, `fee_amount`, `fee_waived`, `event_timestamp`) but only `fee_waived` was used. The `as_of` column is auto-appended by the framework.
  - **AP6 (Row-by-Row Iteration):** V1's `foreach` loop over rows is replaced with SQL aggregation (`COUNT(*)`, `SUM(CASE ...)`). No C# iteration exists in V2.
- **Verification method:** Read the V2 job config JSON. Confirm:
  1. No module entry with `"type": "External"` exists.
  2. The DataSourcing module's `columns` array contains only `["fee_waived"]`.
  3. The Transformation module contains SQL with `COUNT(*)` and `SUM(CASE ...)` aggregation functions. No External module DLL path is referenced anywhere.

### TC-6: Edge Cases

#### TC-6a: Empty Source Data (EC-3, EC-6)
- **Traces to:** EC-3, EC-6, FSD OQ-1
- **Input conditions:** Run the job for a single effective date known to have zero overdraft events.
- **Expected output (V2):** The `CREATE TABLE IF NOT EXISTS` preamble creates an empty table. SQL returns a single row: `total_events=0, charged_count=0, waived_count=0, recovery_rate=0, as_of=NULL`. The CSV file contains header + 1 data row + trailer (row_count=1). No division-by-zero error occurs (guarded by `CASE WHEN COUNT(*) = 0 THEN 0`).
- **V1 comparison note:** V1 returns an empty DataFrame on zero-event dates, producing header + trailer (row_count=0). This is a known intermediate behavioral difference (FSD OQ-1). The final output after full auto-advance is identical because the last effective date has data and Overwrite mode replaces any intermediate files.
- **Verification method:** Identify a zero-event date from the data. Run V2 for that single date. Confirm the job succeeds without error. Confirm the output file is well-formed (no stack trace, no missing header/trailer).

#### TC-6b: Recovery Rate Always Zero (EC-1)
- **Traces to:** EC-1, W4
- **Input conditions:** Standard run across the full date range.
- **Expected output:** The `recovery_rate` column value is exactly `0` in every case. Since `charged_count + waived_count = total_events` and `waived_count >= 0`, it follows that `charged_count <= total_events`, so integer division always truncates to 0.
- **Verification method:** Inspect the output data row and confirm `recovery_rate = 0`. Additionally verify the mathematical invariant: `charged_count + waived_count = total_events` holds in the output row.

#### TC-6c: Overwrite on Multi-Day Runs (EC-4)
- **Traces to:** EC-4
- **Input conditions:** Run the job for two consecutive single dates (e.g., 2024-10-01, then 2024-10-02).
- **Expected output:** After the second run, the output file contains only the second date's data. The first date's output is completely replaced.
- **Verification method:** Run for date A, record `as_of` in the output. Run for date B, confirm `as_of` is now date B and no trace of date A remains in the file.

#### TC-6d: Multi-Statement SQL Execution (FSD OQ-3)
- **Traces to:** FSD OQ-3
- **Input conditions:** Standard job run.
- **Expected output:** The `CREATE TABLE IF NOT EXISTS ...; SELECT ...` multi-statement SQL executes without error. The Transformation module returns the SELECT result set correctly.
- **Verification method:** Confirm the job completes successfully and produces correct output. If multi-statement execution fails (Microsoft.Data.Sqlite error), this is a blocking issue requiring a fallback to two separate Transformation steps.

### TC-7: Proofmark Configuration

- **Traces to:** FSD Section 8
- **Input conditions:** Read the Proofmark YAML config for this job.
- **Expected output:** Config specifies:
  - `comparison_target: "overdraft_recovery_rate"`
  - `reader: csv`
  - `threshold: 100.0`
  - `csv.header_rows: 1`
  - `csv.trailer_rows: 1`
  - No EXCLUDED columns (all output values are deterministic)
  - No FUZZY columns (`recovery_rate` is always exactly 0, integer counts are exact, `as_of` is a date string)
- **Verification method:** Read the Proofmark YAML config at `POC3/proofmark_configs/overdraft_recovery_rate.yaml` and verify all fields match. Confirm `trailer_rows: 1` is set (Overwrite mode produces exactly one trailer at end of file, per CONFIG_GUIDE.md Example 3).

## W-Code Test Cases

### TC-W4: Integer Division (W4)

- **Traces to:** W4, BR-2, EC-1
- **Input conditions:** Full auto-advance run producing the final output.
- **Expected output:** `recovery_rate = 0`. The V2 SQL `SUM(CASE WHEN fee_waived = 0 THEN 1 ELSE 0 END) / COUNT(*)` performs integer division in SQLite (both operands are integers). Since `charged_count < total_events` (because `waived_count >= 1` in the test data), the result truncates to 0. This matches V1's `(decimal)(chargedCount / totalEvents)` where both operands are `int`.
- **Verification method:**
  1. Run V2 and confirm `recovery_rate = 0` in the output.
  2. Run Proofmark and confirm the `recovery_rate` column matches V1 exactly.
  3. Manually compute the correct recovery rate from the database: `SELECT CAST(SUM(CASE WHEN NOT fee_waived THEN 1 ELSE 0 END) AS FLOAT) / COUNT(*) FROM datalake.overdraft_events WHERE as_of BETWEEN '2024-10-01' AND '2024-12-31'`. Confirm this is a non-zero value (proving the integer division bug is real, not a coincidence of data).
- **Regression note:** If the data ever changes such that `charged_count = total_events` (i.e., zero waivers), then `recovery_rate` would become 1 instead of 0. The W4 bug only manifests when `charged_count < total_events`.

### TC-W5: Banker's Rounding (W5)

- **Traces to:** W5, BR-3, EC-2
- **Input conditions:** Full auto-advance run.
- **Expected output:** The output `recovery_rate` is `0`. V1 applies `Math.Round(recoveryRate, 4, MidpointRounding.ToEven)` to a value that is always 0 (due to W4). Rounding 0 to 4 decimal places produces 0. V2 omits the explicit `ROUND()` call because it is a no-op.
- **Verification method:**
  1. Confirm `recovery_rate = 0` in V2 output (same as TC-W4 verification).
  2. Confirm the V2 SQL does NOT contain a `ROUND()` call for recovery_rate (by design -- the no-op is intentionally omitted).
  3. Note FSD discrepancy: BRD says 2 decimal places (BR-3), source code uses 4 decimal places. Since the value is 0, the discrepancy has no output impact.
- **Future risk:** If W4 were ever fixed (integer division replaced with proper decimal division), W5 would become output-affecting. SQLite's `ROUND()` uses round-half-away-from-zero, NOT banker's rounding. At that point, the job would need to escalate to Tier 2 with an External module to replicate `MidpointRounding.ToEven`.

## Notes

- **BRD rounding precision discrepancy (FSD OQ-2):** The BRD states rounding is to 2 decimal places. The V1 source code rounds to 4. Both are moot because the value is always 0, but the V2 implementation follows the source code, not the BRD.
- **Intermediate-day behavioral difference (FSD OQ-1):** On zero-event dates, V2 produces 1 row of zeros while V1 produces 0 rows. This difference only affects intermediate runs; the final Overwrite output is identical. If per-day auditing is ever required, this would need to be addressed.
- **Boolean handling:** PostgreSQL `boolean` values are converted to SQLite `INTEGER` (0/1) by the framework's `ToSqliteValue` method. The SQL correctly uses `fee_waived = 0` for charged (not waived) and `fee_waived = 1` for waived.
- **Strict 100% Proofmark threshold:** No fuzzy or excluded columns are needed. All output values are deterministic integers or date strings. The `recovery_rate` is always exactly 0 in both V1 and V2.
