# DailyTransactionVolume — V2 Test Plan

## Job Info
- **V2 Config**: `daily_transaction_volume_v2.json`
- **Tier**: 1 (Framework Only)
- **External Module**: None

## Pre-Conditions
- **Source table**: `datalake.transactions` must be populated with column: `amount` (plus `as_of` auto-appended by framework)
- **Effective date range**: `firstEffectiveDate = 2024-10-01`, auto-advance through 2024-12-31
- **Shared state**: Executor must inject `__minEffectiveDate` and `__maxEffectiveDate` per run
- **V1 baseline**: `Output/curated/daily_transaction_volume.csv` must exist for comparison
- **Output directory**: `Output/double_secret_curated/` must be writable
- **Non-deterministic trailer**: The `{timestamp}` token in the trailer resolves to `DateTime.UtcNow` at execution time, so V1 and V2 trailer lines will never match byte-for-byte

## Test Cases

### TC-1: Output Schema Validation
- **Expected columns** (exact order per FSD Section 4):
  1. `as_of` (TEXT, date format)
  2. `total_transactions` (INTEGER)
  3. `total_amount` (REAL)
  4. `avg_amount` (REAL)
- Verify the CSV header row contains exactly these 4 column names in this order
- Verify no extra columns are present (notably, V1's CTE computes `min_amount` and `max_amount` but these must NOT appear in output)
- Verify column names are lowercase with underscores

### TC-2: Row Count Equivalence
- V1 and V2 must produce identical data row counts per effective date
- Each effective date produces exactly 1 data row (GROUP BY as_of with single-day auto-advance)
- Over the full run (2024-10-01 through 2024-12-31 = 92 days), expect 92 data rows total
- Trailer rows (1 per day = 92 trailers) are not counted as data rows for this comparison
- Total lines in file: 1 header + 92 data rows + 92 trailer rows = 185 lines

### TC-3: Data Content Equivalence
- All **data row** values must be byte-identical to V1 output
- Compare `Output/curated/daily_transaction_volume.csv` (V1) against `Output/double_secret_curated/daily_transaction_volume.csv` (V2)
- Monetary columns (`total_amount`, `avg_amount`) must match to exact decimal representation (ROUND(..., 2) in SQLite is deterministic)
- Integer column (`total_transactions`) must match exactly
- Date column (`as_of`) must match exactly
- Row ordering must match: `ORDER BY as_of ASC`
- **Trailer rows will NOT match** due to `{timestamp}` non-determinism -- see TC-W1 below
- **No output-affecting W-codes apply** -- data rows are fully deterministic

### TC-4: Writer Configuration
- **includeHeader**: `true` -- header written on first run only (suppressed on Append when file already exists)
- **writeMode**: `Append` -- each daily run appends data rows + trailer; file grows across dates
- **lineEnding**: `CRLF` -- all lines (header, data, trailer) use Windows-style line endings (`\r\n`)
- **trailerFormat**: `CONTROL|{date}|{row_count}|{timestamp}` -- verify token resolution:
  - `{date}` = `__maxEffectiveDate` in `yyyy-MM-dd` format
  - `{row_count}` = number of data rows in that day's DataFrame
  - `{timestamp}` = `DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")` (non-deterministic)
- **Append + Header behavior**: Verify multi-day output structure is:
  ```
  header\r\n
  day1_data\r\n
  CONTROL|2024-10-01|1|{ts1}\r\n
  day2_data\r\n
  CONTROL|2024-10-02|1|{ts2}\r\n
  ...
  ```
- Verify no duplicate headers appear after the first run
- Verify CRLF is used consistently (not mixed with LF)

### TC-5: Anti-Pattern Elimination Verification

#### TC-5a: AP4 — Unused columns eliminated
- V1 sources `transaction_id`, `account_id`, `txn_type`, `amount` from transactions
- V2 sources only `["amount"]` (plus `as_of` auto-appended by framework)
- Verify V2 DataSourcing `columns` array is `["amount"]`
- Verify `transaction_id`, `account_id`, and `txn_type` are not sourced
- Verify output is unaffected:
  - `COUNT(*)` counts all rows regardless of sourced columns
  - `SUM(amount)` and `AVG(amount)` only need the `amount` column
  - `as_of` is auto-appended by framework's DataSourcing module

#### TC-5b: AP8 — Complex SQL / unused CTE eliminated
- V1 SQL uses a CTE (`WITH daily_agg AS (...)`) that computes 6 columns: `total_transactions`, `total_amount`, `avg_amount`, `min_amount`, `max_amount`, and `as_of`
- The outer SELECT only picks 4 of these: `as_of, total_transactions, total_amount, avg_amount`
- `min_amount` and `max_amount` are computed but never used in output
- V2 SQL uses a flat single SELECT computing only the 4 needed columns directly
- Verify V2 SQL does not contain `WITH`, `min_amount`, or `max_amount`
- Verify V2 SQL produces: `SELECT as_of, COUNT(*) AS total_transactions, ROUND(SUM(amount), 2) AS total_amount, ROUND(AVG(amount), 2) AS avg_amount FROM transactions GROUP BY as_of ORDER BY as_of`
- Verify output column order matches V1's outer SELECT exactly

### TC-6: Edge Cases

#### TC-6a: Empty input (no transactions for an effective date)
- When no transactions exist for a given `as_of` date, GROUP BY produces 0 rows
- CsvFileWriter should write 0 data rows for that day
- Trailer should still be appended with `row_count = 0`: `CONTROL|{date}|0|{timestamp}`
- On first run (file does not exist): output is header + trailer only

#### TC-6b: Single transaction on a date
- `total_transactions` = 1
- `total_amount` = `ROUND(amount, 2)` of that single transaction
- `avg_amount` = `total_amount` (AVG of one value equals the value itself)

#### TC-6c: All transactions same amount on a date
- `avg_amount` should equal the common amount (rounded to 2 decimal places)
- `total_amount` = common_amount * total_transactions (subject to ROUND)

#### TC-6d: Large number of transactions on a date
- Verify `COUNT(*)` handles high volumes correctly
- Verify `SUM` and `AVG` precision with ROUND(..., 2) on large aggregates

#### TC-6e: CRLF line ending consistency
- Verify every line in the output file (header, data, trailer) uses `\r\n`
- Verify no bare `\n` (LF-only) lines exist
- Verify no trailing `\r\n\r\n` (double line ending) at end of file

### TC-7: Proofmark Configuration
- **Expected proofmark settings** (from FSD Section 8):
  ```yaml
  comparison_target: "daily_transaction_volume"
  reader: csv
  threshold: 49.0
  csv:
    header_rows: 1
    trailer_rows: 0
  ```
- **threshold**: `49.0` -- reduced from 100% due to non-deterministic `{timestamp}` in trailers
  - Over 92 days: 92 data rows + 92 trailer rows = 184 non-header rows
  - All 92 data rows should match (100% data match)
  - All 92 trailer rows will mismatch (timestamp differs between V1 and V2 runs)
  - Expected match rate: 92/184 = 50.0%
  - Threshold set to 49.0% (1% margin below expected) to handle edge case of zero-transaction days
- **header_rows**: `1` -- single header row on first write
- **trailer_rows**: `0` -- Append-mode file with embedded trailers throughout (not just at end); per CONFIG_GUIDE.md guidance
- **excluded_columns**: None possible -- trailer timestamp is embedded in a pipe-delimited field within a CSV row, making it a single opaque field to Proofmark's CSV parser
- **fuzzy_columns**: None -- all data row columns are deterministic

## W-Code Test Cases

### TC-W1: Non-Deterministic Trailer Timestamp (not a cataloged W-code)
- **What it is**: The trailer format `CONTROL|{date}|{row_count}|{timestamp}` includes a `{timestamp}` token that resolves to `DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")` at execution time (per `CsvFileWriter.cs:66`)
- **Why it matters**: V1 and V2 runs execute at different times, so every trailer line will have a different timestamp, causing a guaranteed mismatch on 50% of non-header rows
- **How V2 handles it**: V2 preserves the same trailer format (required for output fidelity); the non-determinism is accommodated via the reduced Proofmark threshold of 49.0%
- **What to verify**:
  - Trailer lines follow format: `CONTROL|yyyy-MM-dd|N|yyyy-MM-ddTHH:mm:ssZ`
  - The `{date}` token (position 2) matches `__maxEffectiveDate` for that run
  - The `{row_count}` token (position 3) matches the number of data rows written for that day
  - The `{timestamp}` token (position 4) is a valid UTC ISO 8601 timestamp
  - All data rows (non-trailer) are byte-identical between V1 and V2

## Notes
- This job is straightforward Tier 1 -- no External module, no output-affecting wrinkles
- The only complication is the non-deterministic `{timestamp}` in the trailer, which requires a reduced Proofmark threshold
- The 49.0% threshold is calculated precisely: each day produces exactly 1 data row (GROUP BY as_of with single-day auto-advance) and 1 trailer row, so trailers are exactly 50% of non-header content
- V1 computes `min_amount` and `max_amount` in its CTE but never outputs them -- V2 simply does not compute them at all (AP8 elimination), which has zero effect on output
- The `as_of` column is NOT listed in the V2 DataSourcing `columns` array because the framework's DataSourcing module automatically appends it (per `Lib/Modules/DataSourcing.cs:69-73`)
- CRLF line endings mean the output file will be slightly larger than an LF-equivalent; proofmark comparison must account for line ending consistency between V1 and V2
