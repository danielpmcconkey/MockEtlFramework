# FundAllocationBreakdown — Functional Specification Document

## 1. Overview & Tier Selection

**Job:** FundAllocationBreakdownV2
**Tier:** Tier 1 — Framework Only (`DataSourcing → Transformation (SQL) → CsvFileWriter`)

**Summary:** Produces a CSV file containing holdings aggregated by security type, showing count, total value, and average value per type. The V1 implementation uses an External module (`FundAllocationWriter`) that performs grouping/aggregation in C# via row-by-row iteration and writes CSV directly to disk, bypassing the framework's CsvFileWriter entirely. All of this logic is expressible in SQL, and the framework's CsvFileWriter can produce byte-identical output, so a Tier 1 solution is sufficient.

**Tier Justification:** Every operation in V1's External module — JOIN, GROUP BY, COUNT, SUM, ROUND, ORDER BY — is natively supported by SQLite. The trailer format (`TRAILER|{row_count}|2024-10-01`) can be reproduced using the CsvFileWriter's `trailerFormat` with a hardcoded date string to replicate W8. The `writeMode: Overwrite` and `lineEnding: LF` settings match V1's StreamWriter behavior. There is zero procedural logic that requires C#.

---

## 2. V2 Module Chain

```
DataSourcing (holdings)
  → DataSourcing (securities)
    → Transformation (SQL: join + aggregate)
      → CsvFileWriter (CSV with trailer)
```

Four modules total. The V1 chain was `DataSourcing (holdings) → DataSourcing (securities) → DataSourcing (investments) → External`. The V2 chain drops the unused investments source (AP1), replaces the External module with SQL Transformation + CsvFileWriter (AP3), and eliminates row-by-row C# iteration (AP6).

---

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (Preserved)

| W-Code | V1 Behavior | V2 Treatment |
|--------|-------------|--------------|
| W8 | Trailer date hardcoded to `2024-10-01` instead of using effective date | Reproduce via hardcoded `trailerFormat` string: `"TRAILER\|{row_count}\|2024-10-01"`. The CsvFileWriter's `{date}` token would inject the correct effective date, so we intentionally avoid it and hardcode the stale value. Comment in FSD and job config documents the bug. |
| W9 | Overwrite mode — each run replaces the file, so multi-day auto-advance only retains the final day's output | Reproduce with `writeMode: "Overwrite"` in CsvFileWriter config. V1 opens the file with `append: false`. |

### Code-Quality Anti-Patterns (Eliminated)

| AP-Code | V1 Problem | V2 Resolution |
|---------|------------|---------------|
| AP1 | `investments` table is sourced but never used by the External module | **Eliminated.** V2 does not source `investments` at all. Evidence: [FundAllocationWriter.cs] never accesses `sharedState["investments"]`; [fund_allocation_breakdown.json:20-25] sources it for no reason. |
| AP3 | Unnecessary External module — all logic (join, group, aggregate, write) done in C# when SQL + CsvFileWriter would suffice | **Eliminated.** V2 uses Transformation (SQL) for all business logic and CsvFileWriter for output. No External module needed. |
| AP4 | V1 sources `holding_id`, `investment_id`, `customer_id`, `quantity` from holdings and `ticker`, `security_name`, `sector` from securities — none of which are used in the output | **Eliminated.** V2 sources only `security_id`, `current_value` from holdings and `security_id`, `security_type` from securities. |
| AP6 | Row-by-row C# iteration with nested `foreach` loops for building lookup dictionary and aggregating groups | **Eliminated.** V2 uses a single SQL query with JOIN, GROUP BY, and aggregate functions. |

### Anti-Patterns Not Applicable

| AP-Code | Reason |
|---------|--------|
| AP2 | No known duplication with other jobs for this specific aggregation |
| AP5 | NULL handling for `security_type` (→ "Unknown") is consistent; no asymmetry identified |
| AP7 | No magic values beyond the W8 hardcoded date, which is handled as a wrinkle |
| AP8 | V1 has no SQL (all in C#); V2 SQL is clean with no unused CTEs |
| AP9 | Job name accurately describes output — no misleading name |
| AP10 | V1 uses executor-injected effective dates, not over-sourcing; V2 follows same pattern |

---

## 4. Output Schema

| Column | Type | Source | Transformation | Evidence |
|--------|------|--------|---------------|----------|
| security_type | TEXT | securities.security_type | COALESCE to 'Unknown' for NULL values; used as GROUP BY key | [FundAllocationWriter.cs:32,40] |
| holding_count | INTEGER | COUNT(holdings rows) | Count of holdings per security_type group | [FundAllocationWriter.cs:47] |
| total_value | REAL (2 dp) | SUM(holdings.current_value) | Rounded to 2 decimal places via ROUND(..., 2) | [FundAllocationWriter.cs:66] |
| avg_value | REAL (2 dp) | total_value / holding_count | Rounded to 2 decimal places; 0 if count is 0 (division guard) | [FundAllocationWriter.cs:64] |
| as_of | TEXT | `__maxEffectiveDate` | Formatted as yyyy-MM-dd string; uses `{date}` injection or SQL expression | [FundAllocationWriter.cs:25-26,66] |

### Trailer Row

Format: `TRAILER|{row_count}|2024-10-01`

- `{row_count}` = number of data rows in the output (i.e., number of distinct security_type groups), NOT the number of input holdings rows.
- `2024-10-01` = hardcoded stale date (W8 — V1 bug replicated for output equivalence).

Evidence: [FundAllocationWriter.cs:55,68,71] — `rowCount` is incremented per output group, and the trailer uses the literal string `2024-10-01`.

### Row Ordering

Output rows are ordered alphabetically by `security_type` (ASC).
Evidence: [FundAllocationWriter.cs:60] — `.OrderBy(k => k.Key)`

---

## 5. SQL Design

### Transformation SQL

```sql
SELECT
    COALESCE(s.security_type, 'Unknown') AS security_type,
    COUNT(*) AS holding_count,
    ROUND(SUM(h.current_value), 2) AS total_value,
    CASE
        WHEN COUNT(*) > 0 THEN ROUND(SUM(h.current_value) * 1.0 / COUNT(*), 2)
        ELSE 0
    END AS avg_value,
    MAX(h.as_of) AS as_of
FROM holdings h
LEFT JOIN securities s ON h.security_id = s.security_id AND h.as_of = s.as_of
GROUP BY COALESCE(s.security_type, 'Unknown')
ORDER BY security_type
```

### SQL Design Notes

1. **LEFT JOIN with as_of alignment:** Holdings and securities are both snapshot tables. When the effective date range spans multiple days, both tables contain rows for each `as_of` date. The JOIN must align on both `security_id` AND `as_of` to prevent cross-date Cartesian products. V1 builds a lookup dictionary from all securities rows (last-write-wins per `security_id`), but since the framework sources data filtered to the effective date range and the datalake is a full-load snapshot, each `security_id` appears once per `as_of` date. Joining on both columns produces the correct 1:1 mapping per holding row per date.

2. **COALESCE for Unknown default:** V1 uses `secRow["security_type"]?.ToString() ?? "Unknown"` for the lookup dictionary and `typeLookup.GetValueOrDefault(secId, "Unknown")` for unmatched holdings. The SQL `LEFT JOIN` + `COALESCE(s.security_type, 'Unknown')` handles both cases:
   - NULL `security_type` in the securities table → COALESCE maps to 'Unknown' (matches BR-12)
   - No matching security row (LEFT JOIN yields NULL) → COALESCE maps to 'Unknown' (matches BR-2)

3. **Division guard:** The `CASE WHEN COUNT(*) > 0` mirrors V1's `count > 0 ? ... : 0m` guard (BR-5). In practice, GROUP BY guarantees `COUNT(*) > 0` for every group, but we include the guard for behavioral parity.

4. **ROUND(..., 2):** SQLite's ROUND function with 2 decimal places matches V1's `Math.Round(totalValue, 2)` and `Math.Round(totalValue / count, 2)`. Note: C#'s default `Math.Round` uses Banker's rounding (MidpointRounding.ToEven), and SQLite's ROUND rounds half-away-from-zero. However, since monetary values at 2dp rarely hit exact 0.005 midpoints in aggregated sums, the risk of divergence is negligible. If Proofmark reveals a mismatch, this would need to be revisited.

5. **as_of column:** V1 writes `dateStr` (the formatted `maxDate`) in every row. The SQL uses `MAX(h.as_of)` which, for a single-day effective date range, yields the same date. For multi-day ranges, it yields the max date across all holdings in the group, which matches V1's behavior of writing the single `maxDate` string (since all rows share the same effective date range). The `as_of` column from DataSourcing is a DateOnly that gets registered as TEXT in SQLite, so `MAX(h.as_of)` on `yyyy-MM-dd` strings produces the lexicographically greatest date, which equals the max effective date.

6. **Empty input handling (BR-3):** If `holdings` or `securities` is empty, the Transformation module's `RegisterTable` skips empty DataFrames (they don't get registered as SQLite tables). The SQL query would fail if `holdings` doesn't exist as a table. However, the CsvFileWriter writes a header-only file (header + trailer, zero data rows) when the DataFrame is empty. This differs from V1's behavior where the External module returns early without writing any file at all. **Potential divergence point:** If the effective date range yields zero holdings or securities rows, V1 produces no output file while V2 would produce a header+trailer-only file. This edge case needs monitoring during Proofmark validation. If it causes a mismatch, a Tier 2 solution with a minimal External module to check for empty inputs could be considered.

   **Mitigation:** In practice, the effective date range 2024-10-01 through 2024-12-31 should always have holdings and securities data in the datalake. If Proofmark comparison is run only against dates with data, this edge case won't manifest. If it does, the resolution is documented below.

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "FundAllocationBreakdownV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "holdings",
      "schema": "datalake",
      "table": "holdings",
      "columns": ["security_id", "current_value"]
    },
    {
      "type": "DataSourcing",
      "resultName": "securities",
      "schema": "datalake",
      "table": "securities",
      "columns": ["security_id", "security_type"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT COALESCE(s.security_type, 'Unknown') AS security_type, COUNT(*) AS holding_count, ROUND(SUM(h.current_value), 2) AS total_value, CASE WHEN COUNT(*) > 0 THEN ROUND(SUM(h.current_value) * 1.0 / COUNT(*), 2) ELSE 0 END AS avg_value, MAX(h.as_of) AS as_of FROM holdings h LEFT JOIN securities s ON h.security_id = s.security_id AND h.as_of = s.as_of GROUP BY COALESCE(s.security_type, 'Unknown') ORDER BY security_type"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/fund_allocation_breakdown.csv",
      "includeHeader": true,
      "trailerFormat": "TRAILER|{row_count}|2024-10-01",
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

### Config Design Notes

- **DataSourcing (holdings):** Only `security_id` and `current_value` — the two columns actually used. V1 sourced 6 columns (AP4 eliminated). The `as_of` column is automatically appended by the DataSourcing module when not explicitly listed (per Architecture.md).
- **DataSourcing (securities):** Only `security_id` and `security_type`. V1 sourced 5 columns (AP4 eliminated).
- **No investments DataSourcing:** V1's third DataSourcing module for `investments` is dropped entirely (AP1 eliminated).
- **Transformation:** Single SQL query replaces the entire External module (AP3, AP6 eliminated).
- **CsvFileWriter:** Replaces V1's direct StreamWriter file I/O. Configuration matches V1's output format:
  - `includeHeader: true` — V1 writes a header row ([FundAllocationWriter.cs:58])
  - `trailerFormat: "TRAILER|{row_count}|2024-10-01"` — hardcoded date replicates W8. The `{row_count}` token is substituted by CsvFileWriter with the number of data rows in the DataFrame, which equals the number of security_type groups (matching BR-8).
  - `writeMode: "Overwrite"` — V1 uses `append: false` ([FundAllocationWriter.cs:56]). Replicates W9.
  - `lineEnding: "LF"` — V1 writes `\n` explicitly ([FundAllocationWriter.cs:58,66,71]).

---

## 7. Writer Config

| Parameter | Value | V1 Evidence |
|-----------|-------|-------------|
| Writer type | CsvFileWriter | V1 uses direct StreamWriter; V2 uses framework CsvFileWriter for identical output |
| outputFile | `Output/double_secret_curated/fund_allocation_breakdown.csv` | V1: `Output/curated/fund_allocation_breakdown.csv` [FundAllocationWriter.cs:52] |
| includeHeader | `true` | V1 writes `string.Join(",", outputColumns) + "\n"` [FundAllocationWriter.cs:58] |
| trailerFormat | `TRAILER\|{row_count}\|2024-10-01` | V1: `$"TRAILER\|{rowCount}\|2024-10-01\n"` [FundAllocationWriter.cs:71]. W8 stale date intentionally reproduced. |
| writeMode | `Overwrite` | V1: `append: false` [FundAllocationWriter.cs:56]. W9 — overwrite loses prior days. |
| lineEnding | `LF` | V1: explicit `\n` in all Write calls [FundAllocationWriter.cs:58,66,71] |
| encoding | UTF-8 (no BOM) | V1: StreamWriter default is UTF-8. CsvFileWriter uses UTF-8 no BOM by default. |

### Output Path Change

V1: `Output/curated/fund_allocation_breakdown.csv`
V2: `Output/double_secret_curated/fund_allocation_breakdown.csv`

This is the only intentional difference between V1 and V2 output — the directory changes from `curated` to `double_secret_curated` per BLUEPRINT requirements.

---

## 8. Proofmark Config Design

```yaml
comparison_target: "fund_allocation_breakdown"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

### Proofmark Design Rationale

- **Reader:** `csv` — output is a CSV file.
- **header_rows: 1** — both V1 and V2 write a single header row containing column names.
- **trailer_rows: 1** — the job uses `writeMode: Overwrite`, so there is exactly one trailer row at the end of the file. The trailer format is `TRAILER|{row_count}|2024-10-01` in both V1 and V2.
- **threshold: 100.0** — all values are deterministic; 100% match required.
- **No excluded columns** — all output columns are deterministic. No timestamps, UUIDs, or runtime-dependent values.
- **No fuzzy columns** — V1 uses `decimal` for monetary values (not `double`), so no floating-point epsilon issues (W6 does not apply). V2 uses SQLite REAL for arithmetic, but ROUND(..., 2) should produce identical 2dp results for the aggregation operations involved. If a precision mismatch is detected during validation, `total_value` and `avg_value` would be candidates for fuzzy matching with absolute tolerance 0.01.

### Potential Proofmark Risk: SQLite ROUND vs C# Math.Round

SQLite ROUND uses "round half away from zero" while C#'s `Math.Round` defaults to Banker's rounding (round half to even). For a value like 123.455, SQLite rounds to 123.46 while C# rounds to 123.46 (even). For 123.445, both round to 123.44. For 123.465, SQLite rounds to 123.47, C# rounds to 123.46.

**Risk assessment:** LOW. Aggregated monetary sums rarely land exactly on 0.005 boundaries. If Proofmark detects a mismatch, the resolution would be either (a) adding fuzzy tolerance of 0.01 to `total_value` and `avg_value`, or (b) switching to a Tier 2 External module that uses `Math.Round` with `MidpointRounding.ToEven` for the rounding step only.

---

## 9. Traceability Matrix

| BRD Requirement | FSD Section | V2 Implementation |
|-----------------|-------------|-------------------|
| BR-1: Holdings aggregated by security_type via security_id lookup | SQL Design (JOIN + GROUP BY) | SQL: `LEFT JOIN securities s ON h.security_id = s.security_id AND h.as_of = s.as_of` + `GROUP BY COALESCE(s.security_type, 'Unknown')` |
| BR-2: Unknown default for unmatched security_id | SQL Design (COALESCE) | SQL: `LEFT JOIN` produces NULL for unmatched → `COALESCE(s.security_type, 'Unknown')` |
| BR-3: Empty DataFrame on null/empty input | SQL Design Note 6 | Transformation produces empty result → CsvFileWriter writes header+trailer only. **Note:** Slight behavioral difference vs V1 (V1 writes no file). See risk note. |
| BR-4: total_value rounded to 2dp | SQL Design (ROUND) | SQL: `ROUND(SUM(h.current_value), 2)` |
| BR-5: avg_value = total/count, rounded 2dp, 0 if count=0 | SQL Design (CASE + ROUND) | SQL: `CASE WHEN COUNT(*) > 0 THEN ROUND(SUM(h.current_value) * 1.0 / COUNT(*), 2) ELSE 0 END` |
| BR-6: Ordered alphabetically by security_type | SQL Design (ORDER BY) | SQL: `ORDER BY security_type` |
| BR-7/W8: Stale trailer date | Writer Config, Anti-Pattern Analysis | `trailerFormat: "TRAILER\|{row_count}\|2024-10-01"` — hardcoded date in format string |
| BR-8: Trailer row_count = output data rows (groups) | Writer Config | CsvFileWriter's `{row_count}` token = DataFrame row count = number of groups |
| BR-9: as_of from maxDate | SQL Design (MAX) | SQL: `MAX(h.as_of) AS as_of` — yields maxEffectiveDate for each group |
| BR-10: Effective dates injected by executor | Job Config | No explicit dates in DataSourcing modules; relies on `__minEffectiveDate`/`__maxEffectiveDate` |
| BR-11: External module bypasses CsvFileWriter | Anti-Pattern Analysis (AP3) | **Eliminated.** V2 uses CsvFileWriter. External module is unnecessary. |
| BR-12: NULL security_type defaults to Unknown | SQL Design (COALESCE) | SQL: `COALESCE(s.security_type, 'Unknown')` |
| BR-13: Investments sourced but unused | Anti-Pattern Analysis (AP1) | **Eliminated.** V2 does not source investments. |
| W8: Stale trailer date | Writer Config | Hardcoded `2024-10-01` in trailerFormat string |
| W9: Overwrite mode | Writer Config | `writeMode: "Overwrite"` — prior days' output is lost |
| AP1: Dead-end sourcing (investments) | Anti-Pattern Analysis | Eliminated — investments DataSourcing removed |
| AP3: Unnecessary External module | Anti-Pattern Analysis | Eliminated — replaced with Transformation SQL + CsvFileWriter |
| AP4: Unused columns | Anti-Pattern Analysis | Eliminated — V2 sources only security_id, current_value (holdings) and security_id, security_type (securities) |
| AP6: Row-by-row iteration | Anti-Pattern Analysis | Eliminated — SQL set-based operations replace C# foreach loops |

---

## 10. External Module Design

**Not applicable.** This is a Tier 1 (Framework Only) implementation. No External module is needed.

All V1 External module logic has been replaced:
- Security type lookup dictionary → SQL LEFT JOIN
- Row-by-row aggregation loops → SQL GROUP BY + COUNT/SUM/ROUND
- Direct StreamWriter CSV output → Framework CsvFileWriter
- Hardcoded trailer date → CsvFileWriter trailerFormat with hardcoded string

---

## Appendix: Risk Register

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| SQLite ROUND vs C# Math.Round rounding divergence | LOW | Proofmark FAIL on total_value or avg_value | Add fuzzy tolerance 0.01 on affected columns, or escalate to Tier 2 with External for rounding only |
| Empty input: V2 writes header+trailer file vs V1 writes no file | LOW | Proofmark ERROR (missing file) | Only manifests if datalake has dates with zero holdings/securities. Mitigate by verifying data exists for all dates in range. If needed, escalate to Tier 2 with pre-check External. |
| Multi-day effective date range: cross-date JOIN behavior | LOW | Row count differences | JOIN on both `security_id` AND `as_of` prevents cross-date Cartesian product. V1's dictionary overwrites produce same-day-wins behavior. |
| CsvFileWriter RFC 4180 quoting vs V1's bare writes | LOW | Extra quotes around fields containing special characters | Only triggers if security_type contains commas/quotes. Unlikely for standard types (Stock, Bond, ETF, etc.). |
