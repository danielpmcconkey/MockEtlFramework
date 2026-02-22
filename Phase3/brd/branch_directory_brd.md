# BranchDirectory — Business Requirements Document

## Overview

This job produces a reference directory of all bank branches with their addresses, deduplicating by branch_id. The result is written to `curated.branch_directory` using Overwrite mode, retaining only the latest effective date's data.

## Source Tables

### datalake.branches
- **Columns sourced:** branch_id, branch_name, address_line1, city, state_province, postal_code, country
- **Columns actually used:** All 7 sourced columns plus framework-injected as_of
- **Join/filter logic:** No filtering. All branch rows for the effective date are included, then deduplicated by branch_id using ROW_NUMBER.
- **Evidence:** [JobExecutor/Jobs/branch_directory.json:9-10] columns list; [JobExecutor/Jobs/branch_directory.json:14-15] SQL references all columns.

## Business Rules

BR-1: All branches are included in the output directory (no filtering by status, type, or any other attribute).
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_directory.json:14-15] SQL has no WHERE clause filtering branches; only ROW_NUMBER dedup.
- Evidence: [curated.branch_directory] 40 rows = [datalake.branches] 40 distinct branch_ids.

BR-2: The output is deduplicated by branch_id using ROW_NUMBER partitioned by branch_id, keeping the first row per partition.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_directory.json:15] `ROW_NUMBER() OVER (PARTITION BY b.branch_id ORDER BY b.branch_id) AS rn` ... `WHERE rn = 1`

BR-3: The output contains 8 columns: branch_id, branch_name, address_line1, city, state_province, postal_code, country, as_of.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_directory.json:15] SELECT clause lists these 8 columns.
- Evidence: [curated.branch_directory] Schema confirms these 8 columns.

BR-4: Results are ordered by branch_id ascending.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_directory.json:15] `ORDER BY branch_id`

BR-5: Data is written in Overwrite mode — only the most recent effective date's data is retained.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/branch_directory.json:22] `"writeMode": "Overwrite"`.
- Evidence: [curated.branch_directory] Only 1 as_of date (2024-10-31) present.

## Output Schema

| Column | Source | Transformation |
|--------|--------|----------------|
| branch_id | datalake.branches.branch_id | Direct pass-through |
| branch_name | datalake.branches.branch_name | Direct pass-through |
| address_line1 | datalake.branches.address_line1 | Direct pass-through |
| city | datalake.branches.city | Direct pass-through |
| state_province | datalake.branches.state_province | Direct pass-through |
| postal_code | datalake.branches.postal_code | Direct pass-through |
| country | datalake.branches.country | Direct pass-through |
| as_of | datalake.branches.as_of (injected by framework) | Direct pass-through |

## Edge Cases

- **Duplicate branch_ids:** The ROW_NUMBER dedup handles the theoretical case of duplicate branch_ids within a single as_of snapshot. However, in practice there are no duplicates — datalake.branches has exactly 40 rows per as_of, all with unique branch_ids.
- **Weekend dates:** Branches data exists for both weekdays and weekends (unlike accounts/customers). The job will produce output for weekend dates.
- **Empty source data:** If no branches exist for the effective date, the Transformation produces an empty result, and DataFrameWriter writes nothing (Overwrite mode truncates then inserts zero rows).

## Anti-Patterns Identified

- **AP-8: Overly Complex SQL** — The SQL wraps a simple SELECT in a CTE with ROW_NUMBER to deduplicate by branch_id, but the source data has no duplicate branch_ids per as_of date. The entire CTE and ROW_NUMBER construct is unnecessary — a simple `SELECT branch_id, branch_name, address_line1, city, state_province, postal_code, country, as_of FROM branches ORDER BY branch_id` would produce identical results.
  - Evidence: [JobExecutor/Jobs/branch_directory.json:15] Complex CTE with ROW_NUMBER; [datalake.branches] `SELECT branch_id, COUNT(*) FROM datalake.branches WHERE as_of = '2024-10-01' GROUP BY branch_id HAVING COUNT(*) > 1` returns 0 rows — no duplicates exist.
  - V2 approach: Replace with simple SELECT without the CTE/ROW_NUMBER dedup.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [JobExecutor/Jobs/branch_directory.json:14-15], [curated.branch_directory] 40 rows |
| BR-2 | [JobExecutor/Jobs/branch_directory.json:15] ROW_NUMBER clause |
| BR-3 | [JobExecutor/Jobs/branch_directory.json:15], [curated.branch_directory] schema |
| BR-4 | [JobExecutor/Jobs/branch_directory.json:15] ORDER BY |
| BR-5 | [JobExecutor/Jobs/branch_directory.json:22], [curated.branch_directory] 1 date |

## Open Questions

None. The business logic is straightforward. The only notable aspect is the unnecessary complexity of the SQL deduplication.
