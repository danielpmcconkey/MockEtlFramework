# ComplianceResolutionTimeV2 — Functional Specification Document

## 1. Overview & Tier Selection

**Job:** ComplianceResolutionTimeV2
**Tier:** 1 (Framework Only) — `DataSourcing -> Transformation (SQL) -> CsvFileWriter`

This job computes resolution time statistics for cleared compliance events: resolved count, total days to resolve, and average resolution days, grouped by event type and as_of date. The output is a CSV file with a trailer row.

**Tier Justification:** All business logic (filtering, date arithmetic, aggregation, cross join) is expressible in SQLite SQL. No procedural logic is required. The V1 implementation is already Tier 1 (DataSourcing -> Transformation -> CsvFileWriter), so V2 stays at Tier 1 with cleaned-up SQL.

## 2. V2 Module Chain

```
DataSourcing (compliance_events)
    -> Transformation (resolution_stats)
    -> CsvFileWriter (compliance_resolution_time_v2.csv)
```

### Module 1: DataSourcing — `compliance_events`
- **Schema:** datalake
- **Table:** compliance_events
- **Columns:** event_id, customer_id, event_type, event_date, status, review_date
- **Effective dates:** Injected by executor via shared state (`__minEffectiveDate`, `__maxEffectiveDate`)
- **No additionalFilter** — filtering is handled in the SQL Transformation

### Module 2: Transformation — `resolution_stats`
- **SQL:** See Section 5 for full SQL design
- **Result name:** resolution_stats

### Module 3: CsvFileWriter
- **Source:** resolution_stats
- **Output path:** `Output/double_secret_curated/compliance_resolution_time.csv`
- **includeHeader:** true
- **trailerFormat:** `TRAILER|{row_count}|{date}`
- **writeMode:** Overwrite
- **lineEnding:** LF

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (W-codes) — Must Reproduce

| W-code | Applies? | Analysis |
|--------|----------|----------|
| W4 (Integer division) | **YES** | BR-3: `avg_resolution_days` uses integer division (`SUM/COUNT` where both operands are cast to INTEGER). V2 must reproduce this truncation behavior. Since SQLite's integer division naturally truncates, and the V1 SQL is running in the same SQLite Transformation module, using the same CAST pattern in V2 SQL will produce identical results. |
| W9 (Wrong writeMode) | **YES** | BR-5/BRD Write Mode section: Overwrite mode means multi-day runs only retain the last effective date's output. This is V1's behavior and V2 must match it. |

No other W-codes apply to this job:
- W1/W2/W3a-c: No weekend/boundary logic present.
- W5: No rounding — integer truncation only.
- W6: No monetary accumulation with doubles.
- W7/W8: Trailer is handled by the framework's CsvFileWriter, not a manual External module. The `{date}` token is resolved from `__maxEffectiveDate` by the framework — not hardcoded.
- W10: Not a Parquet job.
- W12: Not Append mode.

### Code-Quality Anti-Patterns (AP-codes) — Must Eliminate

| AP-code | Applies? | V1 Problem | V2 Resolution |
|---------|----------|------------|---------------|
| AP1 (Dead-end sourcing) | **NO** | The single sourced table (`compliance_events`) is used in the Transformation SQL. No unused sources. |
| AP4 (Unused columns) | **PARTIAL** | V1 sources `customer_id` but never references it in the Transformation SQL. V2 will remove `customer_id` from the DataSourcing columns list. The `event_id` column is also unused in the SQL output, but it is part of the source table rows that participate in the cross join and COUNT(*), so removing it would not change behavior. However, since it's not referenced in the SQL, V2 will remove it too for cleanliness. |
| AP8 (Complex SQL / unused CTEs) | **YES** | V1's CTE includes `ROW_NUMBER() OVER (PARTITION BY event_type ORDER BY event_date) AS rn` (BR-7), which is computed but never referenced in the outer query. V2 will remove this unused window function. |
| AP3 (Unnecessary External) | **NO** | V1 already uses framework modules, not an External module. |
| AP6 (Row-by-row) | **NO** | No row-by-row iteration — all logic is SQL. |
| AP7 (Magic values) | **NO** | No hardcoded thresholds or magic strings. The status value `'Cleared'` is a business filter, not a magic value. |
| AP10 (Over-sourcing dates) | **NO** | DataSourcing uses the framework's effective date injection, not a full-table pull followed by SQL filtering. |

### Cross Join Preservation (BR-5, BR-6)

The V1 SQL performs a cross join between the `resolved` CTE and the full `compliance_events` table via `JOIN compliance_events ON 1=1`. This inflates `resolved_count` and `total_days` by a factor of M (number of rows in the compliance_events DataFrame). The `avg_resolution_days` integer division cancels this inflation mathematically: `(sum*M) / (N*M) = sum/N`.

**V2 must reproduce this cross join exactly.** The inflated `resolved_count` and `total_days` values are part of V1's output. Removing the cross join would produce different (non-inflated) counts, breaking output equivalence.

V2 SQL will include the cross join with a clear comment explaining:
1. What it does (Cartesian product)
2. Why it exists (V1 behavior — likely a bug, but avg_resolution_days is mathematically correct)
3. That resolved_count and total_days are inflated by factor M

## 4. Output Schema

| Column | Type | Source | Transformation |
|--------|------|--------|---------------|
| event_type | TEXT | compliance_events.event_type | Grouped key from resolved CTE |
| resolved_count | INTEGER | Computed | COUNT(*) over Cartesian product (inflated by cross join — V1 behavior, see BR-5/BR-6) |
| total_days | INTEGER | Computed | SUM(days_to_resolve) over Cartesian product (inflated by cross join — V1 behavior, see BR-5/BR-6) |
| avg_resolution_days | INTEGER | Computed | Integer division: CAST(SUM(days_to_resolve) AS INTEGER) / CAST(COUNT(*) AS INTEGER). Truncated, not rounded. Cross join inflation cancels. |
| as_of | TEXT | compliance_events.as_of | Grouped key from cross-joined compliance_events table |

## 5. SQL Design

### V1 SQL (for reference)
```sql
WITH resolved AS (
    SELECT event_type, event_date, review_date,
           CAST(julianday(review_date) - julianday(event_date) AS INTEGER) AS days_to_resolve,
           ROW_NUMBER() OVER (PARTITION BY event_type ORDER BY event_date) AS rn
    FROM compliance_events
    WHERE status = 'Cleared' AND review_date IS NOT NULL
)
SELECT resolved.event_type,
       COUNT(*) AS resolved_count,
       SUM(days_to_resolve) AS total_days,
       CAST(SUM(days_to_resolve) AS INTEGER) / CAST(COUNT(*) AS INTEGER) AS avg_resolution_days,
       compliance_events.as_of
FROM resolved
JOIN compliance_events ON 1=1
GROUP BY resolved.event_type, compliance_events.as_of
```

### V2 SQL (cleaned)
```sql
-- V2: Removed unused ROW_NUMBER() window function (AP8).
-- Cross join on 1=1 is preserved for output equivalence (BR-5, BR-6):
--   resolved_count and total_days are inflated by factor M (total rows in compliance_events).
--   avg_resolution_days is unaffected because inflation cancels in integer division.
WITH resolved AS (
    SELECT event_type,
           CAST(julianday(review_date) - julianday(event_date) AS INTEGER) AS days_to_resolve
    FROM compliance_events
    WHERE status = 'Cleared'
      AND review_date IS NOT NULL
)
SELECT resolved.event_type,
       COUNT(*) AS resolved_count,
       SUM(days_to_resolve) AS total_days,
       CAST(SUM(days_to_resolve) AS INTEGER) / CAST(COUNT(*) AS INTEGER) AS avg_resolution_days,
       compliance_events.as_of
FROM resolved
JOIN compliance_events ON 1=1
GROUP BY resolved.event_type, compliance_events.as_of
```

### Changes from V1 to V2 SQL:
1. **Removed:** `ROW_NUMBER() OVER (PARTITION BY event_type ORDER BY event_date) AS rn` — never used in outer query (AP8, BR-7)
2. **Removed:** `event_date` and `review_date` from CTE SELECT list — only `days_to_resolve` is used by the outer query; `event_type` is still needed for the GROUP BY
3. **Preserved:** Cross join `JOIN compliance_events ON 1=1` — required for output equivalence (BR-5, BR-6)
4. **Preserved:** Integer division via CAST on both SUM and COUNT — required for output equivalence (BR-3, W4)
5. **Preserved:** `julianday()` date arithmetic with INTEGER cast truncation (BR-2)

## 6. V2 Job Config JSON

```json
{
  "jobName": "ComplianceResolutionTimeV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "compliance_events",
      "schema": "datalake",
      "table": "compliance_events",
      "columns": ["event_type", "event_date", "status", "review_date"]
    },
    {
      "type": "Transformation",
      "resultName": "resolution_stats",
      "sql": "WITH resolved AS (SELECT event_type, CAST(julianday(review_date) - julianday(event_date) AS INTEGER) AS days_to_resolve FROM compliance_events WHERE status = 'Cleared' AND review_date IS NOT NULL) SELECT resolved.event_type, COUNT(*) AS resolved_count, SUM(days_to_resolve) AS total_days, CAST(SUM(days_to_resolve) AS INTEGER) / CAST(COUNT(*) AS INTEGER) AS avg_resolution_days, compliance_events.as_of FROM resolved JOIN compliance_events ON 1=1 GROUP BY resolved.event_type, compliance_events.as_of"
    },
    {
      "type": "CsvFileWriter",
      "source": "resolution_stats",
      "outputFile": "Output/double_secret_curated/compliance_resolution_time.csv",
      "includeHeader": true,
      "trailerFormat": "TRAILER|{row_count}|{date}",
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

### Changes from V1 config:
- **jobName:** `ComplianceResolutionTime` -> `ComplianceResolutionTimeV2`
- **DataSourcing columns:** Removed `event_id` and `customer_id` (AP4 — unused in Transformation SQL)
- **Transformation SQL:** Cleaned up (see Section 5 for details)
- **outputFile:** `Output/curated/compliance_resolution_time.csv` -> `Output/double_secret_curated/compliance_resolution_time.csv`

### Preserved from V1 config:
- **firstEffectiveDate:** `2024-10-01`
- **Writer type:** CsvFileWriter (matching V1)
- **includeHeader:** true
- **trailerFormat:** `TRAILER|{row_count}|{date}`
- **writeMode:** Overwrite (W9 — see anti-pattern analysis)
- **lineEnding:** LF

## 7. Writer Config

| Parameter | V1 Value | V2 Value | Match? |
|-----------|----------|----------|--------|
| Writer type | CsvFileWriter | CsvFileWriter | YES |
| source | resolution_stats | resolution_stats | YES |
| outputFile | Output/curated/compliance_resolution_time.csv | Output/double_secret_curated/compliance_resolution_time.csv | Path changed per V2 convention |
| includeHeader | true | true | YES |
| trailerFormat | TRAILER\|{row_count}\|{date} | TRAILER\|{row_count}\|{date} | YES |
| writeMode | Overwrite | Overwrite | YES |
| lineEnding | LF | LF | YES |

<!-- V1 uses Overwrite — multi-day runs only retain last effective date's output (W9). -->

## 8. Proofmark Config Design

### Reader & CSV Settings
- **Reader:** csv (matching CsvFileWriter output)
- **header_rows:** 1 (includeHeader: true)
- **trailer_rows:** 1 (trailerFormat present + writeMode Overwrite = single trailer at file end)

### Column Overrides
- **Excluded columns:** None
- **Fuzzy columns:** None

**Justification for zero overrides:** All output columns are deterministic. There are no timestamps, UUIDs, or floating-point accumulations. The `{date}` trailer token is resolved from `__maxEffectiveDate` which is the same for both V1 and V2 runs. All computations use integer arithmetic (CAST to INTEGER), eliminating floating-point precision concerns.

### Proofmark Config YAML
```yaml
comparison_target: "compliance_resolution_time"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

## 9. Traceability Matrix

| BRD Requirement | FSD Section | V2 Implementation |
|-----------------|-------------|-------------------|
| BR-1: Cleared + non-null review_date filter | Section 5 (SQL WHERE clause) | CTE WHERE: `status = 'Cleared' AND review_date IS NOT NULL` |
| BR-2: julianday-based days_to_resolve, INTEGER cast | Section 5 (CTE) | `CAST(julianday(review_date) - julianday(event_date) AS INTEGER)` |
| BR-3: Integer division for avg_resolution_days | Section 5 (outer SELECT), Section 3 (W4) | `CAST(SUM(...) AS INTEGER) / CAST(COUNT(*) AS INTEGER)` — integer division preserved for output equivalence |
| BR-4: Group by event_type, as_of | Section 5 (GROUP BY) | `GROUP BY resolved.event_type, compliance_events.as_of` |
| BR-5: Cross join (1=1) | Section 3 (Cross Join Preservation), Section 5 | `JOIN compliance_events ON 1=1` preserved with comment explaining Cartesian product |
| BR-6: Inflation of counts, avg correctness | Section 3 (Cross Join Preservation), Section 4 | Inflated resolved_count and total_days are part of V1 output; avg cancels out |
| BR-7: Unused ROW_NUMBER | Section 3 (AP8), Section 5 | **Eliminated** — removed unused `ROW_NUMBER()` window function from CTE |
| BRD Output Type: CsvFileWriter | Section 2, Section 7 | CsvFileWriter module with matching config |
| BRD Writer: includeHeader true | Section 7 | includeHeader: true |
| BRD Writer: trailerFormat | Section 7 | `TRAILER|{row_count}|{date}` |
| BRD Writer: writeMode Overwrite | Section 7, Section 3 (W9) | writeMode: Overwrite |
| BRD Writer: lineEnding LF | Section 7 | lineEnding: LF |
| BRD Non-deterministic: None | Section 8 | Zero Proofmark exclusions/fuzzy overrides |

## 10. External Module Design

**Not applicable.** This job is Tier 1 (Framework Only). No External module is needed. All business logic is expressed in SQL within the Transformation module.
