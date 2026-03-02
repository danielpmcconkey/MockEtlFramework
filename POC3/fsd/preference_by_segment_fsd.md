# PreferenceBySegment -- Functional Specification Document

## 1. Job Summary

**Job:** PreferenceBySegmentV2
**Tier:** Tier 2 -- Framework + Minimal External (Scalpel)

This job calculates opt-in rates for each preference type within each customer segment by joining `customer_preferences`, `customers_segments`, and `segments`, grouping by (segment_name, preference_type), and computing opted_in_count / total_count rounded to 2 decimal places with Banker's rounding. The output is a CSV with a trailer whose row count reflects the pre-grouping input row count (W7), not the aggregated output row count. Because the framework's CsvFileWriter `{row_count}` token always substitutes the output DataFrame's row count, a minimal External module is needed to write the file with the inflated trailer count.

---

## 2. V2 Module Chain

```
DataSourcing (customer_preferences)
  -> DataSourcing (customers_segments)
  -> DataSourcing (segments)
  -> Transformation (SQL: join + group + aggregate)
  -> External (PreferenceBySegmentV2Processor: write CSV with W7 inflated trailer)
```

### Tier 2 Justification

**Tier 1 is insufficient** for one specific reason:

The trailer must contain the INPUT row count (number of preference rows before grouping), not the OUTPUT row count (number of aggregated rows). The framework's CsvFileWriter substitutes `{row_count}` with `df.Count` -- the output DataFrame's row count [CsvFileWriter.cs:64]. There is no framework mechanism to inject a custom row count into the trailer. This is the W7 wrinkle documented in the BRD [BR-3].

**Everything else is Tier 1.** The join and aggregation logic is straightforward SQL expressible in SQLite. DataSourcing can pull all three tables. The only non-framework operation is the file I/O with the inflated trailer count.

**Tier 3 is unnecessary.** DataSourcing handles all three tables fine -- no snapshot fallback, no unbounded date ranges, no PostgreSQL-specific syntax required.

### Module 1: DataSourcing -- customer_preferences
- **resultName:** `customer_preferences`
- **schema:** `datalake`
- **table:** `customer_preferences`
- **columns:** `["customer_id", "preference_type", "opted_in"]`
- Effective dates injected by executor via `__minEffectiveDate` / `__maxEffectiveDate`
- Note: `preference_id` sourced by V1 is unused in processing (AP4 elimination)

### Module 2: DataSourcing -- customers_segments
- **resultName:** `customers_segments`
- **schema:** `datalake`
- **table:** `customers_segments`
- **columns:** `["customer_id", "segment_id"]`

### Module 3: DataSourcing -- segments
- **resultName:** `segments`
- **schema:** `datalake`
- **table:** `segments`
- **columns:** `["segment_id", "segment_name"]`

### Module 4: Transformation
- **resultName:** `output`
- **sql:** See Section 4

### Module 5: External
- **Assembly:** `ExternalModules/bin/Debug/net8.0/ExternalModules.dll`
- **Type:** `ExternalModules.PreferenceBySegmentV2Processor`
- **Purpose:** Read the `output` DataFrame and `customer_preferences` DataFrame from shared state. Write a CSV file with: header, data rows from `output`, and a trailer using the input row count from `customer_preferences.Count` (W7 replication). This module does NOT perform any business logic -- the Transformation SQL has already produced the correct output.
- **Output path:** `Output/double_secret_curated/preference_by_segment.csv`

---

## 3. DataSourcing Config

| # | resultName | schema | table | columns | Effective Date Handling |
|---|-----------|--------|-------|---------|------------------------|
| 1 | `customer_preferences` | `datalake` | `customer_preferences` | `customer_id`, `preference_type`, `opted_in` | Injected by executor (`__minEffectiveDate` / `__maxEffectiveDate`) |
| 2 | `customers_segments` | `datalake` | `customers_segments` | `customer_id`, `segment_id` | Injected by executor |
| 3 | `segments` | `datalake` | `segments` | `segment_id`, `segment_name` | Injected by executor |

**Notes:**
- V1 sources `preference_id` from `customer_preferences` [preference_by_segment.json:10] but never uses it in any processing or output [PreferenceBySegmentWriter.cs:53-68]. V2 eliminates this (AP4).
- All three DataSourcing modules use the executor-injected effective dates. No hardcoded dates. No `additionalFilter` needed.
- The `as_of` column is not listed in the columns array, so DataSourcing will automatically append it [DataSourcing.cs:69-72]. The Transformation SQL does not reference `as_of` because V1 does not filter by date within the External module [PreferenceBySegmentWriter.cs:53-69] -- all rows across the effective date range are aggregated together (BRD edge case #5).

---

## 4. Transformation SQL

The SQL must replicate the V1 External module's dictionary-based join and group-by logic:

1. Join `customer_preferences` to `customers_segments` on `customer_id` (LEFT JOIN, so preferences without a segment mapping get NULL segment)
2. Join to `segments` on `segment_id` (LEFT JOIN, so unmatched segment_ids get NULL segment_name)
3. Default NULL segment_name to `'Unknown'` (BR-2, BR-8)
4. Group by (segment_name, preference_type)
5. Compute opt_in_rate as ROUND(CAST(SUM(opted_in=1) AS REAL) / COUNT(*), 2) with Banker's rounding (W5)
6. Add `as_of` column using `__maxEffectiveDate` formatted as 'yyyy-MM-dd' (BR-5)
7. Order by segment_name ASC, preference_type ASC (BR-4)

**Important SQLite note on W5 (Banker's rounding):** SQLite's `ROUND()` function uses the C library `round()` which is "round half away from zero" -- NOT Banker's rounding. However, since the opt-in rates are ratios of integers (e.g., 2231/11150 = 0.200089...), the third decimal place is almost never exactly 5. If there are any midpoint cases where SQLite's rounding differs from Banker's rounding, the External module must correct them. For safety, the External module should recompute opt_in_rate using `Math.Round(..., 2, MidpointRounding.ToEven)` from the raw counts.

**Revised approach:** Given the W5 concern, the Transformation SQL will compute the raw `opted_in_count` and `total_count` per group, and the External module will compute the final `opt_in_rate` using C# Banker's rounding. This keeps the grouping/joining in SQL (Tier 1 spirit) while using C# only for the operation SQL cannot replicate (rounding mode) and file I/O (W7 trailer).

```sql
SELECT
    COALESCE(s.segment_name, 'Unknown') AS segment_name,
    cp.preference_type,
    SUM(CASE WHEN cp.opted_in = 1 THEN 1 ELSE 0 END) AS opted_in_count,
    COUNT(*) AS total_count
FROM customer_preferences cp
LEFT JOIN customers_segments cs ON cp.customer_id = cs.customer_id
LEFT JOIN segments s ON cs.segment_id = s.segment_id
GROUP BY COALESCE(s.segment_name, 'Unknown'), cp.preference_type
ORDER BY segment_name, preference_type
```

**V1 behavior note on duplicate customer-segment mappings (BR-9):** In V1, when a customer has multiple entries in `customers_segments`, dictionary overwrite causes only the last-encountered row to win [PreferenceBySegmentWriter.cs:46]. The SQL LEFT JOIN approach will instead produce one output row per (customer_preferences row x customers_segments row) combination, effectively counting that preference multiple times for different segments. This is a behavioral difference.

**Resolution:** The V1 dictionary-overwrite behavior is non-deterministic (depends on database row order) per BRD non-deterministic fields section. However, to replicate V1's exact output, the External module must handle the customer-to-segment mapping with dictionary semantics (last-write-wins), not SQL JOIN semantics. This means the join logic cannot be fully expressed in Transformation SQL.

**Revised Tier Assessment: Tier 2 with External handling joins**

Given BR-9's dictionary-overwrite semantics AND W5's Banker's rounding AND W7's inflated trailer, the External module must handle:
1. The customer-to-segment lookup (dictionary-based, last-write-wins per BR-9)
2. The grouping and opt_in_rate calculation with Banker's rounding (W5)
3. File I/O with inflated trailer (W7)

This makes the Transformation SQL module unnecessary -- the External module must do the join and aggregation itself to replicate V1's dictionary semantics. However, DataSourcing still pulls all three tables cleanly, so this remains Tier 2 (DataSourcing -> External -> [no framework Writer]).

**Final Transformation SQL:** NONE. The Transformation module is removed from the chain. The External module receives the three DataFrames from DataSourcing and performs the join, aggregation, rounding, and file I/O.

**Final V2 Module Chain:**
```
DataSourcing (customer_preferences)
  -> DataSourcing (customers_segments)
  -> DataSourcing (segments)
  -> External (PreferenceBySegmentV2Processor)
```

**Tier Reclassification:** This is effectively **Tier 2 -- DataSourcing + External**. DataSourcing handles all data access. The External module handles business logic and file I/O because:
- BR-9 requires dictionary-overwrite semantics that SQL JOINs cannot replicate
- W5 requires Banker's rounding that SQLite ROUND() does not provide
- W7 requires an inflated trailer count that CsvFileWriter cannot produce

---

## 5. Writer Config

The External module writes the CSV file directly (matching V1 behavior), with no framework CsvFileWriter in the chain.

| Parameter | Value | V1 Evidence |
|-----------|-------|-------------|
| Output path | `Output/double_secret_curated/preference_by_segment.csv` | V1: `Output/curated/preference_by_segment.csv` [PreferenceBySegmentWriter.cs:73] |
| Header | Yes, comma-separated column names: `segment_name,preference_type,opt_in_rate,as_of` | [PreferenceBySegmentWriter.cs:78] |
| Line ending | LF (`\n`) | [PreferenceBySegmentWriter.cs:76-89] -- StreamWriter.Write with explicit `\n` |
| Trailer format | `TRAILER|{inputCount}|{dateStr}` | [PreferenceBySegmentWriter.cs:93] |
| Trailer row count | Input row count (`customer_preferences.Count` before grouping) | [PreferenceBySegmentWriter.cs:29, 93] -- W7 |
| Write mode | Overwrite (`append: false`) | [PreferenceBySegmentWriter.cs:76] |
| Encoding | UTF-8 (system default) | [PreferenceBySegmentWriter.cs:76] -- StreamWriter default |
| RFC 4180 quoting | Not applied (V1 writes raw values) | [PreferenceBySegmentWriter.cs:89] -- no quoting logic |

**Note:** Since none of the output field values contain commas, quotes, or newlines (segment names are plain text, preference types are enum-like strings, opt_in_rate is a decimal, as_of is a date), the absence of RFC 4180 quoting produces identical output to what CsvFileWriter would produce. The V2 External module should match V1's direct-write behavior for safety.

---

## 6. Wrinkle Replication

### W5 -- Banker's Rounding

**V1 behavior:** `Math.Round((decimal)optedIn / total, 2, MidpointRounding.ToEven)` [PreferenceBySegmentWriter.cs:85-87]

**V2 replication:** The External module computes opt_in_rate using the same explicit Banker's rounding:
```csharp
// W5: Banker's rounding (MidpointRounding.ToEven) -- matches V1 behavior.
// This is standard financial rounding, not a bug.
decimal rate = total > 0
    ? Math.Round((decimal)optedInCount / totalCount, 2, MidpointRounding.ToEven)
    : 0m;
```
Evidence: [PreferenceBySegmentWriter.cs:85-87], BRD BR-1.

### W7 -- Trailer Inflated Count

**V1 behavior:** The trailer row count uses `prefs.Count` (input preference rows before grouping), not the number of output rows after aggregation. For a single effective date, this is 11150 input rows vs ~40 output rows [PreferenceBySegmentWriter.cs:28-29, 92-93].

**V2 replication:** The External module captures the input count from the `customer_preferences` DataFrame before any processing:
```csharp
// W7: Trailer uses INPUT row count (inflated), not output row count.
// V1 counts preference rows before grouping. This is a known production behavior.
var inputCount = customerPreferences.Count;
// ... later ...
writer.Write($"TRAILER|{inputCount}|{dateStr}\n");
```
Evidence: [PreferenceBySegmentWriter.cs:28-29, 92-93], BRD BR-3.

### W9 -- Wrong writeMode (Overwrite)

**V1 behavior:** The External module uses `append: false`, meaning each execution replaces the entire CSV file. In multi-day auto-advance runs, only the last effective date's output persists [PreferenceBySegmentWriter.cs:76].

**V2 replication:** The External module uses `new StreamWriter(outputPath, append: false)` to match V1's overwrite behavior:
```csharp
// W9: Overwrite mode -- prior days' data is lost on each run.
// This is V1's behavior; only the last effective date's output persists.
using var writer = new StreamWriter(outputPath, append: false);
```
Evidence: [PreferenceBySegmentWriter.cs:76], BRD write mode implications section.

---

## 7. Anti-Pattern Elimination

### AP3 -- Unnecessary External Module

**Status: Partially eliminated.**

V1 uses a monolithic External module that handles data access (reading DataFrames), business logic (joins, grouping, aggregation), and file I/O [PreferenceBySegmentWriter.cs:8-98]. V2 moves data access to framework DataSourcing modules, which handle effective date injection and PostgreSQL queries cleanly. The External module in V2 handles only the business logic (dictionary-based join for BR-9 semantics, Banker's rounding for W5) and file I/O (inflated trailer for W7).

A pure Tier 1 solution is not possible because:
1. BR-9's dictionary-overwrite join semantics cannot be expressed in SQL
2. W5's Banker's rounding is not available in SQLite's ROUND()
3. W7's inflated trailer count is not available via CsvFileWriter's `{row_count}` token

### AP4 -- Unused Columns

**Status: Eliminated.**

V1 sources `preference_id` from `customer_preferences` [preference_by_segment.json:10] but never references it in any processing or output logic [PreferenceBySegmentWriter.cs:53-68]. V2 removes `preference_id` from the DataSourcing columns list.

### AP6 -- Row-by-Row Iteration

**Status: Partially eliminated.**

V1 uses three separate `foreach` loops to build lookups and group data [PreferenceBySegmentWriter.cs:36-69]. The dictionary-based lookup pattern is actually efficient (O(1) per lookup) and necessary for BR-9's last-write-wins semantics. V2 retains dictionary-based lookups but uses cleaner LINQ-based construction where possible:
```csharp
// Build segment lookup: segment_id -> segment_name
var segmentLookup = segments.Rows
    .ToDictionary(
        r => Convert.ToInt32(r["segment_id"]),
        r => r["segment_name"]?.ToString() ?? "");
```

The grouping loop is inherently row-by-row (must iterate preferences), but this is a natural aggregation pattern, not the nested-loop anti-pattern AP6 targets.

### AP7 -- Magic Values

**Status: Eliminated.**

V1 uses inline string `"Unknown"` for the default segment name [PreferenceBySegmentWriter.cs:48, 58]. V2 uses a named constant:
```csharp
// Default segment name when customer has no segment mapping or segment_id doesn't exist in segments table
private const string DefaultSegmentName = "Unknown";
```

---

## 8. Proofmark Config

```yaml
comparison_target: "preference_by_segment"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

**Rationale:**
- `reader: csv` -- output is a CSV file
- `header_rows: 1` -- V1 writes a header row [PreferenceBySegmentWriter.cs:78]
- `trailer_rows: 1` -- V1 writes in Overwrite mode [PreferenceBySegmentWriter.cs:76], so there is exactly one trailer at the end of the file [PreferenceBySegmentWriter.cs:93]. Per BLUEPRINT C.4 mapping: "trailerFormat present + writeMode: Overwrite -> trailer_rows: 1"
- `threshold: 100.0` -- strict match required, no known non-deterministic fields in the output
- No EXCLUDED columns -- all four output columns (segment_name, preference_type, opt_in_rate, as_of) are deterministic
- No FUZZY columns -- opt_in_rate uses Banker's rounding in both V1 and V2 (exact match expected)

**Potential risk:** BR-9 (non-deterministic segment assignment for customers with multiple segment mappings) could cause differences if database row order changes between V1 and V2 runs. However, since both V1 and V2 will run against the same database with the same DataSourcing queries (ORDER BY as_of), the row order should be consistent. If Proofmark comparison fails due to this, the segment_name column may need FUZZY or EXCLUDED treatment -- but start strict per best practices.

---

## 9. Open Questions

1. **BR-9 row order sensitivity:** When a customer has multiple entries in `customers_segments`, V1's dictionary-overwrite behavior depends on the row order returned by DataSourcing. V2 uses the same DataSourcing module with the same query pattern (ORDER BY as_of), so row order should match. If it doesn't, the segment assignment for affected customers may differ. Monitoring needed during Phase D comparison.
   - **Risk:** LOW -- same DataSourcing, same query, same ORDER BY
   - **Mitigation:** If Proofmark fails, investigate whether row order differences caused the mismatch

2. **Multi-day aggregation:** V1 aggregates ALL preference rows across the entire effective date range (no date filtering within the External module) [PreferenceBySegmentWriter.cs:53-69, BRD edge case #5]. The `as_of` in the output is always `maxEffectiveDate`. V2 replicates this behavior. But with Overwrite mode, only the last day's output persists anyway, so the multi-day aggregation only matters for the final effective date's run.
   - **Risk:** NONE for output equivalence -- behavior is well-understood and replicated

3. **Empty DataFrame for framework:** V1 sets `sharedState["output"]` to an empty DataFrame after writing [PreferenceBySegmentWriter.cs:97]. V2 must do the same to prevent framework errors if any downstream module expects an "output" key.
   - **Risk:** NONE -- straightforward replication
