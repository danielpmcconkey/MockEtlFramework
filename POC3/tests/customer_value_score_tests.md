# CustomerValueScore — V2 Test Plan

## Job Info
- **V2 Config**: `customer_value_score_v2.json`
- **Tier**: Tier 1 — Framework Only (DataSourcing -> Transformation -> CsvFileWriter)
- **External Module**: None (V1 External `ExternalModules.CustomerValueCalculator` replaced with SQL Transformation)

## Pre-Conditions
- Data sources needed:
  - `datalake.customers` — columns: `id`, `first_name`, `last_name`, `as_of`
  - `datalake.accounts` — columns: `account_id`, `customer_id`, `current_balance`, `as_of`
  - `datalake.transactions` — columns: `account_id`, `as_of` (V2 drops unused `transaction_id`, `txn_type`, `amount` per AP4)
  - `datalake.branch_visits` — columns: `customer_id`, `as_of` (V2 drops unused `visit_id`, `branch_id` per AP4)
- Effective date range: `firstEffectiveDate` = 2024-10-01; framework injects `__minEffectiveDate` / `__maxEffectiveDate` per execution date
- All four tables must contain data for the effective date (empty customers or accounts is an edge case — see TC-6a)

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | Output Schema  | Output contains exactly 8 columns in correct order |
| TC-02   | All BRs        | V1 vs V2 row count equivalence |
| TC-03   | All BRs        | V1 vs V2 data content byte-identical |
| TC-04   | Writer Config  | CSV writer settings match V1 |
| TC-05   | AP3, AP4, AP6, AP7 | Anti-pattern elimination verification |
| TC-06a  | BR-1           | Empty input behavior (customers or accounts empty) |
| TC-06b  | BR-10          | Orphan transaction handling |
| TC-06c  | BR-8, BR-9     | Customers with no transactions or visits |
| TC-06d  | Edge Case      | Negative account balance produces negative balance_score |
| TC-06e  | Edge Case      | Score ceiling behavior at 1000 |
| TC-07   | Proofmark      | Proofmark config validation |
| TC-W1   | W5             | Banker's rounding in score computation |
| TC-W2   | W9             | Overwrite mode reproduced |

## Test Cases

### TC-01: Output Schema Validation
- **Traces to:** FSD Section 4 (Output Schema)
- **Expected columns (exact order):**
  1. `customer_id` — INTEGER
  2. `first_name` — TEXT
  3. `last_name` — TEXT
  4. `transaction_score` — REAL
  5. `balance_score` — REAL
  6. `visit_score` — REAL
  7. `composite_score` — REAL
  8. `as_of` — TEXT
- **Verification method:** Parse the header row of the V2 output CSV. Confirm it is exactly: `customer_id,first_name,last_name,transaction_score,balance_score,visit_score,composite_score,as_of`. Count delimiters to verify 7 commas (8 fields). Confirm column order matches.

### TC-02: Row Count Equivalence
- **Traces to:** BR-12 (customer-driven iteration — every customer produces a row)
- **Input conditions:** Run both V1 and V2 for the same effective date (e.g., 2024-10-15).
- **Expected output:** V1 and V2 produce identical row counts. Since iteration is customer-driven (BR-12), the row count must equal the number of distinct customer records returned by DataSourcing for that effective date.
- **Verification method:** Count data rows (excluding header) in both `Output/curated/customer_value_score.csv` and `Output/double_secret_curated/customer_value_score.csv`. The counts must be identical.

### TC-03: Data Content Equivalence
- **Traces to:** All business rules (BR-2 through BR-12)
- **Input conditions:** Both V1 and V2 executed for the same effective date range.
- **Expected output:** All values must be byte-identical between V1 and V2 output. Every row, every column.
- **W-codes affecting comparison:**
  - **W5 (Banker's rounding):** Both C# `Math.Round` and SQLite `ROUND()` use banker's rounding (MidpointRounding.ToEven). No discrepancy expected. See TC-W1.
- **Verification method:** Byte-level diff of V1 and V2 output files. If row ordering differs (unlikely since both use ORDER BY c.id / customer-driven iteration), sort both files before comparison.

### TC-04: Writer Configuration
- **Traces to:** FSD Section 7 (Writer Configuration)
- **Verify the following settings match V1:**
  - `includeHeader`: `true` — first line of CSV is a header row
  - `writeMode`: `Overwrite` — each execution replaces the entire file
  - `lineEnding`: `LF` — lines end with `\n` only, not `\r\n`
  - `trailerFormat`: not configured — no trailer row in output
  - `outputFile`: `Output/double_secret_curated/customer_value_score.csv` (V2 directory; filename matches V1)
- **Verification method:**
  - Read the first line; confirm it contains column names, not data.
  - Read raw bytes; confirm all line endings are `\n` without preceding `\r`.
  - Confirm the last line is a data row (no trailer).
  - Execute twice with different dates; confirm only the second execution's data remains (Overwrite behavior).

### TC-05: Anti-Pattern Elimination Verification

#### TC-05a: AP3 — Unnecessary External Module Eliminated
- **V1 evidence:** Job config references `ExternalModules.CustomerValueCalculator` as an External module.
- **V2 expectation:** No External module in the V2 job config. The module chain is DataSourcing x4 -> Transformation -> CsvFileWriter.
- **Verification method:** Inspect `customer_value_score_v2.json`. Confirm no module with `"type": "External"` exists. Confirm a `"type": "Transformation"` module is present with the SQL logic.

#### TC-05b: AP4 — Unused Columns Eliminated
- **V1 evidence:** `transactions` sourced `["transaction_id", "account_id", "txn_type", "amount"]` — only `account_id` is used (for counting). `branch_visits` sourced `["visit_id", "customer_id", "branch_id"]` — only `customer_id` is used (for counting).
- **V2 expectation:** `transactions` sources only `["account_id"]`. `branch_visits` sources only `["customer_id"]`.
- **Verification method:** Inspect the DataSourcing entries in `customer_value_score_v2.json`. Confirm the columns arrays contain only the required columns.

#### TC-05c: AP6 — Row-by-Row Iteration Eliminated
- **V1 evidence:** `CustomerValueCalculator.cs` uses nested `foreach` loops with dictionaries [lines 34-86].
- **V2 expectation:** All logic expressed in set-based SQL (JOINs, GROUP BY, aggregate functions).
- **Verification method:** Confirm no External module exists. Inspect the Transformation SQL and verify it uses JOIN/GROUP BY patterns, not procedural logic.

#### TC-05d: AP7 — Magic Values Documented
- **V1 evidence:** Literal values `10.0m`, `1000m`, `50.0m`, `0.4m`, `0.35m`, `0.25m` used without named constants or comments.
- **V2 expectation:** All magic values documented in FSD Section 5 (Magic Value Reference) with their business meaning. SQL cannot use named constants, so documentation is the V2 resolution.
- **Verification method:** Confirm FSD Section 5 documents each magic value. This is a partial elimination — accepted since SQL lacks named constant support.

### TC-06: Edge Cases

#### TC-06a: Empty Input Behavior
- **Traces to:** BR-1 (both customers and accounts must be non-empty)
- **V1 behavior:** If either `customers` or `accounts` is null/empty, V1 returns an empty DataFrame -> empty CSV (header only).
- **V2 behavior:** If `customers` is empty, the Transformation module's `RegisterTable` skips registration, and the SQL fails because the `customers` table doesn't exist in SQLite. This would produce an error, not an empty CSV.
- **Risk level:** LOW — the datalake contains customer and account data for all dates in the comparison range (2024-10-01 through 2024-12-31). This edge case does not affect Proofmark comparison.
- **Verification method:** Document as a known Tier 1 limitation. If this edge case surfaces during auto-advance, escalate to Tier 2 with a minimal External module for the empty-input guard.

#### TC-06b: Orphan Transaction Handling
- **Traces to:** BR-10 (transactions with account_id not in accounts are silently skipped)
- **V1 behavior:** `if (customerId == 0) continue;` — orphan transactions excluded from counts.
- **V2 behavior:** INNER JOIN between `transactions` and `accounts` in the `tc` subquery naturally excludes orphan transactions.
- **Verification method:** If datalake contains transactions with `account_id` values not present in `accounts`, confirm those transactions do not inflate any customer's `transaction_score`. Compare V1 and V2 transaction_score values for all customers.

#### TC-06c: Customers with No Transactions or Visits
- **Traces to:** BR-8 (no transactions -> transaction_score = 0), BR-9 (no visits -> visit_score = 0)
- **V1 behavior:** `GetValueOrDefault(customerId, 0)` returns 0 for missing keys.
- **V2 behavior:** LEFT JOIN produces NULL for unmatched customers; `COALESCE(..., 0)` converts to 0.
- **Verification method:** Identify customers in the datalake with no transactions and no branch visits. Confirm their V2 output shows `transaction_score = 0`, `visit_score = 0`, `balance_score` computed from accounts only, and `composite_score` reflects the weighted sum correctly.

#### TC-06d: Negative Account Balance
- **Traces to:** BRD Edge Case, OQ-1
- **V1 behavior:** `Math.Min(totalBalance / 1000.0m, 1000m)` — no floor at 0. Negative balances produce negative `balance_score`.
- **V2 behavior:** `MIN(COALESCE(ab.total_balance, 0.0) / 1000.0, 1000.0)` — same behavior, no floor.
- **Verification method:** If any customer has a negative total balance across accounts, confirm `balance_score` is negative in both V1 and V2 output. Confirm `composite_score` is reduced accordingly.

#### TC-06e: Score Ceiling Behavior
- **Traces to:** BR-3 (transaction_score capped at 1000), BR-4 (balance_score capped at 1000), BR-5 (visit_score capped at 1000)
- **Input conditions:** A customer with >100 transactions (score = 100 * 10 = 1000+), or balance > $1,000,000 (score = 1,000,000 / 1000 = 1000+), or >20 visits (score = 20 * 50 = 1000+).
- **Expected output:** Individual component scores capped at 1000.00. `composite_score` max = 0.4*1000 + 0.35*1000 + 0.25*1000 = 1000.00.
- **Verification method:** Identify customers who hit the ceiling in V1 output. Confirm V2 produces identical capped values.

### TC-07: Proofmark Configuration
- **Traces to:** FSD Section 8 (Proofmark Config Design)
- **Expected Proofmark settings:**
  ```yaml
  comparison_target: "customer_value_score"
  reader: csv
  threshold: 100.0
  csv:
    header_rows: 1
    trailer_rows: 0
  ```
- **Threshold:** 100.0 — full match required. No known sources of non-determinism.
- **Excluded columns:** None — all columns are deterministic.
- **Fuzzy columns:** None initially. If precision mismatches emerge between C# `decimal` and SQLite `REAL` (IEEE 754 double), add fuzzy tolerance for the four score columns with evidence from actual discrepancies.
- **Verification method:** Run Proofmark with the above config. Expect 100% match. If it fails, inspect diff output to determine if fuzzy tolerance is needed on score columns.

## W-Code Test Cases

### TC-W1: W5 — Banker's Rounding
- **What the wrinkle is:** C# `Math.Round` defaults to `MidpointRounding.ToEven` (banker's rounding). When a value is exactly at the midpoint (e.g., 2.345), it rounds to the nearest even digit (2.34, not 2.35).
- **How V2 handles it:** SQLite's `ROUND()` function also uses banker's rounding, naturally matching C#'s behavior. No special handling needed.
- **What to verify:**
  - Confirm SQLite `ROUND(..., 2)` produces the same results as C# `Math.Round(..., 2)` for all score values in the comparison date range.
  - If any score value falls exactly on a midpoint (e.g., x.xx5), confirm both V1 and V2 round to the same value.
- **Verification method:** Compare all `transaction_score`, `balance_score`, `visit_score`, and `composite_score` values between V1 and V2 output. Any rounding discrepancy indicates a W5 issue.

### TC-W2: W9 — Overwrite Mode Reproduced
- **What the wrinkle is:** V1 uses `writeMode: "Overwrite"` which replaces the entire file on each execution. For auto-advance runs across multiple days, only the last effective date's output survives on disk.
- **How V2 handles it:** V2 config specifies `"writeMode": "Overwrite"` to match V1 behavior exactly. The FSD documents this as an intentional reproduction of V1's behavior.
- **What to verify:**
  - After a multi-day auto-advance run, the output file contains only the last effective date's data.
  - The `as_of` column in all rows should correspond to the final effective date in the run.
- **Verification method:** Execute the V2 job for a date range (e.g., 2024-10-01 through 2024-10-03). After completion, inspect the output file. Confirm all `as_of` values equal the last date (2024-10-03) and no data from prior dates remains.

## Notes
- **BRD Correction (Resolved):** The BRD originally stated scores were "rounded to the nearest whole number (0 decimal places)." This was corrected to 2 decimal places based on V1 source code evidence (`Math.Round(..., 2)` at lines 114-117 of CustomerValueCalculator.cs). V2 uses `ROUND(..., 2)` to match.
- **Composite score computation order:** The composite score is computed from un-rounded individual score values, then the result is rounded to 2 decimal places. The SQL replicates this by using the raw `MIN(...)` expressions (before `ROUND`) in the composite calculation. This is critical for output equivalence — rounding individual scores first and then computing the composite would produce different results.
- **Decimal vs double precision risk:** V1 uses C# `decimal` (128-bit) while V2 uses SQLite REAL (64-bit IEEE 754 double). For the score formulas in this job (integer counts * integer-valued multipliers, balance / 1000.0), the values should be exactly representable in both types. `ROUND(..., 2)` eliminates trailing precision noise. Start with strict comparison; escalate to fuzzy only if actual mismatches are observed.
- **OQ-1 (Negative balances):** Whether a floor of 0 should be applied to balance_score is an open question. V1 does not floor, so V2 reproduces the same behavior. This is not a V2 defect.
