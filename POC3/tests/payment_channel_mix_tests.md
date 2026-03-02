# PaymentChannelMix — V2 Test Plan

## Job Info
- **V2 Config**: `payment_channel_mix_v2.json`
- **Tier**: 1 (Framework Only)
- **External Module**: None (V1 already framework-only; no External module in V1 or V2)

## Pre-Conditions

1. PostgreSQL database is accessible at `172.18.0.1` with `datalake.transactions`, `datalake.card_transactions`, and `datalake.wire_transfers` tables populated.
2. V1 baseline output exists at `Output/curated/payment_channel_mix/part-00000.parquet` for comparison.
3. V2 job config `payment_channel_mix_v2.json` is deployed to `JobExecutor/Jobs/`.
4. Effective date range covers 2024-10-01 through 2024-12-31 (92 days).
5. All three source tables contain data for at least some dates in the effective range.

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-1    | Output Schema  | Output columns match V1 schema exactly |
| TC-2    | BR-2, BR-3     | Row count equivalence (3 channels x N dates) |
| TC-3    | BR-1 thru BR-5 | Data content equivalence: channel labels, counts, amounts |
| TC-4    | Writer Config  | Parquet writer: numParts=1, Overwrite, output directory |
| TC-5    | AP4            | Anti-pattern elimination verification |
| TC-6    | EC-1 thru EC-5 | Edge cases: empty channels, no data, multi-date, unused columns |
| TC-7    | FSD Section 8  | Proofmark config correctness |

## Test Cases

### TC-1: Output Schema Validation

- **Traces to:** BRD Output Schema
- **Input conditions:** Standard job run for the full effective date range (2024-10-01 to 2024-12-31).
- **Expected output:** The output Parquet file contains exactly 4 columns in this order:
  1. `payment_channel` (string)
  2. `txn_count` (integer)
  3. `total_amount` (numeric/double)
  4. `as_of` (string/date)
- **Verification method:** Read the V2 Parquet file at `Output/double_secret_curated/payment_channel_mix/part-00000.parquet` and inspect its schema. Confirm column names, types, and order match the V1 Parquet file at `Output/curated/payment_channel_mix/part-00000.parquet`. Use Proofmark or a Parquet reader tool to compare schemas.

### TC-2: Row Count Equivalence

- **Traces to:** BR-2, BR-3
- **Input conditions:** Full auto-advance run across the entire effective date range.
- **Expected output:** The V2 output contains the same number of rows as V1. Since the job uses Overwrite mode, only the final effective date's output survives. The final run produces up to 3 rows (one per channel) for the last effective date. If any channel has zero rows for that date, that channel contributes 0 rows.
- **Verification method:** Count rows in both V1 and V2 Parquet output files. They must be identical. Additionally, verify the expected row count by querying each source table for the final effective date:
  ```sql
  SELECT 'transaction' AS channel, COUNT(*) FROM datalake.transactions WHERE as_of = '2024-12-31'
  UNION ALL
  SELECT 'card', COUNT(*) FROM datalake.card_transactions WHERE as_of = '2024-12-31'
  UNION ALL
  SELECT 'wire', COUNT(*) FROM datalake.wire_transfers WHERE as_of = '2024-12-31'
  ```
  Each channel with `COUNT(*) > 0` contributes 1 row to the output.

### TC-3: Data Content Equivalence

- **Traces to:** BR-1, BR-2, BR-3, BR-4, BR-5
- **Input conditions:** Full auto-advance run.
- **Expected output:** Every row in V2 matches V1 exactly (as a set comparison -- row order may differ):
  - `payment_channel` values are exactly `'transaction'`, `'card'`, and `'wire'` (BR-1)
  - `txn_count` = `COUNT(*)` for each channel per `as_of` date (BR-3)
  - `total_amount` = `ROUND(SUM(amount), 2)` for each channel per `as_of` date (BR-4)
  - `as_of` = the effective date from `GROUP BY` (BR-2)
  - Results are combined via `UNION ALL` (BR-5) -- no deduplication
- **Verification method:** Run Proofmark comparison between V1 and V2 output. Proofmark's Parquet comparison is set-based (row-order-independent), which handles the no-ORDER-BY non-determinism (BR-6). Additionally, manually verify a sample channel by querying:
  ```sql
  SELECT COUNT(*) AS txn_count, ROUND(SUM(amount)::numeric, 2) AS total_amount
  FROM datalake.transactions WHERE as_of = '2024-12-31'
  ```
  and confirming the output row for `payment_channel = 'transaction'` matches.
- **W-code note:** No W-codes apply to this job. All values are deterministic and computed identically in V1 and V2.

### TC-4: Writer Configuration

- **Traces to:** BRD Writer Configuration
- **Input conditions:** Standard job run producing output.
- **Expected output:**
  - File format: Parquet
  - Output directory: `Output/double_secret_curated/payment_channel_mix/`
  - Single part file: `part-00000.parquet`
  - `numParts: 1` -- exactly one output file, no splitting
  - Write mode: Overwrite -- the entire output directory is replaced on each run
  - No trailer (Parquet format does not support trailers)
  - No header configuration needed (Parquet embeds schema metadata)
- **Verification method:**
  - Verify the output directory exists and contains exactly one file: `part-00000.parquet`
  - Confirm no additional `part-NNNNN.parquet` files exist (numParts=1)
  - Run the job twice for different single dates. After the second run, confirm the output directory contains only the second run's data (Overwrite behavior). Open the Parquet file and verify `as_of` corresponds to the second date only.

### TC-5: Anti-Pattern Elimination Verification

- **Traces to:** FSD Section 7
- **Input conditions:** Inspect V2 job config and module chain.
- **Expected output:**
  - **AP4 (Unused Columns) -- ELIMINATED:** V2 DataSourcing for each table sources only `["amount"]`. V1 sourced 4 columns per table but only `amount` (and `as_of`, auto-appended) are used in the SQL:
    - `transactions`: V1 sourced `transaction_id`, `account_id`, `amount`, `description`. V2 sources only `amount`.
    - `card_transactions`: V1 sourced `card_txn_id`, `card_id`, `amount`, `merchant_name`. V2 sources only `amount`.
    - `wire_transfers`: V1 sourced `wire_id`, `customer_id`, `amount`, `counterparty_bank`. V2 sources only `amount`.
  - **No other AP-codes apply:** V1 was already framework-only (no AP3), no dead-end sourcing (no AP1), no row-by-row iteration (no AP6), no magic values (no AP7), no complex SQL (no AP8), no misleading names (no AP9), no over-sourcing dates (no AP10).
- **Verification method:** Read the V2 job config JSON. For each of the three DataSourcing modules, confirm the `columns` array is exactly `["amount"]`. Confirm the Transformation SQL is identical to V1's SQL (no changes needed). Confirm no Extra module entries exist.

### TC-6: Edge Cases

#### TC-6a: No Transactions in One Channel (EC-1)
- **Traces to:** BRD Edge Case 1
- **Input conditions:** A date where one of the three source tables (transactions, card_transactions, or wire_transfers) has zero rows.
- **Expected output:** The channel with no data contributes zero rows to the output. The other channels' rows appear normally via `UNION ALL`. The output is still valid but with fewer than 3 rows.
- **Verification method:** Query each source table for the target date to find one with zero rows. Run the job for that date and confirm the output contains rows only for channels with data. The missing channel should have no row in the output (not a row with zeros -- `GROUP BY` on an empty table produces no groups).

#### TC-6b: No Data in Any Channel (EC-2)
- **Traces to:** BRD Edge Case 2
- **Input conditions:** A date where all three source tables have zero rows (if such a date exists in the test data).
- **Expected output:** The entire `UNION ALL` produces zero rows. The Parquet file is created with the correct column schema but no data rows.
- **Verification method:** If such a date exists, run the job and confirm the Parquet file has zero rows but correct schema. If no such date exists in the test data, this is a theoretical edge case verified by code inspection: an empty `GROUP BY` in SQLite produces no output rows, and three empty `UNION ALL` branches produce an empty result set. Note: Unlike overdraft_recovery_rate, there is no `CREATE TABLE IF NOT EXISTS` preamble here. If DataSourcing returns an empty DataFrame and the table is not registered in SQLite, the SQL could fail. Monitor for this during Phase D testing.

#### TC-6c: Multiple Dates in Effective Range (EC-3)
- **Traces to:** BRD Edge Case 3
- **Input conditions:** Run the job for a multi-day date range (e.g., 2024-10-01 to 2024-10-03).
- **Expected output:** Each date appears as a separate row per channel (up to 3 rows per date). However, because write mode is Overwrite and the executor auto-advances one day at a time, only the last date's output survives.
- **Verification method:** Run the job with auto-advance across 3 dates. Confirm the final output contains only the last date's data. Verify `as_of` values in the output are only from the last date.

#### TC-6d: Channel Cross-Contamination Check (EC-5)
- **Traces to:** BRD Edge Case 5
- **Input conditions:** Standard run.
- **Expected output:** Each channel's `txn_count` and `total_amount` are computed from its own source table only. A transaction in `datalake.transactions` is never counted in the `card` or `wire` channel, and vice versa.
- **Verification method:** For a given date, independently query each source table:
  ```sql
  SELECT COUNT(*), ROUND(SUM(amount)::numeric, 2) FROM datalake.transactions WHERE as_of = '{date}'
  SELECT COUNT(*), ROUND(SUM(amount)::numeric, 2) FROM datalake.card_transactions WHERE as_of = '{date}'
  SELECT COUNT(*), ROUND(SUM(amount)::numeric, 2) FROM datalake.wire_transfers WHERE as_of = '{date}'
  ```
  Compare each result against the corresponding output row. No cross-contamination between channels.

#### TC-6e: Row Ordering Non-Determinism (BR-6)
- **Traces to:** BR-6, BRD Non-Deterministic Fields
- **Input conditions:** Standard run.
- **Expected output:** Row order in the output may vary between runs or SQLite versions because there is no `ORDER BY` clause. The `UNION ALL` typically produces rows in source-SELECT order (`transaction`, then `card`, then `wire`), but this is not guaranteed.
- **Verification method:** Proofmark's Parquet comparison operates as a set comparison (row-order-independent), so non-deterministic ordering does not affect the comparison result. Verify that Proofmark passes even if row order differs between V1 and V2. If manual inspection is needed, sort both outputs by `(payment_channel, as_of)` before comparing.

### TC-7: Proofmark Configuration

- **Traces to:** FSD Section 8
- **Input conditions:** Read the Proofmark YAML config for this job.
- **Expected output:** Config specifies:
  - `comparison_target: "payment_channel_mix"`
  - `reader: parquet`
  - `threshold: 100.0`
  - No `csv` section (not applicable for Parquet)
  - No EXCLUDED columns (all 4 output columns are deterministic: `payment_channel` is a string literal, `txn_count` is an exact integer, `total_amount` is `ROUND(SUM(), 2)` in the same SQLite engine for both V1 and V2, `as_of` is a date)
  - No FUZZY columns (`total_amount` uses `ROUND(SUM(amount), 2)` executed in the same SQLite engine for both V1 and V2, producing bit-identical results; no floating-point accumulation in C# code)
- **Verification method:** Read the Proofmark YAML config at `POC3/proofmark_configs/payment_channel_mix.yaml` and verify all fields match. Confirm no column overrides (excluded or fuzzy) are defined. Confirm the config is minimal (3 fields: comparison_target, reader, threshold).

## W-Code Test Cases

No W-codes apply to this job. The FSD reviewed all 11 W-codes (W1, W2, W3a/b/c, W4, W5, W6, W7, W8, W9, W10, W12) and confirmed none are applicable:

- No day-of-week logic (W1, W2)
- No summary/boundary rows (W3a/b/c)
- No division operations (W4)
- No banker's rounding -- `ROUND(SUM(amount), 2)` uses SQLite's standard rounding, which is round-half-away-from-zero, matching V1 (W5)
- No C# double accumulation (W6)
- No trailer (W7, W8) -- Parquet output
- Overwrite mode is correct for this job's pattern (W9)
- `numParts: 1` is reasonable (W10)
- No CSV header append issue (W12) -- Parquet output

No TC-W test cases are needed.

## Notes

- **V1 was already clean:** This is one of the simpler jobs in the portfolio. V1 already used the framework-only pattern (DataSourcing -> Transformation -> ParquetFileWriter) with no External module. The only V2 change is AP4 elimination (removing unused columns from the three DataSourcing modules).
- **SQL is unchanged:** The V2 Transformation SQL is identical to V1's SQL. No simplification was needed because the V1 SQL was already clean and correct.
- **ROUND behavior:** `ROUND(SUM(amount), 2)` in SQLite uses round-half-away-from-zero, which is the same behavior in both V1 and V2 since both execute the same SQL in the same SQLite engine. There is no risk of rounding divergence.
- **Parquet set comparison:** Proofmark's Parquet reader performs set-based comparison (row-order-independent), which naturally handles the `UNION ALL` non-deterministic row ordering. No special configuration is needed.
- **Empty DataFrame risk:** If any source table returns zero rows for a given date, the DataSourcing module may not register the table in SQLite (per `Transformation.cs:46`). The `GROUP BY` on a non-existent table would cause a SQL error. However, since each channel is a separate SELECT in the `UNION ALL`, only the affected channel would fail. This should be monitored during Phase D testing. Unlike overdraft_recovery_rate, there is no `CREATE TABLE IF NOT EXISTS` preamble in this job's SQL.
