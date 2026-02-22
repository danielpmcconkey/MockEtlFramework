# BRD: BranchDirectory

## Overview
Produces a deduplicated directory of all branches with their addresses, selecting one representative row per branch_id using a ROW_NUMBER window function. Uses Overwrite mode so the output always reflects the latest effective date's data.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| branches | datalake | branch_id, branch_name, address_line1, city, state_province, postal_code, country | Filtered by effective date range. Deduplicated by branch_id using ROW_NUMBER. | [JobExecutor/Jobs/branch_directory.json:6-11] |

## Business Rules
BR-1: The job deduplicates branches by branch_id using a ROW_NUMBER window function, partitioned by branch_id and ordered by branch_id. Only the first row (rn = 1) per branch_id is retained.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_directory.json:15] SQL: `ROW_NUMBER() OVER (PARTITION BY b.branch_id ORDER BY b.branch_id) AS rn` followed by `WHERE rn = 1`

BR-2: Since the ROW_NUMBER is partitioned by branch_id and ordered by branch_id (a deterministic but arbitrary tie-breaker when branch_id is the same), and the framework executes one effective date at a time, the deduplication removes duplicate branch rows that might exist within a single as_of date.
- Confidence: MEDIUM
- Evidence: [JobExecutor/Jobs/branch_directory.json:15] The ORDER BY within the ROW_NUMBER is `b.branch_id`, which is the same as the PARTITION BY key, making the row selection arbitrary among duplicates
- Note: In practice, with single-date execution, the branches data likely has one row per branch_id per as_of, making the deduplication a no-op safety measure.

BR-3: The output includes all 7 branch attribute columns plus as_of, preserving the full address information.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_directory.json:15] SELECT clause: `branch_id, branch_name, address_line1, city, state_province, postal_code, country, as_of`
- Evidence: [curated.branch_directory] Output schema matches: branch_id, branch_name, address_line1, city, state_province, postal_code, country, as_of

BR-4: Write mode is Overwrite -- the curated table is truncated before each write. Only the latest effective date's branch directory is retained.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_directory.json:21] `"writeMode": "Overwrite"`
- Evidence: [curated.branch_directory] Only 1 as_of date (2024-10-31) with 40 rows

BR-5: The output is ordered by branch_id.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_directory.json:15] SQL ends with `ORDER BY branch_id`
- Evidence: [curated.branch_directory] Rows are ordered by branch_id (1, 2, 3, 4, 5...)

BR-6: The branches table has data for all calendar days including weekends (unlike accounts/customers which are weekday-only).
- Confidence: HIGH
- Evidence: [datalake.branches] Has weekend as_of dates (2024-10-05, 2024-10-06, etc.)
- Evidence: [curated.branch_directory] Only shows last date (2024-10-31) due to Overwrite mode, but the job processes all dates including weekends

BR-7: This job has no upstream dependencies. Other jobs (BranchVisitPurposeBreakdown, BranchVisitSummary) depend on it as a SameDay dependency.
- Confidence: HIGH
- Evidence: [control.job_dependencies] BranchVisitPurposeBreakdown and BranchVisitSummary both have SameDay dependency on BranchDirectory

BR-8: No filtering is applied -- all branches are included regardless of any attribute values.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_directory.json:15] SQL has no WHERE clause other than `rn = 1` for deduplication
- Evidence: [curated.branch_directory] 40 rows matches datalake.branches count of 40 per as_of date

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| branch_id | datalake.branches.branch_id | Deduplicated via ROW_NUMBER | [branch_directory.json:15] |
| branch_name | datalake.branches.branch_name | Pass-through (first row per branch) | [branch_directory.json:15] |
| address_line1 | datalake.branches.address_line1 | Pass-through | [branch_directory.json:15] |
| city | datalake.branches.city | Pass-through | [branch_directory.json:15] |
| state_province | datalake.branches.state_province | Pass-through | [branch_directory.json:15] |
| postal_code | datalake.branches.postal_code | Pass-through | [branch_directory.json:15] |
| country | datalake.branches.country | Pass-through | [branch_directory.json:15] |
| as_of | datalake.branches.as_of | Pass-through (from first row per branch) | [branch_directory.json:15] |

## Edge Cases
- **NULL handling**: No explicit NULL handling in the SQL. NULLs in source columns pass through to output.
- **Duplicate branches**: The ROW_NUMBER deduplication ensures exactly one row per branch_id even if duplicates exist in the source.
- **Empty branches source**: If no branch rows exist for the effective date, the SQL produces zero rows. The DataFrameWriter would write nothing (InsertRows early-returns on empty DataFrame per DataFrameWriter.cs:72).
- **Weekend data**: Unlike account-based jobs, this job processes all calendar days since branches has weekend data.
- **Overwrite mode**: Previous date's data is always replaced.

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [branch_directory.json:15] SQL with ROW_NUMBER |
| BR-2 | [branch_directory.json:15] ORDER BY within ROW_NUMBER |
| BR-3 | [branch_directory.json:15] SELECT clause, [curated.branch_directory schema] |
| BR-4 | [branch_directory.json:21], [curated.branch_directory row counts] |
| BR-5 | [branch_directory.json:15] ORDER BY branch_id |
| BR-6 | [datalake.branches as_of dates] |
| BR-7 | [control.job_dependencies] |
| BR-8 | [branch_directory.json:15] SQL, [curated.branch_directory row counts] |

## Open Questions
- **ROW_NUMBER ordering**: The ORDER BY within the ROW_NUMBER is `b.branch_id`, which is the same column as PARTITION BY. This means the tie-breaking between duplicate rows is non-deterministic (database chooses). In practice, with no duplicates per branch_id per as_of, this is moot. Confidence: HIGH that the deduplication is a safety measure rather than meaningful business logic.
