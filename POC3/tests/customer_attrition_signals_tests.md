# CustomerAttritionSignals -- Test Plan

## Overview

This test plan validates the V2 implementation of `CustomerAttritionSignalsV2`, a Tier 2 (Framework + Minimal External) job that produces a per-customer attrition risk scorecard. The job sources customers, accounts, and transactions tables, performs SQL aggregation via a Transformation module, then uses a minimal External module (`CustomerAttritionSignalsV2Processor`) for type-sensitive scoring (decimal avg_balance with banker's rounding, double attrition_score, DateOnly as_of injection). Output is Parquet with 1 part file in Overwrite mode.

**Source Documents:**
- BRD: `POC3/brd/customer_attrition_signals_brd.md`
- FSD: `POC3/fsd/customer_attrition_signals_fsd.md`

**Critical Note -- BRD Weight Discrepancy:** The BRD states dormancy weight=35 and declining txn weight=40. The FSD identifies that the V1 source code uses dormancy weight=**40** and declining txn weight=**35** (swapped). All test cases use the **source code weights** (dormancy=40, declining=35) per the FSD, which is what matters for output equivalence.

---

## Traceability Matrix

| Test ID | BRD Requirement | FSD Section | Description |
|---------|----------------|-------------|-------------|
| TC-CAS-001 | BR-1 | SQL Design | Account count per customer includes all statuses |
| TC-CAS-002 | BR-2 | External Module Design | avg_balance = total_balance / account_count, 0 if no accounts |
| TC-CAS-003 | BR-3 | SQL Design | Transaction count via account join, unknown account_id dropped |
| TC-CAS-004 | BR-4 | External Module Design | Attrition score is weighted sum of 3 binary factors |
| TC-CAS-005 | BR-5 | External Module Design | Risk level classification thresholds |
| TC-CAS-006 | BR-6 | External Module Design | as_of from __maxEffectiveDate (not per-row) |
| TC-CAS-007 | BR-7 | External Module Design | Empty output guard for null/empty customers |
| TC-CAS-008 | BR-8 / W6 | External Module Design | Attrition score uses double arithmetic |
| TC-CAS-009 | BR-9 / W5 | External Module Design | avg_balance rounded to 2dp with banker's rounding |
| TC-CAS-010 | BR-10 | SQL Design | NULL names coalesced to empty string |
| TC-CAS-011 | AP4 | V2 Module Chain | amount column removed from transactions DataSourcing |
| TC-CAS-012 | AP6 | V2 Module Chain | Row-by-row iteration replaced with SQL aggregation |
| TC-CAS-013 | AP7 | External Module Design | Magic values replaced with named constants |
| TC-CAS-014 | -- | Output Schema | Output schema has exactly 9 columns in correct order and types |
| TC-CAS-015 | -- | Writer Configuration | Output is Parquet, 1 part, Overwrite mode |
| TC-CAS-016 | -- | Edge Cases | Customer with zero accounts |
| TC-CAS-017 | -- | Edge Cases | Customer with accounts but no transactions |
| TC-CAS-018 | -- | Edge Cases | Transaction with unknown account_id |
| TC-CAS-019 | -- | Edge Cases | Zero-row input (no customers) |
| TC-CAS-020 | -- | Edge Cases | Boundary: avg_balance exactly $100 |
| TC-CAS-021 | -- | Edge Cases | Boundary: txn_count exactly 3 |
| TC-CAS-022 | -- | Edge Cases | Boundary: attrition_score exactly 75.0 and 40.0 |
| TC-CAS-023 | -- | Edge Cases | Weekend effective dates |
| TC-CAS-024 | -- | Edge Cases | NULL balance values in source |
| TC-CAS-025 | -- | Proofmark | V2 output matches V1 output at 100% threshold |
| TC-CAS-026 | -- | V2 Job Config | Output directory and firstEffectiveDate |
| TC-CAS-027 | -- | Edge Cases | Banker's rounding at .XX5 boundary |
| TC-CAS-028 | -- | Writer Configuration | Overwrite mode: only last date's output survives |
| TC-CAS-029 | FSD Weight Fix | External Module Design | Dormancy weight=40, declining txn weight=35 (code, not BRD text) |

---

## Test Cases

### TC-CAS-001: Account Count Includes All Statuses

**Traces to:** BR-1
**Priority:** HIGH

**Objective:** Verify that account_count per customer counts ALL account rows regardless of account_status (Active, Closed, Dormant, etc.).

**Preconditions:**
- `datalake.accounts` contains rows with varying `account_status` values for the same `customer_id`.

**Steps:**
1. Query the source to find a customer with multiple account statuses:
   ```sql
   SELECT customer_id, account_status, COUNT(*)
   FROM datalake.accounts
   WHERE as_of = '2024-10-01'
   GROUP BY customer_id, account_status
   HAVING COUNT(DISTINCT account_status) > 1
   LIMIT 5;
   ```
2. Run CustomerAttritionSignalsV2 for effective date 2024-10-01.
3. Read the output Parquet file.
4. For the identified customer, verify that `account_count` equals the total number of account rows (all statuses combined).

**Expected Result:**
- `account_count` includes Active, Closed, and any other status accounts -- no status filter is applied.

---

### TC-CAS-002: Average Balance Calculation

**Traces to:** BR-2
**Priority:** HIGH

**Objective:** Verify that `avg_balance = total_balance / account_count`, or 0 if account_count is 0.

**Preconditions:**
- Source data contains customers with varying numbers of accounts and balances.

**Steps:**
1. Identify a customer with known accounts and balances from the source.
2. Manually calculate: sum all `current_balance` values for the customer's accounts, divide by account count.
3. Run CustomerAttritionSignalsV2.
4. Compare the output `avg_balance` value to the manual calculation.

**Expected Result:**
- `avg_balance` = SUM(current_balance) / COUNT(accounts), rounded to 2 decimal places using banker's rounding.

**Edge Case:**
- Customer with 0 accounts: `avg_balance` should be exactly 0 (not NULL, not NaN).

---

### TC-CAS-003: Transaction Count via Account Join

**Traces to:** BR-3
**Priority:** HIGH

**Objective:** Verify that `txn_count` is computed by joining transactions to accounts via `account_id`, then aggregating by `customer_id`. Transactions with unknown `account_id` are silently dropped.

**Preconditions:**
- Source data contains transactions that map to known accounts, and potentially transactions with account_ids that don't exist in the accounts table.

**Steps:**
1. For a known customer, count their transactions by:
   ```sql
   SELECT COUNT(*)
   FROM datalake.transactions t
   INNER JOIN datalake.accounts a ON t.account_id = a.account_id AND t.as_of = a.as_of
   WHERE a.customer_id = {customer_id} AND t.as_of = '2024-10-01';
   ```
2. Run CustomerAttritionSignalsV2 for effective date 2024-10-01.
3. Compare the output `txn_count` for that customer to the manual count.

**Expected Result:**
- `txn_count` matches the count of transactions joined through the accounts table.
- Transactions whose `account_id` does not appear in the accounts table are not counted.

---

### TC-CAS-004: Attrition Score Weighted Calculation

**Traces to:** BR-4 (corrected per FSD)
**Priority:** CRITICAL

**Objective:** Verify the attrition score formula using the **source code weights** (dormancy=40, declining_txn=35, low_balance=25).

**Formula:**
```
dormancy_factor    = (account_count == 0) ? 1.0 : 0.0
declining_txn_factor = (txn_count < 3)    ? 1.0 : 0.0
low_balance_factor = (avg_balance < 100.0) ? 1.0 : 0.0

attrition_score = dormancy_factor * 40.0
                + declining_txn_factor * 35.0
                + low_balance_factor * 25.0
```

**Steps:**
1. Identify customers representing each combination of factors:
   - All 3 factors active (0 accounts, 0 txns, avg_balance=0): score = 40+35+25 = 100.0
   - Only dormancy (0 accounts, but somehow 3+ txns -- impossible since no accounts means no txns, so this is always paired with declining_txn)
   - Only declining_txn (has accounts, <3 txns, avg_balance >= 100): score = 35.0
   - Only low_balance (has accounts, 3+ txns, avg_balance < 100): score = 25.0
   - No factors active (has accounts, 3+ txns, avg_balance >= 100): score = 0.0
   - Dormancy + declining_txn (0 accounts, guaranteed <3 txns): score = 75.0 (this triggers "High" risk)
2. Run CustomerAttritionSignalsV2.
3. For each identified customer, verify the `attrition_score` matches the manual calculation.

**Expected Result:**
- Possible score values: {0.0, 25.0, 35.0, 40.0, 60.0, 65.0, 75.0, 100.0}
- All scores computed using double arithmetic.

---

### TC-CAS-005: Risk Level Classification

**Traces to:** BR-5
**Priority:** HIGH

**Objective:** Verify the risk level classification thresholds.

**Steps:**
1. Run CustomerAttritionSignalsV2.
2. For each output row, verify:
   - `attrition_score >= 75.0` -> `risk_level = "High"`
   - `attrition_score >= 40.0 AND attrition_score < 75.0` -> `risk_level = "Medium"`
   - `attrition_score < 40.0` -> `risk_level = "Low"`

**Expected Result:**
- Every row's `risk_level` is consistent with its `attrition_score`.

**Specific score-to-risk mappings to verify:**
| Score | Risk Level |
|-------|-----------|
| 100.0 | High |
| 75.0 | High |
| 65.0 | Medium |
| 60.0 | Medium |
| 40.0 | Medium |
| 35.0 | Low |
| 25.0 | Low |
| 0.0 | Low |

---

### TC-CAS-006: as_of from __maxEffectiveDate

**Traces to:** BR-6
**Priority:** HIGH

**Objective:** Verify that the `as_of` column in the output is a single constant value equal to `__maxEffectiveDate`, not the per-row `as_of` from source tables.

**Steps:**
1. Run CustomerAttritionSignalsV2 for effective date 2024-10-15.
2. Read the output Parquet file.
3. Check that EVERY row has `as_of = 2024-10-15`.
4. Confirm this is a DateOnly type (not DateTime, not string).

**Expected Result:**
- All rows have identical `as_of` value matching the effective date.
- The value is not from the source data's per-row `as_of` column.

---

### TC-CAS-007: Empty Output Guard

**Traces to:** BR-7
**Priority:** HIGH

**Objective:** Verify that when the pre-scored input is null or empty, the output is an empty DataFrame with the correct 9-column schema.

**Steps:**
1. This test verifies the code path in the External module. If there is an effective date with no customer data (unlikely with real data), the guard triggers.
2. Inspect the V2 External module source code (`ExternalModules/CustomerAttritionSignalsV2Processor.cs`) to verify the empty-input guard:
   - Checks if `pre_scored` is null or has zero rows.
   - Creates a DataFrame with schema: `customer_id, first_name, last_name, account_count, txn_count, avg_balance, attrition_score, risk_level, as_of`.
   - Stores it as `"output"` in shared state.

**Expected Result:**
- Code review confirms the guard clause exists and produces a 9-column empty DataFrame.
- No runtime error when input is empty.

---

### TC-CAS-008: Double Arithmetic for Attrition Score (W6)

**Traces to:** BR-8, W6
**Priority:** HIGH

**Objective:** Verify that the attrition score is computed using `double` (IEEE 754) arithmetic, not `decimal`, to match V1's behavior.

**Steps:**
1. Inspect the V2 External module source code.
2. Confirm that all variables in the attrition score computation (`dormancyFactor`, `decliningTxnFactor`, `lowBalanceFactor`, `attritionScore`, and the weight constants) are declared as `double`.
3. Confirm the output column `attrition_score` is stored as `double` in the DataFrame.

**Expected Result:**
- All score-related variables and constants use `double` type.
- Code comment documents that this replicates V1's `double` arithmetic (W6).

**Note:** For the specific weights used (40.0, 35.0, 25.0) and binary factors (0.0 or 1.0), all values are exactly representable in IEEE 754 double, so epsilon errors are not expected in practice. The possible score values {0.0, 25.0, 35.0, 40.0, 60.0, 65.0, 75.0, 100.0} are all exact.

---

### TC-CAS-009: Banker's Rounding for avg_balance (W5)

**Traces to:** BR-9, W5
**Priority:** HIGH

**Objective:** Verify that `avg_balance` is rounded using `Math.Round(value, 2)` with the default `MidpointRounding.ToEven` (banker's rounding).

**Steps:**
1. Inspect the V2 External module source code.
2. Confirm that `avg_balance` is computed as `decimal` (not `double`).
3. Confirm that `Math.Round(avgBalance, 2)` is called (default banker's rounding).
4. Find or construct a test case where banker's rounding produces a different result than round-half-up:
   - Example: if `total_balance / account_count = 123.445`, banker's rounding produces `123.44` (rounds to even), while round-half-up would produce `123.45`.
   - Example: if `total_balance / account_count = 123.455`, banker's rounding produces `123.46` (rounds to even), while round-half-up would produce `123.46` (same in this case).

**Expected Result:**
- `avg_balance` uses `decimal` type for division.
- `Math.Round` is called with 2 decimal places and default (banker's) rounding.
- Code comment documents W5 replication.

---

### TC-CAS-010: NULL Name Handling

**Traces to:** BR-10
**Priority:** MEDIUM

**Objective:** Verify that NULL `first_name` or `last_name` values are coalesced to empty string `""`.

**Steps:**
1. Query the source to find customers with NULL names:
   ```sql
   SELECT id, first_name, last_name
   FROM datalake.customers
   WHERE (first_name IS NULL OR last_name IS NULL) AND as_of = '2024-10-01';
   ```
2. Run CustomerAttritionSignalsV2 for effective date 2024-10-01.
3. For customers with NULL names in source, verify output has empty string `""` (not NULL).

**Expected Result:**
- NULL first_name -> `""` in output.
- NULL last_name -> `""` in output.
- Non-NULL names pass through unchanged.

**Note:** The FSD places this COALESCE in the SQL Transformation, not the External module: `COALESCE(c.first_name, '') AS first_name`.

---

### TC-CAS-011: amount Column Removed from Transactions (AP4)

**Traces to:** AP4
**Priority:** MEDIUM

**Objective:** Verify that V2 does not source the `amount` column from transactions, which V1 sourced but never used.

**Steps:**
1. Read the V2 job config JSON.
2. Find the DataSourcing module for `transactions`.
3. Confirm the `columns` array is `["transaction_id", "account_id"]` -- no `amount`.

**Expected Result:**
- `amount` is not listed in the transactions DataSourcing columns.

---

### TC-CAS-012: SQL Aggregation Replaces Row-by-Row Iteration (AP6)

**Traces to:** AP6
**Priority:** MEDIUM

**Objective:** Verify that V2 uses a SQL Transformation for aggregation instead of V1's three `foreach` loops with Dictionary lookups.

**Steps:**
1. Read the V2 job config JSON.
2. Confirm a Transformation module exists with `resultName: "pre_scored"`.
3. Confirm the SQL contains:
   - `LEFT JOIN` for account stats (COUNT, SUM).
   - `LEFT JOIN` for transaction count via `INNER JOIN` of transactions to accounts.
   - `GROUP BY` for aggregation.
4. Confirm the External module (`CustomerAttritionSignalsV2Processor`) does NOT contain `foreach` loops for counting accounts or transactions (only for scoring).

**Expected Result:**
- Aggregation (joins, counts, sums) handled in SQL.
- External module only handles scoring logic that requires C# type control.

---

### TC-CAS-013: Named Constants Replace Magic Values (AP7)

**Traces to:** AP7
**Priority:** LOW

**Objective:** Verify that hardcoded threshold values in the External module are replaced with named constants.

**Steps:**
1. Read the V2 External module source code.
2. Confirm the following named constants exist:
   - `DormancyWeight` = 40.0
   - `DecliningTxnWeight` = 35.0
   - `LowBalanceWeight` = 25.0
   - `DecliningTxnThreshold` = 3
   - `LowBalanceThreshold` = 100.0
   - `HighRiskThreshold` = 75.0
   - `MediumRiskThreshold` = 40.0
3. Confirm no raw numeric literals (40.0, 35.0, 25.0, 3, 100.0, 75.0) are used directly in computation logic -- only the named constants.

**Expected Result:**
- All thresholds and weights defined as named constants with descriptive names.
- Values match V1 exactly (for output equivalence).

---

### TC-CAS-014: Output Schema -- 9 Columns in Correct Order and Types

**Traces to:** Output Schema
**Priority:** HIGH

**Objective:** Verify the output has exactly 9 columns in the specified order with correct Parquet types.

**Steps:**
1. Run CustomerAttritionSignalsV2 for a single effective date.
2. Read the output Parquet file.
3. Extract the column names and types.

**Expected Result:**

| Column | Expected Type |
|--------|--------------|
| customer_id | int (Int32) |
| first_name | string |
| last_name | string |
| account_count | int (Int32) |
| txn_count | int (Int32) |
| avg_balance | decimal |
| attrition_score | double |
| risk_level | string |
| as_of | DateOnly |

- Exactly 9 columns in this order.
- Type mismatches (e.g., `long` instead of `int` for customer_id) would indicate a missing type conversion in the External module.

---

### TC-CAS-015: Writer Configuration -- Parquet, 1 Part, Overwrite

**Traces to:** Writer Configuration
**Priority:** HIGH

**Objective:** Verify the writer configuration matches V1.

**Steps:**
1. Read the V2 job config JSON.
2. Confirm ParquetFileWriter settings:
   - `source`: `"output"`
   - `numParts`: 1
   - `writeMode`: `"Overwrite"`

**Expected Result:**
- Writer type is ParquetFileWriter.
- Source is `"output"` (not `"pre_scored"` or any other name).
- 1 part file.
- Overwrite mode.

---

### TC-CAS-016: Edge Case -- Customer with Zero Accounts

**Traces to:** BRD Edge Case 1
**Priority:** HIGH

**Objective:** Verify scoring for a customer who has no accounts at all.

**Steps:**
1. Find a customer with zero accounts in the source data:
   ```sql
   SELECT c.id FROM datalake.customers c
   LEFT JOIN datalake.accounts a ON c.id = a.customer_id AND c.as_of = a.as_of
   WHERE a.account_id IS NULL AND c.as_of = '2024-10-01'
   LIMIT 5;
   ```
2. Run CustomerAttritionSignalsV2 for that effective date.
3. Check the output for that customer.

**Expected Result:**
- `account_count` = 0
- `txn_count` = 0
- `avg_balance` = 0 (exactly 0, not NULL)
- `dormancy_factor` = 1.0 (40 points)
- `declining_txn_factor` = 1.0 (35 points, since 0 < 3)
- `low_balance_factor` = 1.0 (25 points, since 0 < 100)
- `attrition_score` = 100.0
- `risk_level` = "High"

---

### TC-CAS-017: Edge Case -- Customer with Accounts but No Transactions

**Traces to:** BRD Edge Case 2
**Priority:** HIGH

**Objective:** Verify scoring for a customer who has accounts but zero transactions.

**Steps:**
1. Find a customer with accounts but no transactions:
   ```sql
   SELECT a.customer_id, COUNT(DISTINCT a.account_id) AS acct_count,
          COUNT(t.transaction_id) AS txn_count
   FROM datalake.accounts a
   LEFT JOIN datalake.transactions t ON a.account_id = t.account_id AND a.as_of = t.as_of
   WHERE a.as_of = '2024-10-01'
   GROUP BY a.customer_id
   HAVING COUNT(t.transaction_id) = 0
   LIMIT 5;
   ```
2. Run CustomerAttritionSignalsV2.
3. Check the output for that customer.

**Expected Result:**
- `account_count` > 0
- `txn_count` = 0
- `declining_txn_factor` = 1.0 (35 points)
- Risk depends on avg_balance:
  - If avg_balance < 100: score = 35 + 25 = 60.0, risk = "Medium"
  - If avg_balance >= 100: score = 35.0, risk = "Low"

---

### TC-CAS-018: Edge Case -- Transaction with Unknown account_id

**Traces to:** BRD Edge Case 3
**Priority:** MEDIUM

**Objective:** Verify that transactions whose `account_id` does not exist in the accounts table are silently dropped (not counted toward any customer's txn_count).

**Steps:**
1. The SQL uses `INNER JOIN accounts a ON t.account_id = a.account_id` in the transaction count subquery. This naturally drops transactions with unmatched account_ids.
2. Verify by checking if any such orphaned transactions exist:
   ```sql
   SELECT COUNT(*) FROM datalake.transactions t
   WHERE NOT EXISTS (
     SELECT 1 FROM datalake.accounts a
     WHERE a.account_id = t.account_id AND a.as_of = t.as_of
   ) AND t.as_of = '2024-10-01';
   ```
3. If orphaned transactions exist, verify they do not inflate any customer's txn_count.

**Expected Result:**
- Orphaned transactions are excluded from all txn_count aggregations.
- No error or warning raised.

---

### TC-CAS-019: Edge Case -- Zero-Row Input (No Customers)

**Traces to:** BR-7
**Priority:** MEDIUM

**Objective:** Verify behavior when the customers table returns zero rows (triggers the empty-input guard in the External module).

**Steps:**
1. This scenario is unlikely with real data but should be verified through code inspection.
2. Inspect the External module: when `pre_scored` DataFrame is null or has 0 rows, the module should:
   - Create an empty DataFrame with the correct 9-column schema.
   - Store it as `"output"`.
   - ParquetFileWriter should produce a valid Parquet file with 0 data rows.

**Expected Result:**
- Output Parquet file has correct schema (9 columns, correct types).
- Output has 0 data rows.
- No runtime error.

---

### TC-CAS-020: Edge Case -- avg_balance Exactly $100

**Traces to:** BR-4 (low_balance_factor threshold)
**Priority:** HIGH

**Objective:** Verify the boundary behavior at `avg_balance = $100.00` exactly.

**The condition is:** `avg_balance < 100.0` -> low_balance_factor = 1.0

**Steps:**
1. Find or construct a scenario where a customer's avg_balance is exactly $100.00.
2. Run CustomerAttritionSignalsV2.
3. Check the `low_balance_factor` contribution.

**Expected Result:**
- avg_balance = 100.00: `low_balance_factor = 0.0` (100.0 is NOT less than 100.0).
- avg_balance = 99.99: `low_balance_factor = 1.0` (99.99 < 100.0).
- avg_balance = 100.01: `low_balance_factor = 0.0`.

**Note:** The comparison is `(double)avgBalance < LowBalanceThreshold`. The cast from `decimal` to `double` for the comparison must be verified.

---

### TC-CAS-021: Edge Case -- txn_count Exactly 3

**Traces to:** BR-4 (declining_txn_factor threshold)
**Priority:** HIGH

**Objective:** Verify the boundary behavior at `txn_count = 3` exactly.

**The condition is:** `txn_count < 3` -> declining_txn_factor = 1.0

**Steps:**
1. Find customers with exactly 3 transactions.
2. Run CustomerAttritionSignalsV2.
3. Check the declining_txn_factor contribution.

**Expected Result:**
- txn_count = 3: `declining_txn_factor = 0.0` (3 is NOT less than 3).
- txn_count = 2: `declining_txn_factor = 1.0` (2 < 3).
- txn_count = 4: `declining_txn_factor = 0.0`.

---

### TC-CAS-022: Edge Case -- Attrition Score at Classification Boundaries

**Traces to:** BR-5
**Priority:** HIGH

**Objective:** Verify risk level classification at exact threshold boundaries.

**Steps:**
1. Identify customers with scores at exact boundaries:
   - Score = 75.0 (dormancy=40 + declining_txn=35, low_balance=0): should be "High"
   - Score = 40.0 (dormancy=40, declining_txn=0, low_balance=0): should be "Medium"
   - Score = 35.0 (dormancy=0, declining_txn=35, low_balance=0): should be "Low"

**Expected Result:**
- Score >= 75.0: "High" (inclusive)
- Score >= 40.0 and < 75.0: "Medium" (inclusive at 40.0)
- Score < 40.0: "Low"

**Boundary verification:**
| Score | Risk Level | Reasoning |
|-------|-----------|-----------|
| 75.0 | High | 75.0 >= 75.0 |
| 74.99 | Medium | Not possible -- scores are discrete: {0, 25, 35, 40, 60, 65, 75, 100} |
| 40.0 | Medium | 40.0 >= 40.0 but < 75.0 |
| 39.99 | Low | Not possible -- scores are discrete |

**Note:** Since the factors are binary (0.0 or 1.0) and weights are fixed integers, the score is always one of {0.0, 25.0, 35.0, 40.0, 60.0, 65.0, 75.0, 100.0}. Boundary values like 74.99 cannot occur in practice.

---

### TC-CAS-023: Weekend Effective Dates

**Traces to:** Edge Case
**Priority:** LOW

**Objective:** Verify no special weekend handling exists (no W1 Sunday skip, no W2 weekend fallback).

**Steps:**
1. Identify a Saturday and Sunday in the effective date range (e.g., 2024-10-05 Saturday, 2024-10-06 Sunday).
2. Run CustomerAttritionSignalsV2 for these dates.
3. Verify normal output is produced.

**Expected Result:**
- Output is produced normally on weekends.
- No empty output, no date fallback.

---

### TC-CAS-024: Edge Case -- NULL Balance Values

**Traces to:** BRD Edge Case 4
**Priority:** MEDIUM

**Objective:** Verify behavior when `current_balance` is NULL in the accounts table.

**Steps:**
1. Query for NULL balances:
   ```sql
   SELECT COUNT(*) FROM datalake.accounts
   WHERE current_balance IS NULL AND as_of = '2024-10-01';
   ```
2. If NULL balances exist:
   - In the SQL, `SUM(current_balance)` with NULL values: SQL SUM ignores NULLs. A customer with all NULL balances would get `total_balance = NULL`, which COALESCE converts to 0.
   - A customer with some NULL and some non-NULL balances: SUM ignores the NULLs, sums only non-NULLs.
   - `COUNT(*)` for account_count still counts rows regardless of NULL balance.
   - This could produce a mismatch with V1 if V1's `Convert.ToDecimal(null)` throws an exception.

**Expected Result:**
- If NULL balances exist in source data: verify V2's SQL aggregation handles them (SUM ignores NULLs, COUNT still counts the rows).
- This behavior may differ from V1 (which would throw on `Convert.ToDecimal(null)` per BRD Edge Case 4). If source data never has NULL balances, this test is informational only.

---

### TC-CAS-025: Proofmark Comparison -- V2 Matches V1

**Traces to:** Output Equivalence
**Priority:** CRITICAL

**Objective:** Verify that V2 output is identical to V1 output using Proofmark.

**Steps:**
1. Run V1 job (CustomerAttritionSignals) for the full date range (2024-10-01 through 2024-12-31). Output goes to `Output/curated/customer_attrition_signals/`.
2. Run V2 job (CustomerAttritionSignalsV2) for the same full date range. Output goes to `Output/double_secret_curated/customer_attrition_signals/`.
3. Run Proofmark:
   ```bash
   python3 -m proofmark compare \
     --config POC3/proofmark_configs/customer_attrition_signals.yaml \
     --left Output/curated/customer_attrition_signals/ \
     --right Output/double_secret_curated/customer_attrition_signals/ \
     --output POC3/logs/proofmark_reports/customer_attrition_signals.json
   ```
4. Verify exit code is 0 (PASS).

**Expected Result:**
- Proofmark reports 100% match.
- No row or column differences.

**Proofmark Config:**
```yaml
comparison_target: "customer_attrition_signals"
reader: parquet
threshold: 100.0
```

**Fallback:** If Proofmark fails due to epsilon differences in `attrition_score` (W6), add fuzzy override:
```yaml
columns:
  fuzzy:
    - name: "attrition_score"
      tolerance: 0.0001
      tolerance_type: absolute
      reason: "Double-precision arithmetic accumulation (W6)"
```

---

### TC-CAS-026: Output Directory and firstEffectiveDate

**Traces to:** V2 Configuration
**Priority:** MEDIUM

**Objective:** Verify V2 config correctness.

**Steps:**
1. Read the V2 job config JSON at `JobExecutor/Jobs/customer_attrition_signals_v2.json`.
2. Verify:
   - `jobName`: `"CustomerAttritionSignalsV2"`
   - `firstEffectiveDate`: `"2024-10-01"` (matches V1)
   - ParquetFileWriter `outputDirectory`: `"Output/double_secret_curated/customer_attrition_signals/"`

**Expected Result:**
- All config values match V2 conventions and V1 equivalents.

---

### TC-CAS-027: Banker's Rounding at .XX5 Boundary

**Traces to:** BR-9, W5
**Priority:** HIGH

**Objective:** Specifically test the banker's rounding behavior at a .XX5 midpoint to confirm MidpointRounding.ToEven is used.

**Steps:**
1. Banker's rounding (MidpointRounding.ToEven) rounds .5 to the nearest even number:
   - 2.5 -> 2 (rounds down to even)
   - 3.5 -> 4 (rounds up to even)
   - 2.25 with 1dp -> 2.2 (rounds down to even)
   - 2.35 with 1dp -> 2.4 (rounds up to even)
2. For avg_balance rounded to 2dp:
   - 123.445 -> 123.44 (banker's: round down, 4 is even)
   - 123.435 -> 123.44 (banker's: round up, 4 is even)
   - 123.455 -> 123.46 (banker's: round up, 6 is even)
   - 123.465 -> 123.46 (banker's: round down, 6 is even)
3. Find or construct a customer where `total_balance / account_count` produces a .XX5 midpoint.
4. Verify the output matches banker's rounding, not half-up rounding.

**Expected Result:**
- avg_balance uses MidpointRounding.ToEven (banker's rounding).
- Values at .XX5 midpoints round to the nearest even digit.

---

### TC-CAS-028: Overwrite Mode -- Only Last Date Survives

**Traces to:** Writer Configuration, Write Mode Implications
**Priority:** MEDIUM

**Objective:** Verify that in Overwrite mode, each effective date run replaces the entire output. Only the last day's data persists after multi-day gap-fill.

**Steps:**
1. Run CustomerAttritionSignalsV2 for effective dates 2024-10-01 through 2024-10-03.
2. After completion, read the output Parquet file.
3. Check the `as_of` column values.

**Expected Result:**
- All rows have `as_of = 2024-10-03` (the last effective date processed).
- No rows from 2024-10-01 or 2024-10-02 remain.
- Output reflects only the last effective date's data snapshot.

---

### TC-CAS-029: Weight Discrepancy -- Dormancy=40, Declining=35

**Traces to:** FSD Section 3 (BRD Weight Discrepancy)
**Priority:** CRITICAL

**Objective:** Verify that V2 uses the SOURCE CODE weights (dormancy=40.0, declining_txn=35.0), not the BRD text weights (dormancy=35, declining_txn=40).

**Steps:**
1. Read the V2 External module source code.
2. Confirm `DormancyWeight = 40.0` and `DecliningTxnWeight = 35.0`.
3. Find a customer with 0 accounts (dormancy=1.0, declining=1.0, low_balance=1.0):
   - If BRD text weights were used: score = 35 + 40 + 25 = 100.0 (same total)
   - The total happens to be the same (100.0) because all factors are active.
4. Find a customer where ONLY the dormancy factor is active (0 accounts, but this always triggers declining_txn too since 0 txns < 3). So find a customer where dormancy=1, declining=1, low_balance=0:
   - With code weights: score = 40 + 35 = 75.0 -> "High"
   - With BRD text weights: score = 35 + 40 = 75.0 -> "High"
   - The sum is still the same (75.0) because 40+35 = 35+40.
5. Find a case where the individual weights matter -- a customer where only ONE of dormancy or declining_txn is active:
   - Only declining_txn active (has accounts, <3 txns, balance >=100): code gives 35.0, BRD text gives 40.0.
   - With code weight 35.0: risk = "Low" (35 < 40)
   - With BRD text weight 40.0: risk = "Medium" (40 >= 40)
   - **This is the distinguishing case.**
6. Find such a customer in the data and verify V2 output gives score=35.0 and risk="Low".

**Expected Result:**
- For a customer with accounts, <3 transactions, and avg_balance >= $100: `attrition_score = 35.0`, `risk_level = "Low"`.
- This confirms dormancy weight is NOT 35 (which would give score=0 in this case) and declining_txn weight IS 35 (not 40).

---

## Edge Case Summary

| Scenario | Expected Behavior | Test ID |
|----------|------------------|---------|
| Customer with 0 accounts | score=100.0, risk="High" | TC-CAS-016 |
| Customer with accounts, 0 txns | score depends on balance | TC-CAS-017 |
| Orphaned transactions | Silently dropped | TC-CAS-018 |
| No customers at all | Empty output with 9-col schema | TC-CAS-019 |
| avg_balance exactly $100 | low_balance_factor = 0.0 | TC-CAS-020 |
| txn_count exactly 3 | declining_txn_factor = 0.0 | TC-CAS-021 |
| Score exactly 75.0 | risk = "High" | TC-CAS-022 |
| Score exactly 40.0 | risk = "Medium" | TC-CAS-022 |
| Weekend dates | Normal processing | TC-CAS-023 |
| NULL current_balance | SUM ignores NULLs | TC-CAS-024 |
| Banker's rounding .XX5 | Rounds to even | TC-CAS-027 |
| Multi-day Overwrite | Only last date survives | TC-CAS-028 |
| NULL first_name/last_name | Coalesced to "" | TC-CAS-010 |
| Weight discrepancy (only declining_txn active) | score=35.0, risk="Low" | TC-CAS-029 |
