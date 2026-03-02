# BranchDirectory — Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | ROW_NUMBER dedup keeps exactly one row per branch_id |
| TC-02   | BR-2           | Non-deterministic as_of preserved via ORDER BY branch_id in ROW_NUMBER |
| TC-03   | BR-3           | Output is ordered by branch_id ascending |
| TC-04   | BR-4           | Multi-day effective range collapses to one row per branch |
| TC-05   | BR-5           | as_of column is present in output |
| TC-06   | BR-1, BR-4     | Single-day effective range produces one row per branch (ROW_NUMBER is no-op) |
| TC-07   | Output Schema  | Output contains exactly 8 columns in correct order |
| TC-08   | Writer Config  | CSV header row is present |
| TC-09   | Writer Config  | CRLF line endings used throughout |
| TC-10   | Writer Config  | No trailer row in output |
| TC-11   | Writer Config  | Overwrite mode replaces entire file on each execution |
| TC-12   | Edge Case      | NULL handling in address fields |
| TC-13   | Edge Case      | Zero-row output scenario |
| TC-14   | Edge Case      | Branch attribute changes across as_of dates |
| TC-15   | Proofmark      | V2 output matches V1 baseline under strict comparison |
| TC-16   | Proofmark      | Contingency: as_of column excluded if non-deterministic mismatch occurs |

## Test Cases

### TC-01: ROW_NUMBER dedup keeps exactly one row per branch_id
- **Traces to:** BR-1
- **Input conditions:** DataSourcing returns multiple rows for the same branch_id (e.g., branch_id=1 appearing on 2024-10-01, 2024-10-02, 2024-10-03 within the effective date range).
- **Expected output:** Exactly one row per unique branch_id in the output. If 40 distinct branch_ids exist in the datalake, the output should contain exactly 40 data rows.
- **Verification method:** Count distinct branch_id values in the output CSV and compare to the total row count (excluding header). They must be equal.

### TC-02: Non-deterministic as_of preserved via ROW_NUMBER ordering
- **Traces to:** BR-2
- **Input conditions:** Multiple as_of dates exist for the same branch_id. The ROW_NUMBER window function uses `ORDER BY branch_id` (identical value within partition), providing no deterministic tie-breaking.
- **Expected output:** The V2 SQL reproduces the exact same `ROW_NUMBER() OVER (PARTITION BY b.branch_id ORDER BY b.branch_id)` construction as V1. The as_of value selected for each branch should match V1 output because both V1 and V2 use the same data path (DataSourcing orders by as_of, SQLite uses insertion order as implicit tiebreaker).
- **Verification method:** Compare V2 output against V1 baseline. Inspect the SQL in the V2 job config to confirm the ORDER BY clause inside the ROW_NUMBER window function is `ORDER BY b.branch_id` (not `ORDER BY b.as_of` or any other column).

### TC-03: Output is ordered by branch_id ascending
- **Traces to:** BR-3
- **Input conditions:** Standard execution with multiple branches in the effective date range.
- **Expected output:** Data rows in the CSV are sorted by branch_id in ascending numeric order.
- **Verification method:** Parse the branch_id column from all data rows (skipping the header). Verify the sequence is strictly non-decreasing. For example, if branch_ids are 1, 2, 3, ..., 40, they must appear in that order.

### TC-04: Multi-day effective range collapses to one row per branch
- **Traces to:** BR-4
- **Input conditions:** Effective date range spans multiple days (e.g., 2024-10-01 through 2024-10-15). The datalake contains 40 branches on every date, resulting in 40 x 15 = 600 rows from DataSourcing.
- **Expected output:** Output contains exactly 40 rows (one per branch), not 600.
- **Verification method:** Count data rows in the output CSV (excluding header). Compare to the known count of distinct branch_ids in the datalake. Must be equal.

### TC-05: as_of column is present in output
- **Traces to:** BR-5
- **Input conditions:** Standard execution.
- **Expected output:** The output CSV header includes `as_of` as the 8th column. Each data row has a value in the as_of position that is a valid date in `yyyy-MM-dd` format.
- **Verification method:** Parse the header row and confirm `as_of` is present and is the last column. Verify all data rows have a non-empty value in the as_of column matching the date format.

### TC-06: Single-day effective range produces expected output
- **Traces to:** BR-1, BR-4
- **Input conditions:** Effective date range is a single day (e.g., min = max = 2024-10-01). Each branch_id has exactly one row from DataSourcing.
- **Expected output:** ROW_NUMBER assigns rn=1 to every row (no dedup needed). Output contains one row per branch with as_of = the single effective date.
- **Verification method:** Run with a single-day effective date. Verify all as_of values equal that date. Verify row count matches the distinct branch count for that date.

### TC-07: Output contains exactly 8 columns in correct order
- **Traces to:** Output Schema
- **Input conditions:** Standard execution.
- **Expected output:** Header row is: `branch_id,branch_name,address_line1,city,state_province,postal_code,country,as_of` (8 columns in this exact order).
- **Verification method:** Read the first line of the output CSV and compare to the expected header string. Count commas to verify 7 delimiters (8 fields). Verify no extra or missing columns.

### TC-08: CSV header row is present
- **Traces to:** Writer Config (includeHeader: true)
- **Input conditions:** Standard execution.
- **Expected output:** The first line of the output file is a header row containing column names, not data.
- **Verification method:** Read the first line of the CSV. Confirm it matches the expected column names (branch_id, branch_name, ..., as_of). Confirm the second line contains actual data values (e.g., an integer for branch_id, not a column name).

### TC-09: CRLF line endings used throughout
- **Traces to:** Writer Config (lineEnding: CRLF)
- **Input conditions:** Standard execution.
- **Expected output:** Every line in the output file ends with `\r\n` (carriage return + line feed), not just `\n`.
- **Verification method:** Read the raw bytes of the output file. Search for `\n` characters and verify each is preceded by `\r`. Alternatively, count `\r\n` occurrences and compare to total line count.

### TC-10: No trailer row in output
- **Traces to:** Writer Config (trailerFormat: not specified)
- **Input conditions:** Standard execution.
- **Expected output:** The output file contains exactly one header row plus N data rows. No trailer line at the end (no line starting with "TRAILER" or containing `{row_count}` tokens).
- **Verification method:** Count total lines in the output. Verify total lines = 1 (header) + N (data rows). Inspect the last line and confirm it is a regular data row (starts with a numeric branch_id).

### TC-11: Overwrite mode replaces entire file on each execution
- **Traces to:** Writer Config (writeMode: Overwrite)
- **Input conditions:** Execute the job twice with different effective date ranges. First run with 2024-10-01, second run with 2024-10-15.
- **Expected output:** After the second run, the output file reflects only the second execution's data. No residual data from the first run.
- **Verification method:** After the second run, verify all as_of values in the output correspond to the second run's effective date range, not the first.

### TC-12: NULL handling in address fields
- **Traces to:** Edge Case
- **Input conditions:** A branch in the datalake has NULL values for one or more of: address_line1, city, state_province, postal_code.
- **Expected output:** The NULL values appear in the CSV as empty fields (no literal string "NULL"). The row is still included in the output — NULLs do not cause the row to be filtered out.
- **Verification method:** Identify branches with NULL address fields in the datalake. Verify those branches appear in the output CSV with empty values (consecutive commas) for the NULL fields.

### TC-13: Zero-row output scenario
- **Traces to:** Edge Case
- **Input conditions:** Effective date range falls outside all as_of dates in the datalake (e.g., a future date range where no branch data exists).
- **Expected output:** Output CSV contains only the header row, with no data rows. File is still created (not missing).
- **Verification method:** Verify the file exists. Verify it contains exactly one line (the header). Verify the file size is greater than 0 (header is present).

### TC-14: Branch attribute changes across as_of dates
- **Traces to:** Edge Case, BR-2
- **Input conditions:** A branch changes its address between two as_of dates within the effective range (e.g., branch_id=5 has address "123 Main St" on 2024-10-01 and "456 Oak Ave" on 2024-10-02).
- **Expected output:** Only one version of branch_id=5 appears in the output. Which version (which address, which as_of) is non-deterministic per BR-2, but it must match V1 behavior.
- **Verification method:** Compare the V2 output row for that branch_id against the V1 baseline. Both should select the same row due to identical data path and insertion order.

### TC-15: V2 output matches V1 baseline under strict Proofmark comparison
- **Traces to:** Proofmark
- **Input conditions:** Both V1 and V2 have been executed for the same effective date range. Proofmark config uses `reader: csv`, `header_rows: 1`, `trailer_rows: 0`, `threshold: 100.0`, no excluded or fuzzy columns.
- **Expected output:** Proofmark reports 100% match. Every row and column in V2 matches V1 exactly.
- **Verification method:** Run the Proofmark comparison tool with the proposed config. Verify threshold is met. If it fails, inspect the diff output to identify which columns/rows differ.

### TC-16: Contingency — as_of column excluded if non-deterministic mismatch
- **Traces to:** Proofmark, BR-2
- **Input conditions:** TC-15 fails specifically because as_of values differ between V1 and V2 for certain branch_ids, while all other columns match.
- **Expected output:** After adding `as_of` to the Proofmark excluded columns list (with documented reason referencing BR-2), the comparison passes at 100%.
- **Verification method:** Update Proofmark config to exclude `as_of` with reason: "ROW_NUMBER ORDER BY branch_id within PARTITION BY branch_id provides no deterministic tie-breaking. Which as_of value is selected per branch depends on internal row ordering. [branch_directory.json:15] [BRD BR-2]". Re-run comparison. Verify pass.
