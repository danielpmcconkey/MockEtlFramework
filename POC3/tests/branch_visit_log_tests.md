# BranchVisitLog — Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01 | BR-1 | Row-by-row enrichment replaced by SQL LEFT JOINs produces equivalent output |
| TC-02 | BR-2 | Branch lookup uses last-write-wins (latest as_of) when multiple snapshots exist |
| TC-03 | BR-3 | Customer lookup uses last-write-wins (latest as_of) when multiple snapshots exist |
| TC-04 | BR-4 | Weekend guard: empty customers DataFrame produces zero output rows |
| TC-05 | BR-5 | Empty branch_visits DataFrame produces zero output rows |
| TC-06 | BR-6 | Missing branch_id defaults branch_name to empty string |
| TC-07 | BR-7 | Missing customer_id defaults first_name and last_name to NULL |
| TC-08 | BR-8 | addresses table excluded from V2 DataSourcing (AP1 elimination) |
| TC-09 | BR-9 | Branch address columns excluded from V2 DataSourcing (AP4 elimination) |
| TC-10 | BR-10 | Output row ordering matches V1: ORDER BY as_of, visit_id |
| TC-11 | BR-6, BR-7 (AP5) | Asymmetric NULL handling: existing customer with NULL name yields empty string, missing customer yields NULL |
| TC-12 | — | Output schema: correct columns in correct order |
| TC-13 | — | ParquetFileWriter config: 3 parts, Append mode, correct output directory |
| TC-14 | — | Proofmark: all columns STRICT, no exclusions, no fuzzy overrides |
| TC-15 | — | Edge case: multi-date range with lookup collisions |
| TC-16 | — | Edge case: re-run same date produces duplicate rows (Append mode) |
| TC-17 | BR-2, BR-3 | SQL deduplication subqueries: MAX(as_of) + self-join per key |

---

## Test Cases

### TC-01: SQL LEFT JOINs produce equivalent output to V1 External module
- **Traces to:** BR-1
- **Input conditions:** Standard branch_visits data with matching customers and branches for a single effective date (e.g., 2024-10-01). Multiple visits across multiple branches and customers.
- **Expected output:** Each visit row enriched with the customer's first_name, last_name (from customers table) and branch_name (from branches table). Output matches what the V1 BranchVisitEnricher External module would produce row-for-row.
- **Verification method:** Run V1 and V2 jobs for the same effective date. Compare V2 output at `Output/double_secret_curated/branch_visit_log/` against V1 baseline at `Output/curated/branch_visit_log/` using Proofmark with strict comparison on all columns.

### TC-02: Branch lookup last-write-wins deduplication
- **Traces to:** BR-2
- **Input conditions:** Effective date range spans multiple as_of dates (e.g., 2024-10-01 through 2024-10-03). The branches table has entries for branch_id=1 on 2024-10-01 with branch_name="Old Name" and on 2024-10-03 with branch_name="New Name".
- **Expected output:** All visits referencing branch_id=1 (regardless of the visit's own as_of date) show branch_name="New Name" -- the value from the latest as_of snapshot. The V2 SQL achieves this via `INNER JOIN (SELECT branch_id, MAX(as_of) AS max_as_of FROM branches GROUP BY branch_id)`.
- **Verification method:** Proofmark full-run comparison. Additionally, inspect V2 output rows for branch_id=1 to confirm they all reflect the latest branch_name.

### TC-03: Customer lookup last-write-wins deduplication
- **Traces to:** BR-3
- **Input conditions:** Effective date range spans multiple as_of dates. The customers table has entries for customer id=100 on 2024-10-01 with first_name="Alice" and on 2024-10-03 with first_name="Alicia" (name change).
- **Expected output:** All visits referencing customer_id=100 show first_name="Alicia" and the last_name from the 2024-10-03 snapshot. The V2 SQL achieves this via `INNER JOIN (SELECT id, MAX(as_of) AS max_as_of FROM customers GROUP BY id)`.
- **Verification method:** Proofmark full-run comparison. Inspect V2 output for customer_id=100 to confirm latest snapshot values.

### TC-04: Weekend guard — empty customers produces zero output
- **Traces to:** BR-4
- **Input conditions:** Effective date is a weekend date (e.g., Saturday 2024-10-05 or Sunday 2024-10-06) where the customers table has no snapshot rows. The customers DataFrame is empty.
- **Expected output:** Zero data rows written. In V1, the External module explicitly checks for null/empty customers and returns an empty DataFrame. In V2, the Transformation module's `RegisterTable` skips registration of the empty customers table, causing the SQL to fail with "no such table: customers." The job fails for that date. Net effect: no data written for that date, matching V1 behavior (empty output = no rows appended).
- **Verification method:** Verify that the output directory contains no new part files for the weekend date. Confirm the job_runs record for that date shows either Succeeded (zero rows) or Failed (table not registered). Either outcome produces no appended data, matching V1.

### TC-05: Empty branch_visits produces zero output
- **Traces to:** BR-5
- **Input conditions:** Effective date has no branch_visits rows (empty DataFrame).
- **Expected output:** Zero data rows written. If branch_visits is the driving table and it has zero rows, a SQL SELECT from it produces zero output rows. However, if the empty DataFrame causes `RegisterTable` to skip, the SQL errors. Net effect: no data appended.
- **Verification method:** Same as TC-04. Verify no new part files for that date. Confirm V1 and V2 produce identical (empty) output for that date.

### TC-06: Missing branch_id defaults to empty string
- **Traces to:** BR-6
- **Input conditions:** A visit row references branch_id=999 which does not exist in the branches table for any as_of date in the effective range.
- **Expected output:** The output row for that visit has branch_name="" (empty string, not NULL). The V2 SQL achieves this via `COALESCE(b.branch_name, '') AS branch_name`.
- **Verification method:** Proofmark strict comparison on branch_name column. Inspect the specific row in V2 output to confirm empty string rather than NULL.

### TC-07: Missing customer_id defaults to NULL
- **Traces to:** BR-7
- **Input conditions:** A visit row references customer_id=9999 which does not exist in the customers table for any as_of date in the effective range.
- **Expected output:** The output row for that visit has first_name=NULL and last_name=NULL. The V2 SQL achieves this because the LEFT JOIN on customers produces NULL for unmatched rows, and the `CASE WHEN c.id IS NOT NULL THEN ... ELSE NULL END` returns NULL when the customer is not found.
- **Verification method:** Proofmark strict comparison. Inspect the specific row to confirm NULL values (not empty strings) for first_name and last_name.

### TC-08: addresses table excluded from V2 (AP1 elimination)
- **Traces to:** BR-8
- **Input conditions:** V2 job config (`branch_visit_log_v2.json`).
- **Expected output:** The V2 config contains no DataSourcing entry for the `addresses` table. V1 sourced addresses but the BranchVisitEnricher External module never read it from shared state.
- **Verification method:** Inspect the V2 JSON config to confirm there is no module with `"table": "addresses"`. Confirm output equivalence via Proofmark (removing an unused source does not change output).

### TC-09: Branch address columns excluded from V2 (AP4 elimination)
- **Traces to:** BR-9
- **Input conditions:** V2 job config branches DataSourcing entry.
- **Expected output:** The V2 branches DataSourcing only sources `["branch_id", "branch_name"]`. V1 sourced `address_line1`, `city`, `state_province`, `postal_code`, `country` as well, but the External module never used them.
- **Verification method:** Inspect the V2 JSON config branches DataSourcing to confirm only `branch_id` and `branch_name` in the columns array. Confirm output equivalence via Proofmark.

### TC-10: Output row ordering preserved
- **Traces to:** BR-10
- **Input conditions:** Multiple visits across multiple as_of dates with varying visit_ids.
- **Expected output:** Output rows are ordered by `as_of ASC, visit_id ASC`. V1's External module iterates branch_visits rows in order (DataSourcing sorts by as_of), and within the same as_of, the original DB row order (by visit_id) is preserved. V2 SQL uses `ORDER BY bv.as_of, bv.visit_id`.
- **Verification method:** Proofmark full-run comparison (order matters for Parquet part-file splitting). Additionally, inspect V2 output to verify rows are sorted by as_of then visit_id.

### TC-11: Asymmetric NULL handling (AP5 reproduction)
- **Traces to:** BR-6, BR-7, AP5
- **Input conditions:** Three scenarios in the same effective date:
  1. Visit references customer_id=100 who exists in customers but has first_name=NULL in the source data.
  2. Visit references customer_id=9999 who does not exist in customers at all.
  3. Visit references branch_id=50 who exists in branches with branch_name=NULL in the source data.
- **Expected output:**
  1. Customer exists, name is NULL in source: first_name="" (empty string), last_name uses same treatment. V1's `?.ToString() ?? ""` converts NULL to empty string for found customers.
  2. Customer missing entirely: first_name=NULL, last_name=NULL. V1's `GetValueOrDefault` returns `(null!, null!)`.
  3. Branch exists with NULL branch_name: branch_name="" (empty string). V1's branch lookup stores `branch_name?.ToString() ?? ""`.
- **Verification method:** The V2 SQL `CASE WHEN c.id IS NOT NULL THEN COALESCE(c.first_name, '') ELSE NULL END` handles this. Proofmark strict comparison validates. Manually inspect edge-case rows to confirm the asymmetric behavior.

### TC-12: Output schema verification
- **Traces to:** Output Schema (BRD/FSD)
- **Input conditions:** Any successful V2 run.
- **Expected output:** Output Parquet files contain exactly these columns in this order: `visit_id`, `customer_id`, `first_name`, `last_name`, `branch_id`, `branch_name`, `visit_timestamp`, `visit_purpose`, `as_of`. No extra columns, no missing columns, no reordering.
- **Verification method:** Read V2 Parquet output and verify column names and order. Compare against V1 output column schema.

### TC-13: ParquetFileWriter configuration verification
- **Traces to:** Writer Configuration (BRD/FSD)
- **Input conditions:** V2 job config and a successful multi-date run.
- **Expected output:**
  - Output directory: `Output/double_secret_curated/branch_visit_log/`
  - Number of part files per execution: 3 (`part-00000.parquet`, `part-00001.parquet`, `part-00002.parquet`)
  - Write mode: Append (new part files added on each execution, prior files not deleted)
  - Source DataFrame: `output`
- **Verification method:** After a multi-date run, count part files in the output directory. For N effective dates, expect N * 3 part files (Append mode accumulates). Verify part file naming convention.

### TC-14: Proofmark configuration validation
- **Traces to:** FSD Section 8
- **Input conditions:** Proofmark YAML config for branch_visit_log.
- **Expected output:** Config specifies:
  - `comparison_target: "branch_visit_log"`
  - `reader: parquet`
  - `threshold: 100.0`
  - No `columns.excluded` entries
  - No `columns.fuzzy` entries
  All columns are deterministic; no overrides are needed.
- **Verification method:** Review Proofmark YAML. Run Proofmark comparison between V1 (`Output/curated/branch_visit_log/`) and V2 (`Output/double_secret_curated/branch_visit_log/`). Expect 100% match.

### TC-15: Multi-date range lookup collisions
- **Traces to:** BR-2, BR-3, Edge Cases
- **Input conditions:** Effective date range spans 2024-10-01 through 2024-10-03. Branch_id=5 has different branch_name values across the three as_of dates. Customer_id=200 has a name change between 2024-10-01 and 2024-10-03.
- **Expected output:** All visit rows (from all three dates) use the branch_name and customer name from the 2024-10-03 snapshot (the latest as_of). A visit from 2024-10-01 referencing branch_id=5 gets the 2024-10-03 branch_name, not the 2024-10-01 branch_name. This is V1's last-write-wins behavior, faithfully reproduced by V2's `MAX(as_of)` subqueries.
- **Verification method:** Proofmark full-run comparison over the multi-date range. Inspect individual rows to confirm cross-date lookup behavior.

### TC-16: Append mode re-run produces duplicates
- **Traces to:** Write Mode Implications (BRD)
- **Input conditions:** Run V2 for effective date 2024-10-01, then run V2 again for the same date.
- **Expected output:** The output directory contains 6 part files (3 from the first run + 3 from the second run). The data from the first run is NOT overwritten. Duplicate data exists across the two sets of part files. This matches V1 Append behavior.
- **Verification method:** Count part files after two runs. Verify data duplication by reading all part files.

### TC-17: SQL deduplication subquery correctness
- **Traces to:** BR-2, BR-3
- **Input conditions:** Effective date range where a branch or customer has entries on multiple as_of dates, including one where the MAX(as_of) row has a different branch_name or customer name than earlier rows.
- **Expected output:** The deduplication subqueries `SELECT branch_id, MAX(as_of) AS max_as_of FROM branches GROUP BY branch_id` and `SELECT id, MAX(as_of) AS max_as_of FROM customers GROUP BY id` correctly isolate the latest snapshot per key. The outer JOIN on `br.branch_id = br_latest.branch_id AND br.as_of = br_latest.max_as_of` picks exactly one row per branch_id.
- **Verification method:** Proofmark full-run comparison. If branches has multiple as_of rows for the same branch_id, the V2 output must use the value from the latest as_of, matching V1's dictionary overwrite behavior.
