# OverdraftAmountDistribution -- Functional Specification Document

## 1. Job Summary

The `OverdraftAmountDistributionV2` job buckets overdraft events from `datalake.overdraft_events` into five predefined amount ranges (0-50, 50-100, 100-250, 250-500, 500+), computes the event count and total overdraft amount per bucket, and writes the results to a CSV file with a header and a trailer line. The trailer line reports the **input** row count (total overdraft events before bucketing), not the output row count (number of non-empty buckets). Empty buckets are excluded from output. The job uses Overwrite mode, so multi-day auto-advance runs retain only the final effective date's output. V2 moves all bucketing and aggregation logic into SQL (Transformation module), reducing the External module to a minimal I/O adapter responsible solely for writing the CSV with the inflated trailer that the framework's CsvFileWriter cannot produce.

---

## 2. V2 Module Chain

**Tier: 2 -- Framework + Minimal External (SCALPEL)**

`DataSourcing -> Transformation (SQL) -> External (minimal I/O)`

### Tier Justification

Tier 1 is insufficient because the framework's CsvFileWriter supports trailer token substitution via `{row_count}`, but that token resolves to `df.Count` -- the **output** DataFrame's row count. For this job, the output DataFrame has at most 5 rows (one per bucket), while the trailer must report the **input** row count (e.g., 139 source overdraft events). There is no built-in CsvFileWriter token for "count of a different DataFrame." The inflated trailer is a V1 output behavior (W7) that must be preserved for byte-identical output.

Tier 2 is appropriate: DataSourcing pulls the data, Transformation (SQL) performs all bucketing and aggregation logic, and a minimal External module handles only CSV file I/O with the inflated trailer. All business logic lives in SQL; the External is a thin I/O adapter.

| Step | Module Type | Config Key | Purpose |
|------|-------------|------------|---------|
| 1 | DataSourcing | `overdraft_events` | Source overdraft event records from `datalake.overdraft_events` for the effective date range |
| 2 | Transformation | `output` | Bucket amounts into 5 ranges via CASE/WHEN, GROUP BY, exclude empty buckets, attach as_of from first source row |
| 3 | External | -- | Write `output` DataFrame to CSV with header, data rows, and inflated trailer using `overdraft_events.Count` |

---

## 3. DataSourcing Config

**Module 1: DataSourcing -- overdraft_events**

| Property | Value |
|----------|-------|
| resultName | `overdraft_events` |
| schema | `datalake` |
| table | `overdraft_events` |
| columns | `["overdraft_amount"]` |

- **Effective dates:** Not specified in config; injected at runtime via `__minEffectiveDate` / `__maxEffectiveDate` shared state keys. Evidence: [overdraft_amount_distribution.json:4-11] V1 config has no date fields; [BRD BR-8].
- **`as_of` column:** NOT included in the columns list. The DataSourcing module auto-appends `as_of` as a `DateOnly` value (see `DataSourcing.cs:69,105-108`). This matches V1 behavior exactly -- V1's column list also omits `as_of`. Preserving this behavior ensures the `as_of` value type in the DataFrame is `DateOnly`, which is critical for output format consistency with V1 (see Open Questions, OQ-1).
- **Eliminated columns (AP4):** V1 sources `overdraft_id`, `account_id`, `customer_id`, `fee_amount`, `fee_waived`, `event_timestamp` -- none of which are used in the bucketing or aggregation logic. V2 sources only `overdraft_amount`. Evidence: [OverdraftAmountDistributionProcessor.cs:57] only `overdraft_amount` is read for bucketing; [OverdraftAmountDistributionProcessor.cs:43] `as_of` is read from the first row (auto-appended by DataSourcing); [BRD AP4, BR-1].

---

## 4. Transformation SQL

**Module 2: Transformation -- output**

```sql
SELECT
    bucket.amount_bucket,
    bucket.event_count,
    bucket.total_amount,
    src_as_of.as_of
FROM (
    -- BR-1: Bucket boundaries using <= logic, matching V1 exactly
    -- AP7: Boundaries (50, 100, 250, 500) are overdraft amount range thresholds
    SELECT
        CASE
            WHEN overdraft_amount <= 50 THEN '0-50'
            WHEN overdraft_amount <= 100 THEN '50-100'
            WHEN overdraft_amount <= 250 THEN '100-250'
            WHEN overdraft_amount <= 500 THEN '250-500'
            ELSE '500+'
        END AS amount_bucket,
        COUNT(*) AS event_count,
        SUM(overdraft_amount) AS total_amount
    FROM overdraft_events
    GROUP BY
        CASE
            WHEN overdraft_amount <= 50 THEN '0-50'
            WHEN overdraft_amount <= 100 THEN '50-100'
            WHEN overdraft_amount <= 250 THEN '100-250'
            WHEN overdraft_amount <= 500 THEN '250-500'
            ELSE '500+'
        END
    -- BR-2: Empty buckets excluded from output
    HAVING COUNT(*) > 0
) bucket
-- BR-5: as_of taken from the first source row
CROSS JOIN (
    SELECT as_of FROM overdraft_events LIMIT 1
) src_as_of
-- EC-5: Bucket ordering matches V1 dictionary insertion order
ORDER BY
    CASE bucket.amount_bucket
        WHEN '0-50' THEN 1
        WHEN '50-100' THEN 2
        WHEN '100-250' THEN 3
        WHEN '250-500' THEN 4
        WHEN '500+' THEN 5
    END
```

### SQL Design Notes

1. **Bucket boundaries (BR-1):** The `CASE` expression uses `<=` logic matching V1's `if (amount <= 50m)` chain exactly. Evidence: [OverdraftAmountDistributionProcessor.cs:60-64].

2. **Empty bucket exclusion (BR-2):** `HAVING COUNT(*) > 0` replicates V1's `if (kvp.Value.count == 0) continue;` guard. Evidence: [OverdraftAmountDistributionProcessor.cs:80-81].

3. **Decimal precision (BR-3):** V1 uses `decimal` for `total_amount` accumulation. SQLite stores numeric values as REAL (double). The DataSourcing module reads `overdraft_amount` from PostgreSQL as `numeric` (Npgsql maps to `decimal`). When the Transformation module registers this into SQLite via `ToSqliteValue`, decimal values pass through as-is (the `_ => value` fallback case in `Transformation.cs:112`). SQLite's `SUM()` operates on REAL values. For simple addition of monetary amounts, REAL accumulation should produce identical string output. If Proofmark detects epsilon differences, a fuzzy tolerance on `total_amount` can be added. See Open Questions, OQ-2.

4. **as_of from first row (BR-5):** `CROSS JOIN (SELECT as_of FROM overdraft_events LIMIT 1)` retrieves the `as_of` value from the first row of the source data. DataSourcing orders results by `as_of` (`DataSourcing.cs:85`), so `LIMIT 1` returns the minimum `as_of` date. This matches V1's `overdraftEvents.Rows[0]["as_of"]`. The `as_of` value stored in SQLite is a `"yyyy-MM-dd"` string (via `ToSqliteValue`'s `DateOnly` conversion at `Transformation.cs:110`). Evidence: [OverdraftAmountDistributionProcessor.cs:43].

5. **Bucket ordering (EC-5):** `ORDER BY CASE` enforces the deterministic order `0-50, 50-100, 100-250, 250-500, 500+`, matching V1's dictionary insertion order. Evidence: [OverdraftAmountDistributionProcessor.cs:47-53].

6. **Set-based operation (AP6):** The entire bucketing and aggregation is a single SQL `GROUP BY`, replacing V1's `foreach` row iteration. Evidence: [OverdraftAmountDistributionProcessor.cs:55-68].

---

## 5. Writer Config

**Writer type:** Direct file I/O via External module (matches V1 writer type).

V1 uses a StreamWriter in the External module, bypassing CsvFileWriter entirely. V2 retains this approach because the CsvFileWriter's `{row_count}` token cannot produce the inflated input row count required by W7. Evidence: [BRD BR-6]; [CsvFileWriter.cs:64] `{row_count}` resolves to `df.Count`.

| Parameter | V1 Value | V2 Value | Match? |
|-----------|----------|----------|--------|
| Output mechanism | `StreamWriter` in External | `StreamWriter` in External | YES |
| Output path | `Output/curated/overdraft_amount_distribution.csv` | `Output/double_secret_curated/overdraft_amount_distribution.csv` | Path change per V2 convention |
| Header | Yes (`amount_bucket,event_count,total_amount,as_of`) | Yes (same columns) | YES |
| Line ending | `Environment.NewLine` (StreamWriter default) | `Environment.NewLine` (StreamWriter default) | YES |
| Trailer format | `TRAILER\|{inputRowCount}\|{maxDate:yyyy-MM-dd}` | `TRAILER\|{inputRowCount}\|{maxDate:yyyy-MM-dd}` | YES |
| Write mode | Overwrite (`append: false`) | Overwrite (`append: false`) | YES |
| RFC 4180 quoting | No (simple string interpolation) | No (simple string interpolation) | YES |

---

## 6. Wrinkle Replication

### W7 -- Trailer Inflated Count

**V1 behavior:** The trailer line uses the INPUT row count (total overdraft events before bucketing), not the OUTPUT row count (number of non-empty buckets). For example, 139 input events bucketed into 5 groups produce `TRAILER|139|2024-10-15`, not `TRAILER|5|2024-10-15`. Evidence: [OverdraftAmountDistributionProcessor.cs:35,88] `int inputRowCount = overdraftEvents?.Count ?? 0;` used in trailer.

**V2 replication:** The External module reads `overdraft_events.Count` from shared state (the source DataFrame, still present after the Transformation step) to obtain the input row count. The Transformation module adds the `output` DataFrame to shared state but does NOT remove `overdraft_events` -- both coexist. The trailer is written as `TRAILER|{inputRowCount}|{maxDate:yyyy-MM-dd}` using this inflated count. A comment in the External module documents: `// W7: Trailer uses INPUT row count (inflated), not output bucket count. V1 behavior replicated for output equivalence.`

CsvFileWriter cannot replicate this because its only row-count token (`{row_count}`) resolves to the output DataFrame's count. This is the justification for retaining the External module (Tier 2 instead of Tier 1).

### W9 -- Wrong writeMode (Overwrite)

**V1 behavior:** StreamWriter opens with `append: false`, overwriting the file on each execution. During multi-day auto-advance, only the final effective date's output survives. Evidence: [OverdraftAmountDistributionProcessor.cs:75] `new StreamWriter(outputPath, false)`.

**V2 replication:** The External module uses `new StreamWriter(outputPath, false)` identically. A comment documents: `// W9: V1 uses Overwrite -- prior days' data is lost on each run. Replicated for output equivalence.`

---

## 7. Anti-Pattern Elimination

### AP3 -- Unnecessary External Module

**V1 problem:** V1 uses a C# External module for ALL logic: bucketing, aggregation, file I/O. The bucketing and aggregation are textbook SQL operations (CASE/WHEN + GROUP BY + SUM/COUNT). Evidence: [OverdraftAmountDistributionProcessor.cs:46-68] foreach loop with manual dictionary accumulation.

**V2 elimination:** Partially eliminated. All bucketing and aggregation logic moved to SQL Transformation (Module 2). The External module is reduced to a minimal I/O adapter that reads the already-bucketed `output` DataFrame and writes the CSV. Full elimination is blocked by W7 -- the CsvFileWriter cannot produce the inflated trailer count.

### AP4 -- Unused Columns

**V1 problem:** V1 DataSourcing sources 7 columns: `overdraft_id`, `account_id`, `customer_id`, `overdraft_amount`, `fee_amount`, `fee_waived`, `event_timestamp`. Only `overdraft_amount` is used for bucketing/aggregation. `as_of` is used but auto-appended by the framework. Evidence: [OverdraftAmountDistributionProcessor.cs:57] only `overdraft_amount` accessed in the loop; [OverdraftAmountDistributionProcessor.cs:43] only `as_of` accessed outside the loop.

**V2 elimination:** V2 DataSourcing sources only `["overdraft_amount"]`. The `as_of` column is auto-appended by the DataSourcing module. Six unused columns removed.

### AP6 -- Row-by-Row Iteration

**V1 problem:** V1 uses a `foreach` loop over every overdraft event row to assign buckets and accumulate counts/totals into a dictionary. Evidence: [OverdraftAmountDistributionProcessor.cs:55-68].

**V2 elimination:** Replaced by SQL `CASE/GROUP BY` in the Transformation module. Set-based aggregation instead of procedural row-by-row processing.

### AP7 -- Magic Values

**V1 problem:** Bucket boundaries (50, 100, 250, 500) are hardcoded without explanation. Evidence: [OverdraftAmountDistributionProcessor.cs:60-64] bare numeric literals in `if` chain.

**V2 elimination:** In the SQL, boundaries are documented with inline comments explaining they are overdraft amount range thresholds. In the External module, string constants are used for the trailer format and DataFrame names. The boundary VALUES remain identical for output equivalence.

---

## 8. Proofmark Config

```yaml
comparison_target: "overdraft_amount_distribution"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

### Design Rationale

- **Reader:** `csv` -- V1 output is a CSV file written by the External module's StreamWriter. Evidence: [BRD BR-6].
- **header_rows:** `1` -- V1 writes a header row: `amount_bucket,event_count,total_amount,as_of`. Evidence: [OverdraftAmountDistributionProcessor.cs:77].
- **trailer_rows:** `1` -- V1 uses Overwrite mode (`append: false`), so there is exactly one trailer row at the end of the file per run. Evidence: [OverdraftAmountDistributionProcessor.cs:75,88].
- **Excluded columns:** None. All output columns are deterministic. The BRD states: "None identified. Output is deterministic given the same source data and effective date." Evidence: [BRD Non-Deterministic Fields].
- **Fuzzy columns:** None initially. Starting strict per best practices. The `total_amount` column is computed as `SUM(overdraft_amount)` in SQLite (REAL/double) vs. V1's `decimal` accumulation. If Proofmark detects epsilon differences, a fuzzy tolerance on `total_amount` will be added during Phase D with evidence. See Open Questions, OQ-2.
- **Threshold:** `100.0` -- exact match required for all rows.

---

## 9. Open Questions

**OQ-1: `as_of` format -- DateOnly.ToString() vs SQLite TEXT**

V1 writes the `as_of` column by calling `DateOnly.ToString()` (no format string) on the value from the first source row ([OverdraftAmountDistributionProcessor.cs:43]). `DateOnly.ToString()` uses the current culture's short date pattern -- in `InvariantCulture` this produces `"MM/dd/yyyy"` (e.g., `"10/01/2024"`), but in `en-US` it produces `"M/d/yyyy"` (e.g., `"10/1/2024"`). The V2 SQL pipeline produces `as_of` as `"yyyy-MM-dd"` (e.g., `"2024-10-01"`) because the Transformation module converts `DateOnly` to `"yyyy-MM-dd"` format when inserting into SQLite ([Transformation.cs:110]).

**Resolution path:** The V2 External module should NOT read `as_of` from the `output` DataFrame (which contains the SQLite-formatted string). Instead, it should read the `as_of` value directly from the `overdraft_events` DataFrame (which contains the original `DateOnly` object) and call `.ToString()` on it, exactly as V1 does. This ensures the format matches V1 regardless of culture settings. If Proofmark still detects a mismatch, the root cause is culture configuration and can be addressed in Phase D.

**Impact:** HIGH -- format mismatch in the `as_of` column would cause Proofmark failure on every data row.

**OQ-2: SQLite REAL vs C# decimal for total_amount**

V1 accumulates `total_amount` using C# `decimal` ([OverdraftAmountDistributionProcessor.cs:46,67]). V2's SQL `SUM(overdraft_amount)` operates on SQLite REAL (double-precision float). For simple addition of monetary amounts with 2 decimal places, the results should be identical. However, if the source data contains values with many decimal places or values that lack exact binary representations, epsilon differences could appear.

**Resolution path:** Start strict (no fuzzy tolerance). If Proofmark comparison fails on `total_amount`, add a fuzzy tolerance with evidence. Alternatively, the V2 External module could convert the SQL-produced `total_amount` back to `decimal` before formatting, which would produce the same string representation as V1 in most cases.

**Impact:** LOW to MEDIUM -- depends on specific source data values. Simple 2-decimal-place monetary amounts should produce identical results.

**OQ-3: Line ending -- Environment.NewLine platform dependence**

V1 uses `StreamWriter.WriteLine()` which uses `Environment.NewLine` -- this is `\n` on Linux and `\r\n` on Windows ([BRD EC-2]). V2 will also use `StreamWriter.WriteLine()`, producing the same line endings as V1 on the same platform. As long as both V1 and V2 run on the same OS (Linux in this Docker environment), the line endings will match.

**Resolution path:** Non-issue as long as V1 and V2 are both executed in the same Docker container. If they are executed on different platforms, line endings would differ.

**Impact:** LOW -- same platform assumption is safe for this project.
