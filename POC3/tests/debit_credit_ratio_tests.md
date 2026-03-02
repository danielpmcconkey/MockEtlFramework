# DebitCreditRatio — V2 Test Plan

## Job Info
- **V2 Config**: `debit_credit_ratio_v2.json`
- **Tier**: 1 (Framework Only)
- **External Module**: None (V1 used `ExternalModules.DebitCreditRatioCalculator`; V2 replaces it with Transformation SQL)

## Pre-Conditions
- Data source: `datalake.transactions` must be populated with transaction records containing at minimum the columns `account_id`, `txn_type`, `amount`, and `as_of`
- Data source: `datalake.accounts` must be populated with account records containing at minimum the columns `account_id` and `customer_id`
- Full transactions schema: `transaction_id` (integer), `account_id` (integer), `txn_type` (varchar: Debit/Credit), `amount` (numeric), `description` (varchar), `as_of` (date)
- Full accounts schema: `account_id` (integer), `customer_id` (integer), `account_type` (varchar), `balance` (numeric), `interest_rate` (numeric), `credit_limit` (numeric), `status` (varchar), `opened_date` (date), `as_of` (date)
- Effective dates are injected by the executor via shared state (no hard-coded override). `firstEffectiveDate: "2024-10-01"`.
- V1 baseline output must exist at `Output/curated/debit_credit_ratio/part-00000.parquet` for comparison
- The `as_of` column is auto-appended by the DataSourcing module when not listed in the `columns` array

## Test Cases

### TC-1: Output Schema Validation
- Expected columns in exact order (from FSD Section 4):
  1. `account_id` (int) -- aggregation key from transactions
  2. `customer_id` (int) -- LEFT JOIN lookup from accounts, COALESCE to 0
  3. `debit_count` (int) -- conditional count of Debit transactions
  4. `credit_count` (int) -- conditional count of Credit transactions
  5. `debit_credit_ratio` (int) -- integer division: debit_count / credit_count (W4)
  6. `debit_amount` (double) -- SUM of Debit amounts (W6)
  7. `credit_amount` (double) -- SUM of Credit amounts (W6)
  8. `amount_ratio` (double) -- double division: debit_amount / credit_amount (W6)
  9. `as_of` (text/date) -- MIN(as_of) per account
- Verify column count is exactly 9 per row
- Verify Parquet column types match: integers for account_id, customer_id, debit_count, credit_count, debit_credit_ratio; doubles for debit_amount, credit_amount, amount_ratio; string/date for as_of

### TC-2: Row Count Equivalence
- V1 and V2 must produce identical row counts for the same effective date
- One row per distinct `account_id` in the transactions data for the given effective date
- Accounts with transactions of any `txn_type` (including non-Debit/non-Credit) produce an output row (BR-10)
- Accounts that appear only in `accounts` but have no transactions produce no output row (LEFT JOIN from transactions, not accounts)

### TC-3: Data Content Equivalence
- All values must match V1 output (subject to W-code considerations below)
- `account_id` values must match exactly (integer)
- `customer_id` values must match exactly (integer, default 0 for unmatched accounts)
- `debit_count` and `credit_count` must match exactly (integer)
- `debit_credit_ratio` must match exactly (integer -- W4 integer division truncation)
- `debit_amount`, `credit_amount`, `amount_ratio` must match V1 (double precision -- W6 may cause epsilon-level differences; see TC-W2)
- `as_of` must match V1 -- `MIN(as_of)` is equivalent to V1's "first encountered" since DataSourcing orders by as_of (BR-8)
- **W-codes affecting comparison**: W4 (integer division) and W6 (double-precision epsilon). See TC-W1 and TC-W2.
- **Row ordering**: V1 output order is non-deterministic (dictionary enumeration order, BR-11). V2 SQL has no ORDER BY. Parquet row order is not semantically meaningful. Proofmark must compare in a row-order-independent manner.

### TC-4: Writer Configuration
- **Writer type**: ParquetFileWriter
- **source**: `output`
- **outputDirectory**: `Output/double_secret_curated/debit_credit_ratio/` (V2 path)
- **numParts**: 1 -- verify a single `part-00000.parquet` file is produced
- **writeMode**: `Overwrite` -- verify each execution replaces the previous output entirely. Only the latest effective date's results persist.

### TC-5: Anti-Pattern Elimination Verification
- **AP1 (Dead-end sourcing)**: V1 sources `interest_rate` and `credit_limit` from `accounts` but never uses them in the External module. Verify V2 DataSourcing for `accounts` sources only `["account_id", "customer_id"]`.
- **AP3 (Unnecessary External module)**: V1 uses `ExternalModules.DebitCreditRatioCalculator` for aggregation logic that is fully expressible in SQL. Verify V2 config contains NO External module. Verify the module chain is: DataSourcing -> DataSourcing -> Transformation -> ParquetFileWriter.
- **AP4 (Unused columns)**: V1 sources `transaction_id` and `description` from `transactions` but never uses them. Verify V2 DataSourcing for `transactions` sources only `["account_id", "txn_type", "amount"]`. Combined with AP1 fix on accounts, total sourced columns reduced from 9 to 5 (plus 2 auto-appended `as_of`).
- **AP6 (Row-by-row iteration)**: V1 uses `foreach` loops with `Dictionary<int, ...>` for aggregation. Verify V2 uses a single set-based SQL `GROUP BY` with conditional aggregation and `LEFT JOIN`, replacing all procedural iteration.

### TC-6: Edge Cases
- **Empty input behavior (BR-9)**: If `transactions` DataFrame is empty, the Transformation module's `RegisterTable` may skip registration (Transformation.cs:46 checks `df.Rows.Any()`). V1 produces an empty output DataFrame. Verify V2 behavior: if the SQL fails due to unregistered table, the developer must handle this edge case. If it succeeds, verify empty output with correct column schema.
- **Account with only Debits**: `credit_count = 0`, `debit_credit_ratio = 0` (zero guard), `credit_amount = 0.0`, `amount_ratio = 0.0`. Verify these defaults.
- **Account with only Credits**: `debit_count = 0`, `debit_credit_ratio = 0` (integer division 0/N = 0), `debit_amount = 0.0`, `amount_ratio = 0.0`. Verify these defaults.
- **Account in transactions but not in accounts (BR-7)**: `customer_id` defaults to 0 via `COALESCE(a.customer_id, 0)`. Verify the account's transactions are still included in the output.
- **Non-Debit/non-Credit txn_type (BR-10)**: Rows with other txn_types still contribute an account entry (via GROUP BY) but add 0 to all counts and 0.0 to all amounts. Verify the account appears in output with zeroed aggregates (unless it also has Debit/Credit transactions). In practice, database only contains "Debit" and "Credit" types.
- **Multiple as_of dates for one account**: When running across a date range, a single account may have transactions on multiple dates. `MIN(as_of)` should return the earliest date, matching V1's "first encountered" behavior since DataSourcing orders by as_of.
- **Accounts table deduplication**: If multiple snapshots of the same account exist (multiple as_of dates in the range), the SQL subquery `SELECT account_id, customer_id FROM accounts GROUP BY account_id` deduplicates. Since `customer_id` is functionally dependent on `account_id`, the result is deterministic. Verify no duplicate account_id entries in the join.

### TC-7: Proofmark Configuration
- **Expected proofmark settings from FSD Section 8:**
  ```yaml
  comparison_target: "debit_credit_ratio"
  reader: parquet
  threshold: 100.0
  ```
- **Threshold**: 100.0 (exact match required -- start strict)
- **Excluded columns**: None
- **Fuzzy columns**: None initially. Start strict per FSD recommendation.
- **Contingency for W6 columns**: If Proofmark fails on `debit_amount`, `credit_amount`, or `amount_ratio` due to double-precision epsilon divergence between C# loop accumulation and SQLite SUM(), add fuzzy overrides:
  ```yaml
  columns:
    fuzzy:
      - name: "debit_amount"
        tolerance: 0.0000000001
        tolerance_type: absolute
        reason: "W6: Double-precision accumulation order may differ between C# loop and SQLite SUM()"
      - name: "credit_amount"
        tolerance: 0.0000000001
        tolerance_type: absolute
        reason: "W6: Double-precision accumulation order may differ between C# loop and SQLite SUM()"
      - name: "amount_ratio"
        tolerance: 0.0000000001
        tolerance_type: absolute
        reason: "W6: Double-precision division on potentially epsilon-divergent numerator/denominator"
  ```
- **Row ordering**: Parquet comparison must be row-order-independent. V1 row order is non-deterministic (BR-11). Verify Proofmark handles this for Parquet files.

## W-Code Test Cases

### TC-W1: W4 -- Integer Division Truncation
- **What the wrinkle is**: V1 computes `debit_credit_ratio` as `debitCount / creditCount` using C# integer division, which truncates toward zero. For example, an account with 3 debits and 5 credits gets `3 / 5 = 0`, not `0.6`.
- **How V2 handles it**: SQLite natively performs integer division when both operands are integers. The SQL expression `SUM(CASE WHEN ... THEN 1 ELSE 0 END) / SUM(CASE WHEN ... THEN 1 ELSE 0 END)` produces integer operands, so division truncates identically to C#'s `int / int`.
- **What to verify**:
  1. Find accounts where `debit_count < credit_count` -- verify `debit_credit_ratio = 0`
  2. Find accounts where `debit_count >= credit_count` and `credit_count > 0` -- verify `debit_credit_ratio = debit_count / credit_count` (truncated integer division)
  3. Find accounts where `credit_count = 0` -- verify `debit_credit_ratio = 0` (zero guard)
  4. Compare all `debit_credit_ratio` values between V1 and V2 -- must be identical

### TC-W2: W6 -- Double-Precision Epsilon
- **What the wrinkle is**: V1 accumulates monetary amounts using `double` (not `decimal`), causing IEEE 754 floating-point representation errors. `Convert.ToDouble(row["amount"])` followed by `+=` in a loop. Division `debitAmount / creditAmount` is also double-precision.
- **How V2 handles it**: SQLite stores amounts as REAL (IEEE 754 double). `SUM()` accumulates in double precision. The `ELSE 0.0` literal keeps the CASE expression in the REAL domain. Division is also double-precision.
- **What to verify**:
  1. Compare `debit_amount` between V1 and V2 for all accounts -- values should be identical or within epsilon (1e-10)
  2. Compare `credit_amount` between V1 and V2 for all accounts -- same criterion
  3. Compare `amount_ratio` between V1 and V2 for all accounts -- same criterion
  4. **Key risk**: IEEE 754 addition is not associative. V1 accumulates left-to-right in row iteration order. SQLite's `SUM()` may accumulate in a different order or use compensated summation. If epsilon divergence is detected, apply the fuzzy Proofmark config from TC-7 contingency.
  5. Verify that the V2 conversion path (PostgreSQL decimal -> C# decimal -> SQLite REAL -> SUM) produces equivalent results to V1's path (PostgreSQL decimal -> C# decimal -> Convert.ToDouble -> double += loop)

## Notes
- This job migrates from Tier 3 (External module) to Tier 1 (Framework only). This is the most significant architectural change -- the entire `DebitCreditRatioCalculator.cs` External module is replaced by a single SQL query. Extra care is warranted during comparison.
- The FSD notes a known limitation for empty input (BR-9): if `transactions` is empty, the SQLite table may not be registered, causing the SQL to fail. V1 returns an empty DataFrame in this case. The developer should verify this edge case and handle it if necessary. In practice, every effective date in the data range has transactions, so this may never be exercised.
- Row ordering is non-deterministic in both V1 and V2 (BR-11). Proofmark must use row-order-independent comparison for Parquet files. Do not sort the V2 SQL output to "fix" ordering -- this could mask other issues.
- The accounts subquery `GROUP BY account_id` deduplicates multi-snapshot account records. Since customer_id is functionally dependent on account_id in the source data, the arbitrary row selection per group produces the correct result. If this assumption is violated (different customer_ids for the same account_id), results could diverge from V1.
- The `ELSE 0.0` in CASE expressions is critical for W6 replication -- it keeps SQLite in the REAL domain rather than falling back to integer arithmetic. Verify these literals are present in the V2 SQL.
