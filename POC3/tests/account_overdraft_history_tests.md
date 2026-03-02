# AccountOverdraftHistory — Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | Inner join on account_id AND as_of produces correct enriched output |
| TC-02   | BR-2           | Unmatched overdraft events are excluded (INNER JOIN behavior) |
| TC-03   | BR-3           | Output is ordered by as_of ASC then overdraft_id ASC |
| TC-04   | BR-4           | Effective dates are injected at runtime, not hardcoded in config |
| TC-05   | BR-5           | Only account_type is used from accounts table in output |
| TC-06   | BR-6           | event_timestamp is sourced in V1 but excluded from output |
| TC-07   | EC-1           | Unmatched overdraft events silently dropped |
| TC-08   | EC-2           | Empty source data produces empty output with correct part file structure |
| TC-09   | EC-3           | Output is split into exactly 50 Parquet part files |
| TC-10   | EC-4           | Overwrite mode on multi-day auto-advance — only final day survives |
| TC-11   | —              | Output schema: correct columns in correct order |
| TC-12   | —              | NULL handling in join keys |
| TC-13   | —              | Proofmark comparison: no excluded columns, no fuzzy columns |
| TC-14   | BR-5           | Unused sourced columns (account_status, interest_rate, credit_limit) do not appear in output |

## Test Cases

### TC-01: Inner join on account_id AND as_of
- **Traces to:** BR-1
- **Input conditions:** Run for a weekday effective date (e.g., 2024-10-01) where both `datalake.overdraft_events` and `datalake.accounts` have rows with matching `account_id` and `as_of` values.
- **Expected output:** Each output row contains fields from `overdraft_events` (overdraft_id, account_id, customer_id, overdraft_amount, fee_amount, fee_waived, as_of) joined with `account_type` from the matching `accounts` row on the same account_id + as_of.
- **Verification method:** Run V2 job for 2024-10-01. Read output Parquet files. For each row, confirm that `account_type` matches the `accounts` record with the same `account_id` and `as_of` date. Cross-reference against a direct SQL query: `SELECT oe.overdraft_id, oe.account_id, oe.customer_id, a.account_type, oe.overdraft_amount, oe.fee_amount, oe.fee_waived, oe.as_of FROM datalake.overdraft_events oe JOIN datalake.accounts a ON oe.account_id = a.account_id AND oe.as_of = a.as_of WHERE oe.as_of = '2024-10-01' AND a.as_of = '2024-10-01'`.

### TC-02: Unmatched overdraft events excluded
- **Traces to:** BR-2
- **Input conditions:** Identify any overdraft event row where no matching `accounts` record exists for the same `account_id` + `as_of`. If no natural case exists, this is a confirmation test: count overdraft events for a date, count output rows, and verify any delta corresponds to missing account matches.
- **Expected output:** Output row count equals the count of overdraft events that have a matching account record for the same as_of date. Unmatched events do not appear.
- **Verification method:** Compare `SELECT COUNT(*) FROM datalake.overdraft_events WHERE as_of = '2024-10-01'` against the V2 output row count. Any difference must be explainable by missing account matches: `SELECT COUNT(*) FROM datalake.overdraft_events oe WHERE oe.as_of = '2024-10-01' AND NOT EXISTS (SELECT 1 FROM datalake.accounts a WHERE a.account_id = oe.account_id AND a.as_of = oe.as_of)`.

### TC-03: Output ordering by as_of ASC, overdraft_id ASC
- **Traces to:** BR-3
- **Input conditions:** Run for a multi-day effective date range (e.g., 2024-10-01 through 2024-10-03) or a single date with multiple overdraft events.
- **Expected output:** Rows in the output Parquet files are sorted first by `as_of` ascending, then by `overdraft_id` ascending within each date.
- **Verification method:** Read all output Parquet parts in order (part-00000 through part-00049). Concatenate rows. Verify that for each consecutive pair of rows: (a) the as_of value is non-decreasing, and (b) within the same as_of value, overdraft_id is strictly increasing.

### TC-04: Effective dates injected at runtime
- **Traces to:** BR-4
- **Input conditions:** Inspect the V2 job config JSON.
- **Expected output:** No `minEffectiveDate` or `maxEffectiveDate` fields appear in any DataSourcing module configuration. The job relies on the framework's shared state keys `__minEffectiveDate` and `__maxEffectiveDate`.
- **Verification method:** Parse the V2 job config JSON. Confirm no DataSourcing module contains `minEffectiveDate` or `maxEffectiveDate` properties. Verify that the job config matches the FSD Section 6 specification.

### TC-05: Only account_type used from accounts table
- **Traces to:** BR-5
- **Input conditions:** Run job and inspect output schema.
- **Expected output:** The output contains exactly 8 columns: `overdraft_id`, `account_id`, `customer_id`, `account_type`, `overdraft_amount`, `fee_amount`, `fee_waived`, `as_of`. No other accounts columns appear (no `account_status`, `interest_rate`, `credit_limit`).
- **Verification method:** Read any non-empty output Parquet part file. Verify the column list matches exactly. Confirm `account_type` is present and no other accounts-table columns are present.

### TC-06: event_timestamp excluded from output
- **Traces to:** BR-6
- **Input conditions:** Run job and inspect output schema.
- **Expected output:** `event_timestamp` does not appear as a column in the output Parquet files.
- **Verification method:** Read output Parquet schema. Verify `event_timestamp` is not in the column list. Additionally, confirm the V2 DataSourcing config for `overdraft_events` does not include `event_timestamp` in its columns list (AP4 elimination).

### TC-07: Unmatched overdraft events silently dropped (EC-1 detail)
- **Traces to:** EC-1
- **Input conditions:** Same as TC-02 but verifying silent behavior — no error, no log warning, no partial rows with NULL account_type.
- **Expected output:** Job completes successfully. No rows in output have a NULL `account_type`. No error messages related to unmatched joins.
- **Verification method:** Run the job. Verify exit code is success. Scan output for any NULL values in the `account_type` column. Confirm zero NULLs.

### TC-08: Empty source data produces empty output
- **Traces to:** EC-2
- **Input conditions:** Run the job for a weekend date (e.g., 2024-10-05, a Saturday) or any date where `overdraft_events` has zero rows.
- **Expected output:** Job completes successfully. Output directory contains 50 Parquet part files, all empty (zero data rows). Part files still have the correct 8-column schema in their metadata.
- **Verification method:** Run V2 for a weekend effective date. Verify the output directory exists with 50 part files. Read each part file and confirm zero data rows. If the framework supports it, verify schema metadata shows the expected 8 columns.

### TC-09: Exactly 50 Parquet part files
- **Traces to:** EC-3
- **Input conditions:** Run the job for any effective date.
- **Expected output:** The output directory `Output/double_secret_curated/account_overdraft_history/` contains exactly 50 files named `part-00000.parquet` through `part-00049.parquet`.
- **Verification method:** List files in the output directory. Count them. Verify exactly 50 files exist with the expected naming convention.

### TC-10: Overwrite mode — only final day survives on multi-day run
- **Traces to:** EC-4
- **Input conditions:** Run the job in auto-advance mode spanning multiple effective dates (e.g., 2024-10-01 through 2024-10-03). The job uses writeMode: Overwrite.
- **Expected output:** After the run completes, the output directory contains only the data from the final effective date (2024-10-03). Data from 2024-10-01 and 2024-10-02 is gone.
- **Verification method:** Run the job across multiple dates. Read the output Parquet files. Verify that every row's `as_of` value equals the last effective date in the run. Verify no rows from earlier dates exist.

### TC-11: Output schema — correct columns in correct order
- **Traces to:** BRD Output Schema
- **Input conditions:** Run job for a date with data (e.g., 2024-10-01).
- **Expected output:** Output columns are exactly, in order: `overdraft_id`, `account_id`, `customer_id`, `account_type`, `overdraft_amount`, `fee_amount`, `fee_waived`, `as_of`. Total column count is 8.
- **Verification method:** Read output Parquet file schema. Compare column names and order against the BRD Output Schema table. Verify column count is exactly 8.

### TC-12: NULL handling in join keys
- **Traces to:** BR-1, BR-2
- **Input conditions:** Check if any `overdraft_events` rows have NULL `account_id` or NULL `as_of`. If such rows exist in the source data, they should fail the INNER JOIN condition.
- **Expected output:** Rows with NULL join keys are excluded from output (SQL INNER JOIN does not match NULL = NULL).
- **Verification method:** Query `SELECT COUNT(*) FROM datalake.overdraft_events WHERE account_id IS NULL OR as_of IS NULL`. If count > 0, verify those rows do not appear in output. If count = 0, document as not-applicable with current data but tested by design.

### TC-13: Proofmark comparison — V1 vs V2 output equivalence
- **Traces to:** FSD Section 8 (Proofmark Config)
- **Input conditions:** Run both V1 and V2 for the same effective date. V1 output is in `Output/curated/account_overdraft_history/`, V2 in `Output/double_secret_curated/account_overdraft_history/`.
- **Expected output:** Proofmark reports 100% match. No excluded columns (all fields are deterministic). No fuzzy columns (all values are direct pass-throughs, no floating-point accumulation).
- **Verification method:** Run Proofmark with the config from FSD Section 8: `comparison_target: account_overdraft_history`, `reader: parquet`, `threshold: 100.0`. Verify score is 100.0 and no mismatches are reported.

### TC-14: Unused sourced columns do not appear in V2 output
- **Traces to:** BR-5, BR-6, AP4
- **Input conditions:** Inspect V2 job config and V2 output.
- **Expected output:** V2 DataSourcing for `accounts` sources only `account_id` and `account_type` (not `customer_id`, `account_status`, `interest_rate`, `credit_limit`). V2 DataSourcing for `overdraft_events` sources only `overdraft_id`, `account_id`, `customer_id`, `overdraft_amount`, `fee_amount`, `fee_waived` (not `event_timestamp`). Output contains none of the excluded columns.
- **Verification method:** Parse V2 job config JSON. Verify columns lists match the FSD Section 2 specification. Read output Parquet schema and confirm no extra columns are present.
