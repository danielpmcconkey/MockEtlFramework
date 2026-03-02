# TopBranches -- Test Plan

## Job Overview

TopBranches produces a ranked CSV of branches by visit count using `RANK()`, with a CONTROL trailer. Tier 1 framework-only job (DataSourcing + Transformation + CsvFileWriter). V2 eliminates unused `visit_id` column (AP4) and preserves the hardcoded date filter for output equivalence.

---

## Test Cases

### Happy Path

| ID | Description | Expected Behavior | Traces To |
|----|-------------|-------------------|-----------|
| TB-HP-01 | V2 output matches V1 for full date range (2024-10-01 through 2024-12-31) | Proofmark comparison returns exit code 0 (PASS) with threshold 100.0. All data rows match exactly between `Output/curated/top_branches.csv` and `Output/double_secret_curated/top_branches.csv`. | BR-1 through BR-8; FSD Section 4 |
| TB-HP-02 | Branch visit counts are correctly aggregated per branch_id | Each branch_id has a single `total_visits` value equal to COUNT(*) of its rows in branch_visits for the effective date. Verified by Proofmark row-level match. | BR-2; FSD Section 4 (CTE `visit_totals`) |
| TB-HP-03 | RANK() assigns correct positions by descending total_visits | Branches with the highest visit count get rank 1. Ties receive the same rank with gaps (e.g., two rank-1 branches means next is rank 3). Output ordering is rank ASC, branch_id ASC. | BR-3, BR-4; FSD Section 4 |
| TB-HP-04 | Output includes branch_name from branches join | Every row contains a valid branch_name corresponding to the branch_id, sourced from the branches table. | BR-5, BR-6; FSD Section 4 |
| TB-HP-05 | Output includes `as_of` column from branches table | The `as_of` column is present and contains the effective date for each row. On single-day runs, all rows have the same `as_of` value. | BR-6; FSD Section 4 |
| TB-HP-06 | CONTROL trailer contains correct date and row_count | Trailer format is `CONTROL|{date}|{row_count}|{timestamp}`. The `{date}` token resolves to `__maxEffectiveDate`, and `{row_count}` matches the actual number of data rows (excluding header and trailer). | BR-7; FSD Section 5 |

### Writer Configuration Verification

| ID | Description | Expected Behavior | Traces To |
|----|-------------|-------------------|-----------|
| TB-WC-01 | Output file is CSV at correct V2 path | File exists at `Output/double_secret_curated/top_branches.csv`. | FSD Section 5 |
| TB-WC-02 | CSV includes header row | First line of the file contains column names: `branch_id,branch_name,total_visits,rank,as_of`. | FSD Section 5 (`includeHeader: true`) |
| TB-WC-03 | CSV uses LF line endings | All line breaks in the file are `\n` (LF), not `\r\n` (CRLF). | FSD Section 5 (`lineEnding: LF`) |
| TB-WC-04 | Overwrite mode produces single-snapshot file | After running through the full date range, the file reflects only the last effective date's data. No accumulation of prior dates. | FSD Section 5 (`writeMode: Overwrite`); BRD Write Mode Implications |
| TB-WC-05 | CONTROL trailer is the last line | The final line of the file matches the pattern `CONTROL|YYYY-MM-DD|N|<timestamp>`. | BR-7; FSD Section 5 |

### Edge Cases

| ID | Description | Expected Behavior | Traces To |
|----|-------------|-------------------|-----------|
| TB-EC-01 | Branches with zero visits on an effective date | Branches that have no rows in branch_visits for a given date are excluded from output (inner join on visit_totals CTE filters them out). | BRD Edge Case: "Branch with no visits"; FSD Section 4 item 5 |
| TB-EC-02 | Tied visit counts produce RANK() with gaps | If branches A and B both have 10 visits, both get rank N, and the next branch gets rank N+2. Output ordering within a tie is by branch_id ascending. | BR-3, BR-4; BRD Edge Case: "Tied ranks" |
| TB-EC-03 | Hardcoded date filter `WHERE bv.as_of >= '2024-10-01'` | On single-day runs with effective dates >= 2024-10-01, the filter is a no-op (all loaded data passes). Output is identical whether or not the filter exists for the current data range. V2 preserves the filter. | BR-1; FSD Section 4, Section 9 (OQ-2); BRD Edge Case: "Hardcoded date filter" |
| TB-EC-04 | Non-date-aligned join does not duplicate rows on single-day runs | On single-day auto-advance runs, each branch_id appears exactly once in the branches DataFrame, so the join produces no duplicates. Output row count equals the number of branches with at least one visit. | BR-5; FSD Section 9 (OQ-1); BRD Edge Case: "Branch duplication via join" |
| TB-EC-05 | Trailer timestamp is non-deterministic but stripped by Proofmark | The `{timestamp}` token in the trailer produces a UTC timestamp that varies per execution. Proofmark's `trailer_rows: 1` setting strips the trailer before comparison, so this does not cause a mismatch. | BR-7; FSD Section 8 |
| TB-EC-06 | All 40 branches present on a date with universal visits | If all 40 branches have visits on a given effective date, the output contains exactly 40 data rows. | BRD Schema Details (40 branches per date) |

### Anti-Pattern Elimination Verification

| ID | Description | Expected Behavior | Traces To |
|----|-------------|-------------------|-----------|
| TB-AP-01 | AP4: `visit_id` removed from V2 DataSourcing | V2 config for branch_visits sources only `["branch_id"]`. The `visit_id` column is absent from the DataSourcing columns list. Output is unaffected because `COUNT(*)` counts rows, not column values. | BR-8; FSD Section 7 (AP4); KNOWN_ANTI_PATTERNS AP4 |
| TB-AP-02 | AP4 elimination does not change output | Despite removing `visit_id` from sourced columns, the `COUNT(*)` in the SQL still produces the same visit counts. Proofmark comparison PASS confirms no output difference. | BR-8; FSD Section 7 |
| TB-AP-03 | No unnecessary External module | V2 uses Tier 1 (DataSourcing + Transformation + CsvFileWriter) with no External module, matching V1's architecture. | FSD Section 2 (Tier justification); KNOWN_ANTI_PATTERNS AP3 |
| TB-AP-04 | No dead-end sourcing | Both sourced tables (branch_visits, branches) are consumed by the Transformation SQL. No unused DataSourcing entries. | FSD Section 7 (AP1 check); KNOWN_ANTI_PATTERNS AP1 |

### Proofmark Comparison Expectations

| ID | Description | Expected Behavior | Traces To |
|----|-------------|-------------------|-----------|
| TB-PM-01 | Proofmark config uses reader `csv` | Config at `POC3/proofmark_configs/top_branches.yaml` specifies `reader: csv`. | FSD Section 8 |
| TB-PM-02 | Proofmark config has `header_rows: 1` | Matches V1/V2 `includeHeader: true`. | FSD Section 8; CONFIG_GUIDE |
| TB-PM-03 | Proofmark config has `trailer_rows: 1` | Matches V1/V2 Overwrite mode with `trailerFormat` present. Strips the non-deterministic timestamp from comparison. | FSD Section 8; CONFIG_GUIDE Example 3 |
| TB-PM-04 | Proofmark config has `threshold: 100.0` | All data rows must match exactly. No tolerance needed -- all output columns are deterministic. | FSD Section 8 |
| TB-PM-05 | No excluded or fuzzy columns | All columns (branch_id, branch_name, total_visits, rank, as_of) are strictly compared. No non-deterministic data columns identified. | FSD Section 8 |
| TB-PM-06 | Proofmark exit code 0 for full date range run | After running both V1 and V2 for 2024-10-01 through 2024-12-31, comparison produces PASS. | FSD Section 8; BLUEPRINT Phase D |

---

## Proofmark Config (Expected)

```yaml
comparison_target: "top_branches"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```
