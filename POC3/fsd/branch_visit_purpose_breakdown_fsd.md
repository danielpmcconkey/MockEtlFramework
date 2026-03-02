# BranchVisitPurposeBreakdownV2 — Functional Specification Document

## 1. Overview

**Job:** `BranchVisitPurposeBreakdownV2`
**Config:** `branch_visit_purpose_breakdown_v2.json`
**Tier:** 1 (Framework Only) -- `DataSourcing -> Transformation (SQL) -> CsvFileWriter`

**Justification for Tier 1:** The V1 job is already a pure framework chain (DataSourcing + Transformation + CsvFileWriter) with no External module. All business logic is expressible in SQL. The V2 rewrite stays at Tier 1, eliminates anti-patterns in the data sourcing and SQL, and produces byte-identical output.

**Summary:** Produces a per-branch, per-visit-purpose, per-date breakdown of visit counts. Output shows how many visits each branch received for each purpose (Account Opening, Deposit, Inquiry, Loan Application, Withdrawal), joined with branch names from the `branches` table.

---

## 2. V2 Module Chain

```
DataSourcing (branch_visits)
  -> DataSourcing (branches)
  -> Transformation (SQL: purpose_breakdown)
  -> CsvFileWriter (branch_visit_purpose_breakdown.csv)
```

**Key changes from V1:**
- Removed unused `segments` DataSourcing (AP1 elimination)
- Removed unused columns `visit_id` and `customer_id` from `branch_visits` sourcing (AP4 elimination)
- Simplified SQL to remove unused `total_branch_visits` window function (AP8 elimination)

---

## 3. Anti-Pattern Analysis

### Anti-Patterns Identified and Eliminated

| AP Code | V1 Problem | V2 Action | Status |
|---------|-----------|-----------|--------|
| AP1 | `segments` table sourced but never referenced in SQL | Removed `segments` DataSourcing entry entirely | ELIMINATED |
| AP4 | `visit_id` and `customer_id` columns sourced from `branch_visits` but never used in SQL | Removed both columns from the `branch_visits` column list; only source `branch_id` and `visit_purpose` | ELIMINATED |
| AP8 | CTE computes `total_branch_visits` via `SUM(COUNT(*)) OVER (PARTITION BY bv.branch_id, bv.as_of)` but the outer SELECT never references it | Removed the CTE entirely; simplified to a direct query with GROUP BY and JOIN | ELIMINATED |

### Output-Affecting Wrinkles

| W Code | Applies? | Notes |
|--------|----------|-------|
| W1-W12 | No | None of the cataloged wrinkles apply to this job. No Sunday skip, no weekend fallback, no boundary rows, no integer division, no rounding, no double arithmetic, no trailer inflation, no stale date, no wrong writeMode, no absurd numParts, no header-every-append. |

**Wrinkle-free job.** The V1 output is straightforward: grouped counts joined with branch names, written as CSV with a trailer in Append mode. No output-affecting quirks need replication.

---

## 4. Output Schema

| Column | Type | Source | Transformation | Evidence |
|--------|------|--------|---------------|----------|
| `branch_id` | integer | `branch_visits.branch_id` | Direct, GROUP BY key | [BRD BR-1, branch_visit_purpose_breakdown.json:29] |
| `branch_name` | text | `branches.branch_name` | Lookup via date-aligned JOIN on `branch_id` AND `as_of` | [BRD BR-3, branch_visit_purpose_breakdown.json:29] |
| `visit_purpose` | text | `branch_visits.visit_purpose` | Direct, GROUP BY key | [BRD BR-1, branch_visit_purpose_breakdown.json:29] |
| `as_of` | date (text in SQLite) | `branch_visits.as_of` | Direct, GROUP BY key | [BRD BR-1, branch_visit_purpose_breakdown.json:29] |
| `visit_count` | integer | `branch_visits` | `COUNT(*)` per group | [BRD BR-1, branch_visit_purpose_breakdown.json:29] |

**Column order matters.** The SELECT specifies: `branch_id`, `branch_name`, `visit_purpose`, `as_of`, `visit_count`. This must be preserved exactly for byte-identical output.

---

## 5. SQL Design

### V1 SQL (for reference)
```sql
WITH purpose_counts AS (
  SELECT bv.branch_id,
         bv.visit_purpose,
         bv.as_of,
         COUNT(*) AS visit_count,
         SUM(COUNT(*)) OVER (PARTITION BY bv.branch_id, bv.as_of) AS total_branch_visits
  FROM branch_visits bv
  GROUP BY bv.branch_id, bv.visit_purpose, bv.as_of
)
SELECT pc.branch_id,
       b.branch_name,
       pc.visit_purpose,
       pc.as_of,
       pc.visit_count
FROM purpose_counts pc
JOIN branches b ON pc.branch_id = b.branch_id AND pc.as_of = b.as_of
ORDER BY pc.as_of, pc.branch_id, pc.visit_purpose
```

### V2 SQL (simplified, AP8 eliminated)
```sql
SELECT bv.branch_id,
       b.branch_name,
       bv.visit_purpose,
       bv.as_of,
       COUNT(*) AS visit_count
FROM branch_visits bv
JOIN branches b ON bv.branch_id = b.branch_id AND bv.as_of = b.as_of
GROUP BY bv.branch_id, b.branch_name, bv.visit_purpose, bv.as_of
ORDER BY bv.as_of, bv.branch_id, bv.visit_purpose
```

### SQL Design Rationale

1. **CTE removed:** The V1 CTE `purpose_counts` exists solely to compute `total_branch_visits`, which is never selected in the outer query (BRD BR-2, AP8). V2 flattens the query to a single SELECT with GROUP BY + JOIN, which produces identical output rows.

2. **JOIN preserved:** The inner JOIN on `branch_id AND as_of` is a business requirement (BRD BR-3) ensuring date-aligned snapshots. V2 keeps this join condition.

3. **GROUP BY expanded:** V2 groups by `bv.branch_id, b.branch_name, bv.visit_purpose, bv.as_of`. Including `b.branch_name` in GROUP BY is necessary since it's selected and is functionally dependent on `bv.branch_id + bv.as_of` via the JOIN. SQLite requires all non-aggregate SELECT columns in GROUP BY.

4. **ORDER BY preserved:** `ORDER BY bv.as_of, bv.branch_id, bv.visit_purpose` matches V1 exactly (BRD BR-4).

5. **COUNT(*) preserved:** V1 uses `COUNT(*)` not `COUNT(visit_id)`. V2 keeps `COUNT(*)` for identical semantics (BRD BR-7).

6. **Inner JOIN semantics:** Branches with no visits for a given purpose/date will not appear (BRD BR-9). This is preserved by the inner JOIN and GROUP BY.

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "BranchVisitPurposeBreakdownV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "branch_visits",
      "schema": "datalake",
      "table": "branch_visits",
      "columns": ["branch_id", "visit_purpose"]
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
      "resultName": "purpose_breakdown",
      "sql": "SELECT bv.branch_id, b.branch_name, bv.visit_purpose, bv.as_of, COUNT(*) AS visit_count FROM branch_visits bv JOIN branches b ON bv.branch_id = b.branch_id AND bv.as_of = b.as_of GROUP BY bv.branch_id, b.branch_name, bv.visit_purpose, bv.as_of ORDER BY bv.as_of, bv.branch_id, bv.visit_purpose"
    },
    {
      "type": "CsvFileWriter",
      "source": "purpose_breakdown",
      "outputFile": "Output/double_secret_curated/branch_visit_purpose_breakdown.csv",
      "includeHeader": true,
      "trailerFormat": "END|{row_count}",
      "writeMode": "Append",
      "lineEnding": "CRLF"
    }
  ]
}
```

### Config Changes from V1

| Element | V1 | V2 | Reason |
|---------|----|----|--------|
| `jobName` | `BranchVisitPurposeBreakdown` | `BranchVisitPurposeBreakdownV2` | V2 naming convention |
| `segments` DataSourcing | Present | Removed | AP1: dead-end sourcing |
| `branch_visits.columns` | `["visit_id", "customer_id", "branch_id", "visit_purpose"]` | `["branch_id", "visit_purpose"]` | AP4: unused columns removed |
| `sql` | CTE with unused window function | Flat query, no CTE | AP8: unused CTE eliminated |
| `outputFile` | `Output/curated/...` | `Output/double_secret_curated/...` | V2 output path convention |

### Config Preserved from V1

| Element | Value | BRD Reference |
|---------|-------|---------------|
| `firstEffectiveDate` | `"2024-10-01"` | V1 config line 3 |
| `includeHeader` | `true` | BRD Writer Configuration |
| `trailerFormat` | `"END\|{row_count}"` | BRD BR-8 |
| `writeMode` | `"Append"` | BRD Write Mode Implications |
| `lineEnding` | `"CRLF"` | BRD Writer Configuration |

---

## 7. Writer Config

| Property | Value | Matches V1? | Evidence |
|----------|-------|-------------|----------|
| `type` | `CsvFileWriter` | Yes | [branch_visit_purpose_breakdown.json:32] |
| `source` | `purpose_breakdown` | Yes | [branch_visit_purpose_breakdown.json:33] |
| `outputFile` | `Output/double_secret_curated/branch_visit_purpose_breakdown.csv` | Path updated per V2 convention | BLUEPRINT requirement |
| `includeHeader` | `true` | Yes | [branch_visit_purpose_breakdown.json:35] |
| `trailerFormat` | `END\|{row_count}` | Yes | [branch_visit_purpose_breakdown.json:36] |
| `writeMode` | `Append` | Yes | [branch_visit_purpose_breakdown.json:37] |
| `lineEnding` | `CRLF` | Yes | [branch_visit_purpose_breakdown.json:38] |

**Append mode behavior:** The CsvFileWriter writes the header only on the first run (when the file does not yet exist). Each subsequent run appends data rows followed by a trailer line `END|N`. Over 92 days (2024-10-01 through 2024-12-31), the file will contain one header, then 92 blocks of data+trailer. This matches V1 behavior exactly.

Evidence from framework source: `CsvFileWriter.cs:47` -- `if (_includeHeader && !append)` ensures headers are not repeated on appends. `CsvFileWriter.cs:58-68` -- trailer is written after every run's data rows.

---

## 8. Proofmark Config Design

```yaml
comparison_target: "branch_visit_purpose_breakdown"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

### Rationale

- **reader: csv** -- V1 and V2 both output CSV files.
- **header_rows: 1** -- Both V1 and V2 include a header row (`includeHeader: true`). The header is written once at file creation.
- **trailer_rows: 0** -- The job uses Append mode. Each daily run appends data rows + a trailer (`END|{row_count}`). Over the full date range, there are multiple trailer lines embedded throughout the file, not just at the end. Per CONFIG_GUIDE.md Example 4, `trailer_rows: 0` is correct for Append-mode files with embedded trailers.
- **threshold: 100.0** -- No non-deterministic fields. Exact match required.
- **No excluded columns** -- No non-deterministic fields identified (BRD: "None identified").
- **No fuzzy columns** -- All output values are integers or text strings. No floating-point arithmetic. No precision concerns.

---

## 9. Traceability Matrix

| BRD Requirement | FSD Section | V2 Implementation |
|-----------------|-------------|-------------------|
| BR-1: Group by branch_id, visit_purpose, as_of | SQL Design | `GROUP BY bv.branch_id, b.branch_name, bv.visit_purpose, bv.as_of` |
| BR-2: total_branch_visits computed but not output | Anti-Pattern Analysis (AP8) | Removed entirely -- computed value was never output |
| BR-3: Date-aligned branch join (branch_id AND as_of) | SQL Design | `JOIN branches b ON bv.branch_id = b.branch_id AND bv.as_of = b.as_of` |
| BR-4: ORDER BY as_of, branch_id, visit_purpose | SQL Design | `ORDER BY bv.as_of, bv.branch_id, bv.visit_purpose` |
| BR-5: segments table sourced but unused | Anti-Pattern Analysis (AP1) | Removed from V2 config |
| BR-6: customer_id sourced but unused | Anti-Pattern Analysis (AP4) | Removed from V2 column list |
| BR-7: visit_id sourced but unused / COUNT(*) used | Anti-Pattern Analysis (AP4), SQL Design | Removed from V2 column list; COUNT(*) preserved |
| BR-8: Trailer format END\|{row_count} | Writer Config | `"trailerFormat": "END\|{row_count}"` |
| BR-9: No outer join (branches with no visits excluded) | SQL Design | Inner JOIN preserves this behavior |

| Anti-Pattern | Disposition | Evidence |
|-------------|-------------|----------|
| AP1 (Dead-end sourcing) | ELIMINATED | `segments` table removed from V2 config [BRD BR-5] |
| AP4 (Unused columns) | ELIMINATED | `visit_id`, `customer_id` removed from V2 config [BRD BR-6, BR-7] |
| AP8 (Unused CTE / window function) | ELIMINATED | CTE and `total_branch_visits` window function removed; flat query produces identical output [BRD BR-2] |

| Wrinkle | Disposition |
|---------|-------------|
| (none)  | No output-affecting wrinkles apply to this job |

---

## 10. External Module Design

**Not applicable.** This is a Tier 1 (Framework Only) job. No External module is needed.

---

## 11. Open Questions Inherited from BRD

| OQ | Question | FSD Resolution |
|----|----------|----------------|
| OQ-1 | Why is `total_branch_visits` computed but excluded from output? | Treated as dead code (AP8). Removed in V2. If it was intended for a future percentage calculation, that calculation was never implemented in V1 and is not present in V1 output. Output equivalence is unaffected. |
| OQ-2 | Why is the `segments` table sourced but never used? | Treated as dead-end sourcing (AP1). Removed in V2. The segments table has no relationship to visit purpose breakdowns and is not referenced in the SQL. |
