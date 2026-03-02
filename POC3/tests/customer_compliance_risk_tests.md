# CustomerComplianceRisk — Test Plan

## Overview

Test plan for the V2 rewrite of `CustomerComplianceRisk`. This job calculates a composite compliance risk score per customer based on the count of compliance events, wire transfers, and high-value transactions (which are always zero due to a V1 bug). Produces one row per customer with risk scoring factors and a weighted total score, output as CSV with header, LF line endings, Overwrite mode, and no trailer.

**V2 Tier:** Tier 1 (DataSourcing + Transformation SQL + CsvFileWriter)
**BRD:** `POC3/brd/customer_compliance_risk_brd.md`
**FSD:** `POC3/fsd/customer_compliance_risk_fsd.md`

---

## Traceability Matrix

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01 | BR-1 | One output row per customer (including those with zero events/wires) |
| TC-02 | BR-2 | Compliance event count includes all events (no status/type filter) |
| TC-03 | BR-3 | Wire count includes all wires (no direction/amount filter) |
| TC-04 | BR-4 | High-value transaction count keyed by account_id (V1 bug replicated) |
| TC-05 | BR-5 | account_id/customer_id mismatch results in high_txn_count always 0 |
| TC-06 | BR-6 | Risk score formula: (compliance_events * 30.0) + (wire_count * 20.0) + (high_txn_count * 10.0) |
| TC-07 | BR-7 | Banker's rounding to 2 decimal places (moot for integer results) |
| TC-08 | BR-8 | Risk scores are always exact integers (due to integer inputs and weights) |
| TC-09 | BR-9 | Empty customers produces empty output |
| TC-10 | BR-10 | NULL first_name/last_name coalesced to empty string |
| TC-11 | BR-11 | as_of sourced from customer row, not __maxEffectiveDate |
| TC-12 | BR-12 | No transactions exceed 5000 threshold (data range constraint) |
| TC-13 | Writer Config | CSV header present |
| TC-14 | Writer Config | LF line endings |
| TC-15 | Writer Config | Overwrite write mode |
| TC-16 | Writer Config | No trailer row |
| TC-17 | FSD Anti-Pattern | AP1 eliminated (transactions table removed) |
| TC-18 | FSD Anti-Pattern | AP3 eliminated (External replaced by SQL Transformation) |
| TC-19 | FSD Anti-Pattern | AP4 eliminated (unused columns removed) |
| TC-20 | FSD Anti-Pattern | AP6 eliminated (row-by-row replaced by SQL set ops) |
| TC-21 | Edge Case | Customer with zero compliance events and zero wires |
| TC-22 | Edge Case | Customer with compliance events but no wires |
| TC-23 | Edge Case | Customer with wires but no compliance events |
| TC-24 | Edge Case | NULL first_name AND NULL last_name simultaneously |
| TC-25 | Edge Case | Boundary date: first effective date 2024-10-01 |
| TC-26 | Edge Case | Overwrite mode multi-day: only last day survives |
| TC-27 | Output Schema | Column order matches V1 exactly |
| TC-28 | FSD Risk | Row ordering V1 vs V2 (SQLite internal vs DataFrame iteration) |
| TC-29 | FSD Risk | Empty compliance_events or wire_transfers with non-empty customers |
| TC-30 | Edge Case | Large compliance event count (risk score magnitude) |
| TC-31 | FSD W5/W6 | Double/REAL arithmetic equivalence verification |

---

## Test Cases

### TC-01: One output row per customer
**Traces to:** BR-1
**Objective:** Verify that the output contains exactly one row for every customer in the customers DataFrame, including customers with zero compliance events and zero wires.

**Preconditions:** customers table has data for the effective date. Some customers have compliance events, some have wires, some have neither.

**Steps:**
1. Run V2 job for a single effective date.
2. Count distinct customer_ids in the output.
3. Count customers in source data.

**Expected Result:**
- Output row count = number of unique customers for the effective date.
- Every customer_id in `datalake.customers` for that as_of appears in the output.
- Customers with zero compliance events have `compliance_events = 0`.
- Customers with zero wires have `wire_count = 0`.

**Verification:** `SELECT COUNT(DISTINCT id) FROM datalake.customers WHERE as_of = '{date}'` should equal output row count.

---

### TC-02: Compliance event count includes all events
**Traces to:** BR-2
**Objective:** Verify that compliance_events counts ALL events per customer_id with no filter on event_type or status.

**Preconditions:** Customer has compliance events with different event_type and status values.

**Steps:**
1. Run V2 job for a single effective date.
2. For a specific customer, compare output compliance_events with source data.

**Expected Result:**
- compliance_events = total count of all rows in datalake.compliance_events for that customer_id, regardless of event_type or status.

**Verification:** `SELECT COUNT(*) FROM datalake.compliance_events WHERE customer_id = {id} AND as_of = '{date}'` should match the output value.

---

### TC-03: Wire count includes all wires
**Traces to:** BR-3
**Objective:** Verify that wire_count counts ALL wire transfers per customer_id with no filter on direction, amount, or status.

**Preconditions:** Customer has wire transfers with different directions and amounts.

**Steps:**
1. Run V2 job for a single effective date.
2. For a specific customer, compare output wire_count with source data.

**Expected Result:**
- wire_count = total count of all rows in datalake.wire_transfers for that customer_id, regardless of direction, amount, or status.

**Verification:** `SELECT COUNT(*) FROM datalake.wire_transfers WHERE customer_id = {id} AND as_of = '{date}'` should match the output value.

---

### TC-04: High-value transaction count keyed by account_id (V1 bug replicated)
**Traces to:** BR-4
**Objective:** Verify that the V2 implementation correctly replicates the V1 bug where high_txn_count is always 0.

**Steps:**
1. Run V2 job.
2. Examine the high_txn_count column for every row.

**Expected Result:**
- Every row has `high_txn_count = 0`.
- This is correct behavior for output equivalence: V1's account_id/customer_id mismatch means this is always 0.

---

### TC-05: account_id/customer_id mismatch results in high_txn_count always 0
**Traces to:** BR-5
**Objective:** Confirm that the V2 FSD strategy of hardcoding 0 (rather than sourcing transactions and performing the buggy lookup) produces the same result as V1.

**Steps:**
1. Run both V1 and V2 jobs for the same effective date.
2. Compare the high_txn_count column.

**Expected Result:**
- V1 and V2 both produce `high_txn_count = 0` for every row.
- V2 achieves this by hardcoding `0 AS high_txn_count` in SQL, which is equivalent to V1's buggy lookup returning 0 for every customer.

---

### TC-06: Risk score formula
**Traces to:** BR-6
**Objective:** Verify that the risk_score is computed as `(compliance_events * 30.0) + (wire_count * 20.0) + (high_txn_count * 10.0)`.

**Preconditions:** Customer has known compliance_events and wire_count values.

**Steps:**
1. Run V2 job.
2. For a specific customer, manually compute the expected risk_score.

**Expected Result:**
- risk_score = `(compliance_events * 30.0) + (wire_count * 20.0) + (0 * 10.0)`.
- For a customer with 3 compliance events and 2 wires: `(3 * 30.0) + (2 * 20.0) + 0 = 130.0`.

**Verification:** For multiple customers, verify the formula. Since high_txn_count is always 0, risk_score simplifies to `(compliance_events * 30) + (wire_count * 20)`.

---

### TC-07: Banker's rounding to 2 decimal places
**Traces to:** BR-7, W5
**Objective:** Verify that risk_score is rounded to 2 decimal places.

**Steps:**
1. Run V2 job.
2. Examine the risk_score column formatting.

**Expected Result:**
- risk_score values are displayed with up to 2 decimal places.
- Per BR-8, since all inputs are integer counts multiplied by integer weights (30, 20, 10), risk scores are always exact integers. The rounding has no practical effect.
- V1 uses `MidpointRounding.ToEven` (banker's rounding), V2 uses SQLite `ROUND()` (round-half-away-from-zero). The difference is moot because no midpoint values are possible with integer inputs and integer weights.

**Note:** If Proofmark fails on risk_score due to formatting differences (e.g., `130.0` vs `130` vs `130.00`), this should be investigated as a formatting concern, not a rounding concern.

---

### TC-08: Risk scores are always exact integers
**Traces to:** BR-8
**Objective:** Verify that all risk_score values in the output are integer-valued (no fractional component beyond .0).

**Steps:**
1. Run V2 job for the full date range.
2. Check all risk_score values.

**Expected Result:**
- Every risk_score value is an integer (e.g., 0.0, 30.0, 60.0, 90.0, 130.0, etc.).
- No risk_score has a non-zero fractional part.
- This follows from BR-8: integer inputs * integer weights = integer results.

---

### TC-09: Empty customers produces empty output
**Traces to:** BR-9
**Objective:** Verify that if the customers DataFrame is empty for a given effective date, zero output rows are produced.

**Preconditions:** An effective date where no customer records exist (or simulated empty customers DataFrame).

**Steps:**
1. Run V2 job for an effective date with no customers data.
2. Examine output.

**Expected Result:**
- Zero data rows in output.
- If header is enabled and writeMode is Overwrite, the output file contains only the header row.

**FSD Risk Note:** Per FSD Section 5 note #9, if customers DataFrame is empty, the Transformation module's RegisterTable may not register the table in SQLite, causing the query to fail. The FSD acknowledges this risk and proposes a Tier 2 escalation as mitigation if it occurs in Phase D. This test case should verify whether the empty-input scenario causes a failure or gracefully produces zero rows.

---

### TC-10: NULL first_name/last_name coalesced to empty string
**Traces to:** BR-10
**Objective:** Verify that NULL first_name and last_name values are coalesced to empty strings in the output.

**Preconditions:** Customer record with NULL first_name or last_name in datalake.customers.

**Steps:**
1. Run V2 job.
2. Find the output row for a customer with NULL name fields.

**Expected Result:**
- `first_name` appears as an empty string (not NULL, not "null", not missing).
- `last_name` appears as an empty string.
- In CSV, this manifests as empty fields: `1001,,Smith,...` (if first_name is NULL and last_name is "Smith").

**Verification:** V2 SQL uses `COALESCE(c.first_name, '')` which matches V1's `?.ToString() ?? ""`.

---

### TC-11: as_of sourced from customer row
**Traces to:** BR-11
**Objective:** Verify that the as_of value in each output row comes from the customer row's as_of field, not from `__maxEffectiveDate`.

**Steps:**
1. Run V2 job for a single effective date.
2. Examine as_of values in output.

**Expected Result:**
- The as_of column value matches the customer row's as_of from the datalake, which equals the effective date for single-day runs.
- For multi-day runs, different customer rows from different as_of dates would show their respective as_of values (though Overwrite mode means only the last day's output survives).

---

### TC-12: No transactions exceed 5000 threshold
**Traces to:** BR-12
**Objective:** Confirm that the data constraint holds: no transaction amount exceeds 5000 in the datalake.

**Steps:**
1. Query: `SELECT MAX(amount) FROM datalake.transactions`.

**Expected Result:**
- MAX(amount) <= 5000 (BRD states max is 4200.00).
- This confirms that even if the V1 account_id/customer_id lookup worked correctly, high_txn_count would still be 0.

---

### TC-13: CSV header present
**Traces to:** Writer Config (includeHeader: true)
**Objective:** Verify that the output CSV begins with a header row.

**Steps:**
1. Run V2 job.
2. Read the first line of the output file.

**Expected Result:**
- First line: `customer_id,first_name,last_name,compliance_events,wire_count,high_txn_count,risk_score,as_of`

---

### TC-14: LF line endings
**Traces to:** Writer Config (lineEnding: LF)
**Objective:** Verify that the output CSV uses LF (`\n`) line endings, not CRLF.

**Steps:**
1. Run V2 job.
2. Inspect raw bytes of the output file.

**Expected Result:**
- Every line terminates with `\n` (0x0A) only.
- No `\r` (0x0D) characters present before `\n`.

---

### TC-15: Overwrite write mode
**Traces to:** Writer Config (writeMode: Overwrite), W9
**Objective:** Verify that each run replaces the entire output file.

**Steps:**
1. Run V2 job for effective date 2024-10-01.
2. Record the output content.
3. Run V2 job for effective date 2024-10-02.
4. Read the output file.

**Expected Result:**
- The output file contains ONLY data from the 2024-10-02 run.
- Data from 2024-10-01 is gone (overwritten).
- Header appears at the top.

---

### TC-16: No trailer row
**Traces to:** Writer Config (no trailerFormat)
**Objective:** Verify no trailer row is appended to the output.

**Steps:**
1. Run V2 job.
2. Inspect the last line of the output file.

**Expected Result:**
- The last line is a data row, not a trailer.
- No line matches a trailer pattern.

---

### TC-17: AP1 eliminated - transactions table removed
**Traces to:** FSD Section 3 (AP1)
**Objective:** Verify the V2 job config does NOT source the transactions table.

**Steps:**
1. Read the V2 job config JSON.
2. Check all DataSourcing entries.

**Expected Result:**
- No DataSourcing entry references `table: "transactions"`.
- Only `compliance_events`, `wire_transfers`, and `customers` are sourced.
- `high_txn_count` is hardcoded as 0 in the SQL.

---

### TC-18: AP3 eliminated - External replaced by SQL Transformation
**Traces to:** FSD Section 3 (AP3)
**Objective:** Verify that the V2 job uses a Transformation module instead of an External module.

**Steps:**
1. Read the V2 job config JSON.
2. Check module chain.

**Expected Result:**
- No `"type": "External"` entry in the modules array.
- A `"type": "Transformation"` entry exists with the SQL query.
- No V2 External module file exists for this job (no `CustomerComplianceRiskV2Processor.cs`).

---

### TC-19: AP4 eliminated - unused columns removed
**Traces to:** FSD Section 3 (AP4)
**Objective:** Verify that DataSourcing entries only request columns needed for the output.

**Steps:**
1. Read the V2 job config JSON.
2. Check columns arrays.

**Expected Result:**
- `compliance_events` columns: `["customer_id"]` only (not `event_id`, `event_type`, `status`).
- `wire_transfers` columns: `["customer_id"]` only (not `wire_id`, `amount`, `direction`).
- `customers` columns: `["id", "first_name", "last_name"]`.

---

### TC-20: AP6 eliminated - row-by-row replaced by SQL set ops
**Traces to:** FSD Section 3 (AP6)
**Objective:** Verify that V2 uses SQL set-based operations (GROUP BY, LEFT JOIN, COUNT) instead of C# foreach loops.

**Steps:**
1. Read the Transformation SQL in the V2 job config.
2. Verify it uses SQL aggregation constructs.

**Expected Result:**
- SQL uses `LEFT JOIN` subqueries with `COUNT(*)` and `GROUP BY customer_id`.
- No procedural iteration logic (since there is no External module).

---

### TC-21: Customer with zero compliance events and zero wires
**Traces to:** BR-1 (edge case)
**Objective:** Verify that a customer with no compliance events and no wire transfers still appears in the output with all counts = 0 and risk_score = 0.

**Steps:**
1. Identify a customer with no matching compliance_events or wire_transfers for the effective date.
2. Run V2 job and find their output row.

**Expected Result:**
- Row exists for the customer.
- `compliance_events = 0`
- `wire_count = 0`
- `high_txn_count = 0`
- `risk_score = 0.0` (or `0`, depending on formatting)

---

### TC-22: Customer with compliance events but no wires
**Traces to:** BR-1, BR-2, BR-3 (edge case)
**Objective:** Verify correct output when a customer has compliance events but zero wire transfers.

**Steps:**
1. Find such a customer.
2. Run V2 job and check their output row.

**Expected Result:**
- `compliance_events` = correct count from source.
- `wire_count = 0`
- `high_txn_count = 0`
- `risk_score = compliance_events * 30.0`

---

### TC-23: Customer with wires but no compliance events
**Traces to:** BR-1, BR-2, BR-3 (edge case)
**Objective:** Verify correct output when a customer has wire transfers but zero compliance events.

**Steps:**
1. Find such a customer.
2. Run V2 job and check their output row.

**Expected Result:**
- `compliance_events = 0`
- `wire_count` = correct count from source.
- `high_txn_count = 0`
- `risk_score = wire_count * 20.0`

---

### TC-24: NULL first_name AND NULL last_name simultaneously
**Traces to:** BR-10 (edge case)
**Objective:** Verify correct output when both first_name and last_name are NULL.

**Steps:**
1. Find or simulate a customer with both name fields NULL.
2. Run V2 job and check their output row.

**Expected Result:**
- Both `first_name` and `last_name` are empty strings in the CSV.
- In CSV format: `1001,,,3,2,0,130.0,2024-10-01` (both name fields empty between commas).
- customer_id, compliance_events, wire_count, high_txn_count, risk_score, and as_of are all correctly populated.

---

### TC-25: Boundary date - first effective date 2024-10-01
**Traces to:** FSD Section 6
**Objective:** Verify that the job correctly processes the first effective date.

**Steps:**
1. Run V2 job for effective date 2024-10-01.
2. Examine output.

**Expected Result:**
- Output contains customer data for 2024-10-01.
- as_of values in output = 2024-10-01.
- Compliance event and wire counts match source data for that date.

---

### TC-26: Overwrite mode multi-day - only last day survives
**Traces to:** W9, Writer Config
**Objective:** Verify that in a multi-day auto-advance scenario, only the last effective date's output survives due to Overwrite mode.

**Steps:**
1. Run V2 job with auto-advance through dates 2024-10-01 to 2024-10-03.
2. Examine the final output file.

**Expected Result:**
- The output file contains data ONLY from 2024-10-03 (the last date processed).
- No data from 2024-10-01 or 2024-10-02 is present.
- This matches V1 behavior: Overwrite mode replaces the file each time.

---

### TC-27: Output column order matches V1
**Traces to:** FSD Section 4 (Output Schema)
**Objective:** Verify that the output columns appear in the exact order specified by V1.

**Steps:**
1. Run V2 job.
2. Read the header row of the output CSV.

**Expected Result:**
- Column order: `customer_id`, `first_name`, `last_name`, `compliance_events`, `wire_count`, `high_txn_count`, `risk_score`, `as_of`.
- Matches V1 output schema exactly.

---

### TC-28: Row ordering V1 vs V2
**Traces to:** FSD Appendix Risk Register
**Objective:** Verify that V2 output row order matches V1, or document any ordering differences.

**Steps:**
1. Run both V1 and V2 for the same effective date.
2. Compare the customer_id ordering in both outputs.

**Expected Result:**
- Rows appear in the same order. V1 iterates customers in DataFrame order (DataSourcing retrieval order, which is `ORDER BY as_of`). V2's SQL follows SQLite's internal table scan order, which should match insertion order (i.e., DataSourcing return order).
- If ordering differs, this is a known risk per FSD. Mitigation: add `ORDER BY customer_id` or `ORDER BY c.ROWID` to the SQL.

---

### TC-29: Empty compliance_events or wire_transfers with non-empty customers
**Traces to:** FSD Appendix Risk Register
**Objective:** Verify correct behavior when compliance_events or wire_transfers tables are empty for an effective date but customers is not.

**Steps:**
1. Run V2 job for a date where compliance_events has zero rows but customers has data.
2. Examine output.

**Expected Result:**
- All customers appear in output.
- `compliance_events = 0` for all customers (LEFT JOIN returns NULL, COALESCE converts to 0).
- `wire_count` correctly populated from wire_transfers (or 0 if also empty).
- risk_score correctly computed.

**Note:** Per FSD, the Transformation module's `RegisterTable` skips empty DataFrames. If `compliance_events` is empty, the SQLite table won't exist, and the LEFT JOIN subquery may fail. This test validates whether the framework handles this gracefully or whether a Tier 2 escalation is needed.

---

### TC-30: Large compliance event count (risk score magnitude)
**Traces to:** BR-6, BR-8 (stress case)
**Objective:** Verify that the risk score formula produces correct results for customers with many compliance events or wires.

**Steps:**
1. Find a customer with a high count of compliance events and/or wires.
2. Run V2 job and verify the risk_score.

**Expected Result:**
- risk_score = `(compliance_events * 30) + (wire_count * 20)`.
- For example, a customer with 10 compliance events and 5 wires: `(10 * 30) + (5 * 20) = 400.0`.
- No overflow or precision issues (values stay well within double/REAL range).

---

### TC-31: Double/REAL arithmetic equivalence
**Traces to:** W5, W6, BR-8
**Objective:** Verify that SQLite REAL arithmetic produces identical results to C# double arithmetic for the risk score formula.

**Steps:**
1. Run both V1 and V2 for the same effective date.
2. Compare risk_score values for every customer.

**Expected Result:**
- All risk_score values match exactly between V1 and V2.
- Per BR-8, since all inputs are integers and weights are integer multiples (30, 20, 10), the results are always exact integers, so there are no floating-point epsilon differences.
- The rounding mode difference (banker's vs half-away-from-zero) does not apply because no midpoint values are possible with these inputs.

---

## Proofmark Comparison Notes

Per FSD Section 8, the Proofmark config for this job should use:
- `reader: csv`
- `header_rows: 1`
- `trailer_rows: 0`
- `threshold: 100.0`
- No excluded or fuzzy columns

Since the job uses Overwrite mode, only the last effective date's output will be present for comparison. V1 and V2 must be run through the same date range to ensure the final overwritten output matches.

**Known risks for Proofmark comparison:**
1. **Row ordering:** If SQLite internal order differs from V1's DataFrame iteration order, Proofmark may report mismatches. Mitigation: add ORDER BY to SQL.
2. **risk_score formatting:** If V1 outputs `130.0` but V2 outputs `130` (or vice versa), Proofmark may report value mismatches. This is a formatting concern, not a computation concern.
3. **Empty table handling:** If compliance_events or wire_transfers is empty for some effective dates, the SQL query may fail on missing SQLite tables. This would need a Tier 2 escalation.
