# AccountVelocityTracking -- Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | Transactions grouped by (account_id, as_of) with correct count and sum |
| TC-02   | BR-2           | customer_id resolved via accounts lookup; 0 if no match |
| TC-03   | BR-3           | total_amount rounded to 2 decimal places via Math.Round / ROUND |
| TC-04   | BR-4           | Output rows ordered by txn_date ASC, then account_id ASC |
| TC-05   | BR-5           | as_of column is __maxEffectiveDate formatted as yyyy-MM-dd |
| TC-06   | BR-6           | txn_date column is the transaction's as_of (not __maxEffectiveDate) |
| TC-07   | BR-7           | External module sets sharedState["output"] to empty DataFrame |
| TC-08   | BR-8, W12      | Direct CSV write appends with header re-emitted every run |
| TC-09   | BR-9           | credit_limit and apr not sourced in V2 (AP1/AP4 elimination) |
| TC-10   | BR-10          | Weekend transactions processed correctly (transactions exist on weekends) |
| TC-11   | BR-2           | Weekend accounts absent -- all customer_id values default to 0 |
| TC-12   | BR-1, BR-8     | Empty transactions input: header-only CSV appended |
| TC-13   | --             | Output column order matches spec exactly |
| TC-14   | --             | LF line endings (not CRLF) |
| TC-15   | --             | No trailer line in output |
| TC-16   | --             | Proofmark config: header_rows 1, trailer_rows 0, strict comparison |
| TC-17   | BR-3           | Decimal vs REAL arithmetic: total_amount potential precision difference |
| TC-18   | BR-8, W12      | Multi-day run produces multiple interleaved headers in append file |
| TC-19   | BR-1           | account_id cast to integer in output |
| TC-20   | BR-6           | Null as_of fallback to __maxEffectiveDate string |
| TC-21   | --             | No framework writer module in V2 job config |
| TC-22   | BR-2           | DISTINCT accounts deduplication in SQL join |

## Test Cases

### TC-01: Group by (account_id, as_of) with count and sum
- **Traces to:** BR-1
- **Input conditions:** Transactions DataFrame for effective date 2024-10-01. Multiple transactions per account on the same date.
- **Expected output:** One output row per distinct (account_id, txn_date) pair. `txn_count` equals COUNT(*) of transactions for that pair. `total_amount` equals SUM(amount) for that pair, rounded to 2 decimals.
- **Verification method:** Run V2 for 2024-10-01. For a sample of account_ids, verify txn_count and total_amount against direct SQL: `SELECT account_id, as_of, COUNT(*), ROUND(SUM(CAST(amount AS NUMERIC)), 2) FROM datalake.transactions WHERE as_of = '2024-10-01' GROUP BY account_id, as_of`. Compare V2 output against V1 output via Proofmark.

### TC-02: customer_id lookup with default 0
- **Traces to:** BR-2
- **Input conditions:** Weekday effective date where accounts data exists. Some account_ids in transactions may not have matching entries in accounts (if any).
- **Expected output:** Each output row has a `customer_id` resolved from the accounts table. If an account_id in transactions has no match in accounts, `customer_id` = 0.
- **Verification method:** For each output row, verify customer_id matches `SELECT customer_id FROM datalake.accounts WHERE account_id = {X} AND as_of = '2024-10-01' LIMIT 1`, or 0 if no match. The V2 SQL uses `COALESCE(a.customer_id, 0)` via LEFT JOIN, replicating V1's `GetValueOrDefault(accountId, 0)`.

### TC-03: total_amount rounded to 2 decimal places
- **Traces to:** BR-3
- **Input conditions:** Transactions with amounts that produce sums with more than 2 decimal places (e.g., summing 10.125 + 20.375 = 30.500).
- **Expected output:** `total_amount` is rounded to exactly 2 decimal places. V1 uses `Math.Round(total, 2)` with banker's rounding (MidpointRounding.ToEven). V2 SQL uses `ROUND(SUM(...), 2)` which SQLite also implements as banker's rounding.
- **Verification method:** Parse total_amount values from V2 output. Verify each has at most 2 decimal places. For midpoint values (e.g., x.xx5), verify banker's rounding is applied (rounds to nearest even). Compare against V1 output.

### TC-04: Output ordered by txn_date ASC, account_id ASC
- **Traces to:** BR-4
- **Input conditions:** Output with multiple txn_dates and multiple account_ids.
- **Expected output:** Rows are sorted first by txn_date ascending (string sort on yyyy-MM-dd format), then by account_id ascending (integer sort).
- **Verification method:** Parse V2 output rows. Verify the sequence is non-decreasing on txn_date, and within each txn_date, non-decreasing on account_id. The V2 SQL `ORDER BY CAST(t.as_of AS TEXT) ASC, CAST(t.account_id AS INTEGER) ASC` matches V1's `.OrderBy(k => k.Key.txnDate).ThenBy(k => k.Key.accountId)`.

### TC-05: as_of column is __maxEffectiveDate in yyyy-MM-dd format
- **Traces to:** BR-5
- **Input conditions:** Run for effective date 2024-10-01 (single-day run where min = max = 2024-10-01).
- **Expected output:** Every output row has `as_of` = `"2024-10-01"`. This is the __maxEffectiveDate formatted as yyyy-MM-dd, NOT the transaction's as_of date.
- **Verification method:** Parse the `as_of` column from all V2 output rows. Verify every value equals the __maxEffectiveDate string for that run. Confirm it differs from the `txn_date` column only when the effective date range spans multiple days.

### TC-06: txn_date is the transaction's as_of value
- **Traces to:** BR-6
- **Input conditions:** Run for a single effective date 2024-10-01.
- **Expected output:** Each output row's `txn_date` reflects the transaction's `as_of` date (converted to string). For a single-day run, txn_date = "2024-10-01" for all rows (same as as_of in this case, but derived from a different source).
- **Verification method:** Verify txn_date values in V2 output match the `as_of` column from the transactions DataSourcing for the same date range. For multi-day effective date ranges, txn_date will vary per row while as_of remains __maxEffectiveDate.

### TC-07: sharedState["output"] set to empty DataFrame
- **Traces to:** BR-7
- **Input conditions:** Any V2 run (weekday or weekend).
- **Expected output:** After the External module executes, `sharedState["output"]` is an empty DataFrame with columns `["account_id", "customer_id", "txn_date", "txn_count", "total_amount", "as_of"]`. This prevents the framework from attempting to write output via a framework writer (there is none in the config, but the empty output is defensive).
- **Verification method:** Code inspection of the V2 External module. Verify the module sets `sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns)` at the end of execution in all code paths (both data and empty-input paths).

### TC-08: W12 -- Header re-emitted on every append run
- **Traces to:** BR-8, W12
- **Input conditions:** Run V2 for two consecutive effective dates (e.g., 2024-10-01 and 2024-10-02).
- **Expected output:** The output CSV file contains: header row, data rows for 2024-10-01, header row again, data rows for 2024-10-02. The header is `account_id,customer_id,txn_date,txn_count,total_amount,as_of`.
- **Verification method:** After a multi-day run, read the output file. Count the number of lines matching the header string. Verify it equals the number of effective dates processed (one header per run). Verify data rows for each date appear immediately after the corresponding header. Compare structure against V1 output.

### TC-09: Unused columns removed in V2
- **Traces to:** BR-9
- **Input conditions:** V2 job config.
- **Expected output:** The accounts DataSourcing module sources only `["account_id", "customer_id"]` -- no `credit_limit` or `apr`. The transactions DataSourcing sources only `["account_id", "amount"]` -- no `transaction_id`, `txn_timestamp`, `txn_type`, `description`.
- **Verification method:** Inspect the V2 job config JSON. Verify the `columns` arrays contain only the specified columns. This eliminates AP1 (unused tables/columns) and AP4 (over-fetching).

### TC-10: Weekend transactions processed correctly
- **Traces to:** BR-10
- **Input conditions:** Effective date 2024-10-05 (Saturday) or 2024-10-06 (Sunday). Transactions exist on weekends (~4,200-4,350 rows per day per BRD). Accounts do NOT have weekend data.
- **Expected output:** Output contains transaction aggregation rows for the weekend date. Since accounts data is absent on weekends, all `customer_id` values will be 0 (see TC-11). Transaction counts and amounts are computed normally.
- **Verification method:** Run V2 for a weekend date. Verify output has data rows (not empty). Verify txn_count and total_amount are correct by cross-referencing with `SELECT account_id, COUNT(*), SUM(amount) FROM datalake.transactions WHERE as_of = '2024-10-05' GROUP BY account_id`.

### TC-11: Weekend accounts absent -- customer_id defaults to 0
- **Traces to:** BR-2, BRD Edge Cases
- **Input conditions:** Effective date is a weekend (e.g., 2024-10-05). Accounts DataSourcing returns 0 rows because accounts is weekday-only.
- **Expected output:** All output rows have `customer_id` = 0. The V2 SQL LEFT JOIN finds no accounts rows, so `COALESCE(a.customer_id, 0)` produces 0 for every transaction group. This matches V1's `GetValueOrDefault(accountId, 0)` against an empty dictionary.
- **Verification method:** Run V2 for a weekend date. Verify every output row has customer_id = 0. Compare against V1 output for the same weekend date. Note: FSD Section 5 documents that the Transformation module's RegisterTable skips empty DataFrames, so the accounts table won't be registered in SQLite. The External module must handle this fallback (see FSD Section 5 mitigation).

### TC-12: Empty transactions -- header-only CSV appended
- **Traces to:** BR-1, BR-8
- **Input conditions:** An effective date where transactions DataFrame is empty (hypothetical edge case -- per BR-10, transactions exist on all days including weekends, but this tests the defensive guard).
- **Expected output:** The External module appends only a header row to the CSV file (no data rows, no trailer). The file grows by one line per empty run.
- **Verification method:** Code inspection confirms the V2 External module's empty-input guard: if transactions or accounts are null/empty, write header-only CSV. If testable, mock an empty transactions DataFrame and verify only the header line is appended.

### TC-13: Output column order
- **Traces to:** FSD Section 4
- **Input conditions:** Any run that produces data.
- **Expected output:** The CSV header row lists columns in this exact order: `account_id,customer_id,txn_date,txn_count,total_amount,as_of`.
- **Verification method:** Read the first line of the output CSV. Split by comma. Verify the order matches exactly: `["account_id", "customer_id", "txn_date", "txn_count", "total_amount", "as_of"]`.

### TC-14: LF line endings
- **Traces to:** BRD Writer Configuration (lineEnding: LF via writer.NewLine = "\n")
- **Input conditions:** Any V2 run that produces output.
- **Expected output:** All line endings in the output file are `\n` (LF), NOT `\r\n` (CRLF).
- **Verification method:** Read the raw bytes of the output file. Search for `\r` (0x0D). Confirm zero occurrences. The V2 External module sets `writer.NewLine = "\n"` matching V1.

### TC-15: No trailer line
- **Traces to:** BRD Writer Configuration (no trailer)
- **Input conditions:** Any V2 run.
- **Expected output:** The output CSV contains no trailer line. Unlike most other jobs, this job has no trailerFormat. The file ends with the last data row followed by a newline.
- **Verification method:** Read the output file. Verify no line matches the pattern `END|...` or `TRAILER|...`. The last non-empty line should be a data row, not a trailer.

### TC-16: Proofmark strict comparison
- **Traces to:** FSD Section 8
- **Input conditions:** V1 and V2 output for the same effective date range.
- **Expected output:** Proofmark config uses `reader: csv`, `header_rows: 1`, `trailer_rows: 0`, `threshold: 100.0`, and no `columns` overrides (no fuzzy, no excluded). All columns compared strictly.
- **Verification method:** Run Proofmark with the FSD-specified config. Expect 100% match. If `total_amount` shows epsilon differences due to decimal vs REAL arithmetic (see TC-17), the FSD Risk section prescribes adding a fuzzy tolerance of 0.005 on `total_amount` as a fallback.

### TC-17: Decimal vs REAL arithmetic risk on total_amount
- **Traces to:** BR-3, FSD Section 10 Risk
- **Input conditions:** Transactions with amounts that, when summed as IEEE 754 double (SQLite REAL), produce a different rounded result than when summed as C# decimal (V1 approach).
- **Expected output:** For the vast majority of rows, `ROUND(SUM(REAL), 2)` = `Math.Round(decimal_sum, 2)`. However, edge cases may exist where double-precision accumulation introduces floating-point errors that affect the second decimal place after rounding.
- **Verification method:** Run both V1 and V2 for the full date range. Compare total_amount values via Proofmark. If mismatches occur:
  1. If differences are < 0.005 (sub-penny): add `fuzzy` override with `tolerance: 0.005, tolerance_type: absolute` on `total_amount`.
  2. If differences are >= 0.005: escalate -- may need to move SUM accumulation into the External module using C# decimal arithmetic.
  This test case documents the known risk from FSD Section 10.

### TC-18: Multi-day run -- interleaved headers in append file
- **Traces to:** BR-8, W12
- **Input conditions:** Run V2 for the full date range 2024-10-01 through 2024-12-31 (92 days). Write mode is append.
- **Expected output:** The output file contains 92 header rows interspersed throughout the data. The file structure repeats: [header] [data rows for day N] [header] [data rows for day N+1] ...
- **Verification method:** Count header lines in the V2 output file (lines matching `account_id,customer_id,txn_date,txn_count,total_amount,as_of`). The count should equal 92 (one per calendar day). Compare against V1 output -- both should have identical interleaved-header structure. Proofmark with `header_rows: 1` strips only the first header; embedded headers are treated as data and must match between V1 and V2.

### TC-19: account_id cast to integer
- **Traces to:** BR-1
- **Input conditions:** Any weekday effective date.
- **Expected output:** The `account_id` column in the output contains integer values (e.g., `1001`), not string or floating-point representations (e.g., not `"1001"` or `1001.0`).
- **Verification method:** V1 uses `Convert.ToInt32(accountId)`. V2 SQL uses `CAST(t.account_id AS INTEGER)`. Parse account_id values from the CSV output and verify they are clean integers with no decimal point.

### TC-20: Null as_of fallback on transactions
- **Traces to:** BR-6
- **Input conditions:** A transaction row where `as_of` is null (hypothetical -- DataSourcing filters by as_of date range, so null as_of rows are excluded at the source).
- **Expected output:** The V1 code falls back to `dateStr` (maxEffectiveDate) if `row["as_of"]?.ToString()` is null. This is effectively dead code because DataSourcing's WHERE clause excludes null as_of rows. The V2 SQL similarly excludes them via the DataSourcing date filter.
- **Verification method:** Code inspection only. Confirm that the V2 SQL design handles this edge case implicitly: the DataSourcing WHERE clause `as_of >= @minDate AND as_of <= @maxDate` excludes null as_of rows, making the fallback unnecessary. Document this as a dead code path that does not need active testing.

### TC-21: No framework writer module in job config
- **Traces to:** BR-7
- **Input conditions:** V2 job config JSON.
- **Expected output:** The V2 job config contains 4 modules: DataSourcing (transactions), DataSourcing (accounts), Transformation, External. There is NO CsvFileWriter or ParquetFileWriter module. All CSV writing is handled by the External module directly.
- **Verification method:** Inspect the V2 job config JSON. Verify the module list does not contain any module with `"type": "CsvFileWriter"` or `"type": "ParquetFileWriter"`. The External module handles all file I/O per W12.

### TC-22: DISTINCT accounts deduplication in SQL join
- **Traces to:** BR-2
- **Input conditions:** Multi-day effective date range where accounts has multiple rows per account_id (one per as_of date). For example, account_id 1001 appears with as_of 2024-10-01 and 2024-10-02.
- **Expected output:** The LEFT JOIN uses `SELECT DISTINCT account_id, customer_id FROM accounts`, which deduplicates the accounts rows. Each account_id maps to exactly one customer_id. The V1 Dictionary-based lookup achieves the same result (later entries overwrite earlier ones, but customer_id does not change across dates).
- **Verification method:** Verify that `SELECT DISTINCT account_id, customer_id FROM datalake.accounts` returns unique (account_id, customer_id) pairs (i.e., customer_id does not change for a given account_id across dates). If it does change, the DISTINCT subquery might produce multiple rows for one account_id, which would cause the LEFT JOIN to fan out -- this would be a bug. Confirm with: `SELECT account_id, COUNT(DISTINCT customer_id) AS cid_count FROM datalake.accounts GROUP BY account_id HAVING cid_count > 1` -- expect zero results.
