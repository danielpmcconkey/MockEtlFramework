# WireDirectionSummary -- Functional Specification Document

## 1. Job Summary

WireDirectionSummaryV2 aggregates wire transfer activity by direction (Inbound/Outbound), producing per-direction counts, totals, and averages. The output is a CSV file with a trailer line whose row count reflects the number of INPUT rows (before grouping), not the number of OUTPUT rows. Because the framework's CsvFileWriter hardcodes the `{row_count}` trailer token to `df.Count` (i.e., the output row count), it cannot produce the inflated input-row count that V1's trailer requires (W7). V2 therefore uses a Tier 2 chain: DataSourcing and Transformation handle data access and business logic cleanly, while a minimal External module handles only the CSV writing with the inflated trailer count.

---

## 2. V2 Module Chain

**Tier: 2 (Framework + Minimal External)**
`DataSourcing -> Transformation (SQL) -> External (CSV writing with W7 trailer)`

**Justification:** The aggregation logic (GROUP BY direction, COUNT, SUM, AVG, ROUND) is trivially expressible in SQL -- Tier 1 covers the business logic. However, the output file requires a trailer whose row count is the INPUT row count (W7: typically 35-62 rows), not the OUTPUT row count (typically 2 rows). CsvFileWriter's `{row_count}` token is hardcoded to `df.Count` on the output DataFrame [CsvFileWriter.cs:64]. Since Lib is immutable, there is no way to inject a custom count. The External module's sole responsibility is writing the CSV with the correct inflated trailer count. It does NOT perform any business logic -- all aggregation is done in SQL.

**Why not Tier 1:** CsvFileWriter cannot replicate W7. The `{row_count}` token always resolves to the output DataFrame's row count. With 2 direction groups, the trailer would read `TRAILER|2|{date}` instead of the correct `TRAILER|40|{date}` (or whatever the input count is for that day). This is a byte-level output difference that Proofmark would catch.

**Why not Tier 3:** DataSourcing handles the data access perfectly. The effective date injection works correctly. There is no reason to bypass the framework for data loading.

### Module 1: DataSourcing
- **resultName:** `wire_transfers`
- **schema:** `datalake`
- **table:** `wire_transfers`
- **columns:** `["direction", "amount"]`
- **Effective dates:** Injected by executor via shared state (`__minEffectiveDate`, `__maxEffectiveDate`). No hard-coded dates.
- **Column reduction (AP4):** V1 sources `["wire_id", "customer_id", "direction", "amount", "status"]`. Only `direction` and `amount` are used in the aggregation logic. `wire_id`, `customer_id`, and `status` are never referenced [WireDirectionSummaryWriter.cs:30-41 -- loop uses only `direction` and `amount`]. The `as_of` column is automatically appended by DataSourcing [DataSourcing.cs:69-72].

### Module 2: Transformation
- **resultName:** `wire_direction_summary`
- **sql:** See Section 4 below.

### Module 3: External
- **assemblyPath:** `ExternalModules/bin/Debug/net8.0/ExternalModules.dll`
- **typeName:** `ExternalModules.WireDirectionSummaryV2Processor`
- **Responsibility:** Read `wire_transfers` (input DataFrame) and `wire_direction_summary` (output DataFrame) from shared state. Write the output DataFrame to CSV with a trailer using the input DataFrame's row count. This module does ZERO business logic -- it is a W7-aware file writer only.

---

## 3. DataSourcing Config

| Field | Value | Notes |
|-------|-------|-------|
| resultName | `wire_transfers` | Same as V1 for SQL compatibility |
| schema | `datalake` | Source schema |
| table | `wire_transfers` | Source table |
| columns | `["direction", "amount"]` | Reduced from V1's 5 columns (AP4 eliminated). `as_of` auto-appended by DataSourcing. |
| minEffectiveDate | (not specified) | Injected at runtime by executor |
| maxEffectiveDate | (not specified) | Injected at runtime by executor |
| additionalFilter | (not specified) | No status filter -- all statuses included (BR-1) |

**Effective date handling:** The executor sets `__minEffectiveDate` and `__maxEffectiveDate` to the same date for each single-day run. DataSourcing applies `WHERE as_of >= @minDate AND as_of <= @maxDate` at the PostgreSQL level [DataSourcing.cs:74-78]. This eliminates AP10 (over-sourcing dates) -- V1 also uses executor-injected dates for this job.

**`as_of` column:** Not listed in `columns`, so DataSourcing auto-appends it [DataSourcing.cs:69-72, 105-108]. It is returned as a `DateOnly` value. The resulting DataFrame contains columns: `direction`, `amount`, `as_of`.

---

## 4. Transformation SQL

**V2 SQL:**
```sql
SELECT
    direction,
    COUNT(*) AS wire_count,
    ROUND(SUM(amount), 2) AS total_amount,
    ROUND(SUM(amount) * 1.0 / COUNT(*), 2) AS avg_amount,
    as_of
FROM wire_transfers
GROUP BY direction
ORDER BY direction
```

**Design notes:**

- **GROUP BY direction:** Produces one row per distinct direction value (Inbound, Outbound). All statuses included -- no WHERE filter on status (BR-1). Evidence: [WireDirectionSummaryWriter.cs:30-41] -- V1 groups by direction with no status check.

- **COUNT(\*) AS wire_count:** Count of wire transfers per direction (BR-2). Evidence: [WireDirectionSummaryWriter.cs:47].

- **ROUND(SUM(amount), 2) AS total_amount:** Sum of amounts per direction, rounded to 2 decimal places (BR-2). Evidence: [WireDirectionSummaryWriter.cs:48, 55] -- `Math.Round(totalAmount, 2)`.

- **ROUND(SUM(amount) * 1.0 / COUNT(\*), 2) AS avg_amount:** Average amount per direction, rounded to 2 decimal places (BR-2). The `* 1.0` ensures floating-point division in SQLite. Evidence: [WireDirectionSummaryWriter.cs:49] -- `Math.Round(totalAmount / wireCount, 2)`.

- **as_of:** The `as_of` value. In a single-day executor run (min=max effective date), all rows share the same `as_of`. GROUP BY direction does not include `as_of` in the GROUP BY clause -- SQLite will return the `as_of` value from an arbitrary row in each group. Since all rows within a single-day run have the same `as_of`, this is deterministic and equivalent to V1's `wireTransfers.Rows[0]["as_of"]` behavior (BR-4).

- **ORDER BY direction:** Ensures deterministic row ordering (Inbound before Outbound). V1 iterates a Dictionary which has implementation-defined order [BRD Edge Case 1]. By using ORDER BY, V2 produces stable output. Since V1 output is the comparison baseline, we need V1 to also produce Inbound-before-Outbound order. Dictionary insertion order in .NET follows the order of iteration over the source data, which is ordered by DataSourcing's `ORDER BY as_of`. Within a single date, database row order determines which direction appears first. Alphabetical ORDER BY ensures stability regardless.

- **Rounding note (W5):** V1 uses C# `Math.Round(decimal, 2)` which defaults to `MidpointRounding.ToEven` (banker's rounding). SQLite's ROUND uses standard half-up rounding. For the specific data values in `datalake.wire_transfers` (amounts ranging ~1012-49959, with exactly 2 decimal places in source), the difference between rounding modes only manifests at exact midpoints (e.g., x.xx5). If the data contains such midpoints, this could cause a discrepancy. This is flagged as an Open Question (see Section 9). If Proofmark comparison fails on `avg_amount` or `total_amount`, the resolution is to move the rounding into the External module using `Math.Round(value, 2, MidpointRounding.ToEven)`.

---

## 5. Writer Config

The External module (`WireDirectionSummaryV2Processor`) handles file output. Its configuration replicates V1's output format exactly:

| Parameter | Value | V1 Evidence |
|-----------|-------|-------------|
| Output path | `Output/double_secret_curated/wire_direction_summary.csv` | V1: `Output/curated/wire_direction_summary.csv` [WireDirectionSummaryWriter.cs:82] |
| Header | Yes (column names joined by comma) | [WireDirectionSummaryWriter.cs:95] |
| Line ending | LF (`writer.NewLine = "\n"`) | [WireDirectionSummaryWriter.cs:92] |
| Write mode | Overwrite (`append: false`) | [WireDirectionSummaryWriter.cs:91] |
| Trailer format | `TRAILER|{inputCount}|{maxEffectiveDate:yyyy-MM-dd}` | [WireDirectionSummaryWriter.cs:105] |
| Trailer row count | INPUT row count (before grouping) | W7: [WireDirectionSummaryWriter.cs:26, 104] |
| Trailer date | `__maxEffectiveDate` from shared state, formatted `yyyy-MM-dd` | [WireDirectionSummaryWriter.cs:88-89] |
| Trailer date fallback | `DateOnly.FromDateTime(DateTime.Today)` if `__maxEffectiveDate` not in shared state | [WireDirectionSummaryWriter.cs:88] (BR-9) |
| RFC 4180 quoting | No -- values joined by comma via `.ToString()` | [WireDirectionSummaryWriter.cs:100] (BR-5) |
| Empty file behavior | Header + `TRAILER|0|{date}` (no data rows) | [WireDirectionSummaryWriter.cs:20-23] (BR-7) |

**Write mode implications (W9):** V1 uses Overwrite mode. In multi-day gap-fill scenarios, each day's run completely replaces the previous output. Only the last effective date's output survives. This is V1's behavior and V2 replicates it. `// V1 uses Overwrite -- prior days' data is lost on each run.`

---

## 6. Wrinkle Replication

| W-Code | Applies? | V2 Replication Strategy |
|--------|----------|------------------------|
| W1 (Sunday skip) | No | No day-of-week logic in this job. |
| W2 (Weekend fallback) | No | No day-of-week logic. |
| W3a/b/c (Boundary summaries) | No | No summary rows appended. |
| W4 (Integer division) | No | No integer division in V1. V1 uses `decimal` division [WireDirectionSummaryWriter.cs:49]. |
| W5 (Banker's rounding) | **Possible** | V1 uses `Math.Round(decimal, 2)` which defaults to `MidpointRounding.ToEven`. SQLite ROUND uses half-up. If source data hits exact midpoints, outputs could differ. Flagged as Open Question. If comparison fails, move rounding to External module. |
| W6 (Double epsilon) | No | V1 uses `decimal` for accumulation [WireDirectionSummaryWriter.cs:30, 34, 40], not `double`. No epsilon concern. |
| W7 (Trailer inflated count) | **Yes** | **This is the primary wrinkle for this job.** The trailer reports the count of INPUT rows (before grouping), not OUTPUT rows. V1 captures `inputCount = wireTransfers.Count` before grouping [WireDirectionSummaryWriter.cs:26] and uses it in the trailer [WireDirectionSummaryWriter.cs:104]. V2 replicates this by having the External module read the `wire_transfers` DataFrame from shared state and use its `.Count` for the trailer. The External module also reads the `wire_direction_summary` DataFrame (post-aggregation) for the data rows. Comment in V2 code: `// W7: trailer uses input row count (before grouping), not output row count. V1 behavior replicated for output equivalence.` |
| W8 (Trailer stale date) | No | V1 uses `__maxEffectiveDate` for the trailer date (not hardcoded to `"2024-10-01"`). Evidence: [WireDirectionSummaryWriter.cs:88-89]. |
| W9 (Wrong writeMode) | **Document only** | V1 uses Overwrite mode [WireDirectionSummaryWriter.cs:91]. In multi-day runs, only the last day's output survives. V2 replicates the same Overwrite behavior. `// V1 uses Overwrite -- prior days' data is lost on each run.` |
| W10 (Absurd numParts) | No | Not a Parquet job. |
| W12 (Header every append) | No | V1 uses Overwrite, not Append. Header is written once per execution. |

---

## 7. Anti-Pattern Elimination

| AP-Code | Applies? | V1 Problem | V2 Action |
|---------|----------|------------|-----------|
| AP1 (Dead-end sourcing) | No | Only one table sourced, and it is used in the aggregation. | N/A |
| AP2 (Duplicated logic) | No | No cross-job duplication identified. | N/A |
| AP3 (Unnecessary External) | **Partially** | V1 uses an External module for the ENTIRE pipeline: data iteration, grouping, aggregation, AND file writing [WireDirectionSummaryWriter.cs:8-66]. The business logic (GROUP BY direction with COUNT/SUM/AVG) is trivially expressible in SQL. | **Partially eliminated.** V2 moves all business logic to a Transformation SQL module (Tier 1 for data processing). The External module is retained ONLY for file writing due to W7 (the inflated trailer count cannot be produced by CsvFileWriter). The External module does zero business logic -- it reads pre-computed DataFrames and writes them to disk. This reduces the External module from ~107 lines of mixed logic to a focused ~40-line file writer. |
| AP4 (Unused columns) | **Yes** | V1 sources `["wire_id", "customer_id", "direction", "amount", "status"]`. Of these, only `direction` and `amount` are used. `wire_id` is never referenced. `customer_id` is never referenced. `status` is never referenced (BR-1: no status filter). Evidence: [WireDirectionSummaryWriter.cs:30-41] -- loop accesses only `row["direction"]` and `row["amount"]`. | **Eliminated.** V2 sources only `["direction", "amount"]`. `as_of` is auto-appended by DataSourcing. Three columns removed: `wire_id`, `customer_id`, `status`. |
| AP5 (Asymmetric NULLs) | No | V1 converts null direction to empty string [WireDirectionSummaryWriter.cs:33: `?? ""`]. Database shows only `Inbound` and `Outbound` values -- no NULLs in practice. V2 SQL handles this equivalently (NULL directions would form their own group in GROUP BY, which matches the empty-string grouping behavior for non-null data). | N/A |
| AP6 (Row-by-row iteration) | **Yes** | V1 uses a `foreach` loop with a `Dictionary<string, (int, decimal)>` to manually group and aggregate rows [WireDirectionSummaryWriter.cs:30-41]. This is a textbook case of row-by-row processing that SQL can replace. | **Eliminated.** V2 uses `GROUP BY direction` with `COUNT(*)`, `SUM(amount)`, and computed `AVG` in SQL. Set-based operation replaces procedural iteration. |
| AP7 (Magic values) | No | No hardcoded thresholds or magic strings in V1's logic. The direction values ("Inbound", "Outbound") come from the data, not hardcoded comparisons. | N/A |
| AP8 (Complex SQL / unused CTEs) | No | V1 doesn't use SQL at all -- it uses C# iteration. The V1 code comments mention "AP8: complex SQL with unused CTE -- but done in C# here instead" [WireDirectionSummaryWriter.cs:28], which is self-referential commentary, not an actual SQL issue. V2's SQL is straightforward with no unused CTEs. | N/A |
| AP9 (Misleading names) | No | Job name accurately describes output: wire transfers summarized by direction. | N/A |
| AP10 (Over-sourcing dates) | No | V1 uses executor-injected effective dates (no hard-coded dates in DataSourcing config). DataSourcing applies the date filter at the PostgreSQL level. V2 preserves this behavior. | N/A |

**Summary:** Three anti-patterns eliminated. AP3 (unnecessary External) is partially eliminated -- business logic moved to SQL, External retained only for W7 file writing. AP4 (unused columns) is fully eliminated -- 3 unused columns removed. AP6 (row-by-row iteration) is fully eliminated -- replaced with SQL GROUP BY.

---

## 8. Proofmark Config

**Starting position:** Zero exclusions, zero fuzzy overrides.

**Analysis of non-deterministic fields:**
- `as_of`: Deterministic within a single-day executor run (all rows share the same `as_of`). In multi-day runs with Overwrite mode, only the last day's output survives, so the `as_of` value is deterministic as long as V1 and V2 run the same date range.
- Trailer date: Uses `__maxEffectiveDate`, which is deterministic per executor run.
- No timestamps, UUIDs, or other non-reproducible fields.

**Analysis of floating-point concerns:**
- `total_amount`: V1 uses `decimal` accumulation with `Math.Round(decimal, 2)`. V2 uses SQLite `ROUND(SUM(amount), 2)` where `amount` is loaded as REAL (double-precision). The SUM and ROUND may produce different results at epsilon level. However, the source data has amounts with exactly 2 decimal places (e.g., `20012.00`), so SUM of integers-times-100 should be exact. Start strict; add fuzzy if comparison fails.
- `avg_amount`: V1 uses `Math.Round(totalAmount / wireCount, 2)` with `decimal` arithmetic. V2 uses SQLite `ROUND(SUM(amount) * 1.0 / COUNT(*), 2)` with REAL arithmetic. Division results may differ at the last decimal due to `decimal` vs `double` precision, or due to rounding mode differences (banker's vs half-up). Start strict; add fuzzy if comparison fails.

**CSV structure:**
- Header present -> `header_rows: 1`
- Trailer present, Overwrite mode -> `trailer_rows: 1`

**Proposed config:**

```yaml
comparison_target: "wire_direction_summary"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

**Rationale:**
- `reader: csv` -- output is a CSV file
- `header_rows: 1` -- V1 writes a header row [WireDirectionSummaryWriter.cs:95]
- `trailer_rows: 1` -- V1 writes one trailer row in Overwrite mode [WireDirectionSummaryWriter.cs:105]. Since writeMode is Overwrite, only one trailer exists at the end of the file.
- No excluded columns -- all fields are deterministic
- No fuzzy columns -- start strict per best practices. If `avg_amount` or `total_amount` fail comparison due to `decimal` vs SQLite REAL arithmetic differences, add fuzzy with tight absolute tolerance.
- `threshold: 100.0` -- require exact match

**Contingency (if comparison fails on numeric columns):**
```yaml
comparison_target: "wire_direction_summary"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
columns:
  fuzzy:
    - name: "avg_amount"
      tolerance: 0.01
      tolerance_type: absolute
      reason: "V1 uses decimal Math.Round (banker's rounding) vs V2 SQLite ROUND (half-up). Difference possible at exact midpoints. [WireDirectionSummaryWriter.cs:49] [BRD BR-2]"
    - name: "total_amount"
      tolerance: 0.01
      tolerance_type: absolute
      reason: "V1 uses decimal SUM vs V2 SQLite REAL SUM. Epsilon-level difference possible for large accumulations. [WireDirectionSummaryWriter.cs:48] [BRD BR-2]"
```

---

## 9. Open Questions

1. **Banker's rounding vs SQLite ROUND (W5):** V1 uses `Math.Round(decimal, 2)` which defaults to `MidpointRounding.ToEven`. SQLite's ROUND uses half-up rounding. If any computed `avg_amount` value hits an exact midpoint (e.g., `25000.125`), the results will differ by `0.01`. The source `amount` values are `numeric` with 2 decimal places, so `total_amount` (a SUM) is unlikely to hit a midpoint. However, `avg_amount` (total/count) can produce arbitrary decimal expansions. **Resolution path:** Run V2, compare with Proofmark. If `avg_amount` fails, either (a) move the AVG + ROUND computation into the External module using `Math.Round(decimal, 2, MidpointRounding.ToEven)`, or (b) add a fuzzy tolerance of `0.01` absolute.

2. **`as_of` column format:** V1 writes `as_of` via `row["as_of"]?.ToString()` on a `DateOnly` value. The default `DateOnly.ToString()` format depends on the current culture. In a Docker Linux environment with invariant culture, this typically produces `MM/dd/yyyy` (e.g., `10/01/2024`). V2's SQL Transformation stores `as_of` as a SQLite value and re-materializes it into a DataFrame. The format of `as_of` in the V2 output depends on how SQLite round-trips the value and how the External module's `.ToString()` renders it. **Resolution path:** Run V2 and compare. If format differs, the External module can explicitly format the `as_of` column to match V1's representation.

3. **Dictionary iteration order (V1 row ordering):** V1 iterates groups via `foreach` on a `Dictionary<string, ...>` [WireDirectionSummaryWriter.cs:45]. Dictionary iteration order in .NET is typically insertion order, which follows the source data order from DataSourcing (ordered by `as_of`, then database-natural order within a date). V2 uses `ORDER BY direction` in SQL, producing alphabetical order (Inbound, Outbound). If V1's dictionary happens to produce a different order (e.g., Outbound first because the first row in the data is Outbound), the row ordering will differ. **Resolution path:** Check V1 output to confirm row order. If V1 produces Outbound-first, change V2 SQL to `ORDER BY CASE direction WHEN 'Outbound' THEN 1 WHEN 'Inbound' THEN 2 END` or similar. Alternatively, if Proofmark does not enforce row ordering for CSV, this may be a non-issue.

---

## 10. External Module Design

**Module name:** `ExternalModules.WireDirectionSummaryV2Processor`

**Responsibility:** ONLY file writing with W7 trailer behavior. Zero business logic.

**Inputs from shared state:**
- `wire_transfers` (DataFrame): The raw input data from DataSourcing. Used ONLY for `Count` (input row count for W7 trailer).
- `wire_direction_summary` (DataFrame): The aggregated output from Transformation SQL. This is the data written to the CSV.
- `__maxEffectiveDate` (DateOnly): Used for trailer date formatting.

**Behavior:**
1. Read `wire_transfers.Count` as `inputCount` (for W7 trailer).
2. Read `wire_direction_summary` DataFrame as the output data.
3. Read `__maxEffectiveDate` from shared state (with fallback to `DateTime.Today` per BR-9).
4. Write CSV to `Output/double_secret_curated/wire_direction_summary.csv`:
   - Create output directory if needed (BR-8).
   - Open with `append: false` (Overwrite mode).
   - Set `writer.NewLine = "\n"` (LF line endings).
   - Write header row: column names joined by comma.
   - Write data rows: values joined by comma via `.ToString()` (no RFC 4180 quoting, matching V1 per BR-5).
   - Write trailer: `TRAILER|{inputCount}|{maxEffectiveDate:yyyy-MM-dd}` (W7).
5. Store the output DataFrame in shared state as `"output"` (matching V1 behavior per BR-6, though unused by subsequent modules).

**What this module does NOT do:**
- No grouping, aggregation, counting, summing, or averaging.
- No data transformation of any kind.
- No database queries.
- No business logic decisions.

---

## 11. V2 Job Config

```json
{
  "jobName": "WireDirectionSummaryV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "wire_transfers",
      "schema": "datalake",
      "table": "wire_transfers",
      "columns": ["direction", "amount"]
    },
    {
      "type": "Transformation",
      "resultName": "wire_direction_summary",
      "sql": "SELECT direction, COUNT(*) AS wire_count, ROUND(SUM(amount), 2) AS total_amount, ROUND(SUM(amount) * 1.0 / COUNT(*), 2) AS avg_amount, as_of FROM wire_transfers GROUP BY direction ORDER BY direction"
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.WireDirectionSummaryV2Processor"
    }
  ]
}
```

---

## 12. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|-------------|----------------|----------|
| Tier 2 (DataSourcing -> Transformation -> External writer) | BR-3 (inflated trailer), BR-5 (direct CSV) | W7: CsvFileWriter cannot produce inflated trailer count [CsvFileWriter.cs:64] |
| DataSourcing columns: `["direction", "amount"]` | BR-1 (group by direction), BR-2 (aggregations on amount) | AP4 eliminated: [WireDirectionSummaryWriter.cs:30-41] uses only direction + amount |
| No status filter in DataSourcing or SQL | BR-1: All statuses included | [WireDirectionSummaryWriter.cs:30-41] -- no status check |
| SQL: GROUP BY direction with COUNT/SUM/AVG/ROUND | BR-2: Per-direction aggregations | [WireDirectionSummaryWriter.cs:46-49] |
| SQL: `as_of` from source data | BR-4: as_of from first input row | Single-day runs: all rows share same as_of [DataSourcing.cs:85: ORDER BY as_of] |
| External writes CSV with `.ToString()` join (no RFC 4180) | BR-5: Direct CSV without quoting | [WireDirectionSummaryWriter.cs:100] |
| External stores output in shared state as `"output"` | BR-6: Output DataFrame stored but unused | [WireDirectionSummaryWriter.cs:64] |
| External handles null/empty wire_transfers | BR-7: Empty file with header + trailer | [WireDirectionSummaryWriter.cs:20-23] |
| External creates output directory | BR-8: Directory auto-creation | [WireDirectionSummaryWriter.cs:84-86] |
| Trailer date from `__maxEffectiveDate` with fallback | BR-9: Trailer date with fallback | [WireDirectionSummaryWriter.cs:88-89] |
| Trailer uses input row count | W7: Inflated trailer count | [WireDirectionSummaryWriter.cs:26, 104] |
| Overwrite mode | W9 / BRD Write Mode | [WireDirectionSummaryWriter.cs:91] |
| Columns reduced from 5 to 2 | AP4: Unused columns | [wire_direction_summary.json:10] vs [WireDirectionSummaryWriter.cs:30-41] |
| SQL replaces C# foreach loop | AP6: Row-by-row iteration | [WireDirectionSummaryWriter.cs:30-41] |
| Business logic moved from External to SQL | AP3: Unnecessary External (partial) | V1 External does everything; V2 External does only file I/O |
| Proofmark: strict, header_rows=1, trailer_rows=1 | BRD: No non-deterministic fields + Overwrite mode trailer | All output fields deterministic; single trailer at EOF |
