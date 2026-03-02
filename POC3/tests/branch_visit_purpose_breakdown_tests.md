# BranchVisitPurposeBreakdown — Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01 | BR-1 | Visit counts grouped by branch_id, visit_purpose, and as_of |
| TC-02 | BR-2 | total_branch_visits window function removed (AP8 elimination) with no output change |
| TC-03 | BR-3 | Date-aligned branch join on branch_id AND as_of |
| TC-04 | BR-4 | Output ordering: as_of, branch_id, visit_purpose |
| TC-05 | BR-5 | segments table excluded from V2 DataSourcing (AP1 elimination) |
| TC-06 | BR-6 | customer_id column excluded from V2 DataSourcing (AP4 elimination) |
| TC-07 | BR-7 | visit_id column excluded from V2 DataSourcing; COUNT(*) used instead of COUNT(visit_id) |
| TC-08 | BR-8 | Trailer format: END\|{row_count} |
| TC-09 | BR-9 | Inner join semantics: branches with no visits for a purpose/date are excluded |
| TC-10 | — | Output schema: correct columns in correct order |
| TC-11 | — | CsvFileWriter config: header, trailer, CRLF, Append mode |
| TC-12 | — | Proofmark: all columns STRICT, no exclusions, no fuzzy overrides |
| TC-13 | — | Edge case: zero visits for an effective date |
| TC-14 | — | Edge case: branch exists in branch_visits but not in branches for that as_of |
| TC-15 | — | Edge case: all five known visit purposes present for a branch on a single date |
| TC-16 | — | Edge case: Append mode accumulation with multiple trailers |
| TC-17 | — | Edge case: re-run same date produces duplicate rows |
| TC-18 | — | Edge case: NULL visit_purpose handling |

---

## Test Cases

### TC-01: Visit counts grouped correctly
- **Traces to:** BR-1
- **Input conditions:** Standard branch_visits data for a single effective date (e.g., 2024-10-01). Multiple branches, multiple visit purposes. For example: branch_id=1 has 3 Deposit visits, 2 Inquiry visits; branch_id=2 has 5 Account Opening visits.
- **Expected output:** One row per unique (branch_id, visit_purpose, as_of) combination. branch_id=1/Deposit/2024-10-01 with visit_count=3. branch_id=1/Inquiry/2024-10-01 with visit_count=2. branch_id=2/Account Opening/2024-10-01 with visit_count=5. The V2 SQL `GROUP BY bv.branch_id, b.branch_name, bv.visit_purpose, bv.as_of` with `COUNT(*)` produces these aggregations.
- **Verification method:** Run V1 and V2 for the same date. Proofmark strict comparison between `Output/curated/branch_visit_purpose_breakdown.csv` and `Output/double_secret_curated/branch_visit_purpose_breakdown.csv`.

### TC-02: Removal of total_branch_visits does not affect output
- **Traces to:** BR-2 (AP8 elimination)
- **Input conditions:** Any effective date with branch_visits data.
- **Expected output:** The V1 SQL computes `total_branch_visits` via `SUM(COUNT(*)) OVER (PARTITION BY bv.branch_id, bv.as_of)` in a CTE, but the outer SELECT never references it. V2 removes the CTE and window function entirely. The output columns remain: branch_id, branch_name, visit_purpose, as_of, visit_count. No `total_branch_visits` column appears in either V1 or V2 output.
- **Verification method:** Proofmark full-run comparison. Verify V2 output schema contains exactly 5 columns. Confirm no data difference from removing the unused computation.

### TC-03: Date-aligned branch join
- **Traces to:** BR-3
- **Input conditions:** Effective date range spanning multiple as_of dates. Branch_id=1 has branch_name="Main St" on 2024-10-01 and branch_name="Main Street" on 2024-10-02 (name change). Visits exist for branch_id=1 on both dates.
- **Expected output:** The visit rows on 2024-10-01 show branch_name="Main St" and the visit rows on 2024-10-02 show branch_name="Main Street". The JOIN is on `bv.branch_id = b.branch_id AND bv.as_of = b.as_of`, so each visit gets the branch name from its own date's snapshot, NOT the latest snapshot.
- **Verification method:** Proofmark full-run comparison over the date range. Inspect output rows to confirm date-aligned branch names (contrast with branch_visit_log which uses last-write-wins across dates).

### TC-04: Output ordering
- **Traces to:** BR-4
- **Input conditions:** Multi-date, multi-branch, multi-purpose data. At least 3 different as_of dates, 3 different branch_ids, and 3 different visit purposes.
- **Expected output:** Rows are ordered by: (1) as_of ascending, (2) branch_id ascending, (3) visit_purpose ascending (alphabetical). V2 SQL: `ORDER BY bv.as_of, bv.branch_id, bv.visit_purpose`.
- **Verification method:** Proofmark full-run comparison (row ordering matters in CSV). Additionally read the V2 CSV and verify sort order manually for a subset of rows.

### TC-05: segments table excluded from V2 (AP1 elimination)
- **Traces to:** BR-5
- **Input conditions:** V2 job config (`branch_visit_purpose_breakdown_v2.json`).
- **Expected output:** The V2 config contains no DataSourcing entry for the `segments` table. V1 sourced segments but the transformation SQL never referenced it.
- **Verification method:** Inspect the V2 JSON config to confirm there is no module with `"table": "segments"`. Confirm output equivalence via Proofmark (removing an unused source does not change output).

### TC-06: customer_id column excluded from V2 (AP4 elimination)
- **Traces to:** BR-6
- **Input conditions:** V2 job config branch_visits DataSourcing entry.
- **Expected output:** The V2 branch_visits DataSourcing sources only `["branch_id", "visit_purpose"]`. V1 sourced `customer_id` as well, but the SQL never referenced it.
- **Verification method:** Inspect V2 JSON config. Confirm the branch_visits columns array contains only `branch_id` and `visit_purpose`. Confirm output equivalence via Proofmark.

### TC-07: visit_id excluded; COUNT(*) preserved
- **Traces to:** BR-7
- **Input conditions:** V2 job config and SQL.
- **Expected output:** V2 does not source `visit_id` from branch_visits. The SQL uses `COUNT(*)` (not `COUNT(visit_id)`) for the visit_count aggregation. Since COUNT(*) counts all rows regardless of NULLs, and COUNT(visit_id) would skip NULLs, the semantics are identical when visit_id is always non-NULL (it's the primary key). V2 preserves COUNT(*) for safety.
- **Verification method:** Inspect V2 JSON config columns. Verify SQL uses `COUNT(*)`. Proofmark comparison confirms identical counts.

### TC-08: Trailer format
- **Traces to:** BR-8
- **Input conditions:** Single effective date run producing N data rows.
- **Expected output:** After the data rows, a trailer line in the format `END|N` is written (e.g., `END|25` if 25 data rows). The `{row_count}` token is replaced by the CsvFileWriter with the number of data rows written in that execution.
- **Verification method:** Read the V2 CSV output file. Verify the last line after the first run's data is `END|N` where N matches the count of data rows above it. Compare against V1 output trailer.

### TC-09: Inner join excludes branches with no visits
- **Traces to:** BR-9
- **Input conditions:** Branch_id=10 exists in the branches table for 2024-10-01, but no visits reference branch_id=10 on that date. Branch_id=20 has visits but does not exist in the branches table for that as_of.
- **Expected output:**
  - Branch_id=10 (in branches, no visits): does NOT appear in output. The GROUP BY on branch_visits produces no rows for branch_id=10 because there are no visits to count.
  - Branch_id=20 (has visits, not in branches): does NOT appear in output. The INNER JOIN `JOIN branches b ON bv.branch_id = b.branch_id AND bv.as_of = b.as_of` drops visits for branches that don't have a matching branches snapshot.
- **Verification method:** Proofmark comparison. Inspect output to confirm neither branch_id=10 nor branch_id=20 appears for that date.

### TC-10: Output schema verification
- **Traces to:** Output Schema (BRD/FSD)
- **Input conditions:** Any successful V2 run.
- **Expected output:** CSV output contains a header row with exactly these columns in this order: `branch_id`, `branch_name`, `visit_purpose`, `as_of`, `visit_count`. No extra columns, no missing columns, no reordering.
- **Verification method:** Read the first line of the V2 CSV. Verify header matches `branch_id,branch_name,visit_purpose,as_of,visit_count`. Compare against V1 CSV header.

### TC-11: CsvFileWriter configuration verification
- **Traces to:** Writer Configuration (BRD/FSD)
- **Input conditions:** V2 job config and a successful run.
- **Expected output:**
  - Output file: `Output/double_secret_curated/branch_visit_purpose_breakdown.csv`
  - Source DataFrame: `purpose_breakdown`
  - Header: included (first run only, not repeated on Append)
  - Trailer: `END|{row_count}` appended after each run's data
  - Line endings: CRLF (`\r\n`)
  - Write mode: Append
- **Verification method:** Inspect V2 JSON config for all writer properties. After running, open the CSV in a hex editor or use `xxd` to verify CRLF line endings (0x0D 0x0A). Verify header appears only once at the top.

### TC-12: Proofmark configuration validation
- **Traces to:** FSD Section 8
- **Input conditions:** Proofmark YAML config for branch_visit_purpose_breakdown.
- **Expected output:** Config specifies:
  - `comparison_target: "branch_visit_purpose_breakdown"`
  - `reader: csv`
  - `threshold: 100.0`
  - `csv.header_rows: 1`
  - `csv.trailer_rows: 0` (Append mode with embedded trailers)
  - No `columns.excluded` entries
  - No `columns.fuzzy` entries
  All columns are deterministic integers or text strings; no floating-point precision concerns.
- **Verification method:** Review Proofmark YAML. Run Proofmark comparison. Expect 100% match.

### TC-13: Zero visits for an effective date
- **Traces to:** Edge Cases (BRD)
- **Input conditions:** Effective date (e.g., a weekend) where branch_visits has zero rows.
- **Expected output:** Zero data rows from the SQL (branch_visits is the driving table in the GROUP BY). The CsvFileWriter appends a trailer line `END|0`. If DataSourcing returns an empty DataFrame and `RegisterTable` skips registration, the SQL may fail with "no such table." In that case, the job fails and no data is written.
- **Verification method:** Check V2 output after running for a zero-visit date. If the job succeeds, verify trailer shows `END|0`. If the job fails due to unregistered table, verify no data is written for that date. Either outcome matches V1 behavior (no meaningful data appended).

### TC-14: Branch in visits but not in branches table
- **Traces to:** BR-9, Edge Cases
- **Input conditions:** Visit rows reference branch_id=99 on 2024-10-01, but the branches table has no entry for branch_id=99 on that as_of date.
- **Expected output:** Those visits are excluded from the output entirely. The INNER JOIN drops them because there is no matching row in branches for (branch_id=99, as_of=2024-10-01). The visit_count for branch_id=99 is not zero -- the row simply does not exist.
- **Verification method:** Proofmark comparison. Verify branch_id=99 does not appear in the output for that date.

### TC-15: All five visit purposes present for a branch
- **Traces to:** BR-1, Edge Cases
- **Input conditions:** Branch_id=1 on 2024-10-01 has visits for all five known purposes: Account Opening, Deposit, Inquiry, Loan Application, Withdrawal.
- **Expected output:** Five rows for branch_id=1 on 2024-10-01, one per purpose, each with the correct visit_count. The ordering within branch_id=1 is alphabetical by visit_purpose (per BR-4): Account Opening, Deposit, Inquiry, Loan Application, Withdrawal.
- **Verification method:** Proofmark comparison. Inspect the five consecutive rows for branch_id=1 on that date.

### TC-16: Append mode with multiple trailers
- **Traces to:** Write Mode Implications (BRD)
- **Input conditions:** Run V2 for three consecutive effective dates (e.g., 2024-10-01 through 2024-10-03).
- **Expected output:** The output CSV file contains:
  1. One header row at the top (written on the first run only)
  2. Data rows for 2024-10-01
  3. Trailer line `END|N1` (where N1 = row count for 2024-10-01)
  4. Data rows for 2024-10-02
  5. Trailer line `END|N2`
  6. Data rows for 2024-10-03
  7. Trailer line `END|N3`
  The file has multiple embedded trailers, which is expected Append mode behavior.
- **Verification method:** Read the full V2 CSV. Count trailer lines (should be 3). Verify each trailer's row_count matches the data rows immediately above it. Compare full file against V1 output.

### TC-17: Re-run same date produces duplicates
- **Traces to:** Write Mode Implications (BRD)
- **Input conditions:** Run V2 for 2024-10-01, then run again for 2024-10-01.
- **Expected output:** The CSV file contains the 2024-10-01 data twice (with two trailer lines). Append mode does not deduplicate. This matches V1 behavior.
- **Verification method:** Read the CSV after two runs. Verify data rows appear twice and two `END|N` trailer lines exist.

### TC-18: NULL visit_purpose handling
- **Traces to:** BR-1, Edge Cases
- **Input conditions:** A visit row has visit_purpose=NULL.
- **Expected output:** If visit_purpose is NULL, the GROUP BY will create a group for NULL visit_purpose. The row will appear in output with an empty or NULL visit_purpose value and its visit_count. The INNER JOIN will still succeed as long as the branch exists. The ORDER BY will place NULL visit_purpose rows according to SQLite's NULL ordering behavior (NULLs sort first in ascending order).
- **Verification method:** Proofmark comparison. If no NULL visit_purposes exist in the actual datalake data, this test is a defensive check -- verify the SQL handles it correctly should one ever appear.
