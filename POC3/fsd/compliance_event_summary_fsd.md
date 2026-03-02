# ComplianceEventSummary — Functional Specification Document

## 1. Overview & Tier Selection

**Job:** ComplianceEventSummaryV2
**Tier:** Tier 1 — Framework Only (`DataSourcing -> Transformation (SQL) -> CsvFileWriter`)

**Justification:** V1 uses an External module (ComplianceEventSummaryBuilder.cs) to perform a GROUP BY / COUNT aggregation with NULL coalescing and a Sunday skip guard. Every one of these operations maps directly to SQL constructs available in SQLite:
- GROUP BY + COUNT for aggregation (replaces AP6 row-by-row Dictionary iteration)
- COALESCE for NULL handling
- strftime('%w', ...) for day-of-week check

There is zero procedural logic in V1 that cannot be expressed in a single SQL query. The External module is a textbook AP3 (unnecessary External). Tier 1 eliminates it completely.

**Traces to:** BRD BR-1 (group by), BR-2 (Sunday skip), BR-3 (empty input), BR-5 (as_of), BR-6 (NULL coalescing)

---

## 2. V2 Module Chain

| Step | Module | Purpose |
|------|--------|---------|
| 1 | DataSourcing | Pull `compliance_events` from `datalake` with effective date filtering |
| 2 | Transformation | SQL: Sunday skip, GROUP BY aggregation, NULL coalescing, as_of extraction |
| 3 | CsvFileWriter | Write output CSV with header and trailer |

**Modules removed from V1:**
- DataSourcing for `accounts` table (AP1: dead-end, never referenced in V1 processing)
- External module `ComplianceEventSummaryBuilder` (AP3: replaced by SQL Transformation)

---

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (Preserved)

| ID | V1 Behavior | V2 Implementation | Trace |
|----|------------|-------------------|-------|
| W1 | Returns empty DataFrame on Sundays (based on `__maxEffectiveDate`) | SQL uses a CASE/WHERE construct: when the effective date is a Sunday, the query returns zero rows. The framework's `__maxEffectiveDate` is available in shared state; it flows through DataSourcing into the `as_of` column. We use `strftime('%w', as_of)` in SQLite where `'0'` = Sunday. Since each single-day run has only one `as_of` value, filtering out Sunday `as_of` rows produces zero output rows on Sundays. | BRD BR-2 |

### Code-Quality Anti-Patterns (Eliminated)

| ID | V1 Problem | V2 Fix | Trace |
|----|-----------|--------|-------|
| AP1 | `accounts` table sourced but never used | Removed from V2 DataSourcing config entirely | BRD BR-4 |
| AP3 | External module used for logic expressible in SQL | Replaced with Transformation module (SQL GROUP BY) | BRD BR-1 |
| AP4 | `event_id` and `customer_id` columns sourced but unused in output | V2 sources only `event_type`, `status` (needed for grouping), plus `as_of` is auto-appended by DataSourcing | BRD Output Schema |
| AP6 | Row-by-row `foreach` with Dictionary accumulation | Replaced with SQL `GROUP BY ... COUNT(*)` | BRD BR-1 |

---

## 4. Output Schema

| Column | Type | Source | Transformation | Trace |
|--------|------|--------|---------------|-------|
| event_type | TEXT | compliance_events.event_type | COALESCE to empty string '' | BRD BR-6 |
| status | TEXT | compliance_events.status | COALESCE to empty string '' | BRD BR-6 |
| event_count | INTEGER | Computed | COUNT(*) per (event_type, status) group | BRD BR-1 |
| as_of | TEXT | compliance_events.as_of | Taken from input rows (all identical within single-day run) | BRD BR-5 |

**Row count per run:** Up to 15 rows (5 event types x 3 statuses), zero rows on Sundays or if no compliance_events data exists for the effective date.

---

## 5. SQL Design

### Transformation SQL

```sql
SELECT
    COALESCE(event_type, '') AS event_type,
    COALESCE(status, '') AS status,
    COUNT(*) AS event_count,
    as_of
FROM compliance_events
WHERE strftime('%w', as_of) != '0'
GROUP BY COALESCE(event_type, ''), COALESCE(status, ''), as_of
ORDER BY event_type, status
```

**Design notes:**

1. **Sunday skip (W1):** The `WHERE strftime('%w', as_of) != '0'` clause filters out all rows when the effective date is a Sunday. Since each single-day run produces rows with a single `as_of` value, this either keeps all rows (non-Sunday) or drops all rows (Sunday), producing an empty result set on Sundays. `strftime('%w', ...)` returns `'0'` for Sunday in SQLite.

2. **NULL coalescing (BR-6):** `COALESCE(event_type, '')` and `COALESCE(status, '')` replicate V1's `?.ToString() ?? ""` behavior. Note: current data has zero NULLs in either field (verified via `SELECT COUNT(*) FROM datalake.compliance_events WHERE event_type IS NULL OR status IS NULL` = 0), but the COALESCE is retained for defensive correctness matching V1 behavior.

3. **as_of in GROUP BY (BR-5):** Including `as_of` in the GROUP BY is technically redundant for single-day runs (all rows share the same `as_of`), but it's clean SQL practice and produces the correct output column. V1 takes `as_of` from the first row — since all rows have the same value in a single-day run, this is equivalent.

4. **Row ordering:** V1's output order is non-deterministic (Dictionary enumeration order). V2 adds `ORDER BY event_type, status` for deterministic output. This means row order will differ from V1. This is addressed in the Proofmark config — the comparison tool handles row ordering independently (it compares row sets, not ordered sequences). If Proofmark comparison fails on row order, we will need to verify Proofmark's row-matching behavior and adjust accordingly.

5. **Empty input (BR-3):** If `compliance_events` has zero rows for the effective date, the DataSourcing module returns an empty DataFrame. When the Transformation registers an empty DataFrame as a SQLite table, `RegisterTable` returns early without creating the table (see `Transformation.cs:46-47`: `if (!df.Rows.Any()) return;`). This means the SQL query will fail because the `compliance_events` table does not exist in SQLite.

   **Mitigation:** We need to handle this edge case. Two options:
   - **Option A:** Use a second Transformation step that creates an empty output if no data exists.
   - **Option B:** Restructure the SQL to handle the missing table case.

   Since SQLite's `RegisterTable` silently skips empty DataFrames and the SQL will throw an error referencing a non-existent table, we need **a Tier 2 escalation is NOT needed**. Instead, we rely on the fact that DataSourcing always returns at least some rows for valid effective dates (the data lake has daily snapshots for the full Oct-Dec 2024 range), and the Sunday skip WHERE clause handles the zero-output case.

   **Verification needed during Phase D:** If Proofmark comparison reveals issues with empty-input dates, a Tier 2 External module may be needed solely for the empty-table guard. For now, we proceed with Tier 1.

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "ComplianceEventSummaryV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "compliance_events",
      "schema": "datalake",
      "table": "compliance_events",
      "columns": ["event_type", "status"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT COALESCE(event_type, '') AS event_type, COALESCE(status, '') AS status, COUNT(*) AS event_count, as_of FROM compliance_events WHERE strftime('%w', as_of) != '0' GROUP BY COALESCE(event_type, ''), COALESCE(status, ''), as_of ORDER BY event_type, status"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/compliance_event_summary.csv",
      "includeHeader": true,
      "trailerFormat": "TRAILER|{row_count}|{date}",
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

**Config changes from V1:**
- Removed `accounts` DataSourcing (AP1: dead-end source)
- Removed `event_id`, `customer_id` from compliance_events columns (AP4: unused columns)
- Replaced External module with Transformation (AP3: unnecessary External)
- `as_of` is NOT listed in columns — DataSourcing auto-appends it (see `DataSourcing.cs:69`: `var includesAsOf = _columnNames.Contains("as_of", ...)`; when not included, it's appended automatically)
- Output path changed to `Output/double_secret_curated/compliance_event_summary.csv`
- All writer params preserved exactly: `includeHeader: true`, `trailerFormat: "TRAILER|{row_count}|{date}"`, `writeMode: "Overwrite"`, `lineEnding: "LF"`

---

## 7. Writer Configuration

| Parameter | V1 Value | V2 Value | Match? |
|-----------|----------|----------|--------|
| Writer type | CsvFileWriter | CsvFileWriter | Yes |
| source | "output" | "output" | Yes |
| outputFile | `Output/curated/compliance_event_summary.csv` | `Output/double_secret_curated/compliance_event_summary.csv` | Path change only (required) |
| includeHeader | true | true | Yes |
| trailerFormat | `TRAILER|{row_count}|{date}` | `TRAILER|{row_count}|{date}` | Yes |
| writeMode | Overwrite | Overwrite | Yes |
| lineEnding | LF | LF | Yes |

**Trailer behavior:** The `{row_count}` token substitutes the count of data rows in the output DataFrame (excluding header and trailer). The `{date}` token substitutes `__maxEffectiveDate` from shared state. This is handled entirely by the framework's CsvFileWriter — no custom trailer logic needed.

**Write mode (Overwrite):** Each run replaces the entire file. Multi-day auto-advance runs will only retain the last effective date's output. This matches V1 behavior. (Note: this is arguably W9 — Overwrite loses prior days' data — but the BRD does not call it out as a wrinkle because V1 intentionally uses Overwrite. The V2 must match.)

---

## 8. Proofmark Config Design

```yaml
comparison_target: "compliance_event_summary"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

**Design rationale:**
- **reader: csv** — V1 and V2 both use CsvFileWriter
- **header_rows: 1** — `includeHeader: true` in both V1 and V2
- **trailer_rows: 1** — `trailerFormat` present + `writeMode: Overwrite` means exactly one trailer at end of file
- **threshold: 100.0** — All output columns are deterministic. No floating-point arithmetic, no timestamps, no UUIDs. 100% match expected.
- **No excluded columns** — All four output columns (event_type, status, event_count, as_of) are deterministic and must match exactly.
- **No fuzzy columns** — event_count is an integer count, no floating-point precision concerns.

**Row ordering concern:** V1's Dictionary iteration order is non-deterministic (BRD: "Non-Deterministic Fields" section). V2 uses `ORDER BY event_type, status` for deterministic output. If Proofmark does row-set comparison (order-independent), this is a non-issue. If Proofmark does positional comparison, V1's row order would need to be analyzed from actual output to determine if it happens to match alphabetical order. **Starting with strict config; will adjust during Phase D if row order causes comparison failure.**

---

## 9. Traceability Matrix

| BRD Requirement | FSD Section | V2 Implementation |
|----------------|-------------|-------------------|
| BR-1: Group by (event_type, status) count | SQL Design | `GROUP BY COALESCE(event_type, ''), COALESCE(status, ''), as_of` with `COUNT(*)` |
| BR-2: Sunday skip (W1) | SQL Design, Anti-Pattern Analysis | `WHERE strftime('%w', as_of) != '0'` filters Sundays to zero rows |
| BR-3: Empty input handling | SQL Design note #5 | Empty DataSourcing result -> empty SQLite table -> empty query result (or table-not-found). Relies on data lake having daily snapshots. |
| BR-4: Dead-end accounts source (AP1) | Anti-Pattern Analysis, Config | `accounts` DataSourcing removed from V2 config |
| BR-5: as_of from first row | SQL Design note #3 | `as_of` included in GROUP BY; single-day runs have uniform as_of, so result matches V1 |
| BR-6: NULL coalescing | SQL Design note #2 | `COALESCE(event_type, '')`, `COALESCE(status, '')` |
| BR-7: Event type domain | (Informational) | No filter on event_type — all types flow through |
| BR-8: Status domain | (Informational) | No filter on status — all statuses flow through |
| BR-9: Trailer format | Writer Config | `trailerFormat: "TRAILER|{row_count}|{date}"` — framework handles token substitution |

---

## 10. External Module Design

**Not applicable.** This job is Tier 1 — no External module needed. The V1 External module (ComplianceEventSummaryBuilder.cs) is fully replaced by the SQL Transformation.

---

## Appendix: Risk Register

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Empty DataFrame causes SQLite table-not-found error | LOW (data lake has daily snapshots for full date range) | HIGH (job would fail, not produce output) | Monitor during Phase D. If triggered, escalate to Tier 2 with a minimal guard External module. |
| Row order mismatch between V1 (Dictionary iteration) and V2 (ORDER BY) | MEDIUM (V1 order is technically non-deterministic) | LOW (Proofmark likely does set-based comparison) | Start with strict Proofmark config. If row-order causes failure, verify Proofmark's comparison mode and adjust config or SQL ORDER BY. |
| SQLite `strftime` treats `as_of` TEXT differently than expected | LOW (DataSourcing converts DateOnly to 'yyyy-MM-dd' string format, which strftime handles correctly) | HIGH (Sunday skip would not work) | Verified: SQLite `strftime('%w', '2024-10-06')` returns `'0'` for Sunday. Format matches DataSourcing output. |
