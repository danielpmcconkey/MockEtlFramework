# PeakTransactionTimes — V2 Test Plan

## Job Info
- **V2 Config**: `peak_transaction_times_v2.json`
- **Tier**: 2 (Framework + Minimal External / SCALPEL)
- **External Module**: `PeakTransactionTimesV2Writer` (minimal: file I/O only -- W7 trailer + UTF-8 BOM encoding)

## Pre-Conditions
1. PostgreSQL is accessible at `172.18.0.1` with `datalake.transactions` populated.
2. The V1 baseline output exists at `Output/curated/peak_transaction_times.csv`.
3. The V2 External module (`PeakTransactionTimesV2Writer`) is compiled and accessible at the assembly path.
4. The effective date range includes at least one date with transaction data (e.g., 2024-10-01 through 2024-10-15).
5. The V2 job config sources only `transactions` with columns `txn_timestamp` and `amount` (AP1/AP4 elimination verified).

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01 | Output Schema | Output columns are exactly `hour_of_day, txn_count, total_amount, as_of` in order |
| TC-02 | BR-1 through BR-4 | Row count equivalence between V1 and V2 output |
| TC-03 | BR-1 through BR-6 | Data content equivalence via Proofmark |
| TC-04 | Writer Config | CSV format: header, LF line endings, trailer, Overwrite, UTF-8 with BOM |
| TC-05 | AP1, AP3, AP4, AP6 | Anti-pattern elimination verification |
| TC-06 | Edge Cases | Empty input, missing hours, timestamp parsing fallback |
| TC-07 | FSD Section 8 | Proofmark configuration correctness |
| TC-W5 | W5 | Banker's rounding vs SQLite round-half-away-from-zero on total_amount |
| TC-W7 | W7 | Trailer uses input transaction count, not output row count |

## Test Cases

### TC-01: Output Schema Validation
- **Traces to:** BRD Output Schema, FSD Section 4
- **Input conditions:** Standard job run for any date with transaction data.
- **Expected output:** The output CSV header line contains exactly these columns in this order: `hour_of_day,txn_count,total_amount,as_of`. No additional columns, no missing columns, no reordering.
- **Verification method:**
  - Read the first line of `Output/double_secret_curated/peak_transaction_times.csv`.
  - Confirm it is `hour_of_day,txn_count,total_amount,as_of`.
  - Compare against V1 output header at `Output/curated/peak_transaction_times.csv` -- they must be identical.
  - Confirm column count is exactly 4.

### TC-02: Row Count Equivalence
- **Traces to:** BR-1, BR-4
- **Input conditions:** Run V2 for the same effective date range as V1.
- **Expected output:** The number of data rows in V2 output matches V1 exactly. Each row represents one hour-of-day bucket (0-23) that had at least one transaction. Hours with zero transactions do not appear (BRD Edge Case 6).
- **Verification method:**
  - Count data rows in both V1 and V2 output files (excluding header and trailer).
  - The counts must be identical.
  - Cross-reference with `SELECT COUNT(DISTINCT EXTRACT(HOUR FROM txn_timestamp)) FROM datalake.transactions WHERE as_of = '{date}'` to confirm the expected number of active hours.

### TC-03: Data Content Equivalence
- **Traces to:** BR-1 through BR-6
- **Input conditions:** Run V2 for the same effective date range as V1.
- **Expected output:** Every data row in V2 matches V1 exactly (subject to W5/W6 rounding analysis -- see TC-W5).
  - `hour_of_day`: integer 0-23, matches V1's `dt.Hour` extraction.
  - `txn_count`: integer count of transactions in that hour, matches V1's per-hour counter.
  - `total_amount`: `ROUND(SUM(amount), 2)`, matches V1's `Math.Round(total, 2)` (subject to W5 tolerance -- see TC-W5).
  - `as_of`: `yyyy-MM-dd` formatted date from `__maxEffectiveDate`, matches V1's `maxDate.ToString("yyyy-MM-dd")`.
- **Verification method:**
  - Run Proofmark with the config from FSD Section 8.
  - Proofmark must report 100.0% pass rate.
  - If `total_amount` diverges due to W5/W6, apply the phased mitigation from FSD Section 6 (move rounding to External module or promote to FUZZY).

### TC-04: Writer Configuration
- **Traces to:** BRD Writer Configuration, BR-8, BR-10
- **Input conditions:** Standard job run producing output.
- **Expected output:**
  - **File location:** `Output/double_secret_curated/peak_transaction_times.csv`
  - **Header:** First line is column names comma-joined (`hour_of_day,txn_count,total_amount,as_of`)
  - **Line endings:** LF (`\n`), NOT CRLF. Verify with `xxd` or `od` -- no `\r\n` sequences.
  - **Trailer:** Last line is `TRAILER|{count}|{date}` format (see TC-W7 for count semantics).
  - **Write mode:** Overwrite. Running the job twice for different dates produces a file with only the second run's data.
  - **Encoding:** UTF-8 with BOM. The first 3 bytes of the file must be `EF BB BF` (the UTF-8 BOM). Verify with `xxd Output/double_secret_curated/peak_transaction_times.csv | head -1`.
- **Verification method:**
  - Verify file exists at expected path.
  - Read first line and confirm header.
  - Read last line and confirm trailer format.
  - `xxd` check for LF-only line endings and BOM presence.
  - Run job twice, confirm file only contains second run's output.
  - Compare byte-level encoding with V1 output (`diff <(xxd V1_file) <(xxd V2_file)` should show only content differences, not encoding differences).

### TC-05: Anti-Pattern Elimination Verification
- **Traces to:** FSD Section 7

#### TC-05a: AP1 -- Dead-End Sourcing Eliminated
- **V1 problem:** `datalake.accounts` is sourced but never used by the External module.
- **V2 expectation:** The V2 job config has NO DataSourcing entry for `accounts`. Only `transactions` is sourced.
- **Verification method:** Read `peak_transaction_times_v2.json`. Confirm no module with `"table": "accounts"` exists.

#### TC-05b: AP3 -- Unnecessary External Module Partially Eliminated
- **V1 problem:** The entire pipeline (data access, hourly grouping, aggregation, rounding, ordering, file I/O) is in a single monolithic External module.
- **V2 expectation:** Business logic (GROUP BY, SUM, ROUND, ORDER BY) is in the SQL Transformation. The External module handles ONLY CSV file I/O (W7 trailer count + UTF-8 BOM encoding). Zero business logic in the External.
- **Verification method:** Inspect the V2 External module source code. Confirm it contains no grouping, aggregation, or rounding logic. Confirm the SQL Transformation contains the `GROUP BY`, `SUM`, `ROUND`, and `ORDER BY` clauses.

#### TC-05c: AP4 -- Unused Columns Eliminated
- **V1 problem:** `transactions` sources 6 columns (`transaction_id, account_id, txn_timestamp, txn_type, amount, description`); only `txn_timestamp` and `amount` are used. `accounts` sources 4 columns, none used.
- **V2 expectation:** `transactions` sources only `txn_timestamp` and `amount`. `accounts` is removed entirely.
- **Verification method:** Read `peak_transaction_times_v2.json`. Confirm the `transactions` DataSourcing entry has `"columns": ["txn_timestamp", "amount"]` and no other columns.

#### TC-05d: AP6 -- Row-by-Row Iteration Eliminated
- **V1 problem:** `foreach` loop with `Dictionary<int, (int count, decimal total)>` accumulator for hourly grouping.
- **V2 expectation:** Replaced with SQL `GROUP BY CAST(strftime('%H', txn_timestamp) AS INTEGER)` -- a set-based operation.
- **Verification method:** Confirm the V2 Transformation SQL uses `GROUP BY` for hourly aggregation. Confirm the V2 External module does NOT contain any `foreach` loop over transaction rows for grouping or aggregation.

### TC-06: Edge Cases

#### TC-06a: Empty Input (BR-8, BRD Edge Case 1)
- **Input conditions:** Run for an effective date with zero transactions in `datalake.transactions`.
- **Expected output:** The output CSV contains only the header row and a trailer row `TRAILER|0|{date}`. Zero data rows between them.
- **Verification method:** If such a date exists in the test range, run for it. Otherwise, verify by code inspection that the V2 External module handles the case where the aggregated DataFrame has zero rows (writes header + `TRAILER|0|{date}` only).

#### TC-06b: Missing Hours (BRD Edge Case 6)
- **Input conditions:** Transactions exist for only a subset of hours 0-23 (e.g., no transactions between 2:00 AM and 5:00 AM).
- **Expected output:** Only hours with at least one transaction appear in the output. Hours with no transactions are absent -- NOT represented as `hour_of_day=3, txn_count=0, total_amount=0.00`.
- **Verification method:** Count distinct `hour_of_day` values in the output. Compare against `SELECT COUNT(DISTINCT EXTRACT(HOUR FROM txn_timestamp)) FROM datalake.transactions WHERE as_of = '{date}'`. They must match.

#### TC-06c: Timestamp Parsing Fallback (BR-9, FSD OQ-2)
- **Input conditions:** All `txn_timestamp` values in `datalake.transactions` are proper PostgreSQL timestamps.
- **Expected output:** V2's `strftime('%H', txn_timestamp)` correctly extracts the hour for all rows. No rows are excluded due to NULL hour values.
- **Verification method:** Compare V2 output row count against V1. If V1 has rows in hour 0 from fallback parsing, check whether V2's SQL produces the same grouping. Per FSD OQ-2, this is extremely low risk because `txn_timestamp` values are proper timestamps. Monitor for any `NULL` hour_of_day values in V2 output (there should be none).

#### TC-06d: Overwrite Multi-Day Behavior (BRD Write Mode)
- **Input conditions:** Run auto-advance over a multi-day range (e.g., 2024-10-01 through 2024-10-03).
- **Expected output:** After all days process, the output file contains ONLY the final day's data. Prior days' data is overwritten. The `as_of` column shows only the last effective date. The trailer shows the last day's input count and date.
- **Verification method:** Run auto-advance. After completion, verify the `as_of` column has a single value matching the last date. Verify the trailer date matches the last effective date.

### TC-07: Proofmark Configuration
- **Traces to:** FSD Section 8
- **Input conditions:** Proofmark config for this job.
- **Expected output:** Config specifies:
  - `comparison_target: "peak_transaction_times"`
  - `reader: csv`
  - `threshold: 100.0`
  - `csv.header_rows: 1`
  - `csv.trailer_rows: 1`
  - No EXCLUDED columns
  - No FUZZY columns (initial -- see TC-W5 for potential promotion)
- **Verification method:** Read the Proofmark YAML config at `POC3/proofmark_configs/peak_transaction_times.yaml` and verify all fields match the FSD's Proofmark config. Confirm `trailer_rows: 1` (Overwrite mode produces exactly one trailer at file end). Confirm no excluded or fuzzy columns are present initially.

## W-Code Test Cases

### TC-W5: Banker's Rounding vs SQLite Round-Half-Away-From-Zero
- **Traces to:** W5 (FSD Section 6), BR-3
- **Wrinkle:** V1 uses `Math.Round(total, 2)` which defaults to `MidpointRounding.ToEven` (banker's rounding). V2's SQL uses `ROUND(SUM(CAST(amount AS REAL)), 2)` which uses round-half-away-from-zero. These diverge at exact midpoints (e.g., a sum of 1234.565: V1 rounds to 1234.56, SQLite rounds to 1234.57).
- **Additional concern (W6):** V1 accumulates using `decimal` arithmetic; V2 accumulates using SQLite REAL (IEEE 754 double). Intermediate sums may differ, shifting values toward or away from midpoints.
- **Input conditions:** Run for a date range where hourly `total_amount` sums could land on or near a midpoint (X.XX5).
- **Expected output (optimistic):** No divergence -- the accumulated sums do not land on exact midpoints, and `total_amount` values match V1 exactly.
- **Expected output (if divergence detected):** Apply FSD Section 6 phased mitigation:
  1. **Option A (preferred):** Move rounding into the External module. SQL returns raw `SUM(CAST(amount AS REAL))` without `ROUND`. External applies `Math.Round((decimal)totalAmount, 2, MidpointRounding.ToEven)`.
  2. **Option B (fallback):** Add `total_amount` as FUZZY in Proofmark with `tolerance: 0.01, tolerance_type: absolute`.
- **Verification method:**
  - Run Proofmark with strict comparison (no FUZZY on total_amount).
  - If 100% pass: no action needed.
  - If any `total_amount` mismatches: identify the failing rows, confirm the divergence is W5/W6 related (not a logic bug), and apply the appropriate mitigation.

### TC-W7: Trailer Inflated Count
- **Traces to:** W7 (FSD Section 6), BR-5
- **Wrinkle:** The trailer uses the INPUT transaction count (before hourly bucketing), not the output row count. If 4000 transactions span 15 hours, the trailer says `TRAILER|4000|2024-10-15` even though the output has only 15 data rows.
- **Input conditions:** Run for a date with a known number of transactions in `datalake.transactions`.
- **Expected output:** The trailer count matches the total number of input transaction rows for that date, NOT the number of distinct hours in the output.
- **Verification method:**
  - Query `SELECT COUNT(*) FROM datalake.transactions WHERE as_of = '{date}'` to get the expected input count.
  - Read the last line of the V2 output file.
  - Parse the trailer: `TRAILER|{count}|{date}`.
  - Confirm `{count}` equals the input transaction count from the query.
  - Confirm `{count}` does NOT equal the number of data rows in the output (unless by coincidence -- e.g., exactly 1 transaction per hour for all hours).
  - Compare V2 trailer against V1 trailer -- they must be identical.

## Notes
- **Encoding verification is critical.** The UTF-8 BOM (bytes `EF BB BF`) is a key differentiator between V1's `StreamWriter` default and the framework's `CsvFileWriter` (which uses no BOM). The V2 External module must replicate the BOM for byte-level equivalence. Use `xxd` to verify.
- **W5/W6 is the highest-risk area.** The rounding mode difference (banker's vs round-half-away-from-zero) combined with the data type difference (decimal vs double) creates two compounding divergence vectors on `total_amount`. Start strict in Proofmark; escalate only on evidence.
- **The V2 External module must read the `transactions` DataFrame from shared state** (for the input count) AND the `peak_transaction_times` aggregated DataFrame (for the output rows). Both must be available in shared state when the External module executes.
- **BR-7 (empty output DataFrame):** V1 sets `sharedState["output"]` to an empty DataFrame after writing the CSV. The V2 External module should do the same, since no subsequent writer module consumes it. This is a no-op but maintains V1 behavioral parity.
