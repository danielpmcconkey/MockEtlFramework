# BranchTransactionVolume — Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | JOIN on account_id AND as_of ensures date-aligned snapshots |
| TC-02   | BR-2           | Output grouped by account_id, customer_id, as_of — one row per account per date |
| TC-03   | BR-3           | total_amount is ROUND(SUM(amount), 2) |
| TC-04   | BR-4           | Output ordered by as_of then account_id |
| TC-05   | BR-5           | branches table sourced in V1 but removed in V2 (AP1 eliminated) |
| TC-06   | BR-6           | customers table sourced in V1 but removed in V2 (AP1 eliminated) |
| TC-07   | BR-7           | Unused columns (description, interest_rate) removed in V2 (AP4 eliminated) |
| TC-08   | BR-8           | All transaction types included — no txn_type filter |
| TC-09   | BR-9           | txn_count uses COUNT(*), not COUNT of a specific column |
| TC-10   | Output Schema  | Output contains exactly 5 columns in correct order |
| TC-11   | Writer Config  | Parquet output with numParts=1 produces single part file |
| TC-12   | Writer Config  | Overwrite mode replaces entire output directory |
| TC-13   | Edge Case      | Accounts with no transactions (inner join exclusion) |
| TC-14   | Edge Case      | Transactions with no matching account (inner join exclusion) |
| TC-15   | Edge Case      | Zero-row output scenario |
| TC-16   | Edge Case      | NULL handling in amount field |
| TC-17   | Edge Case      | Rounding boundary values |
| TC-18   | Edge Case      | Multi-day effective range produces multiple rows per account |
| TC-19   | Edge Case      | Weekend/Sunday date behavior |
| TC-20   | Proofmark      | V2 output matches V1 baseline under strict comparison |

## Test Cases

### TC-01: JOIN on account_id AND as_of ensures date-aligned snapshots
- **Traces to:** BR-1
- **Input conditions:** Transactions and accounts tables both have data for multiple as_of dates. A transaction for account_id=100 on as_of=2024-10-01 should join only with the account record for account_id=100 on as_of=2024-10-01, not with the account record on as_of=2024-10-02.
- **Expected output:** Each output row reflects the account's customer_id as of the same date as the transaction. No cross-date joins occur.
- **Verification method:** For a known account_id that exists on multiple dates, verify the customer_id in each output row matches the accounts table for that specific as_of date. Confirm the SQL contains `JOIN accounts a ON t.account_id = a.account_id AND t.as_of = a.as_of`.

### TC-02: Output grouped by account_id, customer_id, as_of
- **Traces to:** BR-2
- **Input conditions:** Multiple transactions exist for the same account_id on the same as_of date (e.g., account_id=100 has 5 transactions on 2024-10-01).
- **Expected output:** Exactly one row per (account_id, customer_id, as_of) combination. The 5 transactions are aggregated into a single row with txn_count=5.
- **Verification method:** Query the output for a specific account_id and as_of. Verify only one row exists. Verify txn_count matches the number of source transactions for that account on that date. Check that no duplicate (account_id, as_of) pairs exist anywhere in the output.

### TC-03: total_amount is ROUND(SUM(amount), 2)
- **Traces to:** BR-3
- **Input conditions:** An account has transactions with amounts that sum to a value with more than 2 decimal places (e.g., 10.111 + 20.222 + 30.333 = 60.666).
- **Expected output:** total_amount = 60.67 (rounded to 2 decimal places using standard rounding).
- **Verification method:** Identify a test account with known transaction amounts. Manually compute SUM and ROUND to 2 decimals. Compare against the output value. Verify that values like x.xx5 round up (standard rounding, not banker's rounding — SQLite uses half-away-from-zero).

### TC-04: Output ordered by as_of then account_id
- **Traces to:** BR-4
- **Input conditions:** Output spans multiple dates and multiple account_ids.
- **Expected output:** Rows are sorted primarily by as_of ascending, then by account_id ascending within each date.
- **Verification method:** Read all output rows. Verify as_of values are non-decreasing. Within rows sharing the same as_of, verify account_id values are strictly non-decreasing.

### TC-05: branches table sourced in V1 but removed in V2
- **Traces to:** BR-5
- **Input conditions:** V1 job config sources the branches table (resultName "branches"). V2 job config removes this DataSourcing entry entirely.
- **Expected output:** V2 output is identical to V1 output despite not sourcing the branches table. The branches table was never referenced in the transformation SQL, so removing it has no effect on output.
- **Verification method:** Inspect the V2 job config JSON. Confirm no DataSourcing module with `"table": "branches"` exists. Run Proofmark comparison to verify output equivalence.

### TC-06: customers table sourced in V1 but removed in V2
- **Traces to:** BR-6
- **Input conditions:** V1 job config sources the customers table (resultName "customers"). V2 job config removes this DataSourcing entry entirely.
- **Expected output:** V2 output is identical to V1 output despite not sourcing the customers table. The customers table was never referenced in the transformation SQL.
- **Verification method:** Inspect the V2 job config JSON. Confirm no DataSourcing module with `"table": "customers"` exists. Run Proofmark comparison to verify output equivalence.

### TC-07: Unused columns removed in V2
- **Traces to:** BR-7
- **Input conditions:** V1 sources `description` from transactions and `interest_rate` from accounts. Neither is referenced in the transformation SQL. V2 removes these columns from the DataSourcing column lists. V2 also removes `transaction_id` and `txn_type` (AP4).
- **Expected output:** V2 output is identical to V1. Removing unused source columns does not affect the SQL output because the Transformation module only SELECTs what the SQL specifies.
- **Verification method:** Inspect V2 DataSourcing columns for transactions (should be: `account_id`, `amount`) and accounts (should be: `account_id`, `customer_id`). Run Proofmark comparison to verify output equivalence.

### TC-08: All transaction types included
- **Traces to:** BR-8
- **Input conditions:** Transactions include both Debit and Credit types in the datalake.
- **Expected output:** Both Debit and Credit transactions contribute to txn_count and total_amount. No filtering on txn_type occurs.
- **Verification method:** For a known account_id on a specific date, count all transactions in the source (regardless of type) and compare to txn_count in the output. Also sum all amounts (regardless of type) and compare to total_amount (after rounding).

### TC-09: txn_count uses COUNT(*)
- **Traces to:** BR-9
- **Input conditions:** Transactions exist for an account, including rows where some columns might be NULL.
- **Expected output:** txn_count reflects the total number of transaction rows, not a count of non-NULL values in any specific column. COUNT(*) counts rows, not column values.
- **Verification method:** Verify the SQL uses `COUNT(*) AS txn_count` (not `COUNT(column_name)`). For a known account with N transactions on a given date, verify txn_count = N in the output.

### TC-10: Output contains exactly 5 columns in correct order
- **Traces to:** Output Schema
- **Input conditions:** Standard execution.
- **Expected output:** Output Parquet schema contains exactly 5 columns in this order: `account_id`, `customer_id`, `txn_count`, `total_amount`, `as_of`.
- **Verification method:** Read the Parquet file schema. Verify column names and order. Verify column types: account_id (int/long), customer_id (int/long), txn_count (long), total_amount (double), as_of (string/text).

### TC-11: Parquet output with numParts=1 produces single part file
- **Traces to:** Writer Config (numParts: 1)
- **Input conditions:** Standard execution.
- **Expected output:** The output directory contains exactly one file: `part-00000.parquet`.
- **Verification method:** List files in the output directory `Output/double_secret_curated/branch_transaction_volume/`. Verify only `part-00000.parquet` exists. No `part-00001.parquet` or additional part files.

### TC-12: Overwrite mode replaces entire output directory
- **Traces to:** Writer Config (writeMode: Overwrite)
- **Input conditions:** Execute the job twice. First run with effective date 2024-10-01, second run with effective date 2024-10-15.
- **Expected output:** After the second run, the output directory contains only the second run's data. No residual data from the first run persists.
- **Verification method:** After the second run, read the Parquet file and verify all as_of values correspond to the second run's effective date range, not the first.

### TC-13: Accounts with no transactions are excluded (inner join)
- **Traces to:** Edge Case
- **Input conditions:** An account exists in the accounts table for a given as_of date but has no transactions on that date.
- **Expected output:** That account does not appear in the output for that date. Inner join excludes accounts with no matching transaction rows.
- **Verification method:** Identify an account_id that has an account record on a specific date but zero transactions. Verify that account_id + date combination does not appear in the output.

### TC-14: Transactions with no matching account are excluded (inner join)
- **Traces to:** Edge Case
- **Input conditions:** A transaction exists with an account_id that has no matching record in the accounts table for that as_of date (orphaned transaction).
- **Expected output:** That transaction is excluded from the output. Inner join filters out transactions with no matching account.
- **Verification method:** Identify a transaction whose account_id does not appear in the accounts table for the same as_of. Verify that account_id + date combination does not appear in the output.

### TC-15: Zero-row output scenario
- **Traces to:** Edge Case
- **Input conditions:** Effective date range falls outside all as_of dates in the datalake (e.g., a far-future date where no transaction or account data exists). Alternatively, no transactions join to any accounts on the given date(s).
- **Expected output:** Output Parquet file is created but contains zero data rows. The file still exists with the correct schema.
- **Verification method:** Verify the output file exists. Read the Parquet file and verify row count is 0. Verify the schema still contains the expected 5 columns.

### TC-16: NULL handling in amount field
- **Traces to:** Edge Case
- **Input conditions:** A transaction row has a NULL amount value.
- **Expected output:** SUM with NULL values: SUM ignores NULLs per SQL standard. If all amounts for a group are NULL, SUM returns NULL, and ROUND(NULL, 2) returns NULL. If some amounts are NULL and some are not, only non-NULL values are summed.
- **Verification method:** If NULL amounts exist in the test data, verify that total_amount correctly reflects the sum of non-NULL amounts. If all amounts for a group are NULL, verify total_amount is NULL (not 0).

### TC-17: Rounding boundary values
- **Traces to:** BR-3, Edge Case
- **Input conditions:** Transaction amounts that produce sums ending in exactly .xx5 (the rounding boundary), e.g., amounts summing to 100.125.
- **Expected output:** SQLite's ROUND function uses "round half away from zero" (not banker's rounding). So 100.125 rounds to 100.13, and -100.125 rounds to -100.13.
- **Verification method:** Identify or construct a test case where the sum hits a .xx5 boundary. Verify the output matches SQLite's rounding behavior. This is critical because both V1 and V2 run through the same SQLite engine, so rounding should be identical.

### TC-18: Multi-day effective range produces multiple rows per account
- **Traces to:** Edge Case, BR-2
- **Input conditions:** Effective date range spans 3 days (e.g., 2024-10-01 through 2024-10-03). An account has transactions on all 3 days.
- **Expected output:** That account appears in the output 3 times — once per as_of date — each with its own txn_count and total_amount for that date.
- **Verification method:** For a known account_id, count the number of output rows. Verify it equals the number of dates in the effective range on which that account had transactions. Verify each row has a distinct as_of value.

### TC-19: Weekend/Sunday date behavior
- **Traces to:** Edge Case
- **Input conditions:** Effective date range includes weekend dates (Saturday/Sunday). No W1 (Sunday skip) or W2 (weekend fallback) wrinkles are identified for this job.
- **Expected output:** Weekend dates are processed normally. If transactions and accounts exist for a Saturday or Sunday as_of, they appear in the output. No dates are skipped.
- **Verification method:** Run with an effective date range that includes a weekend. Verify weekend as_of dates appear in the output if source data exists for those dates. Confirm no special date-skipping logic is applied.

### TC-20: V2 output matches V1 baseline under strict Proofmark comparison
- **Traces to:** Proofmark
- **Input conditions:** Both V1 and V2 have been executed for the same effective date range. Proofmark config uses `reader: parquet`, `threshold: 100.0`, no excluded columns, no fuzzy columns.
- **Expected output:** Proofmark reports 100% match. All rows and columns in V2 match V1 exactly.
- **Verification method:** Run Proofmark comparison tool with the proposed config: `comparison_target: "branch_transaction_volume"`, `reader: parquet`, `threshold: 100.0`. Verify threshold is met. If it fails, inspect the diff to identify discrepancies — since no non-deterministic fields were identified, any mismatch indicates a real bug.
