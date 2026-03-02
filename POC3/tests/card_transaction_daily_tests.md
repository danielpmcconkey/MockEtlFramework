# CardTransactionDaily — Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | Transactions enriched with card_type via card_id lookup (LEFT JOIN) |
| TC-02   | BR-2           | Transactions grouped by card_type, one output row per card_type |
| TC-03   | BR-3           | txn_count is the count of transactions per card_type |
| TC-04   | BR-4           | total_amount is the sum of amounts per card_type |
| TC-05   | BR-5           | avg_amount uses Banker's rounding (MidpointRounding.ToEven) to 2 dp |
| TC-06   | BR-6           | Unmatched card_id defaults card_type to "Unknown" |
| TC-07   | BR-7           | MONTHLY_TOTAL row appended on last day of month |
| TC-08   | BR-8           | MONTHLY_TOTAL row contains aggregate totals across all card types |
| TC-09   | BR-9           | Dead-end accounts and customers DataSourcing removed in V2 (AP1) |
| TC-10   | BR-10          | as_of value taken from first row of card_transactions |
| TC-11   | BR-11          | No weekend fallback — maxDate used directly for end-of-month check |
| TC-12   | BR-12          | Empty input (null or zero rows) returns empty DataFrame |
| TC-13   | BR-13          | Unused sourced columns eliminated in V2 (AP4) |
| TC-14   | Edge Case 1    | End-of-month falling on a weekend |
| TC-15   | Edge Case 2    | Cards not found for transactions — "Unknown" card_type |
| TC-16   | Edge Case 3    | Trailer row count includes MONTHLY_TOTAL row |
| TC-17   | Edge Case 4    | as_of from first transaction row matches effective date |
| TC-18   | Edge Case 5    | Division by zero guard — txn_count = 0 produces avg_amount = 0 |
| TC-19   | Writer Config  | CsvFileWriter with correct header, trailer, Overwrite, LF line endings |
| TC-20   | Output Schema  | Column order: card_type, txn_count, total_amount, avg_amount, as_of |
| TC-21   | FSD W5         | Banker's rounding explicitly specified with MidpointRounding.ToEven |
| TC-22   | FSD W3b        | End-of-month boundary logic uses __maxEffectiveDate from shared state |
| TC-23   | Proofmark      | Proofmark config: csv reader, header_rows=1, trailer_rows=1, no EXCLUDED/FUZZY |
| TC-24   | FSD AP3/AP6    | SQL Transformation handles JOIN (replaces row-by-row dictionary lookup) |
| TC-25   | Non-EOM date   | No MONTHLY_TOTAL row on dates that are not last day of month |

## Test Cases

### TC-01: Card type enrichment via LEFT JOIN
- **Traces to:** BR-1
- **Input conditions:** Run for a weekday effective date (e.g., 2024-10-01) where both card_transactions and cards have data. Card_transactions rows reference card_ids that exist in the cards table.
- **Expected output:** Each output row has a card_type value that matches the card_type from the cards table for the corresponding card_id. The SQL Transformation performs a LEFT JOIN between card_transactions and cards on card_id and as_of.
- **Verification method:** For a sample of output card_type values, trace back to the cards table and confirm the lookup is correct. Cross-reference with `SELECT DISTINCT c.card_type FROM datalake.cards c WHERE c.as_of = '2024-10-01'`.

### TC-02: Grouping by card_type
- **Traces to:** BR-2
- **Input conditions:** Run for a weekday effective date. Expect multiple card types (e.g., Credit, Debit, Prepaid) from the cards table.
- **Expected output:** One output row per distinct card_type. No duplicate card_type values in output (excluding the potential MONTHLY_TOTAL row).
- **Verification method:** Extract card_type values from output (excluding any MONTHLY_TOTAL row). Verify each is unique. Verify the set of card_types matches what the input data would produce.

### TC-03: txn_count is COUNT per card_type
- **Traces to:** BR-3
- **Input conditions:** Run for 2024-10-01. Independently count transactions per card_type by joining card_transactions to cards.
- **Expected output:** Each output row's txn_count matches the independent count of transactions for that card_type.
- **Verification method:** Compare output against `SELECT COALESCE(c.card_type, 'Unknown') AS card_type, COUNT(*) AS txn_count FROM datalake.card_transactions ct LEFT JOIN datalake.cards c ON ct.card_id = c.card_id AND ct.as_of = c.as_of WHERE ct.as_of = '2024-10-01' GROUP BY COALESCE(c.card_type, 'Unknown')`.

### TC-04: total_amount is SUM(amount) per card_type
- **Traces to:** BR-4
- **Input conditions:** Run for 2024-10-01. Independently sum amounts per card_type.
- **Expected output:** Each output row's total_amount matches the independent SUM of transaction amounts for that card_type. Accumulated using decimal precision (no floating-point epsilon drift).
- **Verification method:** Compare output against `SELECT COALESCE(c.card_type, 'Unknown') AS card_type, SUM(ct.amount) AS total_amount FROM datalake.card_transactions ct LEFT JOIN datalake.cards c ON ct.card_id = c.card_id AND ct.as_of = c.as_of WHERE ct.as_of = '2024-10-01' GROUP BY COALESCE(c.card_type, 'Unknown')`. Note: SQL uses double precision; V2 uses C# decimal. For large sums, verify exact match at the decimal level.

### TC-05: avg_amount with Banker's rounding
- **Traces to:** BR-5, W5
- **Input conditions:** Run for a date where at least one card_type group has total_amount/txn_count that hits a midpoint value (e.g., exactly X.XX5). If no natural midpoint exists, verify rounding to 2 decimal places on any data.
- **Expected output:** avg_amount = Math.Round(total_amount / txn_count, 2, MidpointRounding.ToEven). For midpoint values, rounds to even (e.g., 2.345 -> 2.34, 2.355 -> 2.36).
- **Verification method:** For each output row, manually compute total_amount / txn_count and apply Banker's rounding to 2 dp. Compare against output avg_amount. Verify the V2 code uses explicit `MidpointRounding.ToEven`.

### TC-06: Unknown card_type fallback
- **Traces to:** BR-6, Edge Case 2
- **Input conditions:** A scenario where card_transactions contains a card_id that does not exist in the cards table for the same as_of date. This could happen if a card was added to the transaction system but not yet in the cards snapshot.
- **Expected output:** Transactions with unmatched card_ids produce a row with card_type = "Unknown". The txn_count, total_amount, and avg_amount reflect those orphaned transactions.
- **Verification method:** Verify the SQL uses `COALESCE(c.card_type, 'Unknown')` in the LEFT JOIN. If any "Unknown" rows appear in output, verify their counts/amounts match orphaned transactions.

### TC-07: MONTHLY_TOTAL row on last day of month
- **Traces to:** BR-7, W3b
- **Input conditions:** Run for effective date 2024-10-31 (last day of October, a Thursday — weekday with data).
- **Expected output:** Output includes all per-card-type rows PLUS one additional row with card_type = "MONTHLY_TOTAL". Total row count = (number of distinct card types) + 1.
- **Verification method:** Read the output CSV. Verify the last data row (before trailer) has card_type = "MONTHLY_TOTAL". Verify it is present only when the effective date is the last day of its month.

### TC-08: MONTHLY_TOTAL calculation
- **Traces to:** BR-8
- **Input conditions:** Run for 2024-10-31. Independently compute the total txn_count, total_amount, and avg_amount across all card types.
- **Expected output:** The MONTHLY_TOTAL row has:
  - txn_count = sum of all per-card-type txn_count values
  - total_amount = sum of all per-card-type total_amount values
  - avg_amount = Math.Round(total_amount / txn_count, 2, MidpointRounding.ToEven)
  - as_of = same as_of as all other rows (first row's as_of)
- **Verification method:** Sum the txn_count and total_amount values from the non-MONTHLY_TOTAL rows. Compute the expected avg_amount with Banker's rounding. Compare against the MONTHLY_TOTAL row's values.

### TC-09: Dead-end sourcing eliminated (AP1)
- **Traces to:** BR-9, AP1
- **Input conditions:** Inspect the V2 job config JSON.
- **Expected output:** The V2 config does NOT contain DataSourcing entries for the `accounts` or `customers` tables. Only `card_transactions` and `cards` are sourced.
- **Verification method:** Parse the V2 job config JSON. Verify only two DataSourcing modules exist, targeting `card_transactions` and `cards` respectively. No `accounts` or `customers` entries.

### TC-10: as_of from first row of enriched transactions
- **Traces to:** BR-10, Edge Case 4
- **Input conditions:** Run for a single weekday effective date (e.g., 2024-10-01).
- **Expected output:** All output rows (including MONTHLY_TOTAL if present) have the same as_of value, which is the as_of from the first row of the enriched_txns DataFrame.
- **Verification method:** Read the output CSV. Verify all as_of values in data rows are identical and match the effective date. In single-day auto-advance mode, the first row's as_of should equal the effective date.

### TC-11: No weekend fallback
- **Traces to:** BR-11
- **Input conditions:** Inspect the V2 External module code.
- **Expected output:** The end-of-month check uses `__maxEffectiveDate` directly from shared state. There is NO code that adjusts the date for weekends (no "previous Friday" fallback logic).
- **Verification method:** Code review of CardTransactionDailyV2Processor.cs. Confirm the maxDate variable is read directly from shared state and used as-is in the end-of-month comparison.

### TC-12: Empty input guard
- **Traces to:** BR-12
- **Input conditions:** Run for an effective date where card_transactions has zero rows (e.g., a weekend date like 2024-10-05), OR a scenario where the enriched_txns DataFrame is null/empty after the SQL Transformation.
- **Expected output:** The External module returns an empty DataFrame with the correct column schema (card_type, txn_count, total_amount, avg_amount, as_of). The CsvFileWriter produces a file with a header row, no data rows, and a trailer showing `TRAILER|0|{date}`.
- **Verification method:** Read the output CSV. Verify it contains exactly: (1) header row with column names, (2) trailer row `TRAILER|0|{effective_date}`. No data rows in between.

### TC-13: Unused columns eliminated (AP4)
- **Traces to:** BR-13, AP4
- **Input conditions:** Inspect the V2 job config JSON.
- **Expected output:**
  - card_transactions DataSourcing: columns = `["card_id", "amount"]` (V1 sourced 8 columns)
  - cards DataSourcing: columns = `["card_id", "card_type"]` (V1 sourced 3 columns including unused customer_id)
- **Verification method:** Parse the V2 job config JSON. Verify each DataSourcing module's columns array contains only the specified columns.

### TC-14: End-of-month on a weekend
- **Traces to:** Edge Case 1
- **Input conditions:** Consider a month where the last day falls on a weekend (e.g., November 30, 2024 is a Saturday). If the effective date is 2024-11-30, the cards table may have no data for that date (weekday-only snapshots).
- **Expected output:** Two possibilities depending on card_transactions data:
  - If card_transactions has no data for 2024-11-30: empty output guard triggers (TC-12 behavior). The MONTHLY_TOTAL check still fires (maxDate IS last day of month), but there are no groups to summarize, so the output remains empty.
  - If card_transactions has data but cards does not: all transactions get card_type = "Unknown" (BR-6). The MONTHLY_TOTAL row reflects the "Unknown" group aggregates.
- **Verification method:** Run for a weekend end-of-month date. Verify the output matches one of the expected behaviors above. The key assertion is that the end-of-month check uses maxDate directly (no weekend adjustment per BR-11).

### TC-15: Cards not found for transactions
- **Traces to:** Edge Case 2, BR-6
- **Input conditions:** A scenario where some card_ids in card_transactions do not have matching entries in the cards table for the same as_of date.
- **Expected output:** Those transactions are grouped under card_type = "Unknown". The "Unknown" row has correct txn_count, total_amount, and avg_amount reflecting only the orphaned transactions.
- **Verification method:** Identify orphaned card_ids by LEFT JOIN-ing card_transactions to cards. Verify the "Unknown" row's aggregates match the orphaned transactions' counts and amounts.

### TC-16: Trailer row count includes MONTHLY_TOTAL
- **Traces to:** Edge Case 3
- **Input conditions:** Run for 2024-10-31 (end-of-month). Suppose there are 3 distinct card types plus 1 MONTHLY_TOTAL row = 4 data rows.
- **Expected output:** The trailer reads `TRAILER|4|2024-10-31`. The row_count in the trailer (4) includes the MONTHLY_TOTAL row because the framework's CsvFileWriter counts all rows in the output DataFrame.
- **Verification method:** Read the last line of the CSV. Parse the trailer format `TRAILER|{row_count}|{date}`. Verify row_count = (number of card_type groups) + 1 (for MONTHLY_TOTAL). Verify date = effective date.

### TC-17: as_of from first row matches effective date in single-day mode
- **Traces to:** Edge Case 4, BR-10
- **Input conditions:** Run for a single weekday effective date in auto-advance (single-day gap-fill) mode.
- **Expected output:** The as_of column for all output rows equals the effective date string (e.g., "2024-10-01"). Because DataSourcing filters to a single as_of date, the first row's as_of is that date.
- **Verification method:** Read output CSV. Verify as_of on every data row is the effective date string. This is a natural consequence of single-day execution, but confirms the "first row" logic is stable.

### TC-18: Division by zero guard
- **Traces to:** Edge Case 5
- **Input conditions:** A scenario where txn_count is 0 for a group (or for the MONTHLY_TOTAL aggregate). This could occur if enriched_txns contains rows but a particular card_type group has count 0 after some edge condition. In practice, any row in the input means count >= 1, so this guard primarily protects the MONTHLY_TOTAL row in edge cases.
- **Expected output:** avg_amount = 0 (not NaN, not Infinity, not an exception) when txn_count is 0.
- **Verification method:** Code review: verify the V2 External module has a conditional check `count > 0 ? Math.Round(total / count, 2, MidpointRounding.ToEven) : 0m` for both per-card-type and MONTHLY_TOTAL rows.

### TC-19: Writer configuration — CsvFileWriter parameters
- **Traces to:** BRD Writer Configuration
- **Input conditions:** Inspect the V2 job config JSON.
- **Expected output:** The V2 CsvFileWriter config specifies:
  - `"type": "CsvFileWriter"`
  - `"source": "output"`
  - `"outputFile": "Output/double_secret_curated/card_transaction_daily.csv"`
  - `"includeHeader": true`
  - `"trailerFormat": "TRAILER|{row_count}|{date}"`
  - `"writeMode": "Overwrite"`
  - `"lineEnding": "LF"`
- **Verification method:** Parse the V2 job config JSON and verify each writer parameter matches exactly.

### TC-20: Output column order
- **Traces to:** FSD Section 4 (Output Schema)
- **Input conditions:** Run the job and read the output CSV.
- **Expected output:** The header row reads: `card_type,txn_count,total_amount,avg_amount,as_of`. Column order matches V1 exactly.
- **Verification method:** Read the first line of the output CSV. Split by comma and verify order is [card_type, txn_count, total_amount, avg_amount, as_of].

### TC-21: Banker's rounding implementation
- **Traces to:** W5, BR-5
- **Input conditions:** Code review of CardTransactionDailyV2Processor.cs.
- **Expected output:** The V2 External module uses `Math.Round(value, 2, MidpointRounding.ToEven)` with the explicit rounding mode parameter. The rounding is applied to both per-card-type avg_amount and the MONTHLY_TOTAL avg_amount.
- **Verification method:** Read the External module source code. Search for all `Math.Round` calls. Verify each specifies `MidpointRounding.ToEven` explicitly. Verify no other rounding method is used.

### TC-22: End-of-month boundary uses __maxEffectiveDate
- **Traces to:** W3b, BR-7
- **Input conditions:** Code review of CardTransactionDailyV2Processor.cs.
- **Expected output:** The External module reads `__maxEffectiveDate` from shared state and compares `maxDate.Day == DateTime.DaysInMonth(maxDate.Year, maxDate.Month)` to determine if the MONTHLY_TOTAL row should be appended. A comment documents the W3b behavior.
- **Verification method:** Read the External module source code. Verify the boundary check reads from `__maxEffectiveDate` and uses the correct calendar comparison. Verify a comment referencing W3b is present.

### TC-23: Proofmark configuration validation
- **Traces to:** FSD Section 8 (Proofmark Config Design)
- **Input conditions:** Inspect the Proofmark YAML config for this job.
- **Expected output:** Config specifies:
  - `comparison_target: "card_transaction_daily"`
  - `reader: csv`
  - `threshold: 100.0`
  - `csv.header_rows: 1`
  - `csv.trailer_rows: 1`
  - No `columns.excluded` entries
  - No `columns.fuzzy` entries
- **Verification method:** Parse the Proofmark YAML and verify all fields. Confirm no column overrides — strict comparison is appropriate because: (a) V2 uses the same C# decimal type and Banker's rounding as V1, so avg_amount and total_amount will match exactly; (b) no non-deterministic fields exist; (c) trailer_rows=1 is correct because writeMode=Overwrite produces exactly one trailer at the end of file.

### TC-24: SQL Transformation replaces row-by-row lookup
- **Traces to:** AP3, AP6
- **Input conditions:** Inspect the V2 job config JSON and External module code.
- **Expected output:** The V2 config includes a Transformation module with SQL that performs a LEFT JOIN between card_transactions and cards. The External module does NOT build a card_type lookup dictionary — it receives pre-joined data with card_type already on each row.
- **Verification method:** Verify the V2 job config contains a Transformation module with `LEFT JOIN cards c ON ct.card_id = c.card_id AND ct.as_of = c.as_of` and `COALESCE(c.card_type, 'Unknown')`. Verify the External module reads `enriched_txns` (not raw `card_transactions` and `cards`).

### TC-25: No MONTHLY_TOTAL on non-end-of-month dates
- **Traces to:** BR-7 (inverse)
- **Input conditions:** Run for an effective date that is NOT the last day of its month (e.g., 2024-10-15, a Tuesday).
- **Expected output:** Output contains only per-card-type rows. No row with card_type = "MONTHLY_TOTAL" exists. Row count = number of distinct card types only.
- **Verification method:** Read the output CSV. Verify no row has card_type = "MONTHLY_TOTAL". Verify the trailer row_count equals the number of distinct card_type values (no +1 for MONTHLY_TOTAL).
