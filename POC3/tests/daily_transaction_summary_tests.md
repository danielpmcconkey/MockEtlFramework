# DailyTransactionSummary — V2 Test Plan

## Job Info
- **V2 Config**: `daily_transaction_summary_v2.json`
- **Tier**: 1 (Framework Only)
- **External Module**: None

## Pre-Conditions
- **Source table**: `datalake.transactions` must be populated with columns: `account_id`, `txn_type`, `amount`, `as_of` (auto-appended by framework)
- **Effective date range**: `firstEffectiveDate = 2024-10-01`, auto-advance through 2024-12-31
- **Shared state**: Executor must inject `__minEffectiveDate` and `__maxEffectiveDate` per run
- **V1 baseline**: `Output/curated/daily_transaction_summary.csv` must exist for comparison
- **Output directory**: `Output/double_secret_curated/` must be writable

## Test Cases

### TC-1: Output Schema Validation
- **Expected columns** (exact order per FSD Section 4):
  1. `account_id` (TEXT)
  2. `as_of` (TEXT)
  3. `total_amount` (REAL)
  4. `transaction_count` (INTEGER)
  5. `debit_total` (REAL)
  6. `credit_total` (REAL)
- Verify the CSV header row contains exactly these 6 column names in this order
- Verify no extra columns are present
- Verify column names are lowercase with underscores (no camelCase, no spaces)

### TC-2: Row Count Equivalence
- V1 and V2 must produce identical row counts per effective date
- Over the full run (2024-10-01 through 2024-12-31), total data row counts must match
- Trailer rows are not counted as data rows for this comparison
- Each effective date should produce one row per unique (account_id, as_of) combination

### TC-3: Data Content Equivalence
- All values must be byte-identical to V1 output
- Compare `Output/curated/daily_transaction_summary.csv` (V1) against `Output/double_secret_curated/daily_transaction_summary.csv` (V2)
- Monetary columns (`total_amount`, `debit_total`, `credit_total`) must match to exact decimal representation (ROUND(..., 2) in SQLite is deterministic)
- Integer column (`transaction_count`) must match exactly
- Text columns (`account_id`, `as_of`) must match exactly
- Row ordering must match: `ORDER BY as_of ASC, account_id ASC`
- **No W-codes apply** -- there are zero output-affecting wrinkles for this job, so full byte-identical comparison is expected

### TC-4: Writer Configuration
- **includeHeader**: `true` -- header written on first run only (suppressed on Append when file already exists)
- **writeMode**: `Append` -- each daily run appends data rows + trailer; file grows across dates
- **lineEnding**: `LF` -- all lines (header, data, trailer) use Unix-style line endings (`\n`)
- **trailerFormat**: `TRAILER|{row_count}|{date}` -- verify token resolution:
  - `{row_count}` = number of data rows in that day's DataFrame
  - `{date}` = `__maxEffectiveDate` in `yyyy-MM-dd` format
- **Append + Header behavior**: Verify multi-day output structure is:
  ```
  header
  day1_row1
  day1_row2
  ...
  TRAILER|{count}|{date1}
  day2_row1
  ...
  TRAILER|{count}|{date2}
  ```
- Verify no duplicate headers appear after the first run

### TC-5: Anti-Pattern Elimination Verification

#### TC-5a: AP1 — Dead-end sourcing eliminated
- V1 sources `datalake.branches` (branch_id, branch_name) but never uses it in SQL
- V2 must NOT have a DataSourcing entry for `branches`
- Verify V2 config contains only one DataSourcing module (for `transactions`)
- Verify output is unaffected by removal (branches data was never referenced)

#### TC-5b: AP4 — Unused columns eliminated
- V1 sources `transaction_id`, `account_id`, `txn_timestamp`, `txn_type`, `amount`, `description` from transactions
- V2 sources only `account_id`, `txn_type`, `amount` (plus `as_of` auto-appended by framework)
- Verify V2 DataSourcing `columns` array is `["account_id", "txn_type", "amount"]`
- Verify `transaction_id`, `txn_timestamp`, and `description` are not sourced
- Verify output is unaffected (these columns were never used in the SQL)

#### TC-5c: AP8 — Complex SQL / unnecessary subquery eliminated
- V1 SQL wraps the aggregation in a subquery: `SELECT sub.* FROM (...) sub ORDER BY ...`
- V2 SQL uses a flat single SELECT with GROUP BY and ORDER BY directly
- Verify V2 SQL does not contain a subquery wrapper
- Verify column order in V2 SELECT list matches V1's outer SELECT exactly: `account_id, as_of, total_amount, transaction_count, debit_total, credit_total`
- Verify aggregation logic is preserved verbatim (all ROUND, SUM, CASE WHEN, COUNT expressions identical)

### TC-6: Edge Cases

#### TC-6a: Empty input (no transactions for an effective date)
- When no transactions exist for a given `as_of` date, GROUP BY produces 0 rows
- CsvFileWriter should write 0 data rows for that day
- Trailer should still be appended with `row_count = 0`: `TRAILER|0|{date}`
- On first run (file does not exist): output is header + trailer only

#### TC-6b: Account with only Debit transactions
- `credit_total` should be `0.0` (via `ELSE 0` in CASE WHEN)
- `total_amount` should equal `debit_total`
- `transaction_count` should reflect all Debit transactions

#### TC-6c: Account with only Credit transactions
- `debit_total` should be `0.0` (via `ELSE 0` in CASE WHEN)
- `total_amount` should equal `credit_total`
- `transaction_count` should reflect all Credit transactions

#### TC-6d: Account with mixed Debit and Credit transactions
- `total_amount` = `debit_total` + `credit_total` (rounded to 2 decimal places)
- `transaction_count` = count of all transactions (both types)
- Verify ROUND(..., 2) is applied to each aggregate independently before summing for `total_amount`

#### TC-6e: Rounding behavior
- All monetary values use SQLite `ROUND(..., 2)` which is deterministic
- Verify that `total_amount` is `ROUND(SUM(debit) + SUM(credit), 2)` -- note the ROUND is on the SUM, not on individual amounts
- Verify no floating-point drift across the full date range

### TC-7: Proofmark Configuration
- **Expected proofmark settings** (from FSD Section 8):
  ```yaml
  comparison_target: "daily_transaction_summary"
  reader: csv
  threshold: 100.0
  csv:
    header_rows: 1
    trailer_rows: 0
  ```
- **threshold**: `100.0` -- all computations are deterministic; exact match required
- **header_rows**: `1` -- single header row on first write
- **trailer_rows**: `0` -- Append-mode file with embedded trailers throughout (not just at end)
- **excluded_columns**: None -- all columns are deterministic
- **fuzzy_columns**: None -- all monetary columns use SQLite ROUND(..., 2) which is deterministic; no double-precision accumulation issues

## W-Code Test Cases

No W-codes apply to this job. The FSD confirms that none of the cataloged wrinkles (W1 through W12) affect this job. All computations are deterministic, the trailer uses framework tokens (`{row_count}`, `{date}`), the writeMode is correct (Append), and no External module introduces bugs.

## Notes
- This is one of the cleanest jobs in the portfolio -- no wrinkles, no External module, straightforward SQL aggregation
- The only V1 issues are code-quality anti-patterns (AP1, AP4, AP8) that are eliminated without any output impact
- The `total_amount` formula (`SUM(debit) + SUM(credit)`) is semantically equivalent to `SUM(amount)` because all transactions are either Debit or Credit, but V2 preserves the V1 formula for output equivalence
- The `as_of` column is NOT listed in the V2 DataSourcing `columns` array because the framework's DataSourcing module automatically appends it (per `Lib/Modules/DataSourcing.cs:69-73`)
