# BranchCardActivity — Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01 | BR-1 | Synthetic branch assignment via customer_id % MAX(branch_id) + 1 |
| TC-02 | BR-2 | All card transactions included regardless of authorization_status |
| TC-03 | BR-3 | card_id sourced in V1 but unused; removed in V2 |
| TC-04 | BR-4 | customers JOIN serves only as existence filter |
| TC-05 | BR-5 | segments table removed in V2 (sourced but unused in V1) |
| TC-06 | BR-6 | MAX(branch_id) computed across all as_of dates in effective range |
| TC-07 | BR-7 | Output grouped by branch_id, branch_name, as_of |
| TC-08 | BR-8 | total_card_amount rounded to 2 decimal places |
| TC-09 | BR-9 | country column sourced in V1 but unused; removed in V2 |
| TC-10 | BR-1, BR-6 | Modulo assignment boundary: customer_id values at extremes |
| TC-11 | BR-4 | Card transactions with no matching customer excluded |
| TC-12 | — | Branches with no assigned transactions do not appear in output |
| TC-13 | — | Output column order matches spec |
| TC-14 | — | Parquet writer config: numParts=50, writeMode=Overwrite (W10) |
| TC-15 | — | Proofmark: all columns STRICT, no EXCLUDED or FUZZY |
| TC-16 | — | Weekend effective date behavior |
| TC-17 | — | Zero-row output scenario |
| TC-18 | — | Multi-day effective date range produces multiple as_of rows |
| TC-19 | BR-1 | Modulo stability: MAX(branch_id) consistent across dates |
| TC-20 | BR-8 | Rounding behavior for exact midpoint amounts |

## Test Cases

### TC-01: Synthetic Branch Assignment via Modulo
- **Traces to:** BR-1
- **Input conditions:** Card transactions exist for various customer_ids. Branches table has branch_ids 1 through 40 (observed max). The modulo formula is `(customer_id % MAX(branch_id)) + 1`.
- **Expected output:** Each card transaction is mapped to a branch using the formula. For example, customer_id = 83 with MAX(branch_id) = 40: branch_id = (83 % 40) + 1 = 4. The transaction contributes to branch 4's aggregates.
- **Verification method:** For a sample of customer_ids in the output, manually compute `(customer_id % 40) + 1` and verify the card transaction counts are attributed to the correct branch. Compare V1 and V2 — both should produce identical branch assignments.

### TC-02: All Authorization Statuses Included
- **Traces to:** BR-2
- **Input conditions:** Card transactions include rows with authorization_status = 'Approved' and 'Declined' (and any other status values present in the data).
- **Expected output:** All transactions contribute to card_txn_count and total_card_amount regardless of authorization_status. No filtering occurs.
- **Verification method:** Query source data to count total card transactions per branch (via modulo mapping). Compare against V2 output card_txn_count. The count should include both Approved and Declined transactions. V2 does not source authorization_status at all (AP4 removal), so the absence of filtering is guaranteed by design.

### TC-03: card_id Removed in V2 (AP4)
- **Traces to:** BR-3
- **Input conditions:** V1 sources card_id from card_transactions but never uses it in the SQL. V2 removes it from the DataSourcing columns list.
- **Expected output:** V2 output is identical to V1. The removal of card_id from DataSourcing does not affect any output column since it was never referenced in the transformation SQL.
- **Verification method:** Run both V1 and V2 for the same effective date. Compare outputs via Proofmark. They should match exactly. Verify card_id does not appear in V2's job config DataSourcing columns.

### TC-04: Customers JOIN as Existence Filter
- **Traces to:** BR-4
- **Input conditions:** Card transactions exist for customer_ids that have matching rows in the customers table, and potentially for customer_ids that do NOT have matching rows.
- **Expected output:** Only card transactions where customer_id has a match in customers.id are included in the output. No customer columns (first_name, last_name) appear in the output. V2 sources only the `id` column from customers (AP4 cleanup).
- **Verification method:** Identify any card transaction customer_ids not present in the customers table. Verify those transactions are excluded from the output totals. Verify no customer name columns appear in the output schema.

### TC-05: Segments Table Removed (AP1)
- **Traces to:** BR-5
- **Input conditions:** V1 sources the segments table (segment_id, segment_name) but never references it in the SQL. V2 removes the segments DataSourcing module entirely.
- **Expected output:** V2 output is identical to V1. The removal of the dead-end segments sourcing has no effect on output.
- **Verification method:** Verify the V2 job config does not contain a DataSourcing module for segments. Run Proofmark comparison — output should match.

### TC-06: MAX(branch_id) Across All Dates
- **Traces to:** BR-6
- **Input conditions:** The branches DataSourcing returns data for all as_of dates in the effective range. The subquery `SELECT MAX(branch_id) FROM branches` operates over this full DataFrame.
- **Expected output:** MAX(branch_id) = 40 (observed stable value). The modulo operation uses this value consistently for all transactions in the run.
- **Verification method:** Query `SELECT MAX(branch_id) FROM datalake.branches` to confirm the expected value. Run the V2 job and verify branch_id values in the output are all in range [1, 40]. No branch_id should exceed MAX(branch_id) or be less than 1 (the formula `(x % N) + 1` always produces values in [1, N]).

### TC-07: Grouping by Branch and Date
- **Traces to:** BR-7
- **Input conditions:** Card transactions exist for multiple branches and multiple as_of dates (in a multi-day run or across auto-advance).
- **Expected output:** One output row per unique (branch_id, branch_name, as_of) combination. Each row contains the aggregated card_txn_count and total_card_amount for that branch on that date.
- **Verification method:** For a specific date and branch, manually count card transactions attributed to that branch (via modulo) and sum their amounts. Compare against the output row. Verify no duplicate (branch_id, as_of) pairs exist in the output.

### TC-08: Rounding total_card_amount to 2 Decimal Places
- **Traces to:** BR-8
- **Input conditions:** Card transactions for a branch on a given date have amounts that sum to a value with more than 2 decimal places.
- **Expected output:** total_card_amount = ROUND(SUM(amount), 2). For typical monetary data with 2dp, the sum usually has <= 2dp and ROUND is a no-op. Both V1 and V2 use the same SQLite ROUND function.
- **Verification method:** Verify all total_card_amount values in the output have at most 2 decimal places. For a sample branch/date, manually compute SUM and ROUND and compare.

### TC-09: country Column Removed in V2 (AP4)
- **Traces to:** BR-9
- **Input conditions:** V1 sources country from branches but never references it in the SQL. V2 removes it from the DataSourcing columns list.
- **Expected output:** V2 output is identical to V1. The removal of the unused country column does not affect output.
- **Verification method:** Verify country is absent from V2's branches DataSourcing columns. Run Proofmark comparison — output should match.

### TC-10: Modulo Assignment Boundary Values
- **Traces to:** BR-1, BR-6
- **Input conditions:** Test edge cases for the modulo formula `(customer_id % 40) + 1`:
  - customer_id = 0: branch_id = (0 % 40) + 1 = 1
  - customer_id = 39: branch_id = (39 % 40) + 1 = 40
  - customer_id = 40: branch_id = (40 % 40) + 1 = 1 (wraps around)
  - customer_id = 1: branch_id = (1 % 40) + 1 = 2
  - Very large customer_id (e.g., 999999): branch_id = (999999 % 40) + 1
- **Expected output:** All computed branch_ids fall within [1, 40]. The modulo wrapping is correct.
- **Verification method:** For each test customer_id, compute expected branch_id manually. If those customer_ids exist in the data, verify their transactions are attributed to the correct branch in the output.

### TC-11: Card Transactions Without Matching Customer
- **Traces to:** BR-4
- **Input conditions:** A card transaction exists with a customer_id that has no corresponding row in the customers table (orphan transaction).
- **Expected output:** The orphan transaction is excluded from the output because the INNER JOIN on `ct.customer_id = c.id` eliminates it.
- **Verification method:** Query for card_transactions.customer_id values not in customers.id for the effective date. If any exist, verify those transactions do not contribute to any branch's card_txn_count or total_card_amount in the output.

### TC-12: Branches With No Assigned Transactions
- **Traces to:** BRD Edge Case "Zero transactions for a branch"
- **Input conditions:** A branch_id exists in the branches table but no card transaction maps to it via the modulo formula for the given effective date.
- **Expected output:** That branch does NOT appear in the output. The SQL uses INNER JOINs (no outer join to branches), so branches with no transactions are excluded.
- **Verification method:** Identify a branch_id that receives zero transactions via modulo mapping for a specific date. Verify it is absent from the output for that date.

### TC-13: Output Column Order
- **Traces to:** FSD Section 4
- **Input conditions:** Run the V2 job for any valid effective date.
- **Expected output:** Parquet output columns are in this exact order: branch_id, branch_name, card_txn_count, total_card_amount, as_of.
- **Verification method:** Read the Parquet file schema metadata. Verify column names and order match the specification exactly. Verify there are exactly 5 columns — no extra columns from vestigial sourcing.

### TC-14: Parquet Writer — numParts=50 and Overwrite (W10)
- **Traces to:** BRD Writer Configuration, FSD Section 7, W10
- **Input conditions:** Run the V2 job. The output has at most ~40 rows per date (one per branch).
- **Expected output:** Output is written to `Output/double_secret_curated/branch_card_activity/` with 50 part files (part-00000.parquet through part-00049.parquet). Most part files will be empty (since max ~40 data rows spread across 50 parts). Write mode is Overwrite.
- **Verification method:** Verify exactly 50 part files exist in the output directory. Verify the total row count across all parts matches the expected number of branch/date combinations. Verify no stale data from prior runs (Overwrite mode).

### TC-15: Proofmark Configuration — All Columns STRICT
- **Traces to:** FSD Section 8
- **Input conditions:** Run Proofmark comparison between V1 (Output/curated/branch_card_activity/) and V2 (Output/double_secret_curated/branch_card_activity/).
- **Expected output:** All 5 columns compared strictly. No EXCLUDED or FUZZY overrides. Threshold = 100.0. Reader = parquet.
- **Verification method:** Confirm the Proofmark YAML config has: comparison_target "branch_card_activity", reader "parquet", threshold 100.0, no columns section. Both V1 and V2 use the same SQLite ROUND function, so no rounding mode divergence is expected. Proofmark should PASS.

### TC-16: Weekend Effective Date
- **Traces to:** BRD Edge Case "Weekend data"
- **Input conditions:** Effective date falls on a Saturday or Sunday. Per BRD observation, card transactions and branch data exist on all dates including weekends.
- **Expected output:** Normal output produced with data for the weekend date. No special weekend skip or fallback logic applies. The as_of column reflects the weekend date.
- **Verification method:** Run the V2 job for a known weekend date. Verify output is produced with the correct as_of. Compare V1 and V2 — both should produce identical results since neither has weekend guard logic.

### TC-17: Zero-Row Output
- **Traces to:** BRD Edge Cases
- **Input conditions:** Effective date selected such that either: (a) no card transactions exist, (b) no customers match the transaction customer_ids (all filtered by inner join), or (c) no branches match via modulo mapping.
- **Expected output:** Empty Parquet output — zero data rows, valid schema with 5 columns.
- **Verification method:** Verify the output Parquet files parse without error, contain the expected 5-column schema, and have zero data rows. This validates the framework handles empty result sets from the SQL gracefully.

### TC-18: Multi-Day Effective Date Range
- **Traces to:** BR-7, BRD Write Mode Implications
- **Input conditions:** Job runs with an effective date range spanning multiple days (e.g., 3 days). Card transactions exist for each date.
- **Expected output:** Multiple output rows per branch — one per as_of date. The GROUP BY includes ct.as_of, so branch aggregates are date-specific. Overwrite mode means the final execution's output replaces all prior output.
- **Verification method:** Run the job for a multi-day range. Verify distinct as_of values in the output match the effective date range. Verify each (branch_id, as_of) pair has correct aggregates for that specific date. Compare V1 and V2.

### TC-19: Modulo Stability — MAX(branch_id) Consistency
- **Traces to:** BR-1, BR-6, BRD Edge Case "Modulo branch assignment"
- **Input conditions:** Branches table has a stable set of branch_ids across all as_of dates. MAX(branch_id) = 40 for all dates in the effective range.
- **Expected output:** The modulo mapping is consistent — the same customer_id always maps to the same branch_id within a single run. If MAX(branch_id) were to change across dates (branches added/removed), the modulo result would shift, potentially causing incorrect aggregation.
- **Verification method:** Query `SELECT DISTINCT MAX(branch_id) FROM datalake.branches GROUP BY as_of` across the data range. Confirm the value is stable (always 40). If it varies, the modulo mapping becomes date-dependent and the output may be unreliable — flag for investigation.

### TC-20: Rounding Midpoint Behavior
- **Traces to:** BR-8
- **Input conditions:** Look for or construct a scenario where SUM(ct.amount) for a branch/date is exactly at a 2dp midpoint (e.g., 500.125).
- **Expected output:** Since both V1 and V2 use SQLite's ROUND function (both go through Transformation SQL), rounding behavior is identical. ROUND(500.125, 2) = 500.13 in SQLite (half-away-from-zero).
- **Verification method:** Verify V1 and V2 produce the same total_card_amount for the branch/date in question. Unlike bond_maturity_schedule (where V1 uses C# Math.Round), this job's V1 already uses SQLite ROUND, so no rounding mode divergence is expected. This test confirms that assumption.
