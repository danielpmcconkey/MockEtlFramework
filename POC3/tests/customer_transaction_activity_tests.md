# CustomerTransactionActivity — V2 Test Plan

## Job Info
- **V2 Config**: `customer_transaction_activity_v2.json`
- **Tier**: 1 (Framework Only — replaces V1 External module with SQL Transformation)
- **External Module**: None (V1 used `ExternalModules.CustomerTxnActivityBuilder`; V2 eliminates it via AP3)

## Pre-Conditions
- Data sources required:
  - `datalake.transactions` — columns: `account_id`, `txn_type`, `amount`, `as_of` (auto-appended by DataSourcing). Note: V1 also sourced `transaction_id` but never used it (AP4 — eliminated in V2).
  - `datalake.accounts` — columns: `account_id`, `customer_id`, `as_of` (auto-appended by DataSourcing)
- Effective date range starts at `2024-10-01` (firstEffectiveDate), auto-advancing one day at a time
- Both tables must have data for the effective date being processed; INNER JOIN excludes unmatched transactions
- The V1 External module (`CustomerTxnActivityBuilder.cs`) performs: account-to-customer dictionary lookup, per-customer aggregation (count, sum, debit/credit counts), and as_of extraction from first transaction row
- V2 replaces all of this with a single SQL Transformation

## Test Cases

### TC-1: Output Schema Validation
- **Expected columns (exact order from FSD Section 4):**
  1. `customer_id` — integer
  2. `as_of` — date (text)
  3. `transaction_count` — integer
  4. `total_amount` — decimal
  5. `debit_count` — integer
  6. `credit_count` — integer
- Verify the header row in the CSV matches this exact column order
- Verify `total_amount` renders with the same precision/format as V1 (no extra or missing decimal places)
- Verify `as_of` date format matches V1 output exactly

### TC-2: Row Count Equivalence
- V1 vs V2 must produce identical row counts for each effective date
- One output row per customer with at least one matched transaction
- Customers whose transactions all have unmatched account_ids (customer_id = 0) produce no rows
- Over a full auto-advance run, the accumulated CSV must have the same total number of data rows
- Append mode accumulates rows across dates — verify total line count (minus 1 header) matches V1

### TC-3: Data Content Equivalence
- All values must be byte-identical to V1 output
- **customer_id**: exact integer match per customer
- **as_of**: must be the MIN(as_of) from the transactions table for each run — matches V1's `transactions.Rows[0]["as_of"]` behavior (BR-7). Since auto-advance processes one day at a time, all transaction rows share the same as_of, so MIN() equals the first row's value.
- **transaction_count**: COUNT(*) of all matched transactions per customer — must match V1's loop counter
- **total_amount**: SUM(amount) with no rounding — must match V1's raw decimal accumulation (BR-4). Watch for potential SQLite REAL vs C# decimal precision differences (see TC-W1).
- **debit_count**: count of transactions where txn_type == "Debit" exactly (case-sensitive, BR-3)
- **credit_count**: count of transactions where txn_type == "Credit" exactly (case-sensitive, BR-3)
- **Row ordering**: V2 uses `ORDER BY customer_id` (ascending). V1 uses dictionary insertion order (BR-10). These may differ — Proofmark will validate. If mismatch, SQL ORDER BY must be adjusted.

### TC-4: Writer Configuration
- **includeHeader**: `true` — header written on first file creation only; subsequent Append runs do not re-add header (framework CsvFileWriter behavior, line 47: `if (_includeHeader && !append)`)
- **writeMode**: `Append` — each effective date's results are appended to the existing file
- **lineEnding**: `LF` — Unix-style line endings throughout
- **trailerFormat**: not configured — no trailer rows in output
- **source**: `output` — writer reads from the Transformation result named "output"
- **outputFile**: `Output/double_secret_curated/customer_transaction_activity.csv` (V2 path)

### TC-5: Anti-Pattern Elimination Verification
- **AP3 (Unnecessary External module):** Verify V2 config does NOT contain an External module entry. V1 used `ExternalModules.CustomerTxnActivityBuilder` for a join + group-by operation that SQL handles natively. V2 replaces it with a Transformation module containing the equivalent SQL.
  - Verify V2 config has exactly 2 DataSourcing entries (transactions, accounts), 1 Transformation, and 1 CsvFileWriter
  - Verify output is functionally equivalent despite replacing the External module with SQL
- **AP4 (Unused columns):** Verify V2 DataSourcing for transactions does NOT include `transaction_id`. V1 sourced it but the External module never referenced it (BR-11). V2 sources only `account_id`, `txn_type`, `amount`.
- **AP6 (Row-by-row iteration):** Verify V2 uses set-based SQL (JOIN + GROUP BY) instead of V1's `foreach` loop iteration pattern. This is inherently verified by the Tier 1 SQL approach — no procedural code exists in V2.

### TC-6: Edge Cases
- **Empty transactions:** If `transactions` has no data for a given effective date, the Transformation SQL returns zero rows. V1 returns an empty output (BR-6). Verify V2 behavior matches — CsvFileWriter appends nothing or appends zero data rows. Note FSD concern: if the SQLite table is not registered (Transformation.RegisterTable skips empty DataFrames), the SQL may error on "no such table." Monitor for this during testing.
- **Empty accounts:** If `accounts` has no data for a given effective date, the INNER JOIN produces zero rows. V1 returns an empty output (BR-5). Same empty-table concern as above applies.
- **Unmatched transactions:** Transactions whose account_id has no matching entry in accounts are silently dropped by the INNER JOIN. Additionally, if an account row has `customer_id = 0`, V2's `WHERE a.customer_id != 0` clause excludes those transactions, matching V1's `if (customerId == 0) continue` guard (BR-2).
- **Transaction types beyond Debit/Credit:** If a txn_type value is neither "Debit" nor "Credit", it is still counted in `transaction_count` (COUNT(*)) but not in `debit_count` or `credit_count`. Verify `debit_count + credit_count <= transaction_count` is possible. Current data only has Debit and Credit, but the logic must handle other values gracefully.
- **Multi-day effective range aggregation:** BR-8 specifies cross-date aggregation — all transactions in the sourced range are aggregated into one row per customer. Since auto-advance processes one day at a time, this is moot for single-day runs. But if a multi-day window is ever used, the SQL aggregates across all dates correctly.
- **Append mode with header:** On first write, header is included. On subsequent appends, header must NOT be re-added. Verify no duplicate header rows appear mid-file. W12 does NOT apply here because V1 uses the framework's CsvFileWriter (not the External module) for file I/O.
- **Re-running the same date:** Append mode means re-running produces duplicate rows. This matches V1 behavior. No deduplication expected.

### TC-7: Proofmark Configuration
- **Expected proofmark config file:** `POC3/proofmark_configs/customer_transaction_activity.yaml`
- **Settings from FSD Section 8:**
  - `comparison_target`: `customer_transaction_activity`
  - `reader`: `csv`
  - `threshold`: `100.0` (strict — byte-identical match expected initially)
  - `header_rows`: `1`
  - `trailer_rows`: `0`
  - `excluded columns`: none (no non-deterministic fields identified)
  - `fuzzy columns`: none initially — start strict
- **Potential adjustments if strict comparison fails:**
  - If `total_amount` shows floating-point epsilon differences (SQLite REAL vs C# decimal), add fuzzy tolerance: `tolerance: 0.01, tolerance_type: absolute` on the `total_amount` column
  - If row ordering differs (V1 dictionary insertion order vs V2 `ORDER BY customer_id`), this is a data correctness issue — fix the SQL ORDER BY, do NOT add Proofmark exclusions for ordering

## W-Code Test Cases

### TC-W1: W6 — Double Epsilon (Potential Concern)
- **What the wrinkle is:** V1 accumulates `total_amount` using C# `decimal` type (`Convert.ToDecimal`, line 48 of CustomerTxnActivityBuilder.cs). SQLite internally uses REAL (IEEE 754 double) for arithmetic. For typical monetary amounts with 2 decimal places, double-precision sums should match decimal sums, but edge cases with many additions could introduce epsilon drift.
- **How V2 handles it:** V2 uses `SUM(t.amount)` in SQLite, which operates on REAL values. The FSD assessed this as LOW risk because the amounts are simple sums of PostgreSQL `numeric` columns and the magnitudes involved don't trigger epsilon issues.
- **What to verify:**
  - Compare `total_amount` values between V1 and V2 for every customer across all effective dates
  - If any difference exceeds 0.005 (half a cent), investigate whether it's a systematic precision issue
  - If epsilon differences appear, update Proofmark config with fuzzy tolerance on `total_amount`
  - If all values match exactly at 100% threshold, no action needed

### TC-W2: BR-10 — Row Ordering (Dictionary Insertion Order vs ORDER BY)
- **What the wrinkle is:** V1 outputs rows in dictionary insertion order — the order in which each `customer_id` is first encountered while iterating the transactions DataFrame. V2 uses `ORDER BY a.customer_id` (ascending numeric order). These may not be the same.
- **How V2 handles it:** The FSD uses `ORDER BY a.customer_id` as the primary candidate, noting that lower customer_ids tend to have lower account_ids which tend to appear earlier in the transaction list, making ascending order the most likely match. Proofmark will validate.
- **What to verify:**
  - Run both V1 and V2 and compare row ordering
  - If ordering differs, determine V1's exact order pattern and adjust the SQL ORDER BY clause accordingly (e.g., `ORDER BY MIN(t.rowid)` or another deterministic ordering that matches V1's encounter sequence)
  - This is a data correctness issue, not a Proofmark tolerance issue — the fix must be in the SQL, not in comparison settings

## Notes
- This job is a significant Tier upgrade: V1 uses a full External module (CustomerTxnActivityBuilder.cs) that V2 replaces entirely with a SQL Transformation. This is the core AP3 elimination.
- The FSD identifies two medium-risk areas that need validation during testing:
  1. **Row ordering (BR-10):** V2's `ORDER BY customer_id` may not match V1's dictionary insertion order. This is the most likely cause of a Proofmark mismatch and the first thing to investigate if comparison fails.
  2. **total_amount precision:** SQLite REAL vs C# decimal. Low probability of mismatch but must be verified.
- The empty-table crash scenario (FSD Section 5, Empty Data Behavior) is assessed as low risk because the datalake uses full-load daily snapshots and the framework processes one day at a time. However, if a date with no data is encountered, the Transformation SQL will fail because SQLite tables won't be registered. Monitor test runs for this error.
- OQ-1 from BRD (cross-date aggregation intentionality) does not affect V2 implementation — V2 replicates V1's behavior regardless of whether it's intentional.
- BR-9 (last-write-wins account lookup) is moot in single-day execution because each day has exactly one as_of per account. No special handling needed in V2 SQL.
