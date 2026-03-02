# AccountBalanceSnapshot -- Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | Output contains exactly 6 columns in correct order |
| TC-02   | BR-2           | Branches table is not sourced in V2 (dead-end eliminated) |
| TC-03   | BR-3           | Empty input (zero rows) produces empty output with correct schema |
| TC-04   | BR-4           | All column values are verbatim passthroughs from source |
| TC-05   | BR-5           | as_of column is present and matches source accounts.as_of |
| TC-06   | BR-6           | Weekday-only data produces correct row counts (2,869 per day) |
| TC-07   | BR-1           | Unused columns (open_date, interest_rate, credit_limit) are not sourced |
| TC-08   | Writer Config   | Parquet output uses Append mode with 2 part files |
| TC-09   | Edge Case       | Weekend date produces zero-row append (empty Parquet partition) |
| TC-10   | Edge Case       | NULL column values written as Parquet nulls (no coalescing) |
| TC-11   | Edge Case       | Multi-day run appends rows cumulatively (no deduplication) |
| TC-12   | Edge Case       | Month-end boundary date produces normal output (no special rows) |
| TC-13   | Edge Case       | Quarter-end boundary date produces normal output (no special rows) |
| TC-14   | FSD: Tier 1     | V2 produces identical output without an External module |
| TC-15   | Proofmark       | Proofmark comparison passes with zero exclusions and zero fuzzy columns |

## Test Cases

### TC-01: Output contains exactly 6 columns in correct order
- **Traces to:** BR-1
- **Input conditions:** Run V2 job for a single weekday (e.g., 2024-10-01) where accounts data exists.
- **Expected output:** Parquet output contains exactly 6 columns in order: `account_id`, `customer_id`, `account_type`, `account_status`, `current_balance`, `as_of`. No other columns are present.
- **Verification method:** Read the V2 Parquet output and inspect the schema. Compare column names and order against the BRD output schema. The FSD specifies that DataSourcing's `columns` array determines order, with `as_of` appended last [FSD Section 4].

### TC-02: Branches table is not sourced in V2
- **Traces to:** BR-2 (AP1 elimination)
- **Input conditions:** Inspect the V2 job config JSON.
- **Expected output:** The V2 config contains exactly one DataSourcing module entry (for `accounts`). There is no DataSourcing entry for `branches`.
- **Verification method:** Read `account_balance_snapshot_v2.json` and verify the modules array contains only one DataSourcing entry with `table: "accounts"`. This confirms AP1 (dead-end sourcing) is eliminated per FSD Section 3.

### TC-03: Empty input produces empty output
- **Traces to:** BR-3
- **Input conditions:** Run V2 job for a date where the accounts table has zero rows (e.g., a weekend date like 2024-10-05, Saturday, or 2024-10-06, Sunday).
- **Expected output:** Parquet part files are written but contain zero data rows. No error is thrown. The output directory exists with 2 part files, each containing only the schema (column definitions) and no data rows.
- **Verification method:** Read the output Parquet files and confirm row count is 0. Verify the schema header is still present (6 columns). The FSD notes that DataSourcing returns an empty DataFrame and ParquetFileWriter writes empty parts [FSD Section 1, empty-input handling].

### TC-04: All column values are verbatim passthroughs
- **Traces to:** BR-4
- **Input conditions:** Run V2 job for a single weekday. Query `datalake.accounts` for the same effective date to get the source data.
- **Expected output:** Every row in the V2 Parquet output matches the corresponding source row exactly -- same account_id, customer_id, account_type, account_status, current_balance, and as_of values. No values are transformed, rounded, truncated, or defaulted.
- **Verification method:** Compare V2 output row-by-row against a direct SQL query of `datalake.accounts` for the same date and columns. Use exact matching (no epsilon tolerance). This confirms the passthrough nature documented in BRD BR-4 and FSD Section 4.

### TC-05: as_of column matches source accounts.as_of
- **Traces to:** BR-5
- **Input conditions:** Run V2 job for a single weekday (e.g., 2024-10-01).
- **Expected output:** Every row's `as_of` value equals the effective date of the source snapshot (e.g., `2024-10-01`). The `as_of` column is auto-appended by the DataSourcing module since it is not listed in the explicit `columns` array [FSD Section 2, DataSourcing.cs:69-72].
- **Verification method:** Read V2 Parquet output and verify all `as_of` values match the expected effective date. Cross-reference with `SELECT DISTINCT as_of FROM datalake.accounts WHERE as_of = '2024-10-01'`.

### TC-06: Weekday data produces correct row counts
- **Traces to:** BR-6
- **Input conditions:** Run V2 job for multiple individual weekdays across the date range (e.g., 2024-10-01, 2024-10-15, 2024-11-01, 2024-12-31).
- **Expected output:** Each weekday produces exactly 2,869 rows in the output, consistent with the source accounts table. The total row count after N weekday runs is N * 2,869 (Append mode).
- **Verification method:** Count rows in V2 Parquet output after each run. Compare to `SELECT COUNT(*) FROM datalake.accounts WHERE as_of = '<date>'` for each date. The BRD documents 2,869 rows per day based on DB query evidence.

### TC-07: Unused columns are not sourced in V2
- **Traces to:** BR-1 (AP4 elimination)
- **Input conditions:** Inspect the V2 job config JSON.
- **Expected output:** The DataSourcing module's `columns` array contains exactly `["account_id", "customer_id", "account_type", "account_status", "current_balance"]`. The columns `open_date`, `interest_rate`, and `credit_limit` are absent.
- **Verification method:** Read `account_balance_snapshot_v2.json` and verify the columns list. This confirms AP4 (unused columns) is eliminated per FSD Section 3.

### TC-08: Writer configuration matches V1 (Append mode, 2 parts)
- **Traces to:** BRD Writer Configuration
- **Input conditions:** Run V2 job for two consecutive weekdays (e.g., 2024-10-01 then 2024-10-02).
- **Expected output:** After the first run, the output directory contains 2 part files with 2,869 rows total. After the second run, the same 2 part files contain 5,738 rows total (2 * 2,869). Output is not overwritten between runs -- data accumulates.
- **Verification method:** Count rows after each run. Verify 2 part files exist in `Output/double_secret_curated/account_balance_snapshot/` named `part-00000.parquet` and `part-00001.parquet`. Confirm rows from the first run are still present after the second run (Append semantics). Writer config must match: numParts=2, writeMode=Append [FSD Section 7].

### TC-09: Weekend date produces zero-row append
- **Traces to:** Edge Case (BRD: Weekend dates)
- **Input conditions:** Run V2 job for a Saturday (e.g., 2024-10-05) or Sunday (e.g., 2024-10-06).
- **Expected output:** DataSourcing returns zero rows because `datalake.accounts` has no weekend `as_of` dates. The ParquetFileWriter writes an empty append (zero data rows added). If prior weekday data exists, it is preserved (not overwritten, since this is Append mode).
- **Verification method:** Run for a weekday first (confirm rows exist), then run for a weekend date. Verify total row count is unchanged after the weekend run. Verify no error is thrown.

### TC-10: NULL column values written as Parquet nulls
- **Traces to:** Edge Case (BRD: Null handling)
- **Input conditions:** If any rows in `datalake.accounts` contain NULL values for any of the 6 output columns, run V2 job for a date containing such rows.
- **Expected output:** NULL values in the source are written as Parquet null values in the output. They are NOT replaced with empty strings, zeros, or default values.
- **Verification method:** Query `datalake.accounts` for rows with NULL values in any output column. If found, verify the corresponding V2 output rows contain Parquet nulls (not substituted values). The BRD states: "Null column values will be written as Parquet nulls" and BR-4 confirms verbatim passthrough.

### TC-11: Multi-day run appends rows cumulatively
- **Traces to:** Edge Case (BRD: Write Mode Implications)
- **Input conditions:** Run V2 job for a 5-weekday range (e.g., 2024-10-01 through 2024-10-07, which includes 5 weekdays).
- **Expected output:** Output contains 5 * 2,869 = 14,345 rows. Each day's rows have the correct `as_of` date. No deduplication occurs. Rows from all 5 days coexist in the output.
- **Verification method:** Count total rows and verify. Group by `as_of` and confirm 2,869 rows per date. Verify 5 distinct `as_of` values are present.

### TC-12: Month-end boundary produces normal output
- **Traces to:** Edge Case (date boundaries)
- **Input conditions:** Run V2 job for 2024-10-31 (last day of October, a weekday).
- **Expected output:** Normal output of 2,869 rows. No summary rows, boundary markers, or special behavior. The job has no W3a/W3b/W3c wrinkles [FSD Section 3].
- **Verification method:** Verify row count is exactly 2,869. Verify no rows contain aggregated/summary values. All rows should be standard account snapshots.

### TC-13: Quarter-end boundary produces normal output
- **Traces to:** Edge Case (date boundaries)
- **Input conditions:** Run V2 job for 2024-12-31 (last day of Q4, a weekday).
- **Expected output:** Normal output of 2,869 rows. No quarterly summary rows or special behavior. No W-codes apply to this job [FSD Section 3].
- **Verification method:** Same as TC-12 but for the quarter-end date.

### TC-14: V2 Tier 1 implementation produces identical output to V1
- **Traces to:** FSD Tier Justification, BR-4
- **Input conditions:** Run both V1 and V2 jobs for the full date range (2024-10-01 through 2024-12-31).
- **Expected output:** V2 output in `Output/double_secret_curated/account_balance_snapshot/` is byte-identical to V1 output in `Output/curated/account_balance_snapshot/`. The V2 Tier 1 chain (DataSourcing -> ParquetFileWriter) produces the same data as V1's Tier 3 chain (DataSourcing -> External -> ParquetFileWriter).
- **Verification method:** Run Proofmark comparison between V1 and V2 output directories. Proofmark must report PASS with 100% threshold. This validates the FSD's Tier 1 justification that DataSourcing alone produces the exact output schema without needing an External module [FSD Section 1].

### TC-15: Proofmark comparison with zero exclusions and zero fuzzy
- **Traces to:** FSD Proofmark Config Design
- **Input conditions:** Run Proofmark with the designed config: `reader: parquet`, `threshold: 100.0`, no `excluded_columns`, no `fuzzy_columns`.
- **Expected output:** Proofmark exits with code 0 (PASS). All 6 columns match exactly between V1 and V2 output.
- **Verification method:** Execute Proofmark with the config from FSD Section 8. Verify exit code is 0. Read the Proofmark report JSON and confirm zero mismatches across all columns. This validates the FSD's assertion that no exclusions or fuzzy overrides are needed.
