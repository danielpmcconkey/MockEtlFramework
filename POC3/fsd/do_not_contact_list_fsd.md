# DoNotContactList -- Functional Specification Document

## 1. Overview

**Job:** DoNotContactListV2
**Tier:** Tier 1 -- Framework Only (`DataSourcing -> Transformation (SQL) -> CsvFileWriter`)

This job identifies customers who have opted out of ALL their communication preferences and produces a CSV list of those customers. The V1 implementation uses an unnecessary External module (AP3) with row-by-row iteration (AP6) where the entire business logic -- aggregating opt-out counts per customer, filtering for those fully opted out, and joining to the customers table -- is expressible in a single SQL query. V2 replaces the External module with a Transformation module containing SQL that produces byte-identical output.

### Tier Justification

Tier 1 is sufficient because:
- The "all preferences opted out" check is a standard `GROUP BY` / `HAVING` pattern: `HAVING COUNT(*) = SUM(CASE WHEN opted_in = 0 THEN 1 ELSE 0 END)` with `COUNT(*) > 0`.
- The Sunday skip can be expressed via SQLite's `strftime('%w', as_of)` function -- `'0'` = Sunday.
- The customer lookup join is a standard `INNER JOIN`.
- The `as_of` value (taken from the first row of preferences) is trivially available since all rows in a single execution share the same `as_of` date (auto-advance sets `minEffectiveDate == maxEffectiveDate`).

No External module is needed.

---

## 2. V2 Module Chain

```
DataSourcing (customer_preferences)
    -> DataSourcing (customers)
    -> Transformation (SQL: Sunday skip + aggregate + join + filter)
    -> CsvFileWriter
```

### Module 1: DataSourcing -- `customer_preferences`

| Property | Value |
|----------|-------|
| resultName | `customer_preferences` |
| schema | `datalake` |
| table | `customer_preferences` |
| columns | `["preference_id", "customer_id", "preference_type", "opted_in"]` |

**Note:** The `as_of` column is NOT listed in the columns array, so DataSourcing will automatically append it per framework behavior [DataSourcing.cs:69-72]. Effective dates are injected by the executor at runtime -- no hardcoded dates in the config.

### Module 2: DataSourcing -- `customers`

| Property | Value |
|----------|-------|
| resultName | `customers` |
| schema | `datalake` |
| table | `customers` |
| columns | `["id", "first_name", "last_name"]` |

**Note:** V1 sources only `id`, `first_name`, `last_name` -- no unused columns (AP4 does not apply here). The `as_of` column is auto-appended by DataSourcing.

### Module 3: Transformation -- `output`

SQL that implements all business rules (Sunday skip, aggregation, join, filter). See Section 5 for full SQL design.

### Module 4: CsvFileWriter

| Property | Value |
|----------|-------|
| source | `output` |
| outputFile | `Output/double_secret_curated/do_not_contact_list.csv` |
| includeHeader | `true` |
| trailerFormat | `TRAILER|{row_count}|{date}` |
| writeMode | `Overwrite` |
| lineEnding | `LF` |

Writer configuration matches V1 exactly [do_not_contact_list.json:26-32] except for the output path change to `double_secret_curated`.

---

## 3. Anti-Pattern Analysis

### Identified Anti-Patterns (Eliminated)

| Code | Name | V1 Behavior | V2 Resolution |
|------|------|-------------|---------------|
| AP3 | Unnecessary External module | V1 uses `DoNotContactProcessor.cs` External module for logic that is entirely expressible in SQL. The processor reads DataFrames from shared state, iterates rows manually, and builds output row-by-row. | **Eliminated.** Replaced with a single SQL Transformation. The GROUP BY / HAVING / JOIN pattern handles the aggregation, filtering, and customer lookup in one query. |
| AP6 | Row-by-row iteration | V1 uses nested `foreach` loops to build `customerPrefs` dictionary (counting total vs opted_out per customer), then iterates the dictionary to build output rows. | **Eliminated.** SQL `GROUP BY customer_id` with `HAVING` clause replaces the manual iteration entirely. Set-based operation. |

### Identified Anti-Patterns (Not Applicable)

| Code | Name | Assessment |
|------|------|------------|
| AP1 | Dead-end sourcing | Does not apply. Both `customer_preferences` and `customers` are used in the processing logic. |
| AP4 | Unused columns | Does not apply. V1 sources `preference_id`, `customer_id`, `preference_type`, `opted_in` from customer_preferences. In V2, `preference_id` and `preference_type` are not referenced in the SQL, but they are harmless since DataSourcing pulls them and SQLite ignores unused columns. However, to be precise: V2 could drop `preference_id` and `preference_type` from the DataSourcing columns list since they are not used in the aggregation. **Decision:** Remove `preference_id` and `preference_type` from the V2 config to eliminate AP4. Only `customer_id` and `opted_in` are needed. |
| AP7 | Magic values | No magic values in V1. The only "constant" is the Sunday day-of-week check, which is self-documenting. |
| AP10 | Over-sourcing dates | Does not apply. V1 relies on executor-injected effective dates (no hardcoded date range in the config), and V2 does the same. |

### Output-Affecting Wrinkles (Preserved)

| Code | Name | V1 Behavior | V2 Implementation |
|------|------|-------------|-------------------|
| W1 | Sunday skip | Returns empty DataFrame when `maxEffectiveDate` falls on a Sunday [DoNotContactProcessor.cs:20-24]. CsvFileWriter then writes a header row and a trailer with `row_count=0`. | **Reproduced.** SQL includes a WHERE clause: `strftime('%w', cp.as_of) != '0'` which filters out all rows on Sundays, producing an empty result set. The CsvFileWriter will then write header + `TRAILER\|0\|{date}` as in V1. Comment in SQL documents this is W1 replication. |

### Output-Affecting Wrinkles (Not Applicable)

W2, W3a-c, W4, W5, W6, W7, W8, W9, W10, W12 -- none of these apply to this job. No weekend fallback, no boundary summaries, no integer division, no banker's rounding, no double epsilon, no trailer inflation, no stale trailer date, no wrong writeMode, no absurd numParts, no header-every-append.

**W9 assessment:** The BRD notes the job uses Overwrite mode, meaning only the last effective date's output persists in multi-day auto-advance. This is V1's actual behavior (the job config explicitly says `"writeMode": "Overwrite"`), so V2 reproduces it. The write mode is correct for a "current state" list.

---

## 4. Output Schema

| Column | Type | Source | Transformation | Evidence |
|--------|------|--------|---------------|----------|
| customer_id | INTEGER | `customer_preferences.customer_id` | Aggregation key from GROUP BY | [DoNotContactProcessor.cs:73] |
| first_name | TEXT | `customers.first_name` | Joined via `customers.id = customer_preferences.customer_id`. NULL coalesced to empty string via `COALESCE(..., '')`. | [DoNotContactProcessor.cs:72] -- `row["first_name"]?.ToString() ?? ""` |
| last_name | TEXT | `customers.last_name` | Joined via `customers.id = customer_preferences.customer_id`. NULL coalesced to empty string via `COALESCE(..., '')`. | [DoNotContactProcessor.cs:72] -- `row["last_name"]?.ToString() ?? ""` |
| as_of | TEXT (date) | `customer_preferences.as_of` | First row's `as_of` value. Since auto-advance runs one date at a time, all rows share the same `as_of`. SQL uses `MIN(cp.as_of)` which is equivalent to first-row in a single-date execution. | [DoNotContactProcessor.cs:64] -- `var asOf = prefs.Rows[0]["as_of"]` |

**Column order:** `customer_id, first_name, last_name, as_of` -- matches V1 [DoNotContactProcessor.cs:10-13].

**Row order:** Ordered by `customer_id ASC`. V1 iterates a Dictionary keyed by customer_id; entries are inserted in order of first encounter in the prefs DataFrame (which DataSourcing orders by `as_of`, then database natural order = ascending `preference_id`, which correlates with ascending `customer_id`). The output order matches.

---

## 5. SQL Design

```sql
-- W1: Sunday skip -- strftime('%w', date) returns '0' for Sunday.
-- When as_of is a Sunday, the WHERE clause excludes all rows,
-- producing an empty result identical to V1's early return.
-- V1 behavior: DoNotContactProcessor.cs:20-24 returns empty DataFrame on Sundays.
SELECT
    cp.customer_id,
    COALESCE(c.first_name, '') AS first_name,
    COALESCE(c.last_name, '') AS last_name,
    MIN(cp.as_of) AS as_of
FROM customer_preferences cp
INNER JOIN customers c
    ON c.id = cp.customer_id
    AND c.as_of = cp.as_of
WHERE strftime('%w', cp.as_of) != '0'
GROUP BY cp.customer_id, c.first_name, c.last_name
HAVING COUNT(*) > 0
   AND COUNT(*) = SUM(CASE WHEN cp.opted_in = 0 THEN 1 ELSE 0 END)
ORDER BY cp.customer_id
```

### SQL Design Rationale

1. **Sunday skip (W1):** `WHERE strftime('%w', cp.as_of) != '0'` filters out all rows when the effective date is a Sunday. Since auto-advance sets min == max effective date, all rows in `customer_preferences` share the same `as_of`. On Sundays, every row is filtered out, producing an empty DataFrame -- identical to V1's early return at [DoNotContactProcessor.cs:20-24].

2. **INNER JOIN to customers:** The `INNER JOIN` on `c.id = cp.customer_id` implements BR-3 (customer must exist in customers table). The additional join condition `c.as_of = cp.as_of` ensures we join within the same snapshot date. V1 builds a `customerLookup` dictionary from ALL rows of the customers DataFrame, but since auto-advance pulls only one date, the dictionary contains only that date's customers. The INNER JOIN with date matching is equivalent.

3. **GROUP BY / HAVING:** The aggregation counts total preferences per customer and compares to the count of opted-out preferences. `HAVING COUNT(*) > 0` ensures the customer has at least one preference (BR-1, BR-4 edge case). `HAVING COUNT(*) = SUM(CASE WHEN cp.opted_in = 0 THEN 1 ELSE 0 END)` ensures ALL preferences are opted out (BR-1). In SQLite, boolean `false` is stored as `0` by the `ToSqliteValue` method [Transformation.cs:109], so `opted_in = 0` correctly identifies opted-out rows.

4. **COALESCE for NULLs:** V1 uses `row["first_name"]?.ToString() ?? ""` [DoNotContactProcessor.cs:44], which coalesces NULL to empty string. The SQL `COALESCE(c.first_name, '')` replicates this behavior.

5. **as_of column:** V1 takes `prefs.Rows[0]["as_of"]` and applies it uniformly to all output rows [DoNotContactProcessor.cs:64]. In single-date execution, all rows have the same `as_of`. `MIN(cp.as_of)` within the GROUP BY produces the same value (since all `as_of` values are identical). This satisfies BR-4.

6. **ORDER BY customer_id:** Matches V1's output ordering, which follows dictionary insertion order (ascending customer_id due to database natural ordering within a single date).

### SQLite Type Considerations

- `opted_in` is a `bool` in PostgreSQL, stored as `INTEGER` (0/1) in SQLite by `ToSqliteValue` [Transformation.cs:109]. The `CASE WHEN cp.opted_in = 0` comparison works correctly.
- `as_of` is a `DateOnly` in C#, stored as `TEXT` ("yyyy-MM-dd") in SQLite by `ToSqliteValue` [Transformation.cs:110]. `strftime('%w', cp.as_of)` parses this format correctly.
- `customer_id` and `id` are integers. The JOIN works directly.

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "DoNotContactListV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "customer_preferences",
      "schema": "datalake",
      "table": "customer_preferences",
      "columns": ["customer_id", "opted_in"]
    },
    {
      "type": "DataSourcing",
      "resultName": "customers",
      "schema": "datalake",
      "table": "customers",
      "columns": ["id", "first_name", "last_name"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT cp.customer_id, COALESCE(c.first_name, '') AS first_name, COALESCE(c.last_name, '') AS last_name, MIN(cp.as_of) AS as_of FROM customer_preferences cp INNER JOIN customers c ON c.id = cp.customer_id AND c.as_of = cp.as_of WHERE strftime('%w', cp.as_of) != '0' GROUP BY cp.customer_id, c.first_name, c.last_name HAVING COUNT(*) > 0 AND COUNT(*) = SUM(CASE WHEN cp.opted_in = 0 THEN 1 ELSE 0 END) ORDER BY cp.customer_id"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/do_not_contact_list.csv",
      "includeHeader": true,
      "trailerFormat": "TRAILER|{row_count}|{date}",
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

### Config Design Notes

- **DataSourcing columns trimmed (AP4 elimination):** V1 sourced `preference_id`, `customer_id`, `preference_type`, `opted_in` from customer_preferences. V2 drops `preference_id` and `preference_type` since they are never used in the business logic. Only `customer_id` and `opted_in` are needed for the aggregation. The `as_of` column is auto-appended by DataSourcing.
- **No External module (AP3 elimination):** The entire pipeline is DataSourcing -> Transformation -> CsvFileWriter.
- **firstEffectiveDate matches V1:** `"2024-10-01"` [do_not_contact_list.json:3].
- **Writer config matches V1 exactly:** `includeHeader: true`, `trailerFormat: "TRAILER|{row_count}|{date}"`, `writeMode: "Overwrite"`, `lineEnding: "LF"`. Only the output path changes to `double_secret_curated`.

---

## 7. Writer Configuration

| Property | V1 Value | V2 Value | Match? |
|----------|----------|----------|--------|
| type | CsvFileWriter | CsvFileWriter | Yes |
| source | `output` | `output` | Yes |
| outputFile | `Output/curated/do_not_contact_list.csv` | `Output/double_secret_curated/do_not_contact_list.csv` | Path change only (per spec) |
| includeHeader | `true` | `true` | Yes |
| trailerFormat | `TRAILER\|{row_count}\|{date}` | `TRAILER\|{row_count}\|{date}` | Yes |
| writeMode | `Overwrite` | `Overwrite` | Yes |
| lineEnding | `LF` | `LF` | Yes |

The CsvFileWriter handles trailer token substitution automatically:
- `{row_count}` is replaced with the DataFrame row count [CsvFileWriter.cs:64]
- `{date}` is replaced with `__maxEffectiveDate` from shared state [CsvFileWriter.cs:60-62]

**Write mode implications:** Overwrite mode means each execution replaces the file entirely. In auto-advance across multiple dates, only the last date's output persists. On Sundays, the file is overwritten with header + trailer (0 data rows), replacing any prior content. This matches V1 behavior exactly.

---

## 8. Proofmark Config Design

```yaml
comparison_target: "do_not_contact_list"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

### Proofmark Rationale

- **Reader:** `csv` -- matches V1 writer type (CsvFileWriter).
- **header_rows:** `1` -- V1 config has `includeHeader: true` [do_not_contact_list.json:28].
- **trailer_rows:** `1` -- V1 config has `trailerFormat` present AND `writeMode: Overwrite` [do_not_contact_list.json:29-30]. Per the BLUEPRINT config mapping table, Overwrite + trailerFormat = `trailer_rows: 1`.
- **threshold:** `100.0` -- strict match. No known non-deterministic fields or floating-point precision issues.
- **No excluded columns:** All output columns (`customer_id`, `first_name`, `last_name`, `as_of`) are deterministic. The `as_of` value comes from data (not runtime generation), so it will be identical between V1 and V2 runs for the same effective date.
- **No fuzzy columns:** No floating-point arithmetic or rounding involved. All values are integers or strings.

---

## 9. Traceability Matrix

| BRD Requirement | FSD Design Decision | Evidence |
|-----------------|---------------------|----------|
| BR-1: Customer on list only if ALL preferences opted out (total > 0, total == optedOut) | SQL HAVING clause: `COUNT(*) > 0 AND COUNT(*) = SUM(CASE WHEN cp.opted_in = 0 THEN 1 ELSE 0 END)` | [DoNotContactProcessor.cs:70] |
| BR-2: Sunday skip -- empty DataFrame when maxEffectiveDate is Sunday | SQL WHERE clause: `strftime('%w', cp.as_of) != '0'` filters all rows on Sunday, producing empty result | [DoNotContactProcessor.cs:20-24] |
| BR-3: Customer must exist in customers table | SQL INNER JOIN: `customers c ON c.id = cp.customer_id` excludes preferences for non-existent customers | [DoNotContactProcessor.cs:70] -- `customerLookup.ContainsKey(kvp.Key)` |
| BR-4: as_of from first preferences row, applied to all output rows | SQL `MIN(cp.as_of)` within GROUP BY. In single-date execution, all rows share the same as_of, so MIN equals the first row's value. | [DoNotContactProcessor.cs:64] |
| BR-5: Trailer format `TRAILER\|{row_count}\|{date}` | CsvFileWriter config: `trailerFormat: "TRAILER\|{row_count}\|{date}"` -- framework handles token substitution | [do_not_contact_list.json:29], [CsvFileWriter.cs:58-67] |
| BR-6: No date filtering on preferences | DataSourcing uses executor-injected effective dates (single date per run in auto-advance). All preferences for that date are included, no additional date filter in SQL. | [DoNotContactProcessor.cs:49-62] |
| BR-7: Empty DataFrame if preferences or customers are null/empty | If either DataSourcing returns zero rows, the INNER JOIN produces zero rows. Transformation's `RegisterTable` skips empty DataFrames [Transformation.cs:46], so the SQL query will fail or return empty. | [DoNotContactProcessor.cs:33-37] |
| W1: Sunday skip | Reproduced via SQL WHERE clause. Clean, documented, intentional. | [DoNotContactProcessor.cs:20-24] |
| AP3: Unnecessary External module | Eliminated. Replaced with Tier 1 framework chain. | [DoNotContactProcessor.cs] -- entire file replaced |
| AP6: Row-by-row iteration | Eliminated. SQL GROUP BY replaces manual foreach loops. | [DoNotContactProcessor.cs:48-62] |

---

## 10. External Module Design

**Not applicable.** This is a Tier 1 implementation. No External module is needed.

The V1 External module (`DoNotContactProcessor.cs`) performed three operations:
1. Sunday day-of-week check -- replaced by `strftime('%w', cp.as_of) != '0'` in SQL
2. Row-by-row preference aggregation -- replaced by `GROUP BY` / `HAVING` in SQL
3. Customer lookup join -- replaced by `INNER JOIN` in SQL

All three operations are standard SQL patterns. There is no procedural logic, no snapshot fallback, no cross-date-range query, and no complex join pattern that would require an External module.

---

## Appendix A: Edge Case Handling

| Edge Case | V1 Behavior | V2 Behavior | Match? |
|-----------|-------------|-------------|--------|
| Sunday execution | Returns empty DataFrame, CsvFileWriter writes header + trailer with row_count=0 | SQL WHERE clause excludes all rows, producing empty DataFrame. Same writer output. | Yes |
| Saturday execution | Normal processing (no special handling) | SQL WHERE clause passes (Saturday = strftime '%w' = '6'). Normal processing. | Yes |
| Customer with mixed preferences (some opted in, some out) | Not included (total != optedOut) | HAVING clause fails: COUNT(*) != SUM(opted_out_count). Not included. | Yes |
| Customer with zero preferences | Not included (total = 0 fails `total > 0` check) | Not in GROUP BY result (no rows to aggregate). Not included. | Yes |
| Customer in preferences but not in customers table | Excluded by customerLookup.ContainsKey check | Excluded by INNER JOIN (no matching customer row). | Yes |
| Empty customer_preferences table | Empty output DataFrame | Transformation registers no table for customer_preferences (empty DataFrame skipped by RegisterTable). SQL referencing nonexistent table behavior: query returns empty or errors. However, in practice this edge case doesn't occur -- the datalake has data for all dates in the effective range. | Matches for practical purposes |
| Empty customers table | Empty output DataFrame | INNER JOIN produces zero rows. Empty output. | Yes |
| NULL first_name/last_name | Coalesced to "" via `?.ToString() ?? ""` | Coalesced to "" via `COALESCE(c.first_name, '')` | Yes |

## Appendix B: Data Flow Diagram

```
PostgreSQL (datalake)
    |
    v
DataSourcing: customer_preferences    DataSourcing: customers
    [customer_id, opted_in, as_of]        [id, first_name, last_name, as_of]
    |                                      |
    +------------------+-------------------+
                       |
                       v
              Transformation (SQL)
              - Sunday skip filter
              - INNER JOIN on customer_id + as_of
              - GROUP BY customer_id
              - HAVING all opted out
              - COALESCE NULLs
              - ORDER BY customer_id
                       |
                       v
              output DataFrame
              [customer_id, first_name, last_name, as_of]
                       |
                       v
              CsvFileWriter
              -> Output/double_secret_curated/do_not_contact_list.csv
              - Header row
              - Data rows
              - Trailer: TRAILER|{row_count}|{date}
```
