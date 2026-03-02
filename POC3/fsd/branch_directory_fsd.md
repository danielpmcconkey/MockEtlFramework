# BranchDirectory — Functional Specification Document

## 1. Overview

BranchDirectoryV2 produces a deduplicated CSV directory of all bank branches with their addresses. It sources the `branches` table from the datalake, deduplicates by `branch_id` using `ROW_NUMBER()`, and writes one row per branch to CSV with CRLF line endings.

**Tier: 1 (Framework Only)**
`DataSourcing -> Transformation (SQL) -> CsvFileWriter`

**Justification:** All business logic (dedup via ROW_NUMBER, column selection, ordering) is expressible in SQL. No procedural logic, no cross-boundary date queries, no operations that require C#. There is zero reason to escalate beyond Tier 1.

---

## 2. V2 Module Chain

### Module 1: DataSourcing
- **resultName:** `branches`
- **schema:** `datalake`
- **table:** `branches`
- **columns:** `["branch_id", "branch_name", "address_line1", "city", "state_province", "postal_code", "country"]`
- **Effective dates:** Injected by the executor via `__minEffectiveDate` / `__maxEffectiveDate` shared state keys. No hardcoded dates.
- **Note:** The `as_of` column is automatically appended by the DataSourcing module even when not listed in `columns` (see `DataSourcing.cs:69-72`). The SQL transformation references `as_of` and it will be available.

### Module 2: Transformation
- **resultName:** `branch_dir`
- **sql:** See Section 5 below.

### Module 3: CsvFileWriter
- **source:** `branch_dir`
- **outputFile:** `Output/double_secret_curated/branch_directory.csv`
- **includeHeader:** `true`
- **writeMode:** `Overwrite`
- **lineEnding:** `CRLF`
- **trailerFormat:** not specified (no trailer)

---

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles to Reproduce

None of the standard W-codes (W1-W12) apply to this job. There are no Sunday skips, weekend fallbacks, boundary summaries, integer division, banker's rounding, double epsilon, trailer quirks, stale dates, wrong write modes, or absurd part counts.

However, there is one output-affecting quirk that must be reproduced:

- **Non-deterministic `as_of` (relates to BR-2):** The V1 `ROW_NUMBER() OVER (PARTITION BY branch_id ORDER BY branch_id)` provides no deterministic tie-breaking when multiple `as_of` dates exist for the same branch. The V2 SQL must reproduce this exact same `ORDER BY branch_id` inside the window function to ensure the same non-deterministic behavior. We do NOT "fix" this by ordering by `as_of` -- that would change the output.

### Code-Quality Anti-Patterns to Eliminate

| AP Code | Applies? | V1 Problem | V2 Action |
|---------|----------|------------|-----------|
| AP1 (Dead-end sourcing) | No | All sourced columns are used in the output. | N/A |
| AP2 (Duplicated logic) | No | No cross-job duplication identified. | N/A |
| AP3 (Unnecessary External) | No | V1 already uses framework-only Tier 1. | N/A |
| AP4 (Unused columns) | No | All 7 sourced columns plus auto-added `as_of` appear in the output. | N/A |
| AP5 (Asymmetric NULLs) | No | No NULL handling transformations. Columns are passed through directly. | N/A |
| AP6 (Row-by-row iteration) | No | No External module; SQL handles everything. | N/A |
| AP7 (Magic values) | No | No hardcoded thresholds or magic strings. | N/A |
| AP8 (Complex SQL / unused CTEs) | **Yes** | The CTE is not unused -- it serves the dedup purpose -- but the `ORDER BY b.branch_id` inside `ROW_NUMBER(PARTITION BY b.branch_id ...)` is semantically meaningless since all rows in the partition have the same `branch_id`. | **Reproduce exactly.** While the ORDER BY is meaningless for tie-breaking, changing it could alter which row the database engine selects, which could change the `as_of` value in the output. The V2 SQL preserves this exact construction for output equivalence. This is documented rather than "fixed." |
| AP9 (Misleading names) | No | Job name accurately describes output. | N/A |
| AP10 (Over-sourcing dates) | No | V1 uses the framework's effective date injection correctly. No manual WHERE on dates. | N/A |

**Summary:** This job is remarkably clean. The only anti-pattern that applies (AP8) cannot be safely eliminated because changing the ROW_NUMBER ordering would risk altering which row wins the dedup, changing the non-deterministic `as_of` value. The V2 preserves the V1 SQL structure and documents the meaningless ORDER BY.

---

## 4. Output Schema

| Column | Source Table.Column | Transformation | Notes |
|--------|-------------------|----------------|-------|
| branch_id | branches.branch_id | Direct; deduplicated via ROW_NUMBER (1 row per branch) | INTEGER. Partition key for dedup. |
| branch_name | branches.branch_name | Direct passthrough | VARCHAR |
| address_line1 | branches.address_line1 | Direct passthrough | VARCHAR |
| city | branches.city | Direct passthrough | VARCHAR |
| state_province | branches.state_province | Direct passthrough | VARCHAR |
| postal_code | branches.postal_code | Direct passthrough | VARCHAR |
| country | branches.country | Direct passthrough | CHAR |
| as_of | branches.as_of | Whichever row wins the ROW_NUMBER dedup | DATE. Non-deterministic when effective range spans multiple days. See BR-2. |

**Output ordering:** Ascending by `branch_id` (BR-3).

---

## 5. SQL Design

The V2 SQL is identical to V1. The logic is already clean and correct -- the CTE serves a genuine dedup purpose.

```sql
WITH branch_data AS (
    SELECT b.branch_id,
           b.branch_name,
           b.address_line1,
           b.city,
           b.state_province,
           b.postal_code,
           b.country,
           b.as_of,
           ROW_NUMBER() OVER (PARTITION BY b.branch_id ORDER BY b.branch_id) AS rn
    FROM branches b
)
SELECT branch_id,
       branch_name,
       address_line1,
       city,
       state_province,
       postal_code,
       country,
       as_of
FROM branch_data
WHERE rn = 1
ORDER BY branch_id
```

**Design notes:**
- The `ROW_NUMBER() OVER (PARTITION BY b.branch_id ORDER BY b.branch_id)` is preserved exactly as V1. The ORDER BY is semantically meaningless for tie-breaking (all rows in a partition share the same `branch_id`), but changing it could alter output. Documented per AP8 prescription.
- The CTE `branch_data` computes `rn` for dedup. The outer query filters to `rn = 1` and orders by `branch_id`.
- `as_of` is included in the SELECT -- its value depends on which row wins the ROW_NUMBER, which is non-deterministic (BR-2).
- The `branches` table name in the SQL refers to the DataSourcing result registered in SQLite shared state, not the PostgreSQL table directly.

---

## 6. V2 Job Config

```json
{
  "jobName": "BranchDirectoryV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "branches",
      "schema": "datalake",
      "table": "branches",
      "columns": ["branch_id", "branch_name", "address_line1", "city", "state_province", "postal_code", "country"]
    },
    {
      "type": "Transformation",
      "resultName": "branch_dir",
      "sql": "WITH branch_data AS (SELECT b.branch_id, b.branch_name, b.address_line1, b.city, b.state_province, b.postal_code, b.country, b.as_of, ROW_NUMBER() OVER (PARTITION BY b.branch_id ORDER BY b.branch_id) AS rn FROM branches b) SELECT branch_id, branch_name, address_line1, city, state_province, postal_code, country, as_of FROM branch_data WHERE rn = 1 ORDER BY branch_id"
    },
    {
      "type": "CsvFileWriter",
      "source": "branch_dir",
      "outputFile": "Output/double_secret_curated/branch_directory.csv",
      "includeHeader": true,
      "writeMode": "Overwrite",
      "lineEnding": "CRLF"
    }
  ]
}
```

---

## 7. Writer Configuration

| Parameter | Value | Matches V1? |
|-----------|-------|-------------|
| Writer type | CsvFileWriter | Yes |
| source | `branch_dir` | Yes |
| outputFile | `Output/double_secret_curated/branch_directory.csv` | Path changed to V2 output directory per project conventions |
| includeHeader | `true` | Yes |
| writeMode | `Overwrite` | Yes |
| lineEnding | `CRLF` | Yes |
| trailerFormat | (not specified) | Yes -- V1 has no trailer |

**Write mode implications:** Overwrite mode means each execution replaces the entire file. When running across the full date range (2024-10-01 through 2024-12-31), the final output reflects only the last effective date's execution. This matches V1 behavior exactly.

---

## 8. Proofmark Config Design

**Starting position:** Zero exclusions, zero fuzzy overrides.

**Analysis of non-deterministic fields:**
- `as_of` is identified as non-deterministic in the BRD (BR-2). The ROW_NUMBER ORDER BY provides no deterministic tie-breaking, so which row's `as_of` wins depends on the database engine's internal row ordering.

**However:** Both V1 and V2 execute through the same framework pipeline:
1. DataSourcing queries PostgreSQL with `ORDER BY as_of` (see `DataSourcing.cs:85`)
2. The resulting DataFrame is loaded into SQLite by the Transformation module
3. SQLite's ROW_NUMBER with `ORDER BY branch_id` (all equal within partition) will use insertion order as the implicit tiebreaker
4. Since both V1 and V2 source the same data in the same insertion order, the same row should win

Therefore, `as_of` should match between V1 and V2 despite being theoretically non-deterministic. **Start with strict comparison.** If Proofmark comparison fails on `as_of`, escalate to EXCLUDED at that point with evidence.

**Proposed config:**

```yaml
comparison_target: "branch_directory"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

**Rationale:**
- `reader: csv` -- V1 uses CsvFileWriter
- `header_rows: 1` -- V1 has `includeHeader: true`
- `trailer_rows: 0` -- V1 has no trailer
- No excluded columns -- start strict per best practices
- No fuzzy columns -- no floating-point arithmetic in this job
- `threshold: 100.0` -- require exact match

**Contingency:** If `as_of` causes comparison failure due to non-deterministic row selection, add:
```yaml
columns:
  excluded:
    - name: "as_of"
      reason: "ROW_NUMBER ORDER BY branch_id within PARTITION BY branch_id provides no deterministic tie-breaking. Which as_of value is selected per branch depends on internal row ordering. [branch_directory.json:15] [BRD BR-2]"
```

---

## 9. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|-------------|----------------|----------|
| Tier 1 (DataSourcing -> Transformation -> CsvFileWriter) | Entire BRD -- all logic is SQL-expressible | V1 config uses this exact chain |
| ROW_NUMBER with ORDER BY branch_id preserved | BR-1, BR-2 | [branch_directory.json:15] |
| ORDER BY branch_id in final SELECT | BR-3 | [branch_directory.json:15] |
| as_of included in output columns | BR-5 | [branch_directory.json:15] |
| Non-deterministic as_of documented | BR-2, BR-4 | [branch_directory.json:15], BRD non-deterministic fields section |
| CsvFileWriter with Overwrite/CRLF/header | BRD Writer Configuration | [branch_directory.json:17-24] |
| Same DataSourcing columns (7 columns, as_of auto-added) | BRD Source Tables | [branch_directory.json:8-10], [DataSourcing.cs:69-72] |
| Proofmark starts strict (no exclusions) | BRD non-deterministic analysis | Both V1 and V2 share same data path; `as_of` should match in practice |
| AP8 preserved for output equivalence | BRD BR-2 (non-deterministic ordering) | Changing ORDER BY could alter output |

---

## 10. External Module Design

**Not applicable.** This is a Tier 1 job. No External module is needed. All business logic is expressed in the Transformation SQL.
