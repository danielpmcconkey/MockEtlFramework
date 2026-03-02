# CardCustomerSpending — Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01 | BR-1 | Weekend fallback: Saturday shifts to Friday |
| TC-02 | BR-1 | Weekend fallback: Sunday shifts to Friday |
| TC-03 | BR-1 | Weekday effective date used as-is |
| TC-04 | BR-2 | Transactions filtered to targetDate only |
| TC-05 | BR-3 | Output has one row per customer_id |
| TC-06 | BR-4 | txn_count is COUNT of transactions per customer |
| TC-07 | BR-5 | total_spending is SUM(amount) per customer |
| TC-08 | BR-6 | Customer names resolved via LEFT JOIN on customer_id = id |
| TC-09 | BR-7 | Accounts table removed from V2 (dead sourcing eliminated) |
| TC-10 | BR-8 | Empty card_transactions produces empty output with correct schema |
| TC-11 | BR-9 | No transactions matching targetDate produces empty output |
| TC-12 | BR-10 | Only first_name and last_name sourced from customers (prefix/suffix removed) |
| TC-13 | BR-11 | Output as_of is the weekend-adjusted targetDate, not __maxEffectiveDate |
| TC-14 | BR-12 | Customer lookup uses last-seen name (MAX as_of wins) |
| TC-15 | BR-6 | Customer not found defaults first_name and last_name to empty string |
| TC-16 | BR-1, BR-2 | Weekend transactions exist in source but are skipped due to Friday fallback |
| TC-17 | — | Output column order and schema verification |
| TC-18 | — | Proofmark comparison: no FUZZY or EXCLUDED columns expected |
| TC-19 | BR-5 | Decimal precision of total_spending through SQLite SUM path |
| TC-20 | BR-11 | Potential as_of type mismatch: V2 string vs V1 DateOnly in Parquet |
| TC-21 | BR-3, BR-4, BR-5 | Single customer with multiple transactions aggregates correctly |
| TC-22 | — | Zero-row output: Parquet file written with correct schema but no data rows |
| TC-23 | BR-12 | Customer with name change across as_of snapshots resolves to latest |
| TC-24 | — | Write mode Overwrite: only last effective date's output survives multi-day runs |

## Test Cases

### TC-01: Weekend fallback — Saturday to Friday
- **Traces to:** BR-1
- **Input conditions:** `__maxEffectiveDate` is a Saturday (e.g., 2024-10-05, which is a Saturday). card_transactions contains rows for both 2024-10-04 (Friday) and 2024-10-05 (Saturday).
- **Expected output:** Output contains only transactions with `as_of = 2024-10-04` (Friday). Saturday transactions are ignored. The `target` CTE computes `date(MAX(as_of), '-1 day')`.
- **Verification method:** Run V2 job for effective date 2024-10-05. Confirm output rows all have `as_of = 2024-10-04`. Confirm no Saturday transaction data appears.

### TC-02: Weekend fallback — Sunday to Friday
- **Traces to:** BR-1
- **Input conditions:** `__maxEffectiveDate` is a Sunday (e.g., 2024-10-06). card_transactions contains rows for 2024-10-04 (Friday), 2024-10-05 (Saturday), and 2024-10-06 (Sunday).
- **Expected output:** Output contains only Friday (2024-10-04) transactions. The `target` CTE computes `date(MAX(as_of), '-2 days')`.
- **Verification method:** Run V2 job for effective date 2024-10-06. Confirm output `as_of = 2024-10-04`. Confirm Saturday and Sunday transaction data absent.

### TC-03: Weekday effective date used as-is
- **Traces to:** BR-1
- **Input conditions:** `__maxEffectiveDate` is a weekday (e.g., 2024-10-03, a Thursday). card_transactions contains rows for 2024-10-03.
- **Expected output:** Output contains transactions for 2024-10-03 with `as_of = 2024-10-03`. No date shifting occurs.
- **Verification method:** Run V2 job for effective date 2024-10-03. Verify `as_of` in output matches the effective date exactly.

### TC-04: Transactions filtered to targetDate only
- **Traces to:** BR-2
- **Input conditions:** card_transactions contains rows spanning multiple as_of dates within the effective date range (e.g., 2024-10-01 through 2024-10-04). Effective date is 2024-10-04 (Friday).
- **Expected output:** Only transactions with `as_of = 2024-10-04` appear in the output. Transactions from 2024-10-01 through 2024-10-03 are excluded by the `WHERE ct.as_of = t.target_date` clause.
- **Verification method:** Run V2 job. Count output rows and compare against count of card_transactions rows where `as_of = '2024-10-04'`, grouped by customer. Confirm earlier dates excluded.

### TC-05: One output row per customer
- **Traces to:** BR-3
- **Input conditions:** card_transactions for the target date has multiple rows for at least one customer_id (e.g., customer 100 has 5 transactions).
- **Expected output:** Exactly one output row per distinct customer_id. No duplicate customer_id values in output.
- **Verification method:** Read output Parquet. Assert `SELECT customer_id, COUNT(*) FROM output GROUP BY customer_id HAVING COUNT(*) > 1` returns zero rows.

### TC-06: txn_count is COUNT per customer
- **Traces to:** BR-4
- **Input conditions:** Customer A has 3 transactions on the target date. Customer B has 1 transaction.
- **Expected output:** Customer A's `txn_count = 3`. Customer B's `txn_count = 1`.
- **Verification method:** Compare V2 output `txn_count` for each customer against `SELECT customer_id, COUNT(*) FROM datalake.card_transactions WHERE as_of = targetDate GROUP BY customer_id`.

### TC-07: total_spending is SUM(amount) per customer
- **Traces to:** BR-5
- **Input conditions:** Customer A has transactions with amounts 100.50, 200.75, 50.00 on the target date.
- **Expected output:** Customer A's `total_spending = 351.25` (sum of amounts).
- **Verification method:** Compare V2 output `total_spending` for each customer against `SELECT customer_id, SUM(amount) FROM datalake.card_transactions WHERE as_of = targetDate GROUP BY customer_id`.

### TC-08: Customer name lookup via JOIN
- **Traces to:** BR-6
- **Input conditions:** card_transactions has rows for customer_id 100. customers table has a row with `id = 100`, `first_name = 'John'`, `last_name = 'Smith'`.
- **Expected output:** Output row for customer_id 100 has `first_name = 'John'` and `last_name = 'Smith'`.
- **Verification method:** Join V2 output back to datalake.customers to confirm name fields match the latest as_of snapshot.

### TC-09: Accounts table not sourced in V2
- **Traces to:** BR-7
- **Input conditions:** V2 job config JSON is inspected.
- **Expected output:** V2 config has no DataSourcing module for the `accounts` table. Only `card_transactions` and `customers` are sourced.
- **Verification method:** Parse V2 JSON config. Assert no module references `"table": "accounts"`. This eliminates AP1 (dead-end sourcing).

### TC-10: Empty card_transactions produces empty output
- **Traces to:** BR-8
- **Input conditions:** The effective date range yields zero rows in card_transactions (e.g., a date before any data exists, or a date with no card transaction data).
- **Expected output:** Output is an empty DataFrame/Parquet file with the correct 6-column schema: `customer_id, first_name, last_name, txn_count, total_spending, as_of`.
- **Verification method:** Run V2 job for a date with no card_transactions data. Confirm output Parquet has zero data rows. Confirm Parquet schema contains all 6 columns.

### TC-11: No transactions matching targetDate
- **Traces to:** BR-9
- **Input conditions:** card_transactions has rows for dates other than the targetDate within the effective range, but no rows for the specific targetDate (after weekend fallback).
- **Expected output:** Empty output with correct schema. The SQL `WHERE ct.as_of = t.target_date` matches nothing.
- **Verification method:** Construct a scenario where the effective date range includes data but the specific targetDate has no transactions. Verify zero output rows.

### TC-12: Unused prefix/suffix columns removed
- **Traces to:** BR-10
- **Input conditions:** V2 DataSourcing config for customers.
- **Expected output:** V2 customers DataSourcing specifies only `["id", "first_name", "last_name"]`. The `prefix` and `suffix` columns from V1 are not sourced. Output schema does not contain `prefix` or `suffix`.
- **Verification method:** Inspect V2 JSON config. Verify columns list for customers DataSourcing. Verify output Parquet schema excludes prefix and suffix.

### TC-13: as_of uses weekend-adjusted targetDate
- **Traces to:** BR-11
- **Input conditions:** Effective date is Saturday 2024-10-05. Weekend fallback produces targetDate = 2024-10-04 (Friday).
- **Expected output:** Every output row has `as_of = 2024-10-04`, not `2024-10-05`.
- **Verification method:** Run V2 for 2024-10-05. Read output Parquet. Assert all `as_of` values equal `2024-10-04`.

### TC-14: Customer lookup resolves to latest as_of snapshot
- **Traces to:** BR-12
- **Input conditions:** customers table has two snapshots for customer 100: `as_of = 2024-10-01` with `first_name = 'John'`, and `as_of = 2024-10-04` with `first_name = 'Jonathan'`. Both are within the effective date range.
- **Expected output:** Output for customer 100 shows `first_name = 'Jonathan'` (the MAX as_of snapshot wins, matching V1's dictionary-overwrite behavior).
- **Verification method:** Identify customers with name changes across snapshots. Verify V2 output uses the name from the latest as_of date.

### TC-15: Customer not found — defaults to empty strings
- **Traces to:** BR-6
- **Input conditions:** card_transactions has a row for customer_id 99999 which does not exist in the customers table.
- **Expected output:** Output row for customer_id 99999 has `first_name = ''` and `last_name = ''`.
- **Verification method:** If such orphan customer_ids exist in the data, verify output shows empty strings. Otherwise, this scenario can be validated via Proofmark V1-V2 comparison (V1 uses `GetValueOrDefault` with `("", "")` default, V2 uses `LEFT JOIN` + `COALESCE`).

### TC-16: Weekend transactions skipped due to Friday fallback
- **Traces to:** BR-1, BR-2
- **Input conditions:** card_transactions has rows for Saturday 2024-10-05 and Sunday 2024-10-06. Effective date is 2024-10-05.
- **Expected output:** Saturday's transactions are not in the output. Friday's (2024-10-04) transactions are used instead. Weekend data exists in the source but is systematically ignored.
- **Verification method:** Run V2 for a weekend date. Verify output reflects only Friday data. Cross-reference with direct DB query for Saturday transactions to confirm they were excluded.

### TC-17: Output column order and schema verification
- **Traces to:** Output Schema (BRD)
- **Input conditions:** Any normal run producing at least one output row.
- **Expected output:** Parquet output columns in exact order: `customer_id, first_name, last_name, txn_count, total_spending, as_of`. Column types: customer_id (int), first_name (string), last_name (string), txn_count (int), total_spending (decimal), as_of (date or string — see TC-20).
- **Verification method:** Read V2 output Parquet schema. Compare column names, order, and types against V1 baseline.

### TC-18: Proofmark comparison — strict match expected
- **Traces to:** FSD Proofmark Config
- **Input conditions:** V1 and V2 jobs run for the same effective date.
- **Expected output:** Proofmark reports 100% match with zero overrides. No FUZZY columns. No EXCLUDED columns. All output fields are deterministic.
- **Verification method:** Run Proofmark with config: `comparison_target: "card_customer_spending"`, `reader: parquet`, `threshold: 100.0`. Verify PASS result. If FAIL, investigate per TC-19 and TC-20.

### TC-19: Decimal precision of total_spending
- **Traces to:** BR-5
- **Input conditions:** Customers with multiple transactions involving decimal amounts (e.g., 100.01 + 200.99 + 0.50).
- **Expected output:** V2 `total_spending` matches V1 exactly. V1 uses C# `decimal` accumulation; V2 uses SQLite `SUM()` on REAL values, then `Convert.ToDecimal()` at Parquet write time.
- **Verification method:** Compare V1 and V2 `total_spending` values for all customers. If any mismatch, investigate whether a FUZZY tolerance is needed for `total_spending` in the Proofmark config. Per FSD Section 10, start strict and add evidence-based overrides.

### TC-20: as_of column type — V2 string vs V1 DateOnly
- **Traces to:** BR-11
- **Input conditions:** V1 writes `as_of` as `DateOnly` to Parquet. V2's SQL produces `as_of` as a string from SQLite's `date()` function.
- **Expected output:** If Parquet types differ (V1: Date, V2: String), Proofmark may report a type mismatch on `as_of`.
- **Verification method:** Inspect V1 and V2 Parquet file schemas. If type mismatch exists, escalation to Tier 2 (minimal External module for type coercion) is required per FSD Section 10. Document finding for developer handoff.

### TC-21: Single customer with multiple transactions
- **Traces to:** BR-3, BR-4, BR-5
- **Input conditions:** Customer 100 has 10 transactions on the target date with varying amounts.
- **Expected output:** Exactly one row for customer 100. `txn_count = 10`. `total_spending = SUM of all 10 amounts`.
- **Verification method:** Query datalake for customer 100's transactions on targetDate. Compare count and sum against V2 output row.

### TC-22: Zero-row output produces valid Parquet
- **Traces to:** BR-8, BR-9
- **Input conditions:** A run that produces zero output rows (empty card_transactions or no matching targetDate).
- **Expected output:** A valid Parquet file (or directory) is created with the correct 6-column schema but zero data rows. The ParquetFileWriter should handle zero-row DataFrames gracefully.
- **Verification method:** Run V2 for a date producing no output. Verify output Parquet file exists, is readable, has zero rows, and has the correct schema.

### TC-23: Customer name change across snapshots
- **Traces to:** BR-12
- **Input conditions:** Customer 200 has `first_name = 'Bob'` on as_of = 2024-10-01 and `first_name = 'Robert'` on as_of = 2024-10-04. Effective date range includes both snapshots.
- **Expected output:** V2 output shows `first_name = 'Robert'` for customer 200 (MAX as_of wins). This matches V1's dictionary-overwrite behavior where the last row (highest as_of) overwrites earlier entries.
- **Verification method:** Identify customers with name changes. Verify V2 uses the latest name. Compare against V1 output via Proofmark.

### TC-24: Overwrite mode — last date wins in multi-day runs
- **Traces to:** Write Mode Implications (BRD)
- **Input conditions:** Auto-advance run covering effective dates 2024-10-01 through 2024-10-04. Each date produces different output.
- **Expected output:** After the run completes, the output directory contains only 2024-10-04's results. Earlier dates' outputs are overwritten.
- **Verification method:** Run V2 in auto-advance mode across multiple dates. After completion, verify output `as_of` values are all from the final effective date. Confirm no leftover data from earlier dates.
