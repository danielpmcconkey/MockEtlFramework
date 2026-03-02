# SecuritiesDirectory -- Functional Specification Document

## 1. Job Summary

The SecuritiesDirectory job produces a CSV listing of all securities and their attributes (ticker, name, type, sector, exchange) for the effective date range, ordered by `security_id`. It is a straightforward pass-through from `datalake.securities` with no filtering, aggregation, or joining. V1 sources a `holdings` table that is never referenced in the SQL -- V2 eliminates this dead-end sourcing.

---

## 2. V2 Module Chain

**Tier: 1 -- Framework Only (DEFAULT)**

| Step | Module | Purpose |
|------|--------|---------|
| 1 | DataSourcing | Load `datalake.securities` for the effective date range |
| 2 | Transformation | SELECT all columns, ORDER BY `security_id` |
| 3 | CsvFileWriter | Write output to CSV |

**Justification:** The entire job is a SELECT with an ORDER BY -- pure SQL. No procedural logic, no complex joins, no operations requiring C#. Tier 1 is the obvious and correct choice.

**V1 uses DataSourcing + Transformation + CsvFileWriter (Tier 1) already.** The V1 module chain is structurally sound. The only change is removing the dead-end `holdings` DataSourcing module (AP1/AP4).

---

## 3. DataSourcing Config

### Source: `securities`

| Property | Value |
|----------|-------|
| resultName | `securities` |
| schema | `datalake` |
| table | `securities` |
| columns | `security_id`, `ticker`, `security_name`, `security_type`, `sector`, `exchange` |

**Effective date handling:** No explicit `minEffectiveDate`/`maxEffectiveDate` in the job config. The executor injects these into shared state at runtime via `__minEffectiveDate` and `__maxEffectiveDate`. DataSourcing reads them automatically to filter the `as_of` column. This matches V1 behavior. [Evidence: securities_directory.json -- no date fields in DataSourcing modules; Architecture.md -- DataSourcing reads shared state keys]

**Note:** The `as_of` column is automatically appended by DataSourcing even though it is not listed in the `columns` array. The Transformation SQL references `s.as_of`, which depends on this framework behavior. [Evidence: Architecture.md -- "Returns a single flat DataFrame with as_of appended as a column"]

### Removed: `holdings` (V1 dead-end source)

V1 sources `datalake.holdings` with columns `holding_id`, `investment_id`, `security_id`, `customer_id`, `quantity`, `current_value`. This table is registered as a SQLite table during Transformation but is never referenced in the SQL query. V2 removes this entirely. [Evidence: BRD BR-3, securities_directory.json:13-18 vs :22]

---

## 4. Transformation SQL

```sql
SELECT s.security_id, s.ticker, s.security_name, s.security_type, s.sector, s.exchange, s.as_of
FROM securities s
ORDER BY s.security_id
```

**Result name:** `securities_dir`

This is identical to V1's SQL. [Evidence: securities_directory.json:21-22]

The SQL selects all sourced columns plus `as_of` from the `securities` table alias, ordered by `security_id` ascending (default direction). No WHERE clause -- all rows within the effective date range are included.

**Column mapping:**

| Output Column | Source | Transformation |
|---------------|--------|---------------|
| security_id | securities.security_id | Pass-through |
| ticker | securities.ticker | Pass-through |
| security_name | securities.security_name | Pass-through |
| security_type | securities.security_type | Pass-through |
| sector | securities.sector | Pass-through |
| exchange | securities.exchange | Pass-through |
| as_of | securities.as_of | Pass-through |

---

## 5. Writer Config

| Property | Value | Evidence |
|----------|-------|----------|
| type | CsvFileWriter | securities_directory.json:25 |
| source | `securities_dir` | securities_directory.json:26 |
| outputFile | `Output/double_secret_curated/securities_directory.csv` | V2 output path per BLUEPRINT convention |
| includeHeader | `true` | securities_directory.json:28 |
| writeMode | `Overwrite` | securities_directory.json:29 |
| lineEnding | `LF` | securities_directory.json:30 |
| trailerFormat | *(not specified -- no trailer)* | securities_directory.json:24-31 -- field absent |

**Write mode implications:** Overwrite mode means each run replaces the entire file. For multi-day auto-advance runs, only the final effective date's output persists. However, since the DataSourcing pulls the full effective date range and the SQL has no WHERE clause, the final run's output will contain rows for ALL dates in the range (one row per security per `as_of` date). [Evidence: BRD -- Write Mode Implications section]

---

## 6. Wrinkle Replication

No W-codes apply to this job.

The BRD identifies no output-affecting wrinkles. The job is a clean pass-through with no:
- Weekend logic (W1, W2)
- Boundary summaries (W3a/b/c)
- Arithmetic (W4, W5, W6)
- Trailer (W7, W8)
- Write mode mismatch (W9 -- Overwrite is appropriate for a full-refresh directory listing)
- Absurd partitioning (W10 -- CSV, not Parquet)
- Header-every-append (W12 -- uses Overwrite, not Append)

---

## 7. Anti-Pattern Elimination

### AP1: Dead-End Sourcing -- ELIMINATED

**V1 problem:** The `holdings` table is sourced (DataSourcing module at config lines 13-18) but never referenced in the Transformation SQL. The SQL query only touches `securities s`. This wastes database I/O and memory loading an entire table for nothing.

**V2 fix:** The `holdings` DataSourcing module is removed entirely from the V2 job config. Only `securities` is sourced.

[Evidence: BRD BR-3, BRD Edge Case #1, securities_directory.json:13-18 sources holdings; :22 SQL only references `securities s`]

### AP4: Unused Columns -- ELIMINATED (via AP1)

**V1 problem:** All six columns sourced from `holdings` (`holding_id`, `investment_id`, `security_id`, `customer_id`, `quantity`, `current_value`) are unused since the entire `holdings` table is unused.

**V2 fix:** Removing the `holdings` DataSourcing module eliminates all unused columns. The `securities` DataSourcing module sources exactly the columns used in the SQL: `security_id`, `ticker`, `security_name`, `security_type`, `sector`, `exchange` (plus `as_of` appended automatically by the framework).

[Evidence: BRD BR-3, securities_directory.json:14-17]

### AP3: Unnecessary External Module -- NOT APPLICABLE

V1 already uses framework modules (DataSourcing + Transformation + CsvFileWriter). No External module is involved.

### AP9: Misleading Names -- NOT APPLICABLE

The job name "SecuritiesDirectory" accurately describes what it produces: a directory listing of securities.

### AP10: Over-Sourcing Dates -- NOT APPLICABLE

V1 does not use explicit date filters in the job config or SQL. Effective dates are injected by the executor and applied automatically by DataSourcing. This is the correct pattern.

---

## 8. Proofmark Config

```yaml
comparison_target: "securities_directory"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

**Rationale:**
- `reader: csv` -- Output is CSV via CsvFileWriter. [Evidence: securities_directory.json:25]
- `header_rows: 1` -- V1 config has `includeHeader: true`. [Evidence: securities_directory.json:28]
- `trailer_rows: 0` -- No `trailerFormat` in V1 config. [Evidence: securities_directory.json:24-31]
- `threshold: 100.0` -- All columns are deterministic pass-throughs from source data. No non-deterministic fields identified in BRD. Zero exclusions and zero fuzzy overrides are appropriate.
- No `columns.excluded` or `columns.fuzzy` entries needed -- every column is a direct pass-through with no arithmetic, no timestamps, and no non-deterministic computation.

---

## 9. Open Questions

1. **Why was `holdings` sourced in V1?** The BRD notes this as an open question. It may be a leftover from a design that intended to join holdings with securities (e.g., to show which securities are held, or to enrich the directory with holding counts). Regardless, since V1's SQL never references it, removing it from V2 does not affect output. [Evidence: BRD Open Question #1]

2. **Multi-day Overwrite semantics:** With Overwrite mode and no date filtering in SQL, a multi-day auto-advance run will produce a file containing rows for all dates in the range, but only the last day's execution output will persist (since each subsequent day overwrites the file). The final file will contain the full range because the last run's DataSourcing pulls `__minEffectiveDate` through `__maxEffectiveDate`. This is V1's behavior and V2 replicates it exactly. [Evidence: BRD Edge Case #2, BRD Write Mode Implications]

---

## Traceability Matrix

| BRD Requirement | FSD Section | Design Decision |
|-----------------|-------------|-----------------|
| BR-1: SQL selects all securities columns + as_of, ORDER BY security_id | Section 4 (Transformation SQL) | Identical SQL reproduced |
| BR-2: No WHERE clause -- all rows included | Section 4 (Transformation SQL) | No filtering applied |
| BR-3: Holdings sourced but unused | Section 3 (Removed: holdings), Section 7 (AP1) | Holdings DataSourcing removed |
| BR-4: Result stored as `securities_dir` | Section 4 (resultName), Section 5 (source) | resultName = `securities_dir`, writer source = `securities_dir` |
| BR-5: Effective dates injected by executor | Section 3 (Effective date handling) | No explicit date config; framework injection |
| BR-6: `as_of` column included in output | Section 4 (Column mapping) | `s.as_of` in SELECT |
| BR-7: ORDER BY security_id ASC | Section 4 (Transformation SQL) | `ORDER BY s.security_id` |
| Edge Case #1: Holdings sourced but unused | Section 7 (AP1) | Eliminated |
| Edge Case #5: RFC 4180 quoting | Section 5 (Writer Config) | CsvFileWriter handles RFC 4180 quoting per framework |
