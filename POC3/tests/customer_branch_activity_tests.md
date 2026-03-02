# CustomerBranchActivity — Test Plan

## Overview

Test plan for the V2 rewrite of `CustomerBranchActivity`. This job produces a per-customer count of branch visits enriched with customer name information, output as CSV with header, CRLF line endings, Append mode, and no trailer.

**V2 Tier:** Tier 2 (DataSourcing + Minimal External + CsvFileWriter)
**BRD:** `POC3/brd/customer_branch_activity_brd.md`
**FSD:** `POC3/fsd/customer_branch_activity_fsd.md`

---

## Traceability Matrix

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01 | BR-1 | Aggregate visit count per customer |
| TC-02 | BR-2 | Customer name resolution via lookup (last-write-wins) |
| TC-03 | BR-3 | Weekend guard: empty customers produces empty output |
| TC-04 | BR-4 | Empty branch_visits produces empty output |
| TC-05 | BR-5 | Single as_of from first branch_visits row |
| TC-06 | BR-6 | Null names for customers not in customers table |
| TC-07 | BR-7 | branches table eliminated (AP1) |
| TC-08 | BR-8 | Unused columns eliminated (AP4) |
| TC-09 | BR-9 | Output row order follows first-encounter order |
| TC-10 | BR-10 | Cross-date aggregation of visit counts |
| TC-11 | Writer Config | CSV header present |
| TC-12 | Writer Config | CRLF line endings |
| TC-13 | Writer Config | Append write mode |
| TC-14 | Writer Config | No trailer row |
| TC-15 | Edge Case | NULL first_name in customers table |
| TC-16 | Edge Case | NULL last_name in customers table |
| TC-17 | Edge Case | Single customer with single visit |
| TC-18 | Edge Case | Customer with visits on every date in range |
| TC-19 | Edge Case | Multiple customers, some with zero visits |
| TC-20 | FSD Anti-Pattern | AP3 partially eliminated (LINQ, not foreach) |
| TC-21 | FSD Anti-Pattern | AP6 eliminated (set-based LINQ) |
| TC-22 | Edge Case | Null customers DataFrame (not just empty) |
| TC-23 | Edge Case | Null branch_visits DataFrame (not just empty) |
| TC-24 | Output Schema | Column order matches V1 exactly |
| TC-25 | Edge Case | Boundary date: first effective date 2024-10-01 |

---

## Test Cases

### TC-01: Aggregate visit count per customer
**Traces to:** BR-1
**Objective:** Verify that the output contains one row per unique customer_id from branch_visits, with the correct total visit count.

**Preconditions:** branch_visits contains rows for multiple customers with varying visit counts. customers table contains matching records.

**Steps:**
1. Run V2 job for a single effective date that has branch_visits data.
2. Examine the output CSV.

**Expected Result:**
- Each unique `customer_id` from branch_visits appears exactly once in the output.
- `visit_count` for each customer equals the total number of branch_visits rows for that customer_id.
- No customer_id from branch_visits is missing from output (even if not in customers table per BR-6).

**Verification:** Compare V2 output row count with `SELECT customer_id, COUNT(*) FROM datalake.branch_visits WHERE as_of = '{date}' GROUP BY customer_id`. Count of output rows should equal count of distinct customer_ids.

---

### TC-02: Customer name resolution via lookup (last-write-wins)
**Traces to:** BR-2
**Objective:** Verify that first_name and last_name are resolved from the customers table using last-write-wins semantics when multiple as_of dates exist.

**Preconditions:** Multi-day effective date range. Customer has different names on different as_of dates (or same name, verifiable via DataSourcing ORDER BY as_of).

**Steps:**
1. Run V2 job with an effective date range spanning multiple days.
2. For a customer with records on multiple as_of dates, check which name appears in output.

**Expected Result:**
- The customer's first_name and last_name correspond to their record from the latest as_of date in the effective range (DataSourcing orders by as_of, so the last entry per id wins in the dictionary).

**Verification:** Query `SELECT first_name, last_name FROM datalake.customers WHERE id = {customer_id} ORDER BY as_of DESC LIMIT 1` for the effective range and confirm it matches the output.

---

### TC-03: Weekend guard - empty customers produces empty output
**Traces to:** BR-3
**Objective:** Verify that if the customers DataFrame is empty (e.g., weekend date with no customer snapshot), the output is an empty DataFrame with the correct schema.

**Preconditions:** An effective date where no customer records exist in the datalake (or simulated empty DataFrame).

**Steps:**
1. Run V2 job for an effective date where customers returns zero rows.
2. Examine output.

**Expected Result:**
- Output contains zero data rows.
- If header is written (first run in Append mode), the header row still contains: `customer_id,first_name,last_name,as_of,visit_count`.
- No error or exception is thrown.

---

### TC-04: Empty branch_visits produces empty output
**Traces to:** BR-4
**Objective:** Verify that if branch_visits is empty, the output is an empty DataFrame with correct schema.

**Preconditions:** An effective date where no branch_visits records exist.

**Steps:**
1. Run V2 job for an effective date where branch_visits returns zero rows.
2. Examine output.

**Expected Result:**
- Output contains zero data rows.
- No error or exception is thrown.

---

### TC-05: Single as_of from first branch_visits row
**Traces to:** BR-5
**Objective:** Verify that the `as_of` column in ALL output rows contains the same value, taken from the first row of the branch_visits DataFrame (which is the earliest date in the effective range, since DataSourcing orders by as_of).

**Preconditions:** Multi-day effective date range (e.g., 2024-10-01 to 2024-10-03). branch_visits has data on multiple dates.

**Steps:**
1. Run V2 job with a multi-day effective date range.
2. Examine the as_of column in all output rows.

**Expected Result:**
- Every output row has the same as_of value.
- That value equals the earliest as_of date in the branch_visits data for the effective range (because DataSourcing orders by as_of, and the first row has the earliest date).

**Verification:** The as_of value should match `SELECT MIN(as_of) FROM datalake.branch_visits WHERE as_of BETWEEN '{min_date}' AND '{max_date}'`.

---

### TC-06: Null names for customers not in customers table
**Traces to:** BR-6
**Objective:** Verify that when a customer_id appears in branch_visits but NOT in the customers table, the output row has null/empty first_name and last_name.

**Preconditions:** A customer_id in branch_visits that does not have a corresponding entry in customers for the effective date range.

**Steps:**
1. Identify a customer_id in branch_visits with no match in customers (or simulate via data).
2. Run V2 job and examine the output row for that customer.

**Expected Result:**
- The row exists in the output (the visit is still counted).
- `first_name` is null (empty in CSV).
- `last_name` is null (empty in CSV).
- `visit_count` correctly reflects the number of visits for that customer.

---

### TC-07: branches table eliminated (AP1)
**Traces to:** BR-7, FSD Section 3 (AP1)
**Objective:** Verify that the V2 job config does NOT source the branches table.

**Preconditions:** V2 job config exists at `JobExecutor/Jobs/customer_branch_activity_v2.json`.

**Steps:**
1. Read the V2 job config JSON.
2. Check all DataSourcing entries.

**Expected Result:**
- No DataSourcing entry references `table: "branches"`.
- Only `branch_visits` and `customers` are sourced.

---

### TC-08: Unused columns eliminated (AP4)
**Traces to:** BR-8, FSD Section 3 (AP4)
**Objective:** Verify that the V2 DataSourcing for branch_visits only sources `customer_id` (not `visit_id`, `branch_id`, `visit_purpose`).

**Preconditions:** V2 job config exists.

**Steps:**
1. Read the V2 job config JSON.
2. Check the columns array for the branch_visits DataSourcing entry.

**Expected Result:**
- branch_visits columns: `["customer_id"]` only.
- `as_of` is auto-appended by the framework and need not be listed.

---

### TC-09: Output row order follows first-encounter order
**Traces to:** BR-9
**Objective:** Verify that output rows are ordered by the first appearance of each customer_id in the branch_visits DataFrame (insertion order).

**Preconditions:** branch_visits has data for multiple customers, with a known order of first appearance (based on as_of ordering from DataSourcing).

**Steps:**
1. Run V2 job for a single effective date.
2. Extract the customer_id column from the output in order.
3. Compare against the order of first appearance of each customer_id in branch_visits (ordered by as_of, then row order within date).

**Expected Result:**
- The output customer_id ordering matches the first-encounter ordering from branch_visits.
- This is preserved by LINQ GroupBy, which maintains group order by first appearance.

---

### TC-10: Cross-date aggregation of visit counts
**Traces to:** BR-10
**Objective:** Verify that visit counts accumulate across ALL as_of dates in the effective range, not per-date.

**Preconditions:** Multi-day effective date range. A customer has visits on multiple dates.

**Steps:**
1. Run V2 job with effective range spanning multiple days (e.g., 2024-10-01 to 2024-10-03).
2. For a customer with visits on day 1 and day 2, check the visit_count.

**Expected Result:**
- visit_count = total visits across ALL dates in the range (e.g., 3 visits on day 1 + 2 visits on day 2 = visit_count of 5).
- Visits are NOT counted per-date and then summed differently.

**Verification:** `SELECT COUNT(*) FROM datalake.branch_visits WHERE customer_id = {id} AND as_of BETWEEN '{min}' AND '{max}'` should match the output visit_count.

---

### TC-11: CSV header present
**Traces to:** Writer Config (includeHeader: true)
**Objective:** Verify that the first line of the CSV output is a header row.

**Steps:**
1. Run V2 job (first run, so file does not yet exist).
2. Read the first line of the output file.

**Expected Result:**
- First line: `customer_id,first_name,last_name,as_of,visit_count`
- Matches V1's column order exactly.

---

### TC-12: CRLF line endings
**Traces to:** Writer Config (lineEnding: CRLF)
**Objective:** Verify that the output CSV uses CRLF (`\r\n`) line endings.

**Steps:**
1. Run V2 job.
2. Inspect the raw bytes of the output file.

**Expected Result:**
- Every line terminates with `\r\n` (0x0D 0x0A), not just `\n`.

---

### TC-13: Append write mode
**Traces to:** Writer Config (writeMode: Append)
**Objective:** Verify that running the job for multiple effective dates appends data rather than overwriting.

**Steps:**
1. Run V2 job for effective date 2024-10-01.
2. Record the file size and row count.
3. Run V2 job for effective date 2024-10-02.
4. Record the new file size and row count.

**Expected Result:**
- File size increases after the second run.
- Data rows from both runs are present in the file.
- Header appears only once (at the top, from the first run). Per CsvFileWriter Append behavior, header is NOT re-written when the file already exists.

---

### TC-14: No trailer row
**Traces to:** Writer Config (no trailerFormat)
**Objective:** Verify that no trailer row is appended to the output.

**Steps:**
1. Run V2 job.
2. Inspect the last line of the output file.

**Expected Result:**
- The last line is a data row, not a trailer.
- No line matches a trailer pattern (e.g., `TRAILER|...`).

---

### TC-15: NULL first_name in customers table
**Traces to:** BR-6 (edge case)
**Objective:** Verify behavior when a customer's first_name is NULL in the source data.

**Preconditions:** A customer record with `first_name = NULL` exists in datalake.customers.

**Steps:**
1. Run V2 job.
2. Find the output row for that customer.

**Expected Result:**
- The `first_name` field in the output is null (empty field in CSV, appearing as `,,`).
- The row is otherwise complete (customer_id, last_name, as_of, visit_count all populated).

**Note:** Per FSD External Module Design, the V2 lookup uses `?.ToString() ?? ""` semantics from V1 code. However, per BRD BR-6, when a customer IS found, the name values come from the lookup. The FSD shows `names.firstName` is set from `last["first_name"]?.ToString() ?? ""`, which would produce an empty string, not null. The test should verify the actual V2 behavior matches V1.

---

### TC-16: NULL last_name in customers table
**Traces to:** BR-6 (edge case)
**Objective:** Same as TC-15 but for last_name.

**Expected Result:**
- The `last_name` field in the output is null/empty.
- All other fields are populated correctly.

---

### TC-17: Single customer with single visit
**Traces to:** BR-1 (boundary case)
**Objective:** Verify correct output when only one customer has exactly one visit.

**Steps:**
1. For an effective date where only one customer visited a branch once, run V2 job.
2. Examine output.

**Expected Result:**
- Exactly one data row in output.
- visit_count = 1.
- customer_id, first_name, last_name, as_of all correctly populated.

---

### TC-18: Customer with visits on every date in range
**Traces to:** BR-10 (stress case)
**Objective:** Verify that visit counts correctly aggregate when a customer has visits on every date in a multi-day range.

**Preconditions:** Multi-day effective range. Customer has visits on each day.

**Steps:**
1. Run V2 job with a multi-day effective range.
2. Check the visit_count for a customer with visits on every date.

**Expected Result:**
- visit_count = sum of all visits across all dates in the range.
- as_of = earliest date in the range (per BR-5).

---

### TC-19: Multiple customers, some with zero visits
**Traces to:** BR-1 (edge case)
**Objective:** Verify that customers with zero visits do NOT appear in the output (since output is driven by branch_visits, not by customers).

**Preconditions:** Customers exist in the customers table who have no matching branch_visits records for the effective date.

**Steps:**
1. Run V2 job.
2. Check if customers without any branch_visits appear in output.

**Expected Result:**
- Customers with zero visits are NOT in the output. The output is driven by branch_visits.customer_id (BR-1: "counts total branch visits per customer"), not by the customers table. Only customers who have at least one visit appear.

---

### TC-20: AP3 partially eliminated (LINQ vs foreach)
**Traces to:** FSD Section 3 (AP3)
**Objective:** Verify that the V2 External module uses LINQ set-based operations, not row-by-row foreach loops.

**Steps:**
1. Read `ExternalModules/CustomerBranchActivityV2Processor.cs`.
2. Check for foreach loops over data rows.

**Expected Result:**
- No `foreach` loops over `Rows` collections for counting or aggregation.
- LINQ `GroupBy`, `ToDictionary`, and `Select` used instead.
- External module is justified solely by the empty-table guard (framework limitation).

---

### TC-21: AP6 eliminated (set-based LINQ)
**Traces to:** FSD Section 3 (AP6)
**Objective:** Same verification as TC-20, confirming row-by-row iteration is eliminated.

**Expected Result:**
- Three V1 foreach loops replaced with LINQ operations.
- Customer name lookup uses `ToDictionary` with `GroupBy` (last-write-wins).
- Visit counting uses `GroupBy` + `Count()`.
- Output row construction uses `Select`.

---

### TC-22: Null customers DataFrame
**Traces to:** BR-3 (edge case)
**Objective:** Verify that if the customers DataFrame is null (not just empty), the output is an empty DataFrame.

**Steps:**
1. Simulate a scenario where customers DataFrame is null in shared state (e.g., if DataSourcing returns null for an absent table).
2. Run the External module.

**Expected Result:**
- Output is an empty DataFrame with the correct schema: `customer_id, first_name, last_name, as_of, visit_count`.
- No NullReferenceException or other error.

---

### TC-23: Null branch_visits DataFrame
**Traces to:** BR-4 (edge case)
**Objective:** Verify that if the branch_visits DataFrame is null, the output is an empty DataFrame.

**Expected Result:**
- Same as TC-22: empty DataFrame with correct schema, no error.

---

### TC-24: Output column order matches V1
**Traces to:** FSD Section 4 (Output Schema)
**Objective:** Verify that the output columns appear in the exact order specified by V1.

**Steps:**
1. Run V2 job.
2. Read the header row of the output CSV.

**Expected Result:**
- Column order: `customer_id`, `first_name`, `last_name`, `as_of`, `visit_count`.
- This matches V1 output column order per [CustomerBranchActivityBuilder.cs:10-13].

---

### TC-25: Boundary date - first effective date 2024-10-01
**Traces to:** Writer Config, FSD Section 6
**Objective:** Verify that the job correctly processes the first effective date (2024-10-01), the bootstrap date specified in the job config.

**Steps:**
1. Run V2 job for effective date 2024-10-01.
2. Examine the output.

**Expected Result:**
- Output contains data for 2024-10-01.
- as_of value in output rows = 2024-10-01.
- File is created (first run, Append mode creates a new file with header).

---

## Proofmark Comparison Notes

Per FSD Section 8, the Proofmark config for this job should use:
- `reader: csv`
- `header_rows: 1`
- `trailer_rows: 0`
- `threshold: 100.0`
- No excluded or fuzzy columns

Since the job uses Append mode, the comparison file will contain accumulated data from all effective dates. V1 and V2 must be run for the same date range to produce comparable output. The Proofmark comparison should cover the full file (all appended rows across all dates).
