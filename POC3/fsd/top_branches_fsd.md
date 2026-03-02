# TopBranches — Functional Specification Document

## 1. Job Summary

TopBranches produces a ranked list of branches by visit count, using `RANK()` with descending visit totals and ordering by rank then branch_id. The output is a CSV with a header row and a `CONTROL` trailer line containing the effective date, row count, and UTC timestamp. Because the framework auto-advances one effective date at a time and the job uses Overwrite mode, each run replaces the file, and the final output reflects only the last effective date's branch_visits data — the V1 SQL's hardcoded `WHERE bv.as_of >= '2024-10-01'` filter is redundant within single-day runs but must be preserved for output equivalence.

## 2. V2 Module Chain

**Tier: 1 — Framework Only (DataSourcing + Transformation + CsvFileWriter)**

| Step | Module | Config Summary |
|------|--------|---------------|
| 1 | DataSourcing | `branch_visits`: `visit_id`, `branch_id` (effective dates injected by executor) |
| 2 | DataSourcing | `branches`: `branch_id`, `branch_name` (effective dates injected by executor) |
| 3 | Transformation | CTE-based SQL: aggregate visit counts, RANK(), join to branches, output ranked list → `top_branches` |
| 4 | CsvFileWriter | source: `top_branches`, Overwrite, LF line endings, header, CONTROL trailer |

**Tier justification:** All V1 business logic — aggregation with COUNT(*), RANK() window function, CTE, and JOIN — is standard SQL fully supported by SQLite. No procedural C# logic is required. The V1 implementation itself is already a Tier 1 framework-only job (DataSourcing + Transformation + CsvFileWriter with no External module), so V2 preserves the same tier.

## 3. DataSourcing Config

### Table 1: `branch_visits`

| Property | Value |
|----------|-------|
| resultName | `branch_visits` |
| schema | `datalake` |
| table | `branch_visits` |
| columns | `visit_id`, `branch_id` |

**Effective date handling:** No explicit `minEffectiveDate`/`maxEffectiveDate` in the config. The executor injects `__minEffectiveDate` and `__maxEffectiveDate` into shared state at runtime. DataSourcing filters `WHERE as_of >= @minDate AND as_of <= @maxDate` automatically. On auto-advance runs, both dates equal the current effective date (single-day load).

**Column rationale:** `visit_id` is sourced to match V1 [top_branches.json:10], although it is not referenced by name in the SQL (COUNT(*) counts all rows — BR-8/AP4 analysis below). `branch_id` is required for grouping and joining. The `as_of` column is appended automatically by DataSourcing since it is not listed in the columns array.

### Table 2: `branches`

| Property | Value |
|----------|-------|
| resultName | `branches` |
| schema | `datalake` |
| table | `branches` |
| columns | `branch_id`, `branch_name` |

**Effective date handling:** Same as branch_visits — injected by executor. Single-day load per auto-advance run.

**Column rationale:** `branch_id` for join key. `branch_name` for output. The `as_of` column is appended automatically by DataSourcing. No other columns from branches are needed (address columns are not sourced and not used).

## 4. Transformation SQL

The SQL replicates V1's exact logic [top_branches.json:22]:

```sql
WITH visit_totals AS (
    SELECT bv.branch_id,
           COUNT(*) AS total_visits
    FROM branch_visits bv
    -- V1 behavior: hardcoded date filter '2024-10-01'.
    -- On single-day auto-advance runs, DataSourcing already filters to the
    -- current effective date, so this WHERE clause is always true (all loaded
    -- data has as_of >= '2024-10-01'). Preserved for output equivalence.
    WHERE bv.as_of >= '2024-10-01'
    GROUP BY bv.branch_id
)
SELECT vt.branch_id,
       b.branch_name,
       vt.total_visits,
       RANK() OVER (ORDER BY vt.total_visits DESC) AS rank,
       b.as_of
FROM visit_totals vt
JOIN branches b ON vt.branch_id = b.branch_id
ORDER BY rank, vt.branch_id
```

**SQL behavior analysis for single-day runs:**

1. **CTE `visit_totals`:** Counts all visits per branch_id from the loaded data. The `WHERE bv.as_of >= '2024-10-01'` is always true because DataSourcing only loads the current effective date, which is always >= 2024-10-01. (BR-1, BR-2)

2. **RANK():** Assigns rank based on descending `total_visits`. Tied branches receive the same rank with gaps in subsequent ranks. (BR-3)

3. **JOIN to branches:** On a single-day run, `branches` contains exactly one row per `branch_id` (40 branches, 1 as_of value each). The join `ON vt.branch_id = b.branch_id` does NOT produce duplicates because there is only one `as_of` per branch in the loaded data. The `b.as_of` column in the output has the same value (the effective date) for every row. (BR-5, BR-6)

4. **ORDER BY:** `rank ASC, vt.branch_id ASC` — deterministic ordering. (BR-4)

5. **Inner JOIN exclusion:** Branches with zero visits on the effective date are excluded because they do not appear in `visit_totals`. (Edge case per BRD)

**Output row count:** On any single-day run, the output contains at most 40 rows (one per branch that has at least one visit on that day).

## 5. Writer Config

| Parameter | V1 Value | V2 Value | Match? |
|-----------|----------|----------|--------|
| Writer type | CsvFileWriter | CsvFileWriter | Yes |
| source | `top_branches` | `top_branches` | Yes |
| outputFile | `Output/curated/top_branches.csv` | `Output/double_secret_curated/top_branches.csv` | Path change per spec |
| includeHeader | `true` | `true` | Yes |
| trailerFormat | `CONTROL\|{date}\|{row_count}\|{timestamp}` | `CONTROL\|{date}\|{row_count}\|{timestamp}` | Yes |
| writeMode | `Overwrite` | `Overwrite` | Yes |
| lineEnding | `LF` | `LF` | Yes |

**Trailer token resolution** (per Architecture.md:241):
- `{date}` → value of `__maxEffectiveDate` from shared state (the current effective date)
- `{row_count}` → count of data rows in the DataFrame (excluding header and trailer)
- `{timestamp}` → UTC now in ISO 8601 format (non-deterministic)

## 6. Wrinkle Replication

No W-codes from `KNOWN_ANTI_PATTERNS.md` apply to this job. Systematic check:

| W-Code | Applies? | Rationale |
|--------|----------|-----------|
| W1 (Sunday skip) | No | No Sunday-specific logic in V1 SQL or config. |
| W2 (Weekend fallback) | No | No weekend date logic. |
| W3a/b/c (Boundary rows) | No | No summary row generation. |
| W4 (Integer division) | No | No division operations. |
| W5 (Banker's rounding) | No | No rounding operations. |
| W6 (Double epsilon) | No | COUNT(*) is integer-only; no floating-point accumulation. |
| W7 (Trailer inflated count) | No | V1 uses the framework CsvFileWriter (not a manual External module write). The trailer `{row_count}` token is resolved by the framework against the actual output DataFrame, so the count is correct. |
| W8 (Trailer stale date) | No | V1 uses the framework `{date}` token, which resolves to `__maxEffectiveDate` — not hardcoded. |
| W9 (Wrong writeMode) | No | Overwrite is appropriate for this job: each run produces a complete snapshot of the ranking, and prior days' output is intentionally replaced. |
| W10 (Absurd numParts) | No | Not a Parquet job. |
| W12 (Header every append) | No | Uses Overwrite mode, not Append. |

**Non-W-code output-affecting behavior to preserve:**

The hardcoded date filter `WHERE bv.as_of >= '2024-10-01'` (BR-1) is a quirk that does not match any formal W-code. It is effectively a no-op on single-day auto-advance runs but must be preserved in V2 for output equivalence. If the job were ever run with a multi-date effective range, the filter would become meaningful. V2 preserves it exactly as V1 has it.

## 7. Anti-Pattern Elimination

### AP4: Unused Columns — `visit_id`

**BRD Evidence:** BR-8 identifies that `visit_id` is sourced [top_branches.json:10] but never directly referenced in the SQL — `COUNT(*)` counts all rows regardless of column values [top_branches.json:22].

**V2 Decision: ELIMINATE.** Remove `visit_id` from the DataSourcing columns list for `branch_visits`. The CTE uses `COUNT(*)`, which counts rows, not column values. Removing `visit_id` from the sourced columns does not change the row count or any computed value. The DataSourcing module will still load the same rows (filtered by effective date); only the column projection changes.

**Verification:** `COUNT(*)` in SQLite (and all SQL engines) counts rows regardless of which columns are selected. Dropping `visit_id` from the source does not affect the DataFrame's row count, so `COUNT(*)` produces the same result.

### AP3: Unnecessary External Module — Not Applicable

V1 already uses Tier 1 (framework modules only). No External module to eliminate.

### AP1: Dead-End Sourcing — Not Applicable

Both sourced tables (`branch_visits`, `branches`) are used in the Transformation SQL.

### AP10: Over-Sourcing Dates — Not Applicable

V1 does not specify explicit `minEffectiveDate`/`maxEffectiveDate` in the DataSourcing configs — it relies on executor-injected dates. This is the correct pattern. The hardcoded `WHERE bv.as_of >= '2024-10-01'` in the Transformation SQL is redundant but harmless; it does not cause over-sourcing at the DataSourcing level.

### AP8: Complex SQL / Unused CTEs — Not Applicable

The CTE `visit_totals` is fully consumed by the main SELECT. No unused computations.

### AP9: Misleading Names — Not Applicable

The job name `TopBranches` accurately describes the output (a ranked list of branches by visit count).

### Summary

| AP Code | V1 Problem | V2 Resolution |
|---------|-----------|---------------|
| AP4 | `visit_id` sourced but never referenced in SQL (COUNT(*) counts rows) [top_branches.json:10, 22] | **Eliminated.** `visit_id` removed from V2 DataSourcing columns for `branch_visits`. |

## 8. Proofmark Config

**Starting position:** Zero exclusions, zero fuzzy overrides.

**Analysis of each output column:**

| Column | Deterministic? | Fuzzy needed? | Verdict |
|--------|---------------|---------------|---------|
| branch_id | Yes — GROUP BY key from source data | No | STRICT |
| branch_name | Yes — direct lookup from branches table | No | STRICT |
| total_visits | Yes — COUNT(*) is deterministic for same input data | No | STRICT |
| rank | Yes — RANK() over deterministic total_visits | No | STRICT |
| as_of | Yes — from branches table, single value per single-day run | No | STRICT |

**Non-deterministic fields:** The `{timestamp}` token in the trailer line produces a UTC timestamp that varies per execution. The trailer row is stripped by Proofmark via `trailer_rows: 1`, so this does not require a column exclusion.

**File structure:**
- Header row: Yes (`includeHeader: true`) → `header_rows: 1`
- Trailer row: Yes (`trailerFormat` present + Overwrite mode) → `trailer_rows: 1`

```yaml
comparison_target: "top_branches"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

No exclusions or fuzzy overrides required. All data columns are deterministically derived from source data and should match exactly between V1 and V2. The trailer row (which contains the non-deterministic `{timestamp}`) is stripped by Proofmark's `trailer_rows: 1` setting.

## 9. Open Questions

### OQ-1 (from BRD): Branch Row Duplication via Non-Date-Aligned Join

**BRD states:** The join `ON vt.branch_id = b.branch_id` without an `as_of` condition would cause duplication if `branches` contained multiple `as_of` values per `branch_id`.

**FSD resolution:** On single-day auto-advance runs (which is how the executor runs this job), DataSourcing loads only one date's worth of branches data. Each `branch_id` appears exactly once in the `branches` DataFrame, so the join does not produce duplicates. The `b.as_of` column in the output is the same value (the effective date) for every row.

If the job were ever run with a multi-date effective range (e.g., manual backfill `dotnet run --project JobExecutor -- 2024-10-01`), the join would produce duplicates — each branch's ranking row would repeat once per `as_of` date in branches. V2 reproduces this behavior faithfully since it uses the same SQL and same DataSourcing configuration.

**Status:** Resolved for V2 purposes. The behavior (whether it's a V1 bug or intentional) is faithfully replicated.

### OQ-2 (from BRD): Hardcoded Date Filter `'2024-10-01'`

**BRD states:** The `WHERE bv.as_of >= '2024-10-01'` is hardcoded rather than parameterized.

**FSD resolution:** On single-day auto-advance runs, DataSourcing already limits data to the current effective date (always >= 2024-10-01), making this filter redundant. V2 preserves the hardcoded date exactly as V1 has it for output equivalence. If the filter were removed, the output would be identical on all current runs, but removing it could change behavior on hypothetical future data loads with dates before 2024-10-01.

**Status:** Preserved in V2 SQL. No functional impact on current data.

## 10. V2 Job Config

```json
{
  "jobName": "TopBranchesV2",
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
      "resultName": "top_branches",
      "sql": "WITH visit_totals AS (SELECT bv.branch_id, COUNT(*) AS total_visits FROM branch_visits bv WHERE bv.as_of >= '2024-10-01' GROUP BY bv.branch_id) SELECT vt.branch_id, b.branch_name, vt.total_visits, RANK() OVER (ORDER BY vt.total_visits DESC) AS rank, b.as_of FROM visit_totals vt JOIN branches b ON vt.branch_id = b.branch_id ORDER BY rank, vt.branch_id"
    },
    {
      "type": "CsvFileWriter",
      "source": "top_branches",
      "outputFile": "Output/double_secret_curated/top_branches.csv",
      "includeHeader": true,
      "trailerFormat": "CONTROL|{date}|{row_count}|{timestamp}",
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

**Key V2 differences from V1 config:**
- `jobName`: `TopBranchesV2` (V2 naming convention)
- `outputFile`: `Output/double_secret_curated/top_branches.csv` (V2 output path)
- `columns` for `branch_visits`: `["branch_id"]` only — `visit_id` removed (AP4 elimination)
- All other parameters identical to V1

## 11. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision |
|----------------|-------------|-----------------|
| BR-1: Hardcoded date filter `'2024-10-01'` | Section 4 (SQL) | Preserved in V2 SQL. Redundant on single-day runs but kept for output equivalence. |
| BR-2: Cross-date aggregation (COUNT(*) per branch_id) | Section 4 (SQL) | Preserved. CTE groups by `bv.branch_id` with no `as_of` in GROUP BY. On single-day runs, effectively counts that day's visits. |
| BR-3: RANK() window function | Section 4 (SQL) | Preserved. `RANK() OVER (ORDER BY vt.total_visits DESC)`. |
| BR-4: ORDER BY rank, branch_id | Section 4 (SQL) | Preserved. `ORDER BY rank, vt.branch_id`. |
| BR-5: Non-date-aligned join | Section 4 (SQL), Section 9 (OQ-1) | Preserved. Join uses `ON vt.branch_id = b.branch_id` without `as_of` condition. No duplication on single-day runs; would duplicate on multi-date runs. |
| BR-6: `as_of` from branches in output | Section 4 (SQL) | Preserved. `b.as_of` is in the SELECT list. On single-day runs, all rows have the same `as_of` value. |
| BR-7: Control trailer with date, row_count, timestamp | Section 5 (Writer Config) | Preserved. `trailerFormat: "CONTROL\|{date}\|{row_count}\|{timestamp}"`. |
| BR-8: `visit_id` sourced but unused | Section 7 (AP4) | **Eliminated.** `visit_id` removed from V2 DataSourcing. COUNT(*) does not depend on specific column presence. |
| OQ-1: Branch duplication from join | Section 9 | Resolved — no duplication on single-day auto-advance runs. V2 replicates V1 behavior faithfully. |
| OQ-2: Hardcoded date filter | Section 9 | Resolved — preserved in V2 for output equivalence. |

## 12. External Module Design

Not applicable. This is a **Tier 1** implementation — no External module required. V1 is also Tier 1.
