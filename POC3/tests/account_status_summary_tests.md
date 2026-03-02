# AccountStatusSummary — Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | Group by (account_type, account_status) with correct count per group |
| TC-02   | BR-2           | Segments table is not sourced in V2 (AP1 elimination) |
| TC-03   | BR-3           | as_of taken from first accounts row and applied uniformly to all output rows |
| TC-04   | BR-4           | Empty accounts (weekend date) produces empty DataFrame with correct schema |
| TC-05   | BR-5           | Currently 3 output rows on weekdays (one per account_type, all Active) |
| TC-06   | BR-6           | customer_id and current_balance not sourced in V2 (AP4 elimination) |
| TC-07   | BR-7           | Trailer format is TRAILER\|{row_count}\|{date} |
| TC-08   | —              | Output schema: correct columns in correct order |
| TC-09   | —              | CSV header row is present |
| TC-10   | —              | Line endings are LF |
| TC-11   | —              | Overwrite mode — only final day's data survives multi-day run |
| TC-12   | —              | Row ordering non-determinism (Dictionary/LINQ GroupBy) |
| TC-13   | —              | Proofmark comparison: V1 vs V2 output equivalence |
| TC-14   | BR-3           | as_of date format is MM/dd/yyyy (DateOnly.ToString() behavior) |
| TC-15   | —              | NULL handling in account_type and account_status grouping keys |
| TC-16   | BR-4           | Sunday effective date produces header + trailer only (zero data rows) |

## Test Cases

### TC-01: Group by (account_type, account_status) with correct counts
- **Traces to:** BR-1
- **Input conditions:** Run the job for a weekday effective date (e.g., 2024-10-01) where `datalake.accounts` has rows.
- **Expected output:** Each output row represents a unique `(account_type, account_status)` combination with the correct count of accounts in that group. The `account_count` value for each group matches `SELECT account_type, account_status, COUNT(account_id) FROM datalake.accounts WHERE as_of = '2024-10-01' GROUP BY account_type, account_status`.
- **Verification method:** Run the V2 job for 2024-10-01. Read the output CSV (skipping header and trailer). For each row, compare `account_type`, `account_status`, and `account_count` against the direct database query above. Verify the total number of groups matches.

### TC-02: Segments table not sourced in V2
- **Traces to:** BR-2
- **Input conditions:** Inspect the V2 job config JSON.
- **Expected output:** No DataSourcing module references `segments` table. V1 sourced segments but never used it (dead-end sourcing, AP1). V2 eliminates it entirely.
- **Verification method:** Parse the V2 job config JSON. Verify there is no module with `"table": "segments"`. Cross-reference against FSD Section 2 which specifies only a single DataSourcing module for `accounts`.

### TC-03: as_of from first accounts row applied to all output rows
- **Traces to:** BR-3
- **Input conditions:** Run for a weekday effective date (e.g., 2024-10-01) that has accounts data.
- **Expected output:** Every output row has the same `as_of` value. That value matches the `as_of` value of the first row in the `accounts` DataFrame for the effective date range.
- **Verification method:** Read the output CSV. Extract the `as_of` column from every data row. Verify all values are identical. Verify the value matches `SELECT DISTINCT as_of FROM datalake.accounts WHERE as_of = '2024-10-01'` (since a single-day effective date means all rows share the same as_of).

### TC-04: Empty accounts produces empty DataFrame with correct schema
- **Traces to:** BR-4
- **Input conditions:** Run the job for a weekend effective date (e.g., 2024-10-05, Saturday or 2024-10-06, Sunday) where `datalake.accounts` has no rows.
- **Expected output:** The job completes successfully (no errors, no crashes). The output CSV contains a header row and a trailer row, but zero data rows. The trailer shows `TRAILER|0|2024-10-05` (or the appropriate date).
- **Verification method:** Run V2 for a weekend date. Read the output CSV file. Verify it has exactly 2 lines: the header line (`account_type,account_status,account_count,as_of`) and the trailer line (`TRAILER|0|{date}`). Verify the job did not fail or throw an exception.

### TC-05: Three output rows on weekdays with current data
- **Traces to:** BR-5
- **Input conditions:** Run for a weekday effective date (e.g., 2024-10-01). Current data has 3 account types (Checking, Savings, Credit) and all accounts have status "Active".
- **Expected output:** Exactly 3 data rows in the CSV, one for each account_type. All rows have `account_status` = "Active".
- **Verification method:** Read the output CSV. Count data rows (exclude header and trailer). Verify count is 3. Verify all `account_status` values are "Active". Verify the 3 `account_type` values are "Checking", "Savings", "Credit" (in any order).

### TC-06: customer_id and current_balance not sourced in V2
- **Traces to:** BR-6
- **Input conditions:** Inspect the V2 job config JSON.
- **Expected output:** The DataSourcing module for `accounts` specifies columns `["account_id", "account_type", "account_status"]` only. Neither `customer_id` nor `current_balance` appears.
- **Verification method:** Parse the V2 job config. Verify the `accounts` DataSourcing module's `columns` array matches exactly `["account_id", "account_type", "account_status"]` as specified in FSD Section 2.

### TC-07: Trailer format TRAILER|{row_count}|{date}
- **Traces to:** BR-7
- **Input conditions:** Run the job for a weekday date (e.g., 2024-10-01) and a weekend date (e.g., 2024-10-05).
- **Expected output:**
  - Weekday: Last line of CSV is `TRAILER|3|2024-10-01` (3 data rows, date = max effective date).
  - Weekend: Last line of CSV is `TRAILER|0|2024-10-05` (0 data rows, date = max effective date).
- **Verification method:** Read the last line of each output CSV. Verify it matches the pattern `TRAILER|{N}|{yyyy-MM-dd}` where N equals the count of data rows and the date is the `__maxEffectiveDate`. Verify the pipe delimiters are literal `|` characters. Verify {row_count} excludes the header and trailer themselves.

### TC-08: Output schema — correct columns in correct order
- **Traces to:** BRD Output Schema
- **Input conditions:** Run job for a weekday date with data.
- **Expected output:** CSV header row contains exactly: `account_type,account_status,account_count,as_of` (4 columns, in this order).
- **Verification method:** Read the first line of the output CSV. Split by comma. Verify the 4 column names match the BRD Output Schema table exactly, in order. Verify column count is 4.

### TC-09: CSV header row is present
- **Traces to:** Writer config (includeHeader: true)
- **Input conditions:** Run job for any effective date.
- **Expected output:** The first line of the output CSV is the header line: `account_type,account_status,account_count,as_of`.
- **Verification method:** Read the first line of the output CSV. Verify it matches the expected header. This is controlled by `"includeHeader": true` in the CsvFileWriter config.

### TC-10: Line endings are LF
- **Traces to:** Writer config (lineEnding: LF)
- **Input conditions:** Run the job for any effective date.
- **Expected output:** All line endings in the output CSV file are `\n` (LF). No `\r\n` (CRLF) sequences.
- **Verification method:** Read the raw bytes of the output CSV. Scan for `\r` (0x0D). Verify zero occurrences. Alternatively, compare file size with expected: (sum of all line lengths) + (number of lines * 1 byte for LF).

### TC-11: Overwrite mode — only final day survives multi-day run
- **Traces to:** BRD Write Mode Implications
- **Input conditions:** Run the job in auto-advance mode spanning multiple effective dates (e.g., 2024-10-01 through 2024-10-03). The job uses writeMode: Overwrite.
- **Expected output:** After the run completes, the output CSV contains only the data from the final effective date. The trailer date reflects the final effective date. Data from earlier dates is gone.
- **Verification method:** Run the job across multiple dates. Read the output CSV. Verify the `as_of` column in all data rows corresponds to the last effective date. Verify the trailer date matches the last effective date.

### TC-12: Row ordering non-determinism
- **Traces to:** BRD Edge Cases (Dictionary iteration order), FSD Section 8 (Proofmark row ordering)
- **Input conditions:** Run the job for a weekday effective date. Note the order of the 3 output rows.
- **Expected output:** The 3 data rows may appear in any order (Checking/Savings/Credit permutations). The output is valid regardless of row order because V1's Dictionary iteration order is not guaranteed and V2's LINQ GroupBy also does not guarantee order.
- **Verification method:** Read output CSV. Verify all 3 expected groups are present regardless of order. If Proofmark comparison fails on row order, document the mismatch as a known non-deterministic ordering issue and consider adding an explicit ORDER BY in the V2 External module (as noted in FSD Section 8).

### TC-13: Proofmark comparison — V1 vs V2 output equivalence
- **Traces to:** FSD Section 8 (Proofmark Config)
- **Input conditions:** Run both V1 and V2 for the same effective date. V1 output is at `Output/curated/account_status_summary.csv`, V2 at `Output/double_secret_curated/account_status_summary.csv`.
- **Expected output:** Proofmark reports 100% match (or match within row-ordering tolerance). No excluded columns. No fuzzy columns. Header (1 row) and trailer (1 row) are accounted for in the Proofmark config.
- **Verification method:** Run Proofmark with the config from FSD Section 8: `comparison_target: account_status_summary`, `reader: csv`, `header_rows: 1`, `trailer_rows: 1`, `threshold: 100.0`. Verify score is 100.0. If row-order mismatch causes failure, investigate whether Proofmark supports order-independent comparison.

### TC-14: as_of date format is MM/dd/yyyy
- **Traces to:** BR-3, FSD Section 4 (Critical: as_of Date Format)
- **Input conditions:** Run the job for 2024-10-01 (weekday with data).
- **Expected output:** The `as_of` column in the CSV output shows `10/01/2024` (MM/dd/yyyy format), NOT `2024-10-01` (yyyy-MM-dd). This is because the External module stores `as_of` as a `DateOnly` object, and CsvFileWriter's `FormatField()` calls `.ToString()` on it, which produces `MM/dd/yyyy`.
- **Verification method:** Read the output CSV. Extract the `as_of` value from any data row. Verify it matches the regex pattern `^\d{2}/\d{2}/\d{4}$` (e.g., `10/01/2024`). Verify it does NOT match `\d{4}-\d{2}-\d{2}`. This is a critical format requirement for V1/V2 equivalence — if V2 converts as_of to a string before putting it in the DataFrame, CsvFileWriter will render it as a string literal rather than calling DateOnly.ToString().

### TC-15: NULL handling in grouping keys
- **Traces to:** BR-1
- **Input conditions:** Check if any `accounts` rows have NULL `account_type` or NULL `account_status` for a given effective date.
- **Expected output:** If NULLs exist, they are grouped using the `?.ToString() ?? ""` coalescing pattern (per FSD Section 10 pseudocode), producing an empty-string key. If no NULLs exist in the current data, this test documents the expected behavior by design.
- **Verification method:** Query `SELECT COUNT(*) FROM datalake.accounts WHERE account_type IS NULL OR account_status IS NULL`. If count > 0, verify output contains a group with empty-string keys. If count = 0, verify V2 External module code includes the null-coalescing pattern (`?.ToString() ?? ""`). Document as "by design, not exercised with current data" if no NULLs exist.

### TC-16: Weekend/Sunday effective date produces header + trailer only
- **Traces to:** BR-4, BRD Edge Cases (Weekend dates)
- **Input conditions:** Run the job for a Saturday (e.g., 2024-10-05) and a Sunday (e.g., 2024-10-06).
- **Expected output:** Both runs complete successfully. The output CSV for each contains only a header line and a trailer line. Zero data rows. The trailer shows `TRAILER|0|{date}`. No "no such table" errors (the External module handles the empty-data case before any SQL/Transformation is attempted).
- **Verification method:** Run V2 for each weekend date. Verify exit status is success. Read the output CSV and count lines: expect exactly 2 (header + trailer). Verify the trailer row_count is 0. Confirm no error output related to missing tables or empty DataFrames — this is the core reason the FSD chose Tier 2 over Tier 1 (Transformation.cs:47 skips empty DataFrames, which would cause "no such table" errors).
