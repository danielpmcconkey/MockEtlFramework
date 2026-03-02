# MerchantCategoryDirectory -- Functional Specification Document

## 1. Overview

V2 replaces V1's `MerchantCategoryDirectory` job with a cleaned-up Tier 1 pipeline. The V1 job is already a framework-only chain (DataSourcing -> Transformation -> CsvFileWriter), so V2 preserves the same architecture. The only changes are: (a) removing the dead `cards` DataSourcing entry (AP1), (b) removing unused columns from that dead source (AP4, moot since the entire source is removed), and (c) redirecting the output path to `Output/double_secret_curated/`.

The job produces a simple reference-data export: all merchant category codes with their descriptions, risk levels, and snapshot date. No filtering, no aggregation, no joins -- pure pass-through SELECT from `merchant_categories`.

**Tier: 1 (Framework Only)** -- `DataSourcing -> Transformation (SQL) -> CsvFileWriter`

**Justification for Tier 1:** The V1 job is already Tier 1. The SQL is a trivial SELECT with no joins, no aggregation, no procedural logic. There is zero reason to escalate to Tier 2 or Tier 3.

---

## 2. V2 Module Chain

### Module 1: DataSourcing -- `merchant_categories`

| Property | Value |
|----------|-------|
| `type` | `DataSourcing` |
| `resultName` | `merchant_categories` |
| `schema` | `datalake` |
| `table` | `merchant_categories` |
| `columns` | `["mcc_code", "mcc_description", "risk_level"]` |

**Changes from V1:**
- **Identical to V1's first DataSourcing entry.** Same table, same columns, same resultName.
- Effective dates are injected at runtime by the executor via shared state keys `__minEffectiveDate` / `__maxEffectiveDate`. No hardcoded dates needed.
- The `as_of` column is automatically appended by the DataSourcing module when not explicitly listed (per `DataSourcing.cs:69-72`).

**V1 sourced but eliminated:**
- The `cards` DataSourcing entry is **removed entirely**. V1 sources `cards` (columns: `card_id`, `customer_id`, `card_type`) but the SQL Transformation never references the `cards` table. This is a textbook AP1 (dead-end sourcing) violation. Evidence: `merchant_category_directory.json:14-17` defines the `cards` DataSourcing; `merchant_category_directory.json:22` SQL is `SELECT mc.mcc_code, mc.mcc_description, mc.risk_level, mc.as_of FROM merchant_categories mc` -- no mention of `cards` anywhere. BRD BR-2.

### Module 2: Transformation -- `output`

| Property | Value |
|----------|-------|
| `type` | `Transformation` |
| `resultName` | `output` |
| `sql` | *(see Section 5)* |

Executes the pass-through SELECT. Produces the `output` DataFrame consumed by the writer.

### Module 3: CsvFileWriter

| Property | Value |
|----------|-------|
| `type` | `CsvFileWriter` |
| `source` | `output` |
| `outputFile` | `Output/double_secret_curated/merchant_category_directory.csv` |
| `includeHeader` | `true` |
| `writeMode` | `Append` |
| `lineEnding` | `LF` |

**Writer config matches V1 exactly** (same writer type, same header/writeMode/lineEnding, no trailer). Only the output path changes to `Output/double_secret_curated/`.

---

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles to Reproduce

| W Code | Applies? | V2 Handling |
|--------|----------|-------------|
| **W1** | **NO** | V1 does not skip Sundays. The `merchant_categories` table has data for all 7 days (BRD Edge Case 2). The job produces output for every date including weekends. |
| **W2** | **NO** | No weekend fallback logic. Data exists for all days. |
| **W9** | **YES -- but intentional, not wrong** | V1 uses Append mode. For a reference data directory that outputs the same 20 MCC codes every day, Append causes the file to accumulate redundant rows (20 rows per day x ~92 days = ~1840 data rows). This is V1's behavior and V2 must reproduce it exactly. The Append mode is part of the V1 specification, not a bug to fix. V2 preserves `writeMode: Append`. |
| **W12** | **NO** | V1 uses the framework's CsvFileWriter, which correctly suppresses headers on Append when the file already exists (`CsvFileWriter.cs:47`: `if (_includeHeader && !append)`). The header appears only once at the top of the file. This is correct behavior, not a W12 anti-pattern. W12 applies to External modules that manually re-emit headers. |

### Code-Quality Anti-Patterns to Eliminate

| AP Code | V1 Problem | V2 Resolution |
|---------|-----------|---------------|
| **AP1** | `cards` table sourced but never used in SQL Transformation | **Removed entirely from V2 config.** V1 sources `cards` with columns `card_id`, `customer_id`, `card_type` at `merchant_category_directory.json:14-17`, but the SQL at line 22 references only `merchant_categories mc`. BRD BR-2. |
| **AP4** | `cards` sourcing includes `card_id`, `customer_id`, `card_type` -- all unused | **Moot** -- the entire `cards` DataSourcing is removed (AP1 resolution). The `merchant_categories` sourcing columns (`mcc_code`, `mcc_description`, `risk_level`) are all used in the SQL SELECT. No column-level waste in the remaining source. |

### Anti-Patterns NOT Applicable

| AP Code | Why Not Applicable |
|---------|-------------------|
| AP2 | No cross-job duplication identified for this reference data export. |
| AP3 | V1 already uses framework modules (no External module). Nothing to replace. |
| AP5 | No NULL handling issues -- all merchant_categories fields are populated (BRD identifies no NULL edge cases). |
| AP6 | No row-by-row iteration -- V1 already uses SQL. |
| AP7 | No magic values -- the SQL is a plain SELECT with no thresholds or hardcoded constants. |
| AP8 | No complex SQL / unused CTEs -- the SQL is a single trivial SELECT. |
| AP9 | Job name accurately describes what it produces (a merchant category directory). |
| AP10 | V1 does not over-source dates. DataSourcing uses the framework's effective date injection, and the SQL has no additional WHERE clause on dates. |

---

## 4. Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| `mcc_code` | `merchant_categories.mcc_code` | Pass-through | `merchant_category_directory.json:22` SQL SELECT |
| `mcc_description` | `merchant_categories.mcc_description` | Pass-through | `merchant_category_directory.json:22` SQL SELECT |
| `risk_level` | `merchant_categories.risk_level` | Pass-through | `merchant_category_directory.json:22` SQL SELECT |
| `as_of` | `merchant_categories.as_of` | Pass-through | `merchant_category_directory.json:22` SQL SELECT `mc.as_of` |

**Column ordering:** Matches V1 exactly: `mcc_code`, `mcc_description`, `risk_level`, `as_of`. This order is defined by the SQL SELECT clause in both V1 and V2.

**No computed columns.** Every column is a direct pass-through from the source table. No aggregation, no joins, no derived values.

---

## 5. SQL Design

```sql
SELECT
    mc.mcc_code,
    mc.mcc_description,
    mc.risk_level,
    mc.as_of
FROM merchant_categories mc
```

### SQL Design Notes

1. **Identical to V1 SQL.** The V1 SQL at `merchant_category_directory.json:22` is `SELECT mc.mcc_code, mc.mcc_description, mc.risk_level, mc.as_of FROM merchant_categories mc`. V2 uses the exact same query. There is no business logic to simplify, no anti-pattern in the SQL itself.

2. **No ORDER BY clause.** V1 does not specify ORDER BY. The output row order depends on the order DataSourcing returns rows from PostgreSQL (which is `ORDER BY as_of` per `DataSourcing.cs:85`) and how SQLite processes the SELECT. To maintain output equivalence, V2 omits ORDER BY just like V1, allowing the natural row order to match.

3. **Table alias `mc`.** V1 uses the `mc` alias. V2 preserves it for consistency, though it's functionally irrelevant for a single-table query.

4. **`as_of` column.** The `as_of` column is automatically appended by DataSourcing (since it's not in the explicit `columns` list). The SQL accesses it via `mc.as_of`. This means each effective date's 20 MCC rows include the snapshot date. Over a multi-day auto-advance run with Append mode, the file accumulates rows for every date.

5. **No WHERE clause.** V1 applies no filtering beyond what DataSourcing already handles via effective date injection. All 20 MCC codes are output for each snapshot date. V2 matches this.

---

## 6. V2 Job Config

```json
{
  "jobName": "MerchantCategoryDirectoryV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "merchant_categories",
      "schema": "datalake",
      "table": "merchant_categories",
      "columns": ["mcc_code", "mcc_description", "risk_level"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT mc.mcc_code, mc.mcc_description, mc.risk_level, mc.as_of FROM merchant_categories mc"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/merchant_category_directory.csv",
      "includeHeader": true,
      "writeMode": "Append",
      "lineEnding": "LF"
    }
  ]
}
```

**Changes from V1 config:**
- `jobName`: `MerchantCategoryDirectory` -> `MerchantCategoryDirectoryV2`
- `outputFile`: `Output/curated/...` -> `Output/double_secret_curated/...`
- **Removed** the `cards` DataSourcing module entirely (AP1 elimination)
- All other config values are identical to V1

---

## 7. Writer Configuration

| Property | V1 Value | V2 Value | Match? |
|----------|---------|---------|--------|
| Writer type | `CsvFileWriter` | `CsvFileWriter` | Yes |
| `source` | `output` | `output` | Yes |
| `outputFile` | `Output/curated/merchant_category_directory.csv` | `Output/double_secret_curated/merchant_category_directory.csv` | Path change only |
| `includeHeader` | `true` | `true` | Yes |
| `trailerFormat` | Not configured | Not configured | Yes |
| `writeMode` | `Append` | `Append` | Yes |
| `lineEnding` | `LF` | `LF` | Yes |

### Append Mode Behavior

The Append mode has specific implications documented in the BRD (Write Mode Implications, Edge Case 1):

1. **Header written once.** On the first execution (when the output file does not exist), CsvFileWriter creates the file and writes the header row. On all subsequent executions, the file already exists, so `append = true` (line 42: `var append = _writeMode == WriteMode.Append && File.Exists(resolvedPath)`), and the header is suppressed (line 47: `if (_includeHeader && !append)`).

2. **Data accumulates.** Each effective date appends 20 MCC rows to the file. Over the full date range (2024-10-01 through 2024-12-31, 92 days), the file will contain 1 header row + 1840 data rows = 1841 total lines.

3. **No trailer.** The V1 config has no `trailerFormat`, so no trailer rows are written. This simplifies the Append behavior -- there are no embedded trailers to worry about.

---

## 8. Proofmark Config Design

Starting point: **zero exclusions, zero fuzzy overrides.**

```yaml
comparison_target: "merchant_category_directory"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

### Rationale

- **`reader: csv`**: Both V1 and V2 use CsvFileWriter.
- **`header_rows: 1`**: Both V1 and V2 have `includeHeader: true`. The CsvFileWriter writes the header only on file creation (first Append), so there is exactly one header row at the top of the file.
- **`trailer_rows: 0`**: No `trailerFormat` is configured in either V1 or V2. No trailer rows exist.
- **No excluded columns**: The BRD identifies zero non-deterministic fields. All output values (`mcc_code`, `mcc_description`, `risk_level`, `as_of`) are deterministic pass-throughs from source data.
- **No fuzzy columns**: All columns are either string pass-throughs (`mcc_code`, `mcc_description`, `risk_level`) or date pass-throughs (`as_of`). No arithmetic, no floating-point, no rounding. Strict comparison is fully appropriate.

### Potential Proofmark Adjustments

1. **Row ordering within a date.** V1's SQL has no ORDER BY. The 20 MCC rows within each `as_of` date will appear in whatever order SQLite returns them. If V1 and V2 happen to produce different row orderings within a date (due to DataSourcing insertion order differences or SQLite query plan differences), Proofmark may flag mismatches. Resolution: if this occurs, add `ORDER BY mc.mcc_code, mc.as_of` to the V2 SQL to stabilize order, and verify V1's actual row order to ensure they match. Since both V1 and V2 use the exact same SQL engine path (DataSourcing -> SQLite Transformation), the risk is very low.

2. **No other adjustments expected.** This is the simplest possible job -- a pass-through SELECT with string and date columns, no computation, no External module. If Proofmark fails, it's almost certainly a row-ordering issue or a file-level difference (e.g., trailing newline), not a data-level discrepancy.

---

## 9. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|-------------|----------------|----------|
| DataSourcing `merchant_categories` with `mcc_code`, `mcc_description`, `risk_level` | BR-1 (SELECT columns) | `merchant_category_directory.json:8-11` |
| Remove `cards` DataSourcing entirely (AP1) | BR-2 (dead cards sourcing) | `merchant_category_directory.json:14-17` vs `:22` -- SQL never references `cards` |
| SQL: pass-through SELECT, no filters | BR-1 (no filtering/aggregation) | `merchant_category_directory.json:22` |
| Output includes `as_of` column | BR-5 (as_of in output) | `merchant_category_directory.json:22` SQL selects `mc.as_of` |
| 20 MCC codes per date | BR-3 (row count) | DB query: 20 rows per as_of in merchant_categories |
| Risk level distribution (Low/Medium/High) | BR-4 (risk levels) | DB query on merchant_categories |
| CsvFileWriter with Append mode | BRD Writer Configuration, Write Mode Implications | `merchant_category_directory.json:29` `writeMode: Append` |
| Header written once (first run only) | BRD Edge Case 1 | `CsvFileWriter.cs:42,47` -- `append` flag suppresses header on subsequent runs |
| `includeHeader: true` | BRD Writer Configuration | `merchant_category_directory.json:28` |
| `lineEnding: LF` | BRD Writer Configuration | `merchant_category_directory.json:30` |
| No trailer | BRD Writer Configuration | `merchant_category_directory.json` -- no `trailerFormat` field |
| Weekend data included (no skip) | BRD Edge Case 2 | DB: merchant_categories has weekend as_of dates |
| File accumulates 20 rows/day over full date range | BRD Edge Case 3 | Append mode + 20 rows/day = cumulative file |
| Tier 1 selection | Module Hierarchy | V1 is already Tier 1; SQL is a trivial pass-through |
| `firstEffectiveDate: 2024-10-01` | V1 config | `merchant_category_directory.json:3` |

---

## 10. External Module Design

**Not applicable.** V2 is Tier 1 -- no External module needed. V1 does not use an External module either. This job is a pure framework pipeline in both V1 and V2.

---

## Appendix: Risk Register

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Row ordering mismatch between V1 and V2 within same as_of date | VERY LOW -- both use identical SQL over identical DataSourcing results in SQLite | LOW -- fixable by adding ORDER BY to stabilize row order | Monitor during Phase D comparison. If triggered, add `ORDER BY mc.mcc_code` to V2 SQL and verify V1's actual order. |
| Empty DataSourcing on hypothetical missing dates | VERY LOW -- merchant_categories has data for all calendar days including weekends (BRD Edge Case 2) | NONE -- this scenario doesn't occur in the 2024-10-01 to 2024-12-31 date range | No mitigation needed. Data confirmed present for all dates. |
| Append mode file size | N/A -- not a correctness risk | N/A | Documented as V1 intentional behavior (BRD Edge Case 3). V2 reproduces it exactly. |
