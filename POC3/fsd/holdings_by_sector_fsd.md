# HoldingsBySector — Functional Specification Document

## 1. Overview & Tier Justification

**Job:** HoldingsBySectorV2
**Tier:** Tier 2 — Framework + Minimal External (DataSourcing -> Transformation -> External -> CsvFileWriter)

This job produces a summary of holdings aggregated by sector, showing the count of holdings and total value per sector. The output is a CSV file with a header and a trailer row.

### Tier Selection Rationale

**Why not Tier 1?** The job's business logic (join holdings to securities, group by sector, aggregate) is fully expressible in SQL and could use DataSourcing -> Transformation -> CsvFileWriter. However, V1 exhibits wrinkle W7: the trailer row contains the INPUT holdings row count (before grouping) rather than the OUTPUT row count (grouped sector rows). The framework's CsvFileWriter substitutes `{row_count}` with `df.Count` (the output DataFrame's row count) — there is no mechanism to override this with an arbitrary value. Since the trailer must say `TRAILER|1303|{date}` (input count) and not `TRAILER|8|{date}` (output count), CsvFileWriter alone cannot produce the correct trailer.

**Why Tier 2 and not Tier 3?** DataSourcing is perfectly capable of pulling the required data from `datalake.holdings` and `datalake.securities` using framework-injected effective dates. The SQL Transformation can perform the join, grouping, and aggregation. The ONLY thing that requires an External module is constructing the CSV file with the inflated trailer count. The External module is a scalpel: it reads the grouped output and the raw holdings count, then writes the file with the correct trailer. No other logic lives in the External.

---

## 2. V2 Module Chain

```
DataSourcing (holdings)
  -> DataSourcing (securities)
    -> Transformation (SQL: join, group, aggregate)
      -> External (HoldingsBySectorV2Processor: write CSV with W7 inflated trailer)
```

### Module 1: DataSourcing — holdings
- **resultName:** `holdings`
- **schema:** `datalake`
- **table:** `holdings`
- **columns:** `["security_id", "current_value"]`
- **Effective dates:** Injected by framework (`__minEffectiveDate` / `__maxEffectiveDate`)
- **Note:** V1 sources 7 columns (`holding_id`, `investment_id`, `security_id`, `customer_id`, `quantity`, `cost_basis`, `current_value`). Only `security_id` (for sector lookup) and `current_value` (for aggregation) are used. The other 5 columns are eliminated per AP4 (unused columns).

### Module 2: DataSourcing — securities
- **resultName:** `securities`
- **schema:** `datalake`
- **table:** `securities`
- **columns:** `["security_id", "sector"]`
- **Effective dates:** Injected by framework (`__minEffectiveDate` / `__maxEffectiveDate`)
- **Note:** V1 sources 6 columns (`security_id`, `ticker`, `security_name`, `security_type`, `sector`, `exchange`). Only `security_id` (join key) and `sector` (aggregation key) are used. The other 4 columns are eliminated per AP4.

### Module 3: Transformation — sector aggregation
- **resultName:** `output`
- **sql:** See Section 5 for full SQL design
- **Purpose:** Join holdings to securities on `security_id`, group by sector (with NULL/unmatched defaulting to "Unknown"), aggregate holding count and total value, round total_value to 2 decimal places, add `as_of` from `__maxEffectiveDate`, order alphabetically by sector.

### Module 4: External — HoldingsBySectorV2Processor
- **Assembly:** `ExternalModules/bin/Debug/net8.0/ExternalModules.dll`
- **Type:** `ExternalModules.HoldingsBySectorV2Processor`
- **Purpose:** Write the CSV file with the W7 inflated trailer. This module:
  1. Reads `output` DataFrame (grouped sector rows) from shared state
  2. Reads `holdings` DataFrame from shared state to get `holdings.Count` (input row count for W7 trailer)
  3. Reads `__maxEffectiveDate` from shared state for the trailer date
  4. Writes CSV to `Output/double_secret_curated/holdings_by_sector.csv` with:
     - Header row: `sector,holding_count,total_value,as_of`
     - Data rows from the `output` DataFrame (already ordered by SQL)
     - Trailer: `TRAILER|{inputCount}|{dateStr}` using the inflated holdings count
  5. Returns shared state (no further modules in chain)
- **Why not CsvFileWriter?** CsvFileWriter's `{row_count}` token substitutes `df.Count` which equals the number of grouped output rows (e.g., 8). V1's trailer uses the raw holdings input count (e.g., 1303). There is no way to override CsvFileWriter's `{row_count}` with a custom value. The External module is the minimum-viable solution for W7 replication.

---

## 3. Anti-Pattern Analysis

### Applicable Code-Quality Anti-Patterns

| Code | Name | Applies? | V2 Action |
|------|------|----------|-----------|
| AP1 | Dead-end sourcing | **No** | Both `holdings` and `securities` are used. No unused sources. |
| AP3 | Unnecessary External | **Partially** | V1 uses a full External module for ALL logic (sourcing via DataSourcing, grouping, writing). V2 moves data sourcing to framework DataSourcing modules, business logic to SQL Transformation, and uses the External ONLY for the W7 trailer write. The External is justified (Tier 2) because CsvFileWriter cannot replicate the inflated trailer count. |
| AP4 | Unused columns | **Yes — ELIMINATED** | V1 sources 13 columns across both tables; only 4 are used (`security_id`, `current_value` from holdings; `security_id`, `sector` from securities). V2 sources only the 4 needed columns. |
| AP6 | Row-by-row iteration | **Yes — ELIMINATED** | V1 uses `foreach` loops to build the sector lookup dictionary and accumulate group counts/values. V2 replaces this with a single SQL `LEFT JOIN ... GROUP BY` in the Transformation module. The External module iterates the output DataFrame for CSV writing only (which is inherently row-by-row I/O). |
| AP7 | Magic values | **Partially** | V1 hardcodes `"Unknown"` as the default sector. V2 uses `COALESCE(s.sector, 'Unknown')` in SQL, which is idiomatic. The External module will use a named constant for the output path. |
| AP10 | Over-sourcing dates | **No** | V1 uses framework-injected effective dates via DataSourcing. V2 does the same. No over-sourcing. |

### Applicable Output-Affecting Wrinkles

| Code | Name | Applies? | V2 Action |
|------|------|----------|-----------|
| W7 | Trailer inflated count | **Yes — REPRODUCED** | V1's trailer uses `holdings.Count` (input rows before grouping) instead of the number of grouped output rows. V2 reproduces this: the External module reads `holdings.Count` from shared state and writes it into the trailer. A comment documents: `// W7: V1 bug — trailer uses input holdings count (before grouping), not output row count. Replicated for output equivalence.` |
| W5 | Banker's rounding | **Yes — RISK DOCUMENTED** | V1 uses `Math.Round(totalValue, 2)` which defaults to `MidpointRounding.ToEven` (banker's rounding). SQLite ROUND() uses round-half-away-from-zero. These diverge at exact midpoints (e.g., 2.5 → 2 in C# but 3 in SQLite). If any sector's `SUM(current_value)` lands on an exact midpoint after REAL accumulation, `total_value` could differ by 0.01. Mitigation: start strict in Proofmark; if Phase D reveals a difference, either add FUZZY tolerance on `total_value` (absolute 0.01) or move the rounding into the Tier 2 External module using `Math.Round(x, 2, MidpointRounding.ToEven)`. |
| W6 | Double epsilon | **Yes — RISK DOCUMENTED** | V1 accumulates `current_value` using C# `decimal` (exact base-10), then rounds. V2's SQL uses SQLite REAL (64-bit IEEE 754 float) for SUM. The intermediate sums may differ from `decimal` accumulation due to floating-point representation. For typical 2-decimal-place monetary values summed across moderate groups (~200 rows per sector), the difference is expected to be negligible after ROUND to 2 places. If Proofmark detects epsilon differences, the mitigation path is the same as W5 (FUZZY or External module rounding). |
| W9 | Wrong writeMode | **Yes — REPRODUCED** | V1 uses `append: false` (Overwrite), meaning each effective date's run replaces the previous. For multi-day auto-advance, only the final date's output persists. V2 reproduces this Overwrite behavior. Comment: `// W9: V1 uses Overwrite — prior days' data is lost on each auto-advance run.` |

### Anti-Patterns NOT Applicable

| Code | Name | Why Not |
|------|------|---------|
| AP2 | Duplicated logic | No cross-job duplication identified. |
| AP5 | Asymmetric NULLs | NULL handling is consistent: NULL sector → "Unknown" in both the securities lookup and unmatched holdings. |
| AP8 | Complex SQL / unused CTEs | V1 has no SQL (all C#). V2's SQL is straightforward with no unused CTEs. |
| AP9 | Misleading names | Job name accurately describes output (holdings grouped by sector). |
| W1-W4, W8, W10, W12 | Other wrinkles | None of these patterns appear in V1 code. No Sunday skip, weekend fallback, boundary rows, integer division, stale trailer date, absurd numParts, or header-every-append. |

---

## 4. Output Schema

| Column | Type | Source | Transformation | Evidence |
|--------|------|--------|---------------|----------|
| sector | TEXT | `securities.sector` | `COALESCE(s.sector, 'Unknown')` for NULLs; `'Unknown'` for unmatched `security_id` (LEFT JOIN) | [HoldingsBySectorWriter.cs:32,40] |
| holding_count | INTEGER | COUNT of holdings rows per sector | `COUNT(h.security_id)` in GROUP BY | [HoldingsBySectorWriter.cs:47] |
| total_value | DECIMAL(2) | `SUM(holdings.current_value)` per sector | `ROUND(SUM(h.current_value), 2)` | [HoldingsBySectorWriter.cs:47,63] |
| as_of | TEXT | `__maxEffectiveDate` from shared state | Formatted as `yyyy-MM-dd` | [HoldingsBySectorWriter.cs:24-25,63] |

### Trailer Row
Format: `TRAILER|{input_holdings_count}|{date}`
- `{input_holdings_count}` = `holdings.Count` (raw input rows, NOT grouped output rows) — **W7 replication**
- `{date}` = `__maxEffectiveDate` formatted as `yyyy-MM-dd`

### Row Ordering
Rows are ordered alphabetically by `sector` (ASC). Evidence: [HoldingsBySectorWriter.cs:59] `.OrderBy(k => k.Key)`.

### File Format
- **Encoding:** UTF-8 (StreamWriter default)
- **Line ending:** LF (`\n`)
- **Header:** Yes (first line: `sector,holding_count,total_value,as_of`)
- **Quoting:** None in V1 (no RFC 4180). V2's External module matches this — no quoting applied. Current sector values (Consumer, Energy, Finance, Healthcare, Industrial, Real Estate, Technology, Utilities) contain no special characters requiring quoting.
- **Write mode:** Overwrite (W9)

### Empty Input Handling (BR-3)
If `holdings` or `securities` is null or empty, no CSV file is written. The External module returns early with an empty `output` DataFrame in shared state. Evidence: [HoldingsBySectorWriter.cs:15-19].

---

## 5. SQL Design

### Transformation SQL

```sql
SELECT
    COALESCE(s.sector, 'Unknown') AS sector,
    COUNT(h.security_id) AS holding_count,
    ROUND(SUM(h.current_value), 2) AS total_value,
    (SELECT MAX(as_of) FROM holdings) AS as_of
FROM holdings h
LEFT JOIN securities s
    ON h.security_id = s.security_id
    AND h.as_of = s.as_of
GROUP BY COALESCE(s.sector, 'Unknown')
ORDER BY sector
```

### SQL Design Notes

1. **LEFT JOIN rationale (BR-2, BR-9):** Holdings whose `security_id` has no match in `securities` must default to sector `'Unknown'`. A LEFT JOIN ensures unmatched holdings are retained. `COALESCE(s.sector, 'Unknown')` handles both cases: (a) no matching securities row (NULL from LEFT JOIN), and (b) matching securities row with NULL sector value.

2. **as_of derivation (BR-6):** V1 gets `maxDate` from `__maxEffectiveDate` in shared state and formats it as `yyyy-MM-dd`. Since DataSourcing injects `as_of` into both tables and the framework filters by the effective date range, `MAX(as_of) FROM holdings` yields the same date. For single-day auto-advance runs (min = max), this is the effective date. SQLite will return the date as a TEXT string in `yyyy-MM-dd` format (since DataSourcing stores DateOnly as TEXT via `ToSqliteValue`).

3. **JOIN condition includes `as_of`:** Both `holdings` and `securities` are full-load snapshot tables. When the effective date range spans multiple days, multiple snapshots coexist in the same DataFrames. Joining on `security_id AND as_of` ensures each holding maps to its same-day security record. V1's dictionary-based lookup (`sectorLookup[secId] = ...`) overwrites per `security_id` across all days (Edge Case 4 in BRD). However, for single-day auto-advance (which is the actual execution pattern), this produces the same result. The `AND h.as_of = s.as_of` condition is the correct relational approach.

4. **ROUND in SQLite (W5):** SQLite ROUND() uses round-half-away-from-zero. V1 uses C# Math.Round() which defaults to MidpointRounding.ToEven (banker's rounding). These will diverge at exact midpoints (e.g., 2.5 rounds to 2 in C# but 3 in SQLite). Additionally, V1 accumulates `current_value` using C# `decimal` (exact base-10 arithmetic) while SQLite uses 64-bit IEEE 754 floats (REAL) for SUM — so even the pre-rounded sums may differ slightly. If the accumulated SUM for any sector lands on or near an exact midpoint (X.XX5), the rounding difference could produce a 0.01 divergence in `total_value`. The Proofmark config starts strict (threshold 100.0, no FUZZY). If Phase D comparison reveals a difference, the mitigation is to promote `total_value` to FUZZY with `tolerance: 0.01, tolerance_type: absolute`, or escalate the rounding to the Tier 2 External module using `Math.Round(totalValue, 2, MidpointRounding.ToEven)` for exact V1 equivalence.

5. **Aggregation accuracy (W5, W6):** V1 accumulates `decimal` values in C# (`Convert.ToDecimal` + `decimal` tuple) and rounds with `Math.Round(totalValue, 2)` which defaults to `MidpointRounding.ToEven` (banker's rounding). SQLite stores these as REAL (double-precision float) and `ROUND(x, 2)` uses round-half-away-from-zero. Two divergence vectors exist: (a) W6 — float vs decimal intermediate sums may differ slightly, and (b) W5 — if the accumulated sum lands on an exact midpoint (X.XX5), the rounding direction differs. For the observed data (monetary values with 2 decimal places, summed across up to ~200 rows per sector), double-precision accumulation should produce identical results to decimal accumulation after ROUND to 2 places in most cases. If Proofmark detects differences, `total_value` can be promoted to FUZZY with `tolerance: 0.01, tolerance_type: absolute`, or the rounding can be moved into the Tier 2 External module using `Math.Round(x, 2, MidpointRounding.ToEven)` for exact V1 equivalence.

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "HoldingsBySectorV2",
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
      "columns": ["security_id", "sector"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT COALESCE(s.sector, 'Unknown') AS sector, COUNT(h.security_id) AS holding_count, ROUND(SUM(h.current_value), 2) AS total_value, (SELECT MAX(as_of) FROM holdings) AS as_of FROM holdings h LEFT JOIN securities s ON h.security_id = s.security_id AND h.as_of = s.as_of GROUP BY COALESCE(s.sector, 'Unknown') ORDER BY sector"
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.HoldingsBySectorV2Processor"
    }
  ]
}
```

### Config Design Notes

- **No CsvFileWriter module:** The External module handles file output because CsvFileWriter's `{row_count}` token cannot replicate W7's inflated trailer count (see Section 2).
- **Column reduction (AP4):** `holdings` sources only `security_id` and `current_value` (was 7 columns). `securities` sources only `security_id` and `sector` (was 6 columns).
- **firstEffectiveDate:** Matches V1's `2024-10-01`.

---

## 7. Writer Config

The External module (HoldingsBySectorV2Processor) writes the CSV file directly, matching V1's exact output format.

| Parameter | Value | Rationale |
|-----------|-------|-----------|
| Output path | `Output/double_secret_curated/holdings_by_sector.csv` | V2 output directory per BLUEPRINT. Filename matches V1. |
| Encoding | UTF-8 (StreamWriter default) | Matches V1 [HoldingsBySectorWriter.cs:55] |
| Line ending | LF (`\n`) | Matches V1 [HoldingsBySectorWriter.cs:57,63,67] — uses `writer.Write(... + "\n")` |
| Header | Yes: `sector,holding_count,total_value,as_of` | Matches V1 [HoldingsBySectorWriter.cs:57] |
| Trailer | `TRAILER|{inputCount}|{dateStr}` | W7: uses raw holdings input count, not output row count [HoldingsBySectorWriter.cs:67] |
| Write mode | Overwrite (`append: false`) | Matches V1 [HoldingsBySectorWriter.cs:55]. W9: prior days' output lost on each run. |
| RFC 4180 quoting | None | Matches V1 — V1 writes raw values with no quoting [HoldingsBySectorWriter.cs:63] |

---

## 8. Proofmark Config Design

```yaml
comparison_target: "holdings_by_sector"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

### Proofmark Design Notes

- **reader: csv** — Output is a CSV file (direct file I/O in both V1 and V2).
- **header_rows: 1** — The CSV has one header row (`sector,holding_count,total_value,as_of`).
- **trailer_rows: 1** — V1 uses Overwrite mode (W9), so the final file has exactly one trailer at the end. Proofmark strips it before comparison.
- **threshold: 100.0** — All values are deterministic. No non-deterministic fields identified in the BRD.
- **No EXCLUDED columns** — No timestamp or UUID fields. All columns are deterministic.
- **No FUZZY columns (initial)** — Starting strict per best practices. W5 and W6 are both applicable: V1 uses C# `decimal` accumulation with `Math.Round(x, 2)` (banker's rounding, MidpointRounding.ToEven), while V2 uses SQLite REAL (64-bit float) accumulation with `ROUND(x, 2)` (round-half-away-from-zero). Two potential divergence sources exist: (1) float vs decimal intermediate sums, and (2) different rounding at exact midpoints. For typical 2-decimal-place monetary values summed across moderate groups, these should produce identical results after ROUND to 2 places. If Proofmark detects differences in Phase D, `total_value` can be promoted to FUZZY with `tolerance: 0.01, tolerance_type: absolute`, or the rounding can be moved into the Tier 2 External module for exact V1 equivalence using `Math.Round(x, 2, MidpointRounding.ToEven)`.

---

## 9. Traceability Matrix

| BRD Requirement | FSD Section | V2 Implementation |
|----------------|-------------|-------------------|
| BR-1: Holdings aggregated by sector via security_id lookup | Sections 2 (Module 3), 5 (SQL) | SQL `LEFT JOIN securities s ON h.security_id = s.security_id`, `GROUP BY sector` |
| BR-2: Unmatched security_id defaults to "Unknown" | Sections 3 (AP analysis), 5 (SQL note 1) | SQL `LEFT JOIN` + `COALESCE(s.sector, 'Unknown')` |
| BR-3: Empty input → no output file, empty DataFrame | Section 4 (Empty Input Handling) | External module checks `holdings`/`securities` count, returns early with empty DataFrame |
| BR-4: total_value rounded to 2 decimal places | Sections 4 (Output Schema), 5 (SQL) | SQL `ROUND(SUM(h.current_value), 2)` |
| BR-5: Output ordered alphabetically by sector | Sections 4 (Row Ordering), 5 (SQL) | SQL `ORDER BY sector` |
| BR-6: as_of from __maxEffectiveDate | Sections 4 (Output Schema), 5 (SQL note 2) | SQL `(SELECT MAX(as_of) FROM holdings)` |
| BR-7 / W7: Inflated trailer count (input rows) | Sections 2 (Module 4), 3 (W7), 4 (Trailer Row) | External module reads `holdings.Count` and writes `TRAILER|{inputCount}|{date}` |
| BR-8: Direct file I/O, empty DataFrame as "output" | Section 2 (Module 4) | External module writes CSV directly, V1 sets empty output — V2 External is the last module so no downstream consumer needs `output` |
| BR-9: NULL sector defaults to "Unknown" | Section 5 (SQL note 1) | SQL `COALESCE(s.sector, 'Unknown')` handles both NULL join result and NULL sector value |
| BR-10: Effective dates from executor injection | Section 2 (Modules 1, 2), 6 (Config) | DataSourcing modules use framework-injected `__minEffectiveDate` / `__maxEffectiveDate` |
| W9: Overwrite mode (prior days lost) | Sections 3 (W9), 7 (Writer Config) | External module uses `append: false` |
| AP3: Unnecessary External (partial) | Section 3 (AP3) | Business logic moved to SQL Transformation; External handles ONLY W7 file write |
| AP4: Unused columns | Sections 2 (Modules 1, 2), 3 (AP4), 6 (Config) | holdings: 7 → 2 columns; securities: 6 → 2 columns |
| AP6: Row-by-row iteration | Section 3 (AP6) | C# loops replaced with SQL `GROUP BY` aggregation |

---

## 10. External Module Design — HoldingsBySectorV2Processor

### Responsibility
This External module has ONE job: write the CSV file with the W7 inflated trailer. All business logic (join, group, aggregate) is handled by the upstream SQL Transformation.

### Interface
Implements `IExternalStep.Execute(Dictionary<string, object> sharedState)`.

### Algorithm

```
1. Read "output" DataFrame from shared state (grouped sector rows from Transformation)
2. Read "holdings" DataFrame from shared state (raw input rows)
3. If output is null or empty OR holdings is null or empty:
     - Store empty DataFrame as "output" in shared state
     - Return (no file written) — BR-3
4. Read __maxEffectiveDate from shared state
5. Format dateStr as yyyy-MM-dd
6. Get inputCount = holdings.Count — W7: inflated count
7. Resolve output path: Output/double_secret_curated/holdings_by_sector.csv
8. Create output directory if needed
9. Open StreamWriter with append: false — W9: Overwrite
10. Write header: "sector,holding_count,total_value,as_of\n"
11. For each row in output DataFrame:
      Write: "{sector},{holding_count},{total_value},{as_of}\n"
12. Write trailer: "TRAILER|{inputCount}|{dateStr}\n" — W7
13. Close writer
14. Return shared state
```

### Named Constants

```csharp
private const string OutputRelativePath = "Output/double_secret_curated/holdings_by_sector.csv";
private const string DefaultSector = "Unknown";  // Used in SQL, documented here for reference
private const string TrailerPrefix = "TRAILER";
private const string HeaderLine = "sector,holding_count,total_value,as_of";
```

### Key Design Decisions

1. **No RFC 4180 quoting:** V1 writes raw CSV with no quoting. V2 matches this. Current sector values are safe (no commas, quotes, or newlines). If quoting differences emerge, Proofmark will catch them.

2. **LF line endings:** V1 uses `writer.Write(... + "\n")` which produces LF. V2 uses the same pattern. The StreamWriter is not configured with a specific NewLine property, matching V1's default behavior.

3. **Overwrite mode (W9):** StreamWriter is opened with `append: false`. Each auto-advance run overwrites the previous day's output. Only the final effective date's file persists.

4. **Inflated trailer (W7):** The trailer uses `holdings.Count` (the raw input DataFrame row count from DataSourcing), NOT `output.Count` (the grouped result). This is a documented V1 bug reproduced for output equivalence.

5. **Empty input handling (BR-3):** The External module checks both `holdings` and `output` (from the Transformation). If either the holdings input or the grouped output is empty, no file is written and an empty DataFrame is stored as `output`. Note: the Transformation SQL with LEFT JOIN and GROUP BY on an empty holdings table would produce 0 rows anyway, so checking `output` is sufficient, but checking `holdings` explicitly matches V1's guard clause and is clearer.

### Data Flow

```
SharedState at External entry:
  "holdings"   -> DataFrame (raw rows from DataSourcing, e.g., 1303 rows)
  "securities" -> DataFrame (raw rows from DataSourcing)
  "output"     -> DataFrame (grouped sector rows from Transformation, e.g., 8 rows)
  "__maxEffectiveDate" -> DateOnly

External writes:
  Output/double_secret_curated/holdings_by_sector.csv

SharedState at External exit:
  (unchanged — External is the last module)
```
