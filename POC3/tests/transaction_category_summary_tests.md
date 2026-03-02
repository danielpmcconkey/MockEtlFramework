# TransactionCategorySummary -- Test Plan

**Job:** TransactionCategorySummaryV2
**BRD:** `POC3/brd/transaction_category_summary_brd.md`
**FSD:** `POC3/fsd/transaction_category_summary_fsd.md`

---

## Test Cases

### Happy Path

| ID | Description | Expected Behavior | BRD Ref | FSD Ref |
|----|-------------|-------------------|---------|---------|
| TCS-001 | Aggregation by txn_type and as_of | Output contains one row per unique `(txn_type, as_of)` combination. Typically 2 rows per date: one for "Credit", one for "Debit". | BR-1 | Sec 4 |
| TCS-002 | total_amount computation | `total_amount` = `ROUND(SUM(amount), 2)` for each `(txn_type, as_of)` group | BR-3 | Sec 4 |
| TCS-003 | transaction_count computation | `transaction_count` = `COUNT(*)` for each `(txn_type, as_of)` group | BR-4 | Sec 4 |
| TCS-004 | avg_amount computation | `avg_amount` = `ROUND(AVG(amount), 2)` for each `(txn_type, as_of)` group | BR-5 | Sec 4 |
| TCS-005 | Sort order: as_of ASC, txn_type ASC | Within each date, "Credit" appears before "Debit" (alphabetical). Dates are in ascending order. | BR-6, Edge Case 5 | Sec 4 |
| TCS-006 | Output schema column order | Columns appear in order: `txn_type`, `as_of`, `total_amount`, `transaction_count`, `avg_amount` | BRD Output Schema | Sec 10 |
| TCS-007 | Append mode -- multi-day accumulation | After a full date range run (2024-10-01 through 2024-12-31), the output file contains all dates' data rows accumulated sequentially with embedded trailers | BRD Write Mode | Sec 5 |
| TCS-008 | Trailer per effective date | Each effective date's data rows are followed by a trailer line `END|{row_count}` where `{row_count}` is the number of data rows for that run (typically 2) | BR-7 | Sec 5 |

### Edge Cases

| ID | Description | Expected Behavior | BRD Ref | FSD Ref |
|----|-------------|-------------------|---------|---------|
| TCS-010 | No transactions for an effective date | GROUP BY produces zero rows. Writer emits `END|0` trailer for that date. No data rows appended. | Edge Case 1 | Sec 11 |
| TCS-011 | Only one txn_type on a date | Only one data row appears for that date (either "Credit" or "Debit"). Trailer shows `END|1`. | Edge Case 2 | Sec 11 |
| TCS-012 | Header only on first run | In Append mode, the header row (`txn_type,as_of,total_amount,transaction_count,avg_amount`) is written only once at the top of the file. Subsequent dates do NOT re-emit the header. | BRD Write Mode | Sec 5 |
| TCS-013 | Multi-day file structure | Expected structure: header + day1 data + `END|2` + day2 data + `END|2` + ... + dayN data + `END|{count}`. No blank lines between sections. | BRD Write Mode | Sec 5 |
| TCS-014 | Trailer row_count reflects output rows, not input rows | The `{row_count}` token resolves to `df.Count` (number of aggregated output rows), not the number of raw input transactions. For a typical day with both Credit and Debit, this is 2. | BR-7 | Sec 5 (Trailer Token Resolution) |
| TCS-015 | SQLite ROUND behavior | `ROUND(..., 2)` in SQLite uses "round half away from zero" rounding. V2 uses the same SQLite ROUND, so results are deterministic and identical. | -- | Sec 6, W5 assessment |

### Anti-Pattern Elimination Verification

| ID | Description | Expected Behavior | Anti-Pattern | FSD Ref |
|----|-------------|-------------------|-------------|---------|
| TCS-020 | Dead-end `segments` table removed (AP1) | V2 job config does NOT include a DataSourcing module for `datalake.segments`. Output is unaffected because the V1 SQL never referenced `segments`. | AP1 | Sec 3 (segments REMOVED), Sec 7 |
| TCS-021 | Unused `transaction_id` column removed (AP4) | V2 DataSourcing for `transactions` does not source `transaction_id`. It was only used inside the removed CTE's `ROW_NUMBER()` ORDER BY. | AP4 | Sec 3 (Columns Removed) |
| TCS-022 | Unused `account_id` column removed (AP4) | V2 DataSourcing for `transactions` does not source `account_id`. It was sourced but never referenced in any part of V1's SQL output. | AP4 | Sec 3 (Columns Removed) |
| TCS-023 | Vestigial CTE with window functions removed (AP8) | V2 SQL queries `transactions` directly with `GROUP BY txn_type, as_of` instead of going through a CTE with unused `ROW_NUMBER()` and `COUNT(*) OVER(...)`. Output is identical because the outer query's aggregation is independent of the CTE's window function columns. | AP8 | Sec 4 |
| TCS-024 | Tier 1 framework-only implementation | V2 uses Tier 1 (DataSourcing + Transformation + CsvFileWriter) with no External module. All business logic expressed in SQL. | AP3 (verified not applicable) | Sec 2 |

### Writer Config Verification

| ID | Description | Expected Behavior | BRD Ref | FSD Ref |
|----|-------------|-------------------|---------|---------|
| TCS-030 | Writer type is CsvFileWriter | V2 uses CsvFileWriter, matching V1 | BRD Output Type | Sec 5 |
| TCS-031 | includeHeader = true | Output CSV starts with header: `txn_type,as_of,total_amount,transaction_count,avg_amount` | BRD Writer Config | Sec 5 |
| TCS-032 | writeMode = Append | Each effective date's output is appended to the file. File grows across the date range. | BRD Writer Config | Sec 5 |
| TCS-033 | lineEnding = LF | All line endings are LF (`\n`), not CRLF | BRD Writer Config | Sec 5 |
| TCS-034 | trailerFormat = END|{row_count} | Each run appends a trailer line matching `END|{N}` where N is the data row count for that run | BRD Writer Config, BR-7 | Sec 5 |
| TCS-035 | source = txn_cat_summary | Writer reads from the Transformation result named `txn_cat_summary` | BRD Writer Config | Sec 5, Sec 9 |
| TCS-036 | Output path | V2 writes to `Output/double_secret_curated/transaction_category_summary.csv` | -- | Sec 5, Sec 9 |

### Proofmark Comparison Expectations

| ID | Description | Expected Behavior | FSD Ref |
|----|-------------|-------------------|---------|
| TCS-040 | Proofmark config: reader = csv | Config uses `reader: csv` for CSV output | Sec 8 |
| TCS-041 | Proofmark config: header_rows = 1 | Config specifies `header_rows: 1` (includeHeader = true) | Sec 8 |
| TCS-042 | Proofmark config: trailer_rows = 0 | Config specifies `trailer_rows: 0` because this is an Append-mode file with embedded trailers throughout (not just at the end). Trailers are treated as data rows for comparison. | Sec 8 |
| TCS-043 | Proofmark config: threshold = 100.0 | Strict match required -- all fields deterministic SQL aggregation | Sec 8 |
| TCS-044 | Proofmark config: no EXCLUDED columns | No non-deterministic fields identified | Sec 8 |
| TCS-045 | Proofmark config: no FUZZY columns | All monetary columns use SQLite `ROUND(..., 2)` which is deterministic. No double-precision C# code involved. | Sec 8 |
| TCS-046 | Full Proofmark pass (exit code 0) | After running V1 and V2 for the full date range (2024-10-01 through 2024-12-31), Proofmark comparison of `Output/curated/transaction_category_summary.csv` vs `Output/double_secret_curated/transaction_category_summary.csv` returns exit code 0. | Sec 8 |

---

## Risk Notes

- **Low-risk job overall.** This is a Tier 1 framework-only job with straightforward SQL aggregation. No External module, no mixed-precision computation, no non-deterministic fields.
- **Trailer handling in Append mode is the main verification point.** The embedded trailers (`END|{row_count}`) throughout the file must match V1 exactly -- same count values, same positions, same format.
- **AP8 elimination (CTE removal) is the biggest code change.** The safety argument is well-documented in the FSD: the outer GROUP BY aggregation is independent of the CTE's window function columns, so removing the CTE cannot change output. Proofmark comparison will confirm.
