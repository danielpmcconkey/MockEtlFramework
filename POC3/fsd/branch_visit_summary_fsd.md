# BranchVisitSummary — Functional Specification Document

## 1. Overview

BranchVisitSummaryV2 produces a per-branch, per-date summary of visit counts by aggregating `branch_visits` and joining with branch names from the `branches` table. Output is a CSV file in Append mode with a trailer line per execution day.

**Tier: 1 (Framework Only)** — `DataSourcing -> Transformation (SQL) -> CsvFileWriter`

**Justification:** The V1 pipeline is already DataSourcing + Transformation + CsvFileWriter with no External module. All business logic (COUNT aggregation, INNER JOIN, ORDER BY) is straightforward SQL expressible in SQLite. There is no procedural logic, no snapshot fallback, no cross-boundary date queries, and no operation that requires an External module. Tier 1 is the correct and simplest choice.

## 2. V2 Module Chain

### Module 1: DataSourcing — `branch_visits`
| Field | Value |
|-------|-------|
| type | DataSourcing |
| resultName | branch_visits |
| schema | datalake |
| table | branch_visits |
| columns | `["branch_id"]` |

**Changes from V1:** Removed `visit_id`, `customer_id`, and `visit_purpose` columns. None of these are referenced in the transformation SQL. `visit_id` is not needed because the aggregation uses `COUNT(*)`, not `COUNT(visit_id)`. `customer_id` and `visit_purpose` are sourced but never used in any downstream computation. This eliminates AP4 (unused columns) and partially addresses AP1 (dead-end sourcing of columns that feed nothing).

### Module 2: DataSourcing — `branches`
| Field | Value |
|-------|-------|
| type | DataSourcing |
| resultName | branches |
| schema | datalake |
| table | branches |
| columns | `["branch_id", "branch_name"]` |

**Changes from V1:** None. Both columns are used in the transformation SQL. `branch_id` participates in the JOIN condition and `branch_name` is selected into the output.

### Module 3: Transformation
| Field | Value |
|-------|-------|
| type | Transformation |
| resultName | visit_summary |
| sql | See Section 5 |

### Module 4: CsvFileWriter
| Field | Value |
|-------|-------|
| type | CsvFileWriter |
| source | visit_summary |
| outputFile | `Output/double_secret_curated/branch_visit_summary.csv` |
| includeHeader | true |
| trailerFormat | `TRAILER\|{row_count}\|{date}` |
| writeMode | Append |
| lineEnding | LF |

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles to Reproduce

| W-Code | Applies? | V2 Handling |
|--------|----------|-------------|
| W1 | NO | No Sunday skip logic in V1 job config or SQL. |
| W2 | NO | No weekend fallback logic. |
| W3a/W3b/W3c | NO | No boundary summary rows. |
| W4 | NO | No integer division — `COUNT(*)` produces an integer count, not a ratio. |
| W5 | NO | No rounding operations. |
| W6 | NO | No floating-point accumulation. |
| W7 | NO | Trailer `{row_count}` correctly reflects output row count (the CsvFileWriter's `df.Count` is the count of output rows from the Transformation, which already reflects the GROUP BY). No inflated count issue. |
| W8 | NO | Trailer `{date}` is populated from `__maxEffectiveDate` via the framework's CsvFileWriter (see `CsvFileWriter.cs:60-62`). No hardcoded date. |
| W9 | NO | Append mode is appropriate for this job — each day's execution adds that day's branch visit counts and a trailer to the cumulative file. |
| W10 | NO | Not applicable (CSV writer, not Parquet). |
| W12 | NO | The framework's CsvFileWriter only writes the header on the first write (when the file doesn't yet exist). On subsequent Append writes, it skips the header (`CsvFileWriter.cs:47`). No repeated headers. |

**No W-codes apply to this job.** The V1 implementation is clean with respect to output-affecting wrinkles.

### Code-Quality Anti-Patterns to Eliminate

| AP-Code | Applies? | V1 Problem | V2 Fix |
|---------|----------|------------|--------|
| AP1 | YES | `visit_id`, `customer_id`, and `visit_purpose` are sourced from `branch_visits` but never referenced in the transformation SQL. These columns flow into DataSourcing but are dead-ends — they consume memory and I/O for no purpose. | Removed all three columns from the `branch_visits` DataSourcing. V2 sources only `branch_id`, which is the sole column used in the SQL (for GROUP BY, JOIN condition, and output). |
| AP4 | YES | Same as AP1 — `visit_id`, `customer_id`, `visit_purpose` are unused columns. | Removed from V2 DataSourcing config. Only `branch_id` is sourced from `branch_visits`. |
| AP3 | NO | V1 already uses framework modules (DataSourcing + Transformation + CsvFileWriter). No unnecessary External module. | N/A |
| AP8 | NO | The V1 SQL uses one CTE (`visit_counts`) which is referenced in the main query. The CTE is not unused. However, it is unnecessary complexity — the same result can be achieved with a single query using a direct JOIN + GROUP BY. See Section 5 for the simplified SQL. While the CTE is not technically "unused," it adds a layer of indirection that serves no purpose when the aggregation and join can be done in one pass. | V2 SQL eliminates the CTE and uses a direct JOIN + GROUP BY. |
| AP9 | NO | Job name `BranchVisitSummary` accurately describes the output. | N/A |
| AP10 | NO | DataSourcing uses framework effective date injection (no explicit date columns in V1 config, no WHERE clause on dates in SQL). The framework handles date filtering at the source. | N/A |

Anti-patterns AP2, AP5, AP6, AP7 do not apply to this job.

### BRD Correction: BR-2 (JOIN Type)

**The BRD incorrectly states that branches are LEFT JOINed.** The actual V1 SQL on `branch_visit_summary.json:22` reads:

```sql
... FROM visit_counts vc JOIN branches b ON vc.branch_id = b.branch_id AND vc.as_of = b.as_of ...
```

`JOIN` in SQLite is an INNER JOIN. This means branches with visits but no matching `branches` record for that `as_of` are **excluded** from the output (not preserved with NULL `branch_name` as the BRD claims). The V2 implementation must reproduce this INNER JOIN behavior.

**Evidence:** `[branch_visit_summary.json:22]` — the keyword is `JOIN`, not `LEFT JOIN`.

**Impact on BR-7:** The BRD's BR-7 states that branches whose `branch_id` exists in visits but not in the branches table will appear with NULL `branch_name`. This is incorrect for the same reason — INNER JOIN excludes those rows entirely.

V2 reproduces the INNER JOIN behavior exactly.

## 4. Output Schema

| Column | Type | Source Table | Source Column | Transformation | BRD Ref |
|--------|------|-------------|---------------|----------------|---------|
| branch_id | INTEGER | branch_visits | branch_id | Direct, grouped via GROUP BY | BR-1 |
| branch_name | TEXT | branches | branch_name | Lookup via date-aligned INNER JOIN on branch_id + as_of | BR-2 (corrected: INNER, not LEFT) |
| as_of | TEXT | branch_visits | as_of | Direct, grouped via GROUP BY | BR-3 |
| visit_count | INTEGER | branch_visits | (all rows) | `COUNT(*)` per branch per date | BR-1 |

**Column order:** `branch_id, branch_name, as_of, visit_count` — matches V1 SQL SELECT list order.

**Row order:** `ORDER BY as_of, branch_id` — matches V1 SQL ORDER BY clause (BR-3).

## 5. SQL Design

### V1 SQL (for reference)

```sql
WITH visit_counts AS (
    SELECT bv.branch_id, COUNT(*) AS visit_count, bv.as_of
    FROM branch_visits bv
    GROUP BY bv.branch_id, bv.as_of
)
SELECT vc.branch_id, b.branch_name, vc.as_of, vc.visit_count
FROM visit_counts vc
JOIN branches b ON vc.branch_id = b.branch_id AND vc.as_of = b.as_of
ORDER BY vc.as_of, vc.branch_id
```

### V2 SQL

```sql
SELECT
    bv.branch_id,
    b.branch_name,
    bv.as_of,
    COUNT(*) AS visit_count
FROM branch_visits bv
JOIN branches b
    ON bv.branch_id = b.branch_id
    AND bv.as_of = b.as_of
GROUP BY bv.branch_id, b.branch_name, bv.as_of
ORDER BY bv.as_of, bv.branch_id
```

**SQL Walkthrough:**

1. **FROM branch_visits bv** — Start with all branch visit records in the effective date range (loaded by DataSourcing with framework date injection). The `as_of` column is automatically appended by `DataSourcing` since it is not explicitly listed in the columns array.

2. **JOIN branches b ON bv.branch_id = b.branch_id AND bv.as_of = b.as_of** — INNER JOIN with the branches table, aligned on both `branch_id` and `as_of`. This ensures date-aligned snapshots are matched. Visits referencing a branch_id that has no matching record in `branches` for that `as_of` are excluded from the output. (BR-2 corrected, BR-1)

3. **COUNT(\*) AS visit_count** — Counts all visit records per group. Since all visits in a branch_id/as_of group are counted, no filtering by `visit_id`, `customer_id`, or `visit_purpose` occurs. (BR-1)

4. **GROUP BY bv.branch_id, b.branch_name, bv.as_of** — One output row per branch per date. `b.branch_name` is included in GROUP BY because it appears in the SELECT list (SQLite requirement for non-aggregate columns). Since the INNER JOIN ensures a 1:1 match on `branch_id + as_of`, `branch_name` is functionally dependent on `branch_id + as_of` and this does not change the grouping semantics.

5. **ORDER BY bv.as_of, bv.branch_id** — Output sorted by date then branch, matching V1. (BR-3)

**Changes from V1 SQL:**

- **CTE eliminated:** V1 uses a CTE (`visit_counts`) to pre-aggregate visits, then JOINs the CTE result with `branches`. V2 performs the JOIN and GROUP BY in a single pass. This is semantically equivalent because the INNER JOIN ensures only branches with visits appear, and grouping by `bv.branch_id, b.branch_name, bv.as_of` produces the same groups as V1's two-step approach. This addresses unnecessary SQL complexity (related to AP8 — while the CTE isn't unused, it's an unnecessary indirection).

- **Output equivalence:** The V2 SQL produces identical rows in identical order to V1. The aggregation logic (COUNT(*) grouped by branch_id + as_of) is unchanged. The JOIN condition (branch_id + as_of) is unchanged. The ORDER BY is unchanged.

## 6. V2 Job Config

```json
{
  "jobName": "BranchVisitSummaryV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "branch_visits",
      "schema": "datalake",
      "table": "branch_visits",
      "columns": ["branch_id"]
    },
    {
      "type": "DataSourcing",
      "resultName": "branches",
      "schema": "datalake",
      "table": "branches",
      "columns": ["branch_id", "branch_name"]
    },
    {
      "type": "Transformation",
      "resultName": "visit_summary",
      "sql": "SELECT bv.branch_id, b.branch_name, bv.as_of, COUNT(*) AS visit_count FROM branch_visits bv JOIN branches b ON bv.branch_id = b.branch_id AND bv.as_of = b.as_of GROUP BY bv.branch_id, b.branch_name, bv.as_of ORDER BY bv.as_of, bv.branch_id"
    },
    {
      "type": "CsvFileWriter",
      "source": "visit_summary",
      "outputFile": "Output/double_secret_curated/branch_visit_summary.csv",
      "includeHeader": true,
      "trailerFormat": "TRAILER|{row_count}|{date}",
      "writeMode": "Append",
      "lineEnding": "LF"
    }
  ]
}
```

## 7. Writer Configuration

| Parameter | Value | Matches V1? | Notes |
|-----------|-------|-------------|-------|
| Writer type | CsvFileWriter | YES | Same writer type as V1 |
| source | visit_summary | YES | Same source DataFrame name as V1 |
| outputFile | `Output/double_secret_curated/branch_visit_summary.csv` | PATH CHANGE | V1: `Output/curated/branch_visit_summary.csv`. V2 writes to `double_secret_curated` per project convention. |
| includeHeader | true | YES | Header row written on first file creation |
| trailerFormat | `TRAILER\|{row_count}\|{date}` | YES | Identical trailer format. `{row_count}` = count of data rows in this execution's output. `{date}` = `__maxEffectiveDate` from shared state. (BR-6) |
| writeMode | Append | YES | Each execution appends data rows and a trailer line to the file. (BR-6, edge case: multiple trailers accumulate) |
| lineEnding | LF | YES | Unix-style line endings |

**Append mode behavior:** The framework's CsvFileWriter writes the header only on the first execution (when the file is created). On subsequent Append executions, it skips the header (`CsvFileWriter.cs:47`: `if (_includeHeader && !append)`). Each execution appends its data rows followed by a trailer line. Over a multi-day run (2024-10-01 through 2024-12-31), the file accumulates:
- One header row at the top
- For each day: data rows + one TRAILER line
- No blank lines between days

## 8. Proofmark Config Design

**Starting assumption:** Zero exclusions, zero fuzzy overrides.

**Analysis of each output column:**

| Column | Deterministic? | Precision Concern? | Decision |
|--------|---------------|-------------------|----------|
| branch_id | YES — direct from source data, grouped | No | STRICT |
| branch_name | YES — direct lookup from branches table | No | STRICT |
| as_of | YES — direct from source data, grouped | No | STRICT |
| visit_count | YES — COUNT(*) is deterministic | No | STRICT |

**Non-deterministic fields from BRD:** None identified. The trailer's `{date}` token uses `__maxEffectiveDate` which is deterministic for a given execution date.

**Trailer handling:** This is an Append-mode file with `trailerFormat` present. Per the CONFIG_GUIDE.md rules: `trailerFormat` present + `writeMode: Append` = `trailer_rows: 0`. The trailers are embedded per-day throughout the file, not only at the file end. Proofmark treats them as regular data rows for comparison purposes, which is correct since both V1 and V2 produce identical trailer lines for each day.

**Conclusion:** All columns should use strict comparison. No exclusions or fuzzy overrides are warranted.

**Proofmark Config:**

```yaml
comparison_target: "branch_visit_summary"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision |
|-----------------|-------------|-----------------|
| BR-1: COUNT per branch per date | Sec 5 (SQL), Sec 4 (Output Schema) | `COUNT(*)` with `GROUP BY bv.branch_id, b.branch_name, bv.as_of` preserved exactly |
| BR-2: Date-aligned branch join | Sec 5 (SQL), Sec 3 (BRD Correction) | **CORRECTED:** V1 uses INNER JOIN, not LEFT JOIN. V2 reproduces INNER JOIN: `JOIN branches b ON bv.branch_id = b.branch_id AND bv.as_of = b.as_of`. BRD erroneously states LEFT JOIN. |
| BR-3: ORDER BY as_of, branch_id | Sec 5 (SQL) | `ORDER BY bv.as_of, bv.branch_id` preserved exactly |
| BR-4: customer_id/visit_purpose unused | Sec 2 (Module 1), Sec 3 (AP1/AP4) | Columns removed from DataSourcing. Neither appears in V1 SQL. |
| BR-5: visit_id unused | Sec 2 (Module 1), Sec 3 (AP1/AP4) | Column removed from DataSourcing. V1 uses `COUNT(*)` not `COUNT(visit_id)`. |
| BR-6: Trailer with row_count + date | Sec 7 (Writer Config), Sec 8 (Proofmark) | `trailerFormat: "TRAILER\|{row_count}\|{date}"` preserved exactly. Framework CsvFileWriter handles token substitution. |
| BR-7: No zero-visit branches; NULL branch_name behavior | Sec 3 (BRD Correction), Sec 5 (SQL) | **CORRECTED:** V1's INNER JOIN means branches with visits but missing from `branches` table are excluded entirely (not preserved with NULL `branch_name`). V2 reproduces this INNER JOIN behavior. |

## 10. External Module Design

**Not applicable.** This is a Tier 1 implementation. No External module is needed. All business logic is expressed in SQL within the Transformation module.
