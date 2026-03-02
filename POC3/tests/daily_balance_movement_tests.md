# DailyBalanceMovement — V2 Test Plan

## Job Info
- **V2 Config**: `daily_balance_movement_v2.json`
- **Tier**: Tier 1 — Framework Only (DataSourcing -> Transformation -> CsvFileWriter), with documented risk of Tier 2 escalation for empty-accounts edge case
- **External Module**: None (V1 External `ExternalModules.DailyBalanceMovementCalculator` replaced with SQL Transformation)

## Pre-Conditions
- Data sources needed:
  - `datalake.transactions` — columns: `account_id`, `txn_type`, `amount`, `as_of` (V2 drops unused `transaction_id` per AP4)
  - `datalake.accounts` — columns: `account_id`, `customer_id`, `as_of`
- Effective date range: `firstEffectiveDate` = 2024-10-01; framework injects `__minEffectiveDate` / `__maxEffectiveDate` per execution date
- Transactions table must contain data for the effective date (empty transactions produces zero-row output)
- Accounts table is weekday-only per BRD — may be empty on weekends (see TC-06b)

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | Output Schema  | Output contains exactly 6 columns in correct order |
| TC-02   | All BRs        | V1 vs V2 row count equivalence |
| TC-03   | BR-1 through BR-5, BR-8 | V1 vs V2 data content equivalence |
| TC-04   | Writer Config  | CSV writer settings match V1 |
| TC-05   | AP3, AP4, AP6  | Anti-pattern elimination verification |
| TC-06a  | BR-6           | Empty transactions input -> empty output |
| TC-06b  | BR-6           | Empty accounts input (weekend scenario) |
| TC-06c  | Edge Case      | Unknown txn_type handling |
| TC-06d  | Edge Case      | Output row ordering |
| TC-06e  | BR-8           | No rounding applied to monetary values |
| TC-07   | Proofmark      | Proofmark config validation |
| TC-W1   | W6             | Double arithmetic epsilon errors reproduced |
| TC-W2   | W9             | Overwrite mode reproduced |

## Test Cases

### TC-01: Output Schema Validation
- **Traces to:** FSD Section 4 (Output Schema)
- **Expected columns (exact order):**
  1. `account_id` — INTEGER
  2. `customer_id` — INTEGER
  3. `debit_total` — REAL (double-precision)
  4. `credit_total` — REAL (double-precision)
  5. `net_movement` — REAL (double-precision)
  6. `as_of` — TEXT
- **Verification method:** Parse the header row of the V2 output CSV. Confirm it is exactly: `account_id,customer_id,debit_total,credit_total,net_movement,as_of`. Count delimiters to verify 5 commas (6 fields). Confirm column order matches.

### TC-02: Row Count Equivalence
- **Traces to:** BR-1 (aggregation per account_id)
- **Input conditions:** Run both V1 and V2 for the same effective date (e.g., a weekday like 2024-10-15).
- **Expected output:** V1 and V2 produce identical row counts. The row count should equal the number of distinct `account_id` values in the transactions table for that effective date.
- **Verification method:** Count data rows (excluding header) in both `Output/curated/daily_balance_movement.csv` and `Output/double_secret_curated/daily_balance_movement.csv`. The counts must be identical.

### TC-03: Data Content Equivalence
- **Traces to:** BR-1 through BR-5, BR-8
- **Input conditions:** Both V1 and V2 executed for the same effective date range (weekday dates only, to avoid the empty-accounts edge case).
- **Expected output:** All values must match between V1 and V2 output.
- **W-codes affecting comparison:**
  - **W6 (Double epsilon):** Both V1 (C# `double` via `Convert.ToDouble`) and V2 (SQLite REAL = IEEE 754 double) use double-precision arithmetic. Accumulation order may differ between row-by-row iteration and SQL `SUM()`, potentially causing epsilon-level differences in `debit_total`, `credit_total`, and `net_movement`.
- **Verification method:** Compare V1 and V2 output files. If row ordering differs (see TC-06d), sort both files by `account_id` before comparison. If epsilon differences appear in monetary columns, escalate to fuzzy Proofmark comparison per TC-W1.

### TC-04: Writer Configuration
- **Traces to:** FSD Section 7 (Writer Configuration)
- **Verify the following settings match V1:**
  - `includeHeader`: `true` — first line of CSV is a header row
  - `writeMode`: `Overwrite` — each execution replaces the entire file (W9: documented bug, reproduced)
  - `lineEnding`: `LF` — lines end with `\n` only, not `\r\n`
  - `trailerFormat`: not configured — no trailer row in output
  - `outputFile`: `Output/double_secret_curated/daily_balance_movement.csv` (V2 directory; filename matches V1)
- **Verification method:**
  - Read the first line; confirm it contains column names, not data.
  - Read raw bytes; confirm all line endings are `\n` without preceding `\r`.
  - Confirm the last line is a data row (no trailer).
  - Execute twice with different dates; confirm only the second execution's data remains (Overwrite behavior).

### TC-05: Anti-Pattern Elimination Verification

#### TC-05a: AP3 — Unnecessary External Module Eliminated
- **V1 evidence:** Job config references `ExternalModules.DailyBalanceMovementCalculator` as an External module.
- **V2 expectation:** No External module in the V2 job config. The module chain is DataSourcing x2 -> Transformation -> CsvFileWriter.
- **Verification method:** Inspect `daily_balance_movement_v2.json`. Confirm no module with `"type": "External"` exists. Confirm a `"type": "Transformation"` module is present with the SQL aggregation logic.

#### TC-05b: AP4 — Unused Columns Eliminated
- **V1 evidence:** `transactions` sourced `["transaction_id", "account_id", "txn_type", "amount"]` — `transaction_id` is never used in the output or any computation.
- **V2 expectation:** `transactions` sources only `["account_id", "txn_type", "amount"]`. The `transaction_id` column is removed.
- **Verification method:** Inspect the DataSourcing entry for transactions in `daily_balance_movement_v2.json`. Confirm `transaction_id` is not in the columns array.

#### TC-05c: AP6 — Row-by-Row Iteration Eliminated
- **V1 evidence:** `DailyBalanceMovementCalculator.cs` uses nested `foreach` loops with manual dictionary accumulation [lines 34-49].
- **V2 expectation:** All logic expressed in set-based SQL using `GROUP BY t.account_id` with `SUM(CASE WHEN ...)` for conditional aggregation.
- **Verification method:** Confirm no External module exists. Inspect the Transformation SQL and verify it uses GROUP BY with SUM(CASE...) patterns.

### TC-06: Edge Cases

#### TC-06a: Empty Transactions Input
- **Traces to:** BR-6 (empty input -> empty output)
- **V1 behavior:** If `transactions` is null or empty, V1 returns an empty DataFrame -> header-only CSV.
- **V2 behavior:** If transactions is empty, the Transformation module's `RegisterTable` skips creating the `transactions` table. The SQL `FROM transactions t` fails with "no such table." However, since the BRD states "Transactions exist every day," this scenario should not occur within the comparison date range.
- **Risk level:** LOW — transactions data exists for all dates per BRD.
- **Verification method:** Document as a known edge case. If encountered, the job will error (not produce empty output). This differs from V1 behavior but does not affect Proofmark comparison.

#### TC-06b: Empty Accounts Input (Weekend Scenario)
- **Traces to:** BR-6, BRD Edge Case ("Accounts are weekday-only")
- **V1 behavior:** If accounts is empty, V1's guard clause returns an empty DataFrame -> header-only CSV.
- **V2 behavior:** If accounts is empty, `RegisterTable` skips creating the `accounts` SQLite table. The SQL references `accounts` in the LEFT JOIN, causing a "no such table: accounts" error. This is a behavioral difference from V1.
- **Risk level:** MEDIUM — this will occur on weekend effective dates. However, the comparison date range may not include weekend-only runs depending on auto-advance behavior.
- **Mitigation:** If Proofmark comparison is run only for weekday dates, this edge case is avoided. If weekend dates are included:
  - **Option A:** Escalate to Tier 2 with a minimal External module for the empty-input guard.
  - **Option B:** Restructure as two Transformation steps (aggregate transactions first, then conditionally join accounts).
- **Verification method:** Run V2 for a weekend date where accounts has no data. Confirm the job fails with a SQLite error. Compare against V1 which produces a header-only CSV. If mismatch, implement the Tier 2 escalation.

#### TC-06c: Unknown Transaction Type
- **Traces to:** BRD Edge Case ("Unknown txn_type")
- **V1 behavior:** Transactions with `txn_type` other than "Debit" or "Credit" are neither summed to debit nor credit totals, but the account_id still creates a group entry with zeros.
- **V2 behavior:** `SUM(CASE WHEN txn_type = 'Debit' THEN ... ELSE 0.0 END)` — transactions with unknown types contribute 0 to both sums. The account_id is still included in the GROUP BY output.
- **Verification method:** If unknown txn_type values exist in the datalake, confirm those accounts appear in the output with correct debit_total and credit_total (excluding the unknown-type amounts). Confirm the behavior matches V1.

#### TC-06d: Output Row Ordering
- **Traces to:** BRD Edge Case ("No explicit ordering on output rows"), FSD Section 4
- **V1 behavior:** No explicit ORDER BY. Dictionary iteration order is insertion order, determined by the order transactions arrive from DataSourcing (which orders by `as_of`).
- **V2 behavior:** SQL `GROUP BY t.account_id` without ORDER BY. SQLite may produce rows in a different order than V1's Dictionary iteration.
- **Impact on Proofmark:** If Proofmark comparison is order-sensitive, row order differences will cause false failures.
- **Verification method:** Compare V1 and V2 output. If row order differs but content matches, confirm Proofmark handles this correctly (order-insensitive comparison). If Proofmark is order-sensitive and fails, add `ORDER BY account_id` to the V2 SQL to force a deterministic order, then verify V1 also happens to produce the same order.

#### TC-06e: No Rounding Applied
- **Traces to:** BR-8 (no explicit rounding)
- **V1 behavior:** `Convert.ToDouble` amounts are accumulated; no `Math.Round` applied to outputs. Raw double values flow through.
- **V2 behavior:** `CAST(t.amount AS REAL)` with `SUM()` — no `ROUND()` in the SQL. Raw REAL values flow through.
- **Verification method:** Confirm the V2 Transformation SQL contains no `ROUND()` calls. Inspect output values and confirm they show full double precision (not truncated to N decimal places).

### TC-07: Proofmark Configuration
- **Traces to:** FSD Section 8 (Proofmark Config Design)
- **Expected Proofmark settings:**
  ```yaml
  comparison_target: "daily_balance_movement"
  reader: csv
  threshold: 100.0
  csv:
    header_rows: 1
    trailer_rows: 0
  ```
- **Threshold:** 100.0 — full match required. Start strict.
- **Excluded columns:** None — all columns are deterministic.
- **Fuzzy columns:** None initially. If W6 epsilon differences manifest, escalate to fuzzy comparison on `debit_total`, `credit_total`, and `net_movement` with absolute tolerance of 0.0001.
- **Potential fuzzy escalation config (if needed):**
  ```yaml
  columns:
    fuzzy:
      - name: "debit_total"
        tolerance: 0.0001
        tolerance_type: absolute
        reason: "W6: V1 double loop accumulation vs V2 SQLite SUM on REAL. Accumulation order may differ at epsilon level."
      - name: "credit_total"
        tolerance: 0.0001
        tolerance_type: absolute
        reason: "W6: Same as debit_total."
      - name: "net_movement"
        tolerance: 0.0001
        tolerance_type: absolute
        reason: "W6: Derived from debit_total and credit_total, both subject to double epsilon."
  ```
- **Verification method:** Run Proofmark with the strict config first. If it passes at 100%, no fuzzy columns needed. If monetary columns show epsilon-level discrepancies, apply the fuzzy escalation config and re-run.

## W-Code Test Cases

### TC-W1: W6 — Double Arithmetic Epsilon Errors
- **What the wrinkle is:** V1 uses `double` (IEEE 754 64-bit) instead of `decimal` for monetary accumulation. `Convert.ToDouble(amount)` converts each transaction amount to double, then accumulates via `+=` in a loop. This introduces floating-point epsilon errors (e.g., 1234.56 may become 1234.5600000000002).
- **How V2 handles it:** SQLite REAL is also IEEE 754 64-bit double-precision. `CAST(t.amount AS REAL)` forces SQLite to use double for the `SUM()` accumulation, replicating V1's arithmetic type. The FSD documents this with an inline SQL comment.
- **What to verify:**
  - Both V1 and V2 use the same floating-point type (double / REAL) for monetary accumulation.
  - Output values for `debit_total`, `credit_total`, and `net_movement` match between V1 and V2.
  - If accumulation order differs (row-by-row vs SQL SUM), epsilon-level differences may appear. These are acceptable if within 0.0001 tolerance.
- **Verification method:** Compare monetary columns between V1 and V2 output at full precision. If exact match: pass. If differences exist, verify they are at the epsilon level (< 0.0001) and escalate Proofmark to fuzzy comparison. If differences exceed epsilon, investigate the SQL or data path for bugs.

### TC-W2: W9 — Overwrite Mode Reproduced
- **What the wrinkle is:** V1 uses `writeMode: "Overwrite"` for daily data. The code comment explicitly flags this as wrong — the intent was likely Append mode to build a daily history. Each execution replaces the entire file, losing all prior days' data.
- **How V2 handles it:** V2 reproduces `"writeMode": "Overwrite"` exactly, matching V1's (buggy) behavior. The FSD documents this as intentional replication.
- **What to verify:**
  - After a multi-day auto-advance run, the output file contains only the last effective date's data.
  - Prior days' data is NOT present in the output file.
  - The `as_of` column values all correspond to the final effective date.
- **Verification method:** Execute the V2 job for a date range (e.g., 2024-10-01 through 2024-10-03). After completion, inspect the output file. Confirm all `as_of` values equal the last date in the range. Confirm file does not contain data from earlier dates.

## Notes
- **Tier escalation risk:** The empty-accounts-on-weekends scenario (TC-06b) is the primary risk for this Tier 1 design. If the comparison date range includes weekend dates where accounts has no data, the V2 job will error while V1 produces an empty CSV. Resolution options are documented in TC-06b. For weekday-only comparison, this is a non-issue.
- **Row ordering risk:** V1's Dictionary iteration order is technically non-deterministic from a specification standpoint, though in practice it follows insertion order (DataSourcing order by as_of). V2's SQL GROUP BY without ORDER BY may produce a different row order. If Proofmark is order-sensitive, this needs to be addressed either by adding ORDER BY to the SQL or by using an order-insensitive comparison mode.
- **No rounding:** Unlike most other jobs in this framework, DailyBalanceMovement applies no rounding to its output values. The raw double-precision values flow through to CSV. This means any epsilon differences from W6 will be visible in the output rather than being masked by ROUND().
- **BRD open questions:**
  - OQ-1: Double arithmetic (W6) is a known bug per code comment. V2 reproduces it for output equivalence. Not a V2 defect.
  - OQ-2: Overwrite mode (W9) is a known bug per code comment. V2 reproduces it for output equivalence. Not a V2 defect.
