# BranchVisitSummary -- Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | Visit count aggregation: COUNT(*) per branch per date |
| TC-02   | BR-2           | INNER JOIN on branch_id AND as_of (FSD corrected from BRD's LEFT JOIN) |
| TC-03   | BR-3           | Output ordering: as_of ascending, then branch_id ascending |
| TC-04   | BR-4           | customer_id and visit_purpose sourced but unused -- removed in V2 |
| TC-05   | BR-5           | visit_id sourced but unused (COUNT(*) not COUNT(visit_id)) -- removed in V2 |
| TC-06   | BR-6           | Trailer format: TRAILER\|{row_count}\|{date} |
| TC-07   | BR-7           | Branch missing from branches table -- excluded by INNER JOIN (FSD correction) |
| TC-08   | BR-1, BR-2     | Multiple branches on same date produce separate rows |
| TC-09   | BR-1           | Single visit for a branch produces visit_count = 1 |
| TC-10   | BR-1, BR-3     | Multi-date output: rows span multiple as_of dates, ordered correctly |
| TC-11   | BR-6           | Append mode: multiple executions accumulate data and trailers |
| TC-12   | --             | Edge case: zero visits for all branches on a given date |
| TC-13   | --             | Edge case: NULL branch_name in branches table |
| TC-14   | --             | Edge case: weekend dates in effective date range |
| TC-15   | --             | Output format: correct columns, correct order, header present |
| TC-16   | --             | Output format: LF line endings |
| TC-17   | --             | Proofmark: all columns STRICT, no fuzzy or excluded overrides |
| TC-18   | --             | Append mode: header written only on first execution |
| TC-19   | BR-2           | Date-aligned join: same branch_id with different as_of dates matches correctly |

## Test Cases

### TC-01: Visit count aggregation per branch per date
- **Traces to:** BR-1
- **Input conditions:** branch_visits contains 5 rows for branch_id=1 on as_of=2024-10-01, and 3 rows for branch_id=2 on as_of=2024-10-01. Both branches exist in the branches table for that as_of.
- **Expected output:** Two data rows: branch_id=1 with visit_count=5, branch_id=2 with visit_count=3. COUNT(*) counts all rows per group regardless of column values.
- **Verification method:** Run job for effective date 2024-10-01. Parse output CSV, verify visit_count column values match expected counts per branch_id/as_of group.

### TC-02: INNER JOIN on branch_id AND as_of
- **Traces to:** BR-2 (FSD-corrected: INNER JOIN, not LEFT JOIN as BRD states)
- **Input conditions:** branch_visits has rows for branch_id=10 on as_of=2024-10-01. branches table has a matching record for branch_id=10 on as_of=2024-10-01 with branch_name="Downtown".
- **Expected output:** Output row includes branch_id=10, branch_name="Downtown". The join is date-aligned (both branch_id and as_of must match).
- **Verification method:** Confirm the output row contains the correct branch_name from the date-matched branches record. Confirm the FSD's correction is implemented: JOIN (INNER), not LEFT JOIN.

### TC-03: Output ordering by as_of then branch_id
- **Traces to:** BR-3
- **Input conditions:** Output contains rows for multiple dates (2024-10-01, 2024-10-02) and multiple branches (branch_id 1, 2, 3) on each date.
- **Expected output:** Rows sorted first by as_of ascending (2024-10-01 rows before 2024-10-02 rows), then within each date by branch_id ascending (1, 2, 3).
- **Verification method:** Parse output CSV data rows (excluding header and trailer). Verify ordering: row N's (as_of, branch_id) tuple is <= row N+1's tuple in lexicographic order.

### TC-04: Unused columns removed -- customer_id and visit_purpose
- **Traces to:** BR-4
- **Input conditions:** V2 DataSourcing for branch_visits sources only ["branch_id"]. The as_of column is appended automatically by the DataSourcing module.
- **Expected output:** The V2 job config does NOT include customer_id or visit_purpose in the branch_visits DataSourcing columns array. Output is unaffected because these columns never appeared in the transformation SQL.
- **Verification method:** Inspect V2 job config JSON. Confirm columns array for branch_visits DataSourcing is ["branch_id"]. Verify output is byte-identical to V1 output (these columns had no effect on the result).

### TC-05: Unused column removed -- visit_id
- **Traces to:** BR-5
- **Input conditions:** V2 DataSourcing for branch_visits does not include visit_id. The aggregation uses COUNT(*), not COUNT(visit_id).
- **Expected output:** Removing visit_id has no effect on output because COUNT(*) counts all rows, not a specific column.
- **Verification method:** Inspect V2 job config JSON. Confirm visit_id is not in the columns array. Run job and compare output to V1 baseline using Proofmark.

### TC-06: Trailer format with row_count and date tokens
- **Traces to:** BR-6
- **Input conditions:** Effective date is 2024-10-05. The transformation produces 4 data rows.
- **Expected output:** Last line of the appended block is `TRAILER|4|2024-10-05`. The {row_count} token resolves to the count of data rows written in this execution (4). The {date} token resolves to __maxEffectiveDate (2024-10-05).
- **Verification method:** Read the output CSV file. Locate the trailer line appended after the data rows for this execution. Verify format matches `TRAILER|{count}|{date}` with correct values.

### TC-07: Branch missing from branches table -- INNER JOIN exclusion
- **Traces to:** BR-7 (FSD correction: BRD incorrectly states NULL branch_name via LEFT JOIN)
- **Input conditions:** branch_visits has 3 rows for branch_id=99 on as_of=2024-10-01. branches table has NO record for branch_id=99 on as_of=2024-10-01.
- **Expected output:** No output row for branch_id=99. The INNER JOIN excludes visits for branches not present in the branches table for that as_of date. (BRD's BR-7 claim of NULL branch_name is incorrect per the FSD's correction.)
- **Verification method:** Run job and verify branch_id=99 does NOT appear in the output CSV.

### TC-08: Multiple branches on same date
- **Traces to:** BR-1, BR-2
- **Input conditions:** branch_visits has visits for branch_id=1 (5 visits), branch_id=2 (3 visits), and branch_id=3 (8 visits), all on as_of=2024-10-01. All three branches exist in the branches table for that date.
- **Expected output:** Three data rows, one per branch, each with the correct visit_count.
- **Verification method:** Parse output CSV. Verify exactly 3 data rows, each with the correct branch_id and visit_count.

### TC-09: Single visit for a branch
- **Traces to:** BR-1
- **Input conditions:** branch_visits has exactly 1 row for branch_id=5 on as_of=2024-10-01. branch_id=5 exists in branches for that date.
- **Expected output:** Output row for branch_id=5 has visit_count=1.
- **Verification method:** Parse output CSV and verify the visit_count value for branch_id=5.

### TC-10: Multi-date output with correct ordering
- **Traces to:** BR-1, BR-3
- **Input conditions:** Effective date range spans 2024-10-01 through 2024-10-03 (via multi-day auto-advance, each day appended separately). Each date has visits for branches 1 and 2.
- **Expected output:** Each day's append block contains rows ordered by as_of then branch_id. Over three executions, the file contains: header, day-1 data, day-1 trailer, day-2 data, day-2 trailer, day-3 data, day-3 trailer.
- **Verification method:** Read the full output CSV. Verify the structural pattern: one header, then repeating blocks of (data rows + trailer) per effective date.

### TC-11: Append mode accumulation across multiple executions
- **Traces to:** BR-6
- **Input conditions:** Job runs for three consecutive effective dates (auto-advance). Each execution appends data rows and a trailer.
- **Expected output:** The output file contains one header row at the top, followed by three blocks of (data rows + TRAILER line). Three TRAILER lines total, each with its respective row_count and date.
- **Verification method:** Read the output CSV. Count the number of TRAILER lines. Verify each TRAILER line's date matches its corresponding execution's __maxEffectiveDate. Verify each TRAILER line's row_count matches the number of data rows in its block.

### TC-12: Zero visits for all branches on a given date
- **Traces to:** Edge case (BRD Edge Cases section)
- **Input conditions:** branch_visits has no rows for as_of=2024-10-01 (no visits occurred). Branches exist in the branches table for that date.
- **Expected output:** Zero data rows for that date. The trailer line is `TRAILER|0|2024-10-01`.
- **Verification method:** Run job for effective date 2024-10-01. Verify the appended block contains only the trailer line `TRAILER|0|2024-10-01` with no data rows preceding it.

### TC-13: NULL branch_name in branches table
- **Traces to:** Edge case
- **Input conditions:** branches table has a record for branch_id=7 on as_of=2024-10-01 with branch_name=NULL. branch_visits has 2 visits for branch_id=7 on that date.
- **Expected output:** Output row for branch_id=7 has an empty/NULL branch_name value and visit_count=2. The INNER JOIN succeeds (the record exists in branches), but the branch_name column value is NULL.
- **Verification method:** Parse output CSV. Verify branch_id=7 row exists with visit_count=2 and branch_name is empty or null representation in CSV format.

### TC-14: Weekend dates in effective date range
- **Traces to:** Edge case
- **Input conditions:** Effective date is a Saturday or Sunday. Data may or may not exist in branch_visits and branches for weekend dates.
- **Expected output:** No special weekend handling exists (no W1 or W2 wrinkles apply per FSD Section 3). If data exists for the weekend date, it is processed normally. If no data exists, zero data rows are output with a zero-count trailer.
- **Verification method:** Run job for a known weekend date. Verify output matches the data available for that date, with no skip or fallback logic.

### TC-15: Output format -- correct columns and column order
- **Traces to:** Output format verification
- **Input conditions:** Standard job execution with at least one data row.
- **Expected output:** CSV header row contains exactly: `branch_id,branch_name,as_of,visit_count` in that order. Data rows have four comma-separated values in the same order.
- **Verification method:** Read the first line of the output CSV. Verify it matches the expected header exactly. Verify data rows have exactly 4 fields.

### TC-16: LF line endings
- **Traces to:** Output format verification (BRD Writer Configuration: lineEnding=LF)
- **Input conditions:** Standard job execution producing output CSV.
- **Expected output:** All line endings in the file are LF (\n), not CRLF (\r\n). This includes header, data rows, and trailer lines.
- **Verification method:** Read the raw bytes of the output CSV. Verify no \r characters exist in the file. Every line boundary is a single \n.

### TC-17: Proofmark configuration -- all columns STRICT
- **Traces to:** Proofmark verification (FSD Section 8)
- **Input conditions:** V1 and V2 output files for the same effective date range.
- **Expected output:** Proofmark comparison passes at 100.0% threshold with no fuzzy or excluded column overrides. All four columns (branch_id, branch_name, as_of, visit_count) are compared strictly.
- **Verification method:** Run Proofmark with the FSD's config (reader: csv, header_rows: 1, trailer_rows: 0, threshold: 100.0, no column overrides). Verify PASS result. Config uses trailer_rows: 0 because Append mode embeds trailers throughout the file, not just at the end.

### TC-18: Append mode -- header written only on first execution
- **Traces to:** Edge case (FSD Section 7, CsvFileWriter.cs:47)
- **Input conditions:** Job runs for multiple consecutive effective dates. The output file does not exist before the first run.
- **Expected output:** The header row `branch_id,branch_name,as_of,visit_count` appears exactly once at the top of the file. Subsequent appends do NOT re-emit the header. (Per CsvFileWriter behavior: header is written only when the file is newly created.)
- **Verification method:** Read the full output CSV after multiple runs. Count occurrences of the header string. Verify exactly 1 occurrence at line 1.

### TC-19: Date-aligned join correctness across snapshots
- **Traces to:** BR-2
- **Input conditions:** branch_id=5 exists in branches on as_of=2024-10-01 with branch_name="Main St" and on as_of=2024-10-02 with branch_name="Main Street" (name changed between snapshots). branch_visits has visits for branch_id=5 on both dates.
- **Expected output:** The output row for as_of=2024-10-01 shows branch_name="Main St". The output row for as_of=2024-10-02 shows branch_name="Main Street". Each date's visit data joins only with its same-date branch snapshot.
- **Verification method:** Parse output CSV. Verify branch_name values for branch_id=5 differ across dates, matching the respective branches snapshot for each as_of.
