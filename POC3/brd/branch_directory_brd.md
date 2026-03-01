# BranchDirectory — Business Requirements Document

## Overview
Produces a deduplicated directory of all bank branches with their addresses, selecting one row per branch_id using ROW_NUMBER partitioning, and outputting to CSV format.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `branch_dir`
- **outputFile**: `Output/curated/branch_directory.csv`
- **includeHeader**: true
- **writeMode**: Overwrite
- **lineEnding**: CRLF
- **trailerFormat**: not specified (no trailer)

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.branches | branch_id, branch_name, address_line1, city, state_province, postal_code, country | Effective date range (injected by executor) | [branch_directory.json:8-10] |

### Schema Details

**branches**: branch_id (integer), branch_name (varchar), address_line1 (varchar), city (varchar), state_province (varchar), postal_code (varchar), country (char), as_of (date)

## Business Rules

BR-1: The job deduplicates branches using `ROW_NUMBER() OVER (PARTITION BY branch_id ORDER BY branch_id)`, keeping only the first row per branch_id (WHERE rn = 1).
- Confidence: HIGH
- Evidence: [branch_directory.json:15] SQL CTE with ROW_NUMBER and WHERE rn = 1

BR-2: The ROW_NUMBER ordering is by `branch_id` (not by `as_of`), which means the selection of which as_of snapshot is used per branch is non-deterministic when multiple as_of dates exist in the effective range.
- Confidence: HIGH
- Evidence: [branch_directory.json:15] `ORDER BY b.branch_id` inside the ROW_NUMBER — since all rows within a partition have the same branch_id, the ORDER BY provides no deterministic tie-breaking

BR-3: The final output is ordered by branch_id ascending.
- Confidence: HIGH
- Evidence: [branch_directory.json:15] `ORDER BY branch_id`

BR-4: When the effective date range spans multiple days, the DataSourcing module returns branches for all dates. The ROW_NUMBER dedup collapses these to one row per branch, but which date's snapshot wins is arbitrary (see BR-2).
- Confidence: HIGH
- Evidence: [branch_directory.json:15] ROW_NUMBER partitions by branch_id; 40 branches exist on every date per DB observation

BR-5: The as_of column is included in the output (selected after the WHERE filter from the CTE).
- Confidence: HIGH
- Evidence: [branch_directory.json:15] `SELECT branch_id, branch_name, address_line1, city, state_province, postal_code, country, as_of`

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| branch_id | branches.branch_id | Direct, deduplicated | [branch_directory.json:15] |
| branch_name | branches.branch_name | Direct | [branch_directory.json:15] |
| address_line1 | branches.address_line1 | Direct | [branch_directory.json:15] |
| city | branches.city | Direct | [branch_directory.json:15] |
| state_province | branches.state_province | Direct | [branch_directory.json:15] |
| postal_code | branches.postal_code | Direct | [branch_directory.json:15] |
| country | branches.country | Direct | [branch_directory.json:15] |
| as_of | branches.as_of | Whichever row wins the ROW_NUMBER | [branch_directory.json:15] |

## Non-Deterministic Fields

- **as_of**: Because ROW_NUMBER is ordered by branch_id within a partition of identical branch_ids, the database engine may return any row's as_of value when multiple dates are in the effective range. The specific as_of value per branch is therefore non-deterministic.

## Write Mode Implications
Overwrite mode: Each execution replaces the entire `Output/curated/branch_directory.csv` file. This is a full-refresh directory that always reflects the latest execution.

## Edge Cases

- **Multi-day effective range**: With 40 branches appearing on every date, the CTE sees 40 x N_days rows. ROW_NUMBER dedup picks one per branch, but the as_of is arbitrary.
- **Branch attribute changes**: If a branch's address changes between as_of dates, which version appears depends on which row wins the ROW_NUMBER — this is undefined behavior.
- **Single-day effective range**: When run for a single date, ROW_NUMBER is effectively a no-op since each branch has exactly one row.
- **CRLF line endings**: Output uses Windows-style line endings.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: ROW_NUMBER dedup | [branch_directory.json:15] |
| BR-2: Non-deterministic as_of | [branch_directory.json:15] |
| BR-3: ORDER BY branch_id | [branch_directory.json:15] |
| BR-4: Multi-day collapse | [branch_directory.json:15], DB observation |
| BR-5: as_of in output | [branch_directory.json:15] |

## Open Questions

OQ-1: Is the non-deterministic ROW_NUMBER ordering intentional, or should it be `ORDER BY b.as_of DESC` (or ASC) to pick the latest (or earliest) snapshot?
- Confidence: MEDIUM — the current ORDER BY is functionally meaningless for tie-breaking
