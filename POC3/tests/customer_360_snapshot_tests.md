# Customer360Snapshot -- Test Plan

## 1. Overview

This test plan validates the V2 implementation of the Customer360Snapshot job, which produces a comprehensive 360-degree customer snapshot aggregating account counts/balances, card counts, and investment counts/values per customer, with weekend fallback logic and Parquet output.

**BRD:** `POC3/brd/customer_360_snapshot_brd.md`
**FSD:** `POC3/fsd/customer_360_snapshot_fsd.md`
**V2 Tier:** Tier 1 (Framework Only: DataSourcing -> Transformation SQL -> ParquetFileWriter)

---

## 2. Traceability Matrix

| Test ID | BRD Requirement | FSD Section | Description |
|---------|----------------|-------------|-------------|
| TC-01 | BR-1 | 5 (date_calc CTE) | Saturday effective date falls back to Friday |
| TC-02 | BR-1 | 5 (date_calc CTE) | Sunday effective date falls back to Friday |
| TC-03 | BR-1 | 5 (date_calc CTE) | Weekday effective date used as-is |
| TC-04 | BR-2 | 5 (filtered_customers, WHERE clauses) | All source tables filtered to target date only |
| TC-05 | BR-3 | 5 (account_agg CTE) | Account count aggregated per customer |
| TC-06 | BR-3 | 5 (account_agg CTE) | Total balance summed per customer |
| TC-07 | BR-4 | 5 (card_agg CTE) | Card count aggregated per customer |
| TC-08 | BR-5 | 5 (investment_agg CTE) | Investment count and value aggregated per customer |
| TC-09 | BR-6 | 5 (COALESCE in final SELECT) | Customer with no accounts/cards/investments gets zeros |
| TC-10 | BR-7 | 5 (ROUND in final SELECT) | Total balance rounded to 2 decimal places |
| TC-11 | BR-7 | 5 (ROUND in final SELECT) | Total investment value rounded to 2 decimal places |
| TC-12 | BR-8 | 5 (d.target_date AS as_of) | as_of reflects weekend-adjusted date, not original |
| TC-13 | BR-9 | 5 (filtered_customers as base) | Output is customer-driven; only customers on target date produce rows |
| TC-14 | BR-10 | 5 (Edge Case: Empty Customers) | No customers on target date produces zero-row output |
| TC-15 | BR-11 / AP1,AP4 | 3 (Anti-Pattern Analysis) | Unused columns eliminated from V2 DataSourcing |
| TC-16 | Writer Config | 7 (Writer Config) | Parquet output, 1 part file, Overwrite mode |
| TC-17 | W2 | 3 (Anti-Pattern Analysis) | Weekend fallback reproduces V1 behavior cleanly |
| TC-18 | AP3 | 1 (Tier Selection) | V2 uses Tier 1 (no External module) |
| TC-19 | -- | 4 (Output Schema) | Column order matches V1 exactly |
| TC-20 | BR-6 | 5 (COALESCE in final SELECT) | NULL first_name/last_name coalesced to empty string |
| TC-21 | -- | 5 (Rounding Behavior) | Rounding edge case: banker's vs half-away-from-zero |
| TC-22 | BR-1 | 5 (date_calc CTE) | Weekend fallback when Friday has no data |
| TC-23 | -- | 8 (Proofmark Config) | Proofmark config is strict with zero exclusions |
| TC-24 | BR-3 | 5 (account_agg CTE) | Customer with multiple accounts on same date |

---

## 3. Test Cases

### TC-01: Saturday Weekend Fallback

**Requirement:** BR-1, W2
**Description:** When `__maxEffectiveDate` falls on a Saturday, the job should use Friday (date - 1 day) as the target date.
**Preconditions:** DataSourcing loads data for a Saturday effective date range. Data exists for the preceding Friday.
**Steps:**
1. Run V2 job with effective date = Saturday (e.g., 2024-10-05, which is a Saturday).
2. Verify the SQL `date_calc` CTE computes `target_date = 2024-10-04` (Friday).
3. Verify all output rows have `as_of = 2024-10-04`.
4. Verify aggregations are computed from Friday's data only.
**Expected Result:** Output as_of = Friday; aggregations drawn from Friday data.
**Pass Criteria:** as_of column in all output rows equals the preceding Friday date; no Saturday data appears in output.

---

### TC-02: Sunday Weekend Fallback

**Requirement:** BR-1, W2
**Description:** When `__maxEffectiveDate` falls on a Sunday, the job should use Friday (date - 2 days) as the target date.
**Preconditions:** DataSourcing loads data for a Sunday effective date range. Data exists for the preceding Friday.
**Steps:**
1. Run V2 job with effective date = Sunday (e.g., 2024-10-06, which is a Sunday).
2. Verify the SQL `date_calc` CTE computes `target_date = 2024-10-04` (Friday).
3. Verify all output rows have `as_of = 2024-10-04`.
**Expected Result:** Output as_of = Friday (two days back); aggregations drawn from Friday data.
**Pass Criteria:** as_of column equals the preceding Friday date.

---

### TC-03: Weekday Effective Date (No Fallback)

**Requirement:** BR-1
**Description:** When `__maxEffectiveDate` falls on a weekday (Mon-Fri), the date is used as-is.
**Preconditions:** Data exists for a weekday effective date.
**Steps:**
1. Run V2 job with effective date = Wednesday (e.g., 2024-10-02).
2. Verify `target_date = 2024-10-02` (no adjustment).
3. Verify output as_of = 2024-10-02.
**Expected Result:** Output uses the effective date directly with no adjustment.
**Pass Criteria:** as_of equals the original effective date.

---

### TC-04: In-Code Date Filtering

**Requirement:** BR-2
**Description:** All source DataFrames are filtered to the target date. Rows from other dates in the DataSourcing range are discarded.
**Preconditions:** DataSourcing loads a range of dates (due to executor behavior). Source tables contain rows for multiple as_of dates within the range.
**Steps:**
1. Run V2 job for a weekday effective date where the source tables contain data for the target date and other dates within the loaded range.
2. Verify that output only includes aggregations from the target date.
3. Verify that rows from other dates in the loaded range do not contribute to counts or sums.
**Expected Result:** Only target-date rows contribute to the output.
**Pass Criteria:** Row counts and aggregation values correspond exclusively to target-date source rows.

---

### TC-05: Account Count Aggregation

**Requirement:** BR-3
**Description:** The output `account_count` reflects the number of account rows per customer on the target date.
**Preconditions:** Customer X has 3 accounts on the target date. Customer Y has 1 account.
**Steps:**
1. Run V2 job.
2. Verify Customer X's `account_count = 3`.
3. Verify Customer Y's `account_count = 1`.
**Expected Result:** account_count matches per-customer row counts from the accounts table on the target date.
**Pass Criteria:** Counts match expected values.

---

### TC-06: Total Balance Aggregation

**Requirement:** BR-3
**Description:** The output `total_balance` is the sum of `current_balance` across all accounts for a customer on the target date.
**Preconditions:** Customer X has accounts with balances 100.50, 200.25, 50.00 on the target date.
**Steps:**
1. Run V2 job.
2. Verify Customer X's `total_balance = 350.75`.
**Expected Result:** total_balance equals the sum of all account balances for that customer on the target date.
**Pass Criteria:** Sum matches expected value.

---

### TC-07: Card Count Aggregation

**Requirement:** BR-4
**Description:** The output `card_count` reflects the number of card rows per customer on the target date.
**Preconditions:** Customer X has 2 cards on the target date. Customer Y has 0 cards.
**Steps:**
1. Run V2 job.
2. Verify Customer X's `card_count = 2`.
3. Verify Customer Y's `card_count = 0` (via COALESCE default).
**Expected Result:** card_count matches per-customer row counts from the cards table on the target date.
**Pass Criteria:** Counts match expected values.

---

### TC-08: Investment Count and Value Aggregation

**Requirement:** BR-5
**Description:** The output `investment_count` and `total_investment_value` reflect the count and sum of investment records per customer on the target date.
**Preconditions:** Customer X has 2 investments (values 5000.00, 3000.50) on the target date.
**Steps:**
1. Run V2 job.
2. Verify Customer X's `investment_count = 2`.
3. Verify Customer X's `total_investment_value = 8000.50`.
**Expected Result:** Both count and sum match expected values.
**Pass Criteria:** investment_count and total_investment_value match expected values.

---

### TC-09: Customer With No Related Records (Default Zeros)

**Requirement:** BR-6
**Description:** A customer present on the target date but with no accounts, cards, or investments should have all aggregated fields set to 0.
**Preconditions:** Customer Z exists on the target date. No account, card, or investment rows reference Customer Z on the target date.
**Steps:**
1. Run V2 job.
2. Verify Customer Z's row: `account_count = 0`, `total_balance = 0`, `card_count = 0`, `investment_count = 0`, `total_investment_value = 0`.
**Expected Result:** All count and sum columns default to 0 via COALESCE.
**Pass Criteria:** All five aggregated columns equal 0 for Customer Z.

---

### TC-10: Total Balance Rounding

**Requirement:** BR-7
**Description:** `total_balance` is rounded to 2 decimal places.
**Preconditions:** Customer X has accounts with balances that produce a sum with more than 2 decimal places (e.g., 100.123 + 200.456 = 300.579).
**Steps:**
1. Run V2 job.
2. Verify `total_balance` is rounded to 2 decimal places (300.58 with standard rounding).
**Expected Result:** total_balance has at most 2 decimal places.
**Pass Criteria:** Value is rounded to 2 decimal places. Note: if source data is already at 2 decimal precision, this is a no-op -- verify ROUND is still applied defensively.

---

### TC-11: Total Investment Value Rounding

**Requirement:** BR-7
**Description:** `total_investment_value` is rounded to 2 decimal places.
**Preconditions:** Customer X has investments whose values sum to a number with more than 2 decimal places.
**Steps:**
1. Run V2 job.
2. Verify `total_investment_value` is rounded to 2 decimal places.
**Expected Result:** total_investment_value has at most 2 decimal places.
**Pass Criteria:** Value is rounded to 2 decimal places.

---

### TC-12: as_of Reflects Weekend-Adjusted Date

**Requirement:** BR-8
**Description:** The `as_of` column in the output is set to the weekend-adjusted target date, not the original `__maxEffectiveDate`.
**Preconditions:** Run on a Saturday (e.g., 2024-10-05).
**Steps:**
1. Run V2 job with effective date = 2024-10-05 (Saturday).
2. Verify output as_of = 2024-10-04 (Friday), not 2024-10-05.
**Expected Result:** as_of is the adjusted date.
**Pass Criteria:** as_of equals Friday, not Saturday.

---

### TC-13: Output Is Customer-Driven

**Requirement:** BR-9
**Description:** Only customers present on the target date produce output rows. Accounts, cards, or investments for customers not in the customers table on the target date do not generate rows.
**Preconditions:** Accounts exist for customer_id=999 on the target date, but customer_id=999 is NOT in the customers table on the target date.
**Steps:**
1. Run V2 job.
2. Verify no output row exists for customer_id=999.
**Expected Result:** No orphaned account/card/investment data produces output rows.
**Pass Criteria:** Output row count equals the number of distinct customers on the target date in the customers table.

---

### TC-14: Zero Customers -- Empty Output

**Requirement:** BR-10
**Description:** When no customer rows exist for the target date, the output is a zero-row DataFrame with the correct schema (9 columns).
**Preconditions:** No customers exist in the source data for the target date (or for the weekend-adjusted date).
**Steps:**
1. Run V2 job for a date with no customer data.
2. Verify output has zero rows.
3. Verify output schema still has all 9 columns: customer_id, first_name, last_name, account_count, total_balance, card_count, investment_count, total_investment_value, as_of.
**Expected Result:** Zero-row output with correct column structure.
**Pass Criteria:** Output file exists, contains zero data rows, and has the expected schema.

---

### TC-15: Unused Columns Eliminated (AP1, AP4)

**Requirement:** BR-11, AP1, AP4
**Description:** V2 DataSourcing configs do NOT include unused columns that V1 sourced (prefix, suffix, interest_rate, credit_limit, apr, card_number_masked, card_id, account_id, investment_id).
**Preconditions:** V2 job config exists.
**Steps:**
1. Read V2 job config JSON.
2. Verify DataSourcing for `customers` sources only: `id`, `first_name`, `last_name`.
3. Verify DataSourcing for `accounts` sources only: `customer_id`, `current_balance`.
4. Verify DataSourcing for `cards` sources only: `customer_id`.
5. Verify DataSourcing for `investments` sources only: `customer_id`, `current_value`.
**Expected Result:** No unused columns in V2 DataSourcing configs.
**Pass Criteria:** Column lists match exactly as specified.

---

### TC-16: Writer Configuration

**Requirement:** Writer Config
**Description:** V2 uses ParquetFileWriter with numParts=1, writeMode=Overwrite, and output to `Output/double_secret_curated/customer_360_snapshot/`.
**Preconditions:** V2 job config exists.
**Steps:**
1. Read V2 job config JSON.
2. Verify writer type = `ParquetFileWriter`.
3. Verify `source` = `output`.
4. Verify `outputDirectory` = `Output/double_secret_curated/customer_360_snapshot/`.
5. Verify `numParts` = 1.
6. Verify `writeMode` = `Overwrite`.
**Expected Result:** All writer params match the specification.
**Pass Criteria:** All five writer parameters match.

---

### TC-17: Weekend Fallback Is Cleanly Implemented (W2)

**Requirement:** W2
**Description:** The weekend fallback logic is implemented in SQL using `strftime('%w')` with a CASE expression, not reproduced from V1's C# DayOfWeek approach. A comment documents the V1 behavior replication.
**Preconditions:** V2 SQL exists in the job config.
**Steps:**
1. Read the V2 Transformation SQL.
2. Verify it uses `strftime('%w', ...)` with a CASE to handle Saturday (6) -> date -1 day and Sunday (0) -> date -2 days.
3. Verify the SQL contains a comment or the FSD documents the W2 replication.
**Expected Result:** Clean SQL implementation of weekend fallback.
**Pass Criteria:** SQL uses strftime-based day-of-week check, not C# logic.

---

### TC-18: Tier 1 -- No External Module

**Requirement:** AP3
**Description:** V2 uses Tier 1 (DataSourcing -> Transformation -> ParquetFileWriter) with no External module.
**Preconditions:** V2 job config exists.
**Steps:**
1. Read V2 job config JSON.
2. Verify no module has `"type": "External"`.
3. Verify the module chain is: DataSourcing (x4) -> Transformation -> ParquetFileWriter.
**Expected Result:** No External module in the config.
**Pass Criteria:** Module chain matches Tier 1 pattern.

---

### TC-19: Column Order

**Requirement:** Output Schema
**Description:** Output column order matches V1 exactly: customer_id, first_name, last_name, account_count, total_balance, card_count, investment_count, total_investment_value, as_of.
**Preconditions:** V2 job produces output.
**Steps:**
1. Run V2 job and inspect the output Parquet schema.
2. Verify column order: customer_id, first_name, last_name, account_count, total_balance, card_count, investment_count, total_investment_value, as_of.
**Expected Result:** Column order is identical to V1.
**Pass Criteria:** Column order matches exactly.

---

### TC-20: NULL Name Handling

**Requirement:** BR-6
**Description:** NULL first_name or last_name values are coalesced to empty string in the output.
**Preconditions:** A customer on the target date has NULL first_name and/or NULL last_name in the source.
**Steps:**
1. Run V2 job.
2. Verify the customer's output row has `first_name = ""` and/or `last_name = ""` (not NULL).
**Expected Result:** NULL names replaced with empty string.
**Pass Criteria:** No NULL values in first_name or last_name columns in output.

---

### TC-21: Rounding Midpoint Edge Case

**Requirement:** BR-7, W5
**Description:** SQLite ROUND uses half-away-from-zero, while V1's Math.Round uses banker's rounding (ToEven). For most values this is identical, but at exact .XX5 midpoints the behavior may differ.
**Preconditions:** Source data contains balances that sum to an exact .XX5 midpoint for a customer (e.g., sum = 100.125).
**Steps:**
1. Run V2 job.
2. Check the rounded value for `total_balance` or `total_investment_value`.
3. Compare against V1 output.
**Expected Result:** If source data is stored at 2 decimal precision, sums remain at 2 decimal places and ROUND is a no-op. If a midpoint discrepancy is detected, the Proofmark config should be updated with a fuzzy tolerance of 0.01 on the affected columns.
**Pass Criteria:** Either values match V1 exactly, or the discrepancy is documented and a fuzzy Proofmark override is applied per the FSD's contingency plan.

---

### TC-22: Weekend Fallback When Friday Has No Data

**Requirement:** BR-1, BR-14 (edge case)
**Description:** When the effective date is a Saturday or Sunday but no data exists for the preceding Friday, the output should have zero rows.
**Preconditions:** Effective date is Saturday. No customer data exists for the preceding Friday.
**Steps:**
1. Run V2 job with effective date = a Saturday where no Friday data exists.
2. Verify output has zero rows.
**Expected Result:** Zero-row output (no customers found for the target date).
**Pass Criteria:** Output file has zero data rows.

---

### TC-23: Proofmark Config Validation

**Requirement:** Proofmark Config
**Description:** The Proofmark config for this job should be strict with no exclusions and no fuzzy overrides by default.
**Preconditions:** Proofmark config YAML exists at `POC3/proofmark_configs/customer_360_snapshot.yaml`.
**Steps:**
1. Read the Proofmark config.
2. Verify `reader: parquet`.
3. Verify `threshold: 100.0`.
4. Verify no `columns.excluded` entries.
5. Verify no `columns.fuzzy` entries (unless a rounding discrepancy per TC-21 necessitated one).
**Expected Result:** Strict config with zero exclusions and zero fuzzy overrides.
**Pass Criteria:** Config matches the FSD's proposed default strict configuration.

---

### TC-24: Multiple Accounts Per Customer

**Requirement:** BR-3
**Description:** When a customer has multiple accounts on the target date, account_count reflects the total number and total_balance reflects the sum of all balances.
**Preconditions:** Customer X has 5 accounts with varying balances on the target date.
**Steps:**
1. Run V2 job.
2. Verify `account_count = 5` for Customer X.
3. Verify `total_balance` = sum of all 5 account balances.
**Expected Result:** Aggregation covers all accounts for the customer.
**Pass Criteria:** Count and sum match expected values.

---

## 4. Edge Case Summary

| Edge Case | Covered By | Expected Behavior |
|-----------|-----------|-------------------|
| Saturday effective date | TC-01, TC-12 | Falls back to Friday (date - 1) |
| Sunday effective date | TC-02 | Falls back to Friday (date - 2) |
| Weekday effective date | TC-03 | Used as-is |
| Weekend fallback to date with no data | TC-22 | Zero-row output |
| Customer with no accounts/cards/investments | TC-09 | All aggregated columns = 0 |
| No customers on target date | TC-14 | Zero-row output with correct schema |
| NULL first_name or last_name | TC-20 | Coalesced to empty string |
| Rounding midpoint (.XX5 boundary) | TC-21 | Match V1; fuzzy tolerance if needed |
| Multi-date data in DataSourcing range | TC-04 | Only target date data used |
| Orphaned accounts (no matching customer) | TC-13 | Not included in output |
| Multiple accounts per customer | TC-24 | Correctly aggregated |
| Unused source columns | TC-15 | Eliminated in V2 |

---

## 5. Output Format Validation

| Property | Expected Value | Test |
|----------|---------------|------|
| File format | Parquet | TC-16 |
| Number of part files | 1 | TC-16 |
| Write mode | Overwrite | TC-16 |
| Output path | `Output/double_secret_curated/customer_360_snapshot/` | TC-16 |
| Column count | 9 | TC-19, TC-14 |
| Column order | customer_id, first_name, last_name, account_count, total_balance, card_count, investment_count, total_investment_value, as_of | TC-19 |
