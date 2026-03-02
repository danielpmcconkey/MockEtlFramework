# WeekendTransactionPattern -- Functional Specification Document

## 1. Job Summary

This job classifies each effective date's transactions as "Weekday" or "Weekend" based on the day of week, producing counts, totals, and averages for each category. On Sundays, it additionally emits two weekly summary rows (`WEEKLY_TOTAL_Weekday` and `WEEKLY_TOTAL_Weekend`) aggregating the full Monday-through-Sunday week. The V1 implementation uses an unnecessary External module (AP3) and over-sources the entire Q4 2024 date range on every run (AP10). The V2 replaces the V1 External module with a Tier 2 pipeline: DataSourcing (with hardcoded date range -- AP10 retained by necessity), a minimal External module for date injection AND rounding, followed by a SQL Transformation and CsvFileWriter. Output goes to `Output/double_secret_curated/weekend_transaction_pattern.csv`.

## 2. V2 Module Chain

**Tier: 2 (Framework + Minimal External)**

```
DataSourcing ("transactions") -> External (date injection + rounding) -> Transformation ("output") -> CsvFileWriter
```

**Justification:** A pure Tier 1 approach cannot work because:
1. DataSourcing must source 7+ days to support Sunday weekly summaries (AP10 retained).
2. Transformation SQL needs to know which specific date is the "current" effective date, but has no access to `__maxEffectiveDate` from shared state.
3. **Rounding mode divergence (W5):** V1 uses `Math.Round(decimal, 2)` which defaults to `MidpointRounding.ToEven` (banker's rounding). SQLite's `ROUND()` uses round-half-away-from-zero. These produce different results at exact midpoints (e.g., `X.XX5`). Since the External module already exists for date injection, rounding is moved into it to guarantee V1-equivalent output.

The External module handles TWO operations:
1. **Date injection:** Read `__maxEffectiveDate` from shared state, create a single-row `effective_date` DataFrame so the SQL can filter/classify by date.
2. **Post-SQL rounding:** After the Transformation runs, read the `output` DataFrame and apply `Math.Round(decimal, 2, MidpointRounding.ToEven)` to `total_amount` and `avg_amount` columns, replacing SQLite's half-away-from-zero rounding with V1-equivalent banker's rounding.

**Revised module chain (final):**

```
DataSourcing ("transactions") -> External (date injector) -> Transformation ("pre_output") -> External (rounding) -> CsvFileWriter
```

Wait -- the framework executes modules sequentially from the config array. We cannot have two External modules with different behaviors from the same class. And the Transformation must run AFTER the date injection External but BEFORE the rounding External.

**Correct approach:** A single External module that:
1. Injects the `effective_date` DataFrame (for use by a subsequent Transformation), OR
2. Handles everything post-DataSourcing: date injection, SQL-equivalent aggregation in LINQ, and banker's rounding.

Since we cannot split the External module around the Transformation, the cleanest solution is:

```
DataSourcing ("transactions") -> External (date injector) -> Transformation ("pre_output") -> External (rounding fixer)
```

But wait -- the framework config is a flat array of modules executed in sequence. Can we have two External modules? Yes, each is just a module entry. But both would need different `typeName` values.

**Simplest correct approach:** Use the single External module to inject the `effective_date` DataFrame. Let the Transformation do the aggregation with SQLite's `ROUND()`. Then use a SECOND External module to fix the rounding on the output. This requires two separate External module classes.

**Even simpler:** Have the SQL produce the raw aggregations WITHOUT rounding (`SUM(amount)` and `SUM(amount) * 1.0 / COUNT(*)`), and have a post-Transformation External module apply `Math.Round(decimal, 2, MidpointRounding.ToEven)` to `total_amount` and `avg_amount`. This avoids any SQLite rounding mode issues entirely.

**Final architecture:**

| Step | Module | resultName | Purpose |
|------|--------|------------|---------|
| 1 | DataSourcing | `transactions` | Load transactions (amount, as_of) with hardcoded Q4 2024 range |
| 2 | External | (injects `effective_date`) | Inject `__maxEffectiveDate` as single-row DataFrame |
| 3 | Transformation | `pre_output` | SQL: classify, aggregate, produce 2 or 4 rows with UNROUNDED amounts |
| 4 | External | (fixes `pre_output` -> `output`) | Apply `Math.Round(decimal, 2, MidpointRounding.ToEven)` to `total_amount` and `avg_amount`. Store as `output`. |
| 5 | CsvFileWriter | | Write CSV with header and trailer to V2 output path |

## 3. DataSourcing Config

**V2 retains AP10 (hardcoded date range).** V1 hardcodes `minEffectiveDate: "2024-10-01"` and `maxEffectiveDate: "2024-12-31"` [weekend_transaction_pattern.json:11-12]. The V1 External module then filters to `as_of == maxDate` for daily rows and a computed Monday-Sunday range for weekly rows [WeekendTransactionPatternProcessor.cs:37, 77-87].

AP10 is retained because eliminating it would require:
1. Framework modification to support dynamic date computation (forbidden).
2. A more complex External module that queries PostgreSQL directly (reintroduces AP3).

The over-sourcing does not affect output correctness. Documented with comment: `// AP10 retained: framework cannot compute dynamic date offsets for weekly summary range`.

### DataSourcing Module Config

```json
{
  "type": "DataSourcing",
  "resultName": "transactions",
  "schema": "datalake",
  "table": "transactions",
  "columns": ["amount", "as_of"],
  "minEffectiveDate": "2024-10-01",
  "maxEffectiveDate": "2024-12-31"
}
```

**AP4 eliminated:** V1 sources `transaction_id`, `account_id`, `txn_timestamp`, `txn_type`, `amount` [weekend_transaction_pattern.json:10]. Of these, only `amount` and `as_of` are referenced in the External module [WeekendTransactionPatternProcessor.cs:39, 36]. V2 drops all unused columns.

**Note**: DataSourcing automatically appends `as_of` if not in the column list [DataSourcing.cs:69], but since we explicitly include it, it will not be duplicated.

## 4. External Module 1: Date Injector

### Class: `ExternalModules.WeekendTransactionPatternV2DateInjector`

Minimal module that injects `__maxEffectiveDate` into a queryable DataFrame.

```csharp
public class WeekendTransactionPatternV2DateInjector : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var maxDate = (DateOnly)sharedState["__maxEffectiveDate"];
        var dateStr = maxDate.ToString("yyyy-MM-dd");

        var mondayStr = maxDate.DayOfWeek == DayOfWeek.Sunday
            ? maxDate.AddDays(-6).ToString("yyyy-MM-dd")
            : "";  // Not used on non-Sundays

        var effDateRows = new List<Row>
        {
            new Row(new Dictionary<string, object?>
            {
                ["max_date"] = dateStr,
                ["is_sunday"] = maxDate.DayOfWeek == DayOfWeek.Sunday ? 1 : 0,
                ["monday_of_week"] = mondayStr
            })
        };

        sharedState["effective_date"] = new DataFrame(
            effDateRows,
            new List<string> { "max_date", "is_sunday", "monday_of_week" }
        );

        return sharedState;
    }
}
```

## 5. Transformation SQL

The SQL produces raw aggregations WITHOUT rounding. Rounding is handled by the post-Transformation External module using `Math.Round(decimal, 2, MidpointRounding.ToEven)` to match V1's banker's rounding behavior.

```sql
WITH params AS (
    SELECT max_date, is_sunday, monday_of_week FROM effective_date
),
daily_txns AS (
    SELECT t.amount, t.as_of
    FROM transactions t, params p
    WHERE t.as_of = p.max_date
),
daily_agg AS (
    SELECT
        CASE
            WHEN CAST(strftime('%w', as_of) AS INTEGER) IN (0, 6) THEN 'Weekend'
            ELSE 'Weekday'
        END AS day_type,
        COUNT(*) AS txn_count,
        SUM(amount) AS total_amount,
        as_of
    FROM daily_txns
    GROUP BY day_type, as_of
),
daily_output AS (
    SELECT
        'Weekday' AS day_type,
        COALESCE((SELECT txn_count FROM daily_agg WHERE day_type = 'Weekday'), 0) AS txn_count,
        COALESCE((SELECT total_amount FROM daily_agg WHERE day_type = 'Weekday'), 0) AS total_amount,
        CASE
            WHEN COALESCE((SELECT txn_count FROM daily_agg WHERE day_type = 'Weekday'), 0) > 0
            THEN (SELECT total_amount FROM daily_agg WHERE day_type = 'Weekday')
                * 1.0
                / (SELECT txn_count FROM daily_agg WHERE day_type = 'Weekday')
            ELSE 0
        END AS avg_amount,
        (SELECT max_date FROM params) AS as_of
    UNION ALL
    SELECT
        'Weekend' AS day_type,
        COALESCE((SELECT txn_count FROM daily_agg WHERE day_type = 'Weekend'), 0) AS txn_count,
        COALESCE((SELECT total_amount FROM daily_agg WHERE day_type = 'Weekend'), 0) AS total_amount,
        CASE
            WHEN COALESCE((SELECT txn_count FROM daily_agg WHERE day_type = 'Weekend'), 0) > 0
            THEN (SELECT total_amount FROM daily_agg WHERE day_type = 'Weekend')
                * 1.0
                / (SELECT txn_count FROM daily_agg WHERE day_type = 'Weekend')
            ELSE 0
        END AS avg_amount,
        (SELECT max_date FROM params) AS as_of
),
weekly_txns AS (
    SELECT t.amount, t.as_of
    FROM transactions t, params p
    WHERE p.is_sunday = 1
      AND t.as_of >= p.monday_of_week
      AND t.as_of <= p.max_date
),
weekly_agg AS (
    SELECT
        CASE
            WHEN CAST(strftime('%w', as_of) AS INTEGER) IN (0, 6) THEN 'WEEKLY_TOTAL_Weekend'
            ELSE 'WEEKLY_TOTAL_Weekday'
        END AS day_type,
        COUNT(*) AS txn_count,
        SUM(amount) AS total_amount,
        as_of
    FROM weekly_txns
    GROUP BY day_type
),
weekly_output AS (
    SELECT
        'WEEKLY_TOTAL_Weekday' AS day_type,
        COALESCE((SELECT txn_count FROM weekly_agg WHERE day_type = 'WEEKLY_TOTAL_Weekday'), 0) AS txn_count,
        COALESCE((SELECT total_amount FROM weekly_agg WHERE day_type = 'WEEKLY_TOTAL_Weekday'), 0) AS total_amount,
        CASE
            WHEN COALESCE((SELECT txn_count FROM weekly_agg WHERE day_type = 'WEEKLY_TOTAL_Weekday'), 0) > 0
            THEN (SELECT total_amount FROM weekly_agg WHERE day_type = 'WEEKLY_TOTAL_Weekday')
                * 1.0
                / (SELECT txn_count FROM weekly_agg WHERE day_type = 'WEEKLY_TOTAL_Weekday')
            ELSE 0
        END AS avg_amount,
        (SELECT max_date FROM params) AS as_of
    WHERE (SELECT is_sunday FROM params) = 1
    UNION ALL
    SELECT
        'WEEKLY_TOTAL_Weekend' AS day_type,
        COALESCE((SELECT txn_count FROM weekly_agg WHERE day_type = 'WEEKLY_TOTAL_Weekend'), 0) AS txn_count,
        COALESCE((SELECT total_amount FROM weekly_agg WHERE day_type = 'WEEKLY_TOTAL_Weekend'), 0) AS total_amount,
        CASE
            WHEN COALESCE((SELECT txn_count FROM weekly_agg WHERE day_type = 'WEEKLY_TOTAL_Weekend'), 0) > 0
            THEN (SELECT total_amount FROM weekly_agg WHERE day_type = 'WEEKLY_TOTAL_Weekend')
                * 1.0
                / (SELECT txn_count FROM weekly_agg WHERE day_type = 'WEEKLY_TOTAL_Weekend')
            ELSE 0
        END AS avg_amount,
        (SELECT max_date FROM params) AS as_of
    WHERE (SELECT is_sunday FROM params) = 1
)
SELECT day_type, txn_count, total_amount, avg_amount, as_of FROM daily_output
UNION ALL
SELECT day_type, txn_count, total_amount, avg_amount, as_of FROM weekly_output
```

### SQL Design Notes

1. **No `ROUND()` in SQL**: All rounding is deferred to the post-Transformation External module. This eliminates the W5 rounding mode divergence between SQLite's round-half-away-from-zero and C#'s `MidpointRounding.ToEven` (banker's rounding). The SQL produces raw `SUM(amount)` and `SUM(amount) * 1.0 / COUNT(*)` values. The External module applies `Math.Round(decimal, 2, MidpointRounding.ToEven)` to match V1 exactly.

2. **COALESCE patterns for zero-count categories**: Ensures both "Weekday" and "Weekend" rows always appear, even when all transactions fall into one category (BR-4). V1 unconditionally adds both rows [WeekendTransactionPatternProcessor.cs:55-71].

3. **Weekly summary gated by `is_sunday`**: The `WHERE (SELECT is_sunday FROM params) = 1` clause ensures weekly rows only appear on Sundays (BR-6). The External date injector sets `is_sunday = 1` only when `maxDate.DayOfWeek == DayOfWeek.Sunday`.

## 6. External Module 2: Rounding Fixer

### Class: `ExternalModules.WeekendTransactionPatternV2Rounder`

Post-Transformation module that applies banker's rounding to match V1's `Math.Round(decimal, 2)` behavior.

```csharp
public class WeekendTransactionPatternV2Rounder : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var preOutput = sharedState.TryGetValue("pre_output", out var val)
            ? val as DataFrame
            : null;

        if (preOutput == null || preOutput.Count == 0)
        {
            // Pass through empty DataFrame as-is
            sharedState["output"] = preOutput ?? new DataFrame(
                new List<Row>(),
                new List<string> { "day_type", "txn_count", "total_amount", "avg_amount", "as_of" }
            );
            return sharedState;
        }

        var outputColumns = new List<string>
        {
            "day_type", "txn_count", "total_amount", "avg_amount", "as_of"
        };

        var outputRows = new List<Row>();
        foreach (var row in preOutput.Rows)
        {
            var totalAmount = Convert.ToDecimal(row["total_amount"]);
            var avgAmount = Convert.ToDecimal(row["avg_amount"]);

            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["day_type"] = row["day_type"],
                ["txn_count"] = row["txn_count"],
                // W5: Apply banker's rounding (MidpointRounding.ToEven) to match V1's
                // Math.Round(decimal, 2) behavior [WeekendTransactionPatternProcessor.cs:59,67,107,115]
                ["total_amount"] = Math.Round(totalAmount, 2, MidpointRounding.ToEven),
                ["avg_amount"] = Math.Round(avgAmount, 2, MidpointRounding.ToEven),
                ["as_of"] = row["as_of"]
            }));
        }

        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
```

### Why rounding is in the External module, not SQL

V1 uses `Math.Round(decimal, 2)` [WeekendTransactionPatternProcessor.cs:59, 67, 107, 115] which defaults to `MidpointRounding.ToEven` (banker's rounding). SQLite's `ROUND(value, 2)` uses round-half-away-from-zero. These produce different results at exact midpoints (e.g., `125.125` rounds to `125.12` with banker's rounding but `125.13` with half-away-from-zero).

Given that `avg_amount` is computed as `total/count`, the result can produce arbitrary decimal expansions where exact midpoints are plausible. Moving rounding into the External module guarantees V1-equivalent output regardless of data values.

## 7. Writer Config

```json
{
  "type": "CsvFileWriter",
  "source": "output",
  "outputFile": "Output/double_secret_curated/weekend_transaction_pattern.csv",
  "includeHeader": true,
  "trailerFormat": "TRAILER|{row_count}|{date}",
  "writeMode": "Overwrite",
  "lineEnding": "LF"
}
```

All writer parameters match V1 exactly [weekend_transaction_pattern.json:21-26]:
- **source**: `output` (the rounding fixer's result DataFrame)
- **outputFile**: Changed from `Output/curated/` to `Output/double_secret_curated/` per V2 convention
- **includeHeader**: `true`
- **trailerFormat**: `TRAILER|{row_count}|{date}` -- `{row_count}` = number of data rows in the output DataFrame, `{date}` = `__maxEffectiveDate` formatted as `yyyy-MM-dd` [CsvFileWriter.cs:60-64]
- **writeMode**: `Overwrite` -- each run replaces the file entirely
- **lineEnding**: `LF`

### Trailer Behavior

Per CsvFileWriter.cs:64, `{row_count}` is substituted with `df.Count` (the output DataFrame's row count). On non-Sundays, the output has 2 rows; on Sundays, 4 rows. This matches V1 behavior per BR-11.

`{date}` is substituted with `__maxEffectiveDate.ToString("yyyy-MM-dd")` [CsvFileWriter.cs:60-61], which matches V1's `dateStr` usage [WeekendTransactionPatternProcessor.cs:25].

## 8. V2 Job Config

```json
{
  "jobName": "WeekendTransactionPatternV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "transactions",
      "schema": "datalake",
      "table": "transactions",
      "columns": ["amount", "as_of"],
      "minEffectiveDate": "2024-10-01",
      "maxEffectiveDate": "2024-12-31"
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.WeekendTransactionPatternV2DateInjector"
    },
    {
      "type": "Transformation",
      "resultName": "pre_output",
      "sql": "WITH params AS (SELECT max_date, is_sunday, monday_of_week FROM effective_date), daily_txns AS (SELECT t.amount, t.as_of FROM transactions t, params p WHERE t.as_of = p.max_date), daily_agg AS (SELECT CASE WHEN CAST(strftime('%w', as_of) AS INTEGER) IN (0, 6) THEN 'Weekend' ELSE 'Weekday' END AS day_type, COUNT(*) AS txn_count, SUM(amount) AS total_amount, as_of FROM daily_txns GROUP BY day_type, as_of), daily_output AS (SELECT 'Weekday' AS day_type, COALESCE((SELECT txn_count FROM daily_agg WHERE day_type = 'Weekday'), 0) AS txn_count, COALESCE((SELECT total_amount FROM daily_agg WHERE day_type = 'Weekday'), 0) AS total_amount, CASE WHEN COALESCE((SELECT txn_count FROM daily_agg WHERE day_type = 'Weekday'), 0) > 0 THEN (SELECT total_amount FROM daily_agg WHERE day_type = 'Weekday') * 1.0 / (SELECT txn_count FROM daily_agg WHERE day_type = 'Weekday') ELSE 0 END AS avg_amount, (SELECT max_date FROM params) AS as_of UNION ALL SELECT 'Weekend' AS day_type, COALESCE((SELECT txn_count FROM daily_agg WHERE day_type = 'Weekend'), 0) AS txn_count, COALESCE((SELECT total_amount FROM daily_agg WHERE day_type = 'Weekend'), 0) AS total_amount, CASE WHEN COALESCE((SELECT txn_count FROM daily_agg WHERE day_type = 'Weekend'), 0) > 0 THEN (SELECT total_amount FROM daily_agg WHERE day_type = 'Weekend') * 1.0 / (SELECT txn_count FROM daily_agg WHERE day_type = 'Weekend') ELSE 0 END AS avg_amount, (SELECT max_date FROM params) AS as_of), weekly_txns AS (SELECT t.amount, t.as_of FROM transactions t, params p WHERE p.is_sunday = 1 AND t.as_of >= p.monday_of_week AND t.as_of <= p.max_date), weekly_agg AS (SELECT CASE WHEN CAST(strftime('%w', as_of) AS INTEGER) IN (0, 6) THEN 'WEEKLY_TOTAL_Weekend' ELSE 'WEEKLY_TOTAL_Weekday' END AS day_type, COUNT(*) AS txn_count, SUM(amount) AS total_amount, as_of FROM weekly_txns GROUP BY day_type), weekly_output AS (SELECT 'WEEKLY_TOTAL_Weekday' AS day_type, COALESCE((SELECT txn_count FROM weekly_agg WHERE day_type = 'WEEKLY_TOTAL_Weekday'), 0) AS txn_count, COALESCE((SELECT total_amount FROM weekly_agg WHERE day_type = 'WEEKLY_TOTAL_Weekday'), 0) AS total_amount, CASE WHEN COALESCE((SELECT txn_count FROM weekly_agg WHERE day_type = 'WEEKLY_TOTAL_Weekday'), 0) > 0 THEN (SELECT total_amount FROM weekly_agg WHERE day_type = 'WEEKLY_TOTAL_Weekday') * 1.0 / (SELECT txn_count FROM weekly_agg WHERE day_type = 'WEEKLY_TOTAL_Weekday') ELSE 0 END AS avg_amount, (SELECT max_date FROM params) AS as_of WHERE (SELECT is_sunday FROM params) = 1 UNION ALL SELECT 'WEEKLY_TOTAL_Weekend' AS day_type, COALESCE((SELECT txn_count FROM weekly_agg WHERE day_type = 'WEEKLY_TOTAL_Weekend'), 0) AS txn_count, COALESCE((SELECT total_amount FROM weekly_agg WHERE day_type = 'WEEKLY_TOTAL_Weekend'), 0) AS total_amount, CASE WHEN COALESCE((SELECT txn_count FROM weekly_agg WHERE day_type = 'WEEKLY_TOTAL_Weekend'), 0) > 0 THEN (SELECT total_amount FROM weekly_agg WHERE day_type = 'WEEKLY_TOTAL_Weekend') * 1.0 / (SELECT txn_count FROM weekly_agg WHERE day_type = 'WEEKLY_TOTAL_Weekend') ELSE 0 END AS avg_amount, (SELECT max_date FROM params) AS as_of WHERE (SELECT is_sunday FROM params) = 1) SELECT day_type, txn_count, total_amount, avg_amount, as_of FROM daily_output UNION ALL SELECT day_type, txn_count, total_amount, avg_amount, as_of FROM weekly_output"
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.WeekendTransactionPatternV2Rounder"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/weekend_transaction_pattern.csv",
      "includeHeader": true,
      "trailerFormat": "TRAILER|{row_count}|{date}",
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

## 9. Wrinkle Replication

### W3a -- End-of-week boundary (Sunday weekly summary rows)

**BRD reference**: BR-6, BR-7 -- On Sundays, two `WEEKLY_TOTAL_*` rows are appended aggregating Mon-Sun.

**V2 replication**: The Transformation SQL includes a `weekly_output` CTE gated by `WHERE (SELECT is_sunday FROM params) = 1`. When the effective date is a Sunday, the date injector External module sets `is_sunday = 1` and `monday_of_week` to the Monday 6 days prior. The SQL aggregates all transactions in that range, classified by day of week.

### W5 -- Banker's rounding: RESOLVED

**BRD reference**: BR-8 -- `Math.Round(decimal, 2)` with `MidpointRounding.ToEven`.

**V2 replication**: The rounding fixer External module (`WeekendTransactionPatternV2Rounder`) applies `Math.Round(decimal, 2, MidpointRounding.ToEven)` to both `total_amount` and `avg_amount` after the SQL Transformation produces unrounded values. This guarantees V1-equivalent rounding behavior regardless of whether input data hits exact midpoints.

**Previous approach (SUPERSEDED)**: The original FSD deferred the rounding divergence to Phase D comparison. This revision resolves it now by moving rounding out of SQL and into C# where `MidpointRounding.ToEven` is available.

## 10. Anti-Pattern Elimination

### AP3 -- Unnecessary External module: MOSTLY ELIMINATED

**V1 problem**: V1 uses a full C# External module (`WeekendTransactionPatternProcessor.cs`) for logic that is largely expressible in SQL [weekend_transaction_pattern.json:14-18].

**V2 approach**: Two minimal External modules replace the monolithic V1 processor. The date injector (15 lines) bridges `__maxEffectiveDate` into a DataFrame. The rounding fixer (25 lines) applies banker's rounding. All business logic (classification, aggregation, weekly summary, zero-count handling) lives in SQL. Combined, the two External modules contain zero business logic -- they are adapters for framework limitations (missing scalar state access in SQL, wrong rounding mode in SQLite).

### AP4 -- Unused columns: ELIMINATED

**V1 problem**: DataSourcing sources `transaction_id`, `account_id`, `txn_timestamp`, `txn_type` [weekend_transaction_pattern.json:10]. None referenced in the External module [WeekendTransactionPatternProcessor.cs].

**V2 approach**: DataSourcing sources only `amount` and `as_of`.

### AP6 -- Row-by-row iteration: ELIMINATED

**V1 problem**: Two `foreach` loops over all transaction rows [WeekendTransactionPatternProcessor.cs:33-51, 83-101].

**V2 approach**: SQL `GROUP BY`, `COUNT(*)`, `SUM()` for set-based aggregation. The rounding fixer iterates only the 2-4 output rows (not the input rows), which is trivial.

### AP10 -- Over-sourced date range: RETAINED (documented)

**V1 problem**: DataSourcing hardcodes `minEffectiveDate: "2024-10-01"` and `maxEffectiveDate: "2024-12-31"` [weekend_transaction_pattern.json:11-12].

**V2 approach**: Retained because eliminating it would require framework modification or a more complex External module. Documented with comment.

## 11. Proofmark Config

```yaml
comparison_target: "weekend_transaction_pattern"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

**Justification:**
- **reader**: `csv` -- V1 and V2 both use CsvFileWriter.
- **threshold**: `100.0` -- strict match. Rounding divergence is now resolved in the External module.
- **header_rows**: `1` -- `includeHeader: true`.
- **trailer_rows**: `1` -- `trailerFormat` present with `writeMode: Overwrite`.
- **No excluded columns**: No non-deterministic fields.
- **No fuzzy columns**: Rounding is handled by `Math.Round(decimal, 2, MidpointRounding.ToEven)` in the External module, matching V1 exactly. No epsilon tolerance needed.

## 12. Open Questions

### OQ-1: RESOLVED -- SQLite `ROUND` vs C# `Math.Round` rounding mode

**Resolution**: Rounding has been moved out of SQL and into the post-Transformation External module (`WeekendTransactionPatternV2Rounder`). The module applies `Math.Round(decimal, 2, MidpointRounding.ToEven)` to both `total_amount` and `avg_amount`, exactly matching V1's `Math.Round(decimal, 2)` default behavior. The SQL now produces unrounded intermediate values only.

### OQ-2: `decimal` to `double` precision in SQLite

**Risk**: LOW

V1 accumulates amounts as `decimal` (exact). V2 stores amounts as `REAL` (double) in SQLite [Transformation.cs:98-104]. For sums of ~4000 values in the range [20.00, 1800.00], IEEE 754 double precision (53-bit mantissa) has sufficient precision. The maximum sum (~4300 * 1800 = ~7.7M) is well within double's exact representable range for 2-decimal-place values.

**Action**: No mitigation needed. The post-SQL rounding fixer converts back to `decimal` before rounding, which recaptures precision. If Proofmark reveals epsilon-level differences, add a fuzzy tolerance on `total_amount` (absolute tolerance 0.01).

### OQ-3: First week boundary (Oct 6 is first Sunday)

**Risk**: NONE

For the first Sunday (2024-10-06), `monday_of_week = 2024-10-06 - 6 = 2024-09-30`. But sourced data starts at 2024-10-01 (hardcoded `minEffectiveDate`). The SQL's `WHERE t.as_of >= p.monday_of_week` will simply find no rows for Sep 30, matching V1's behavior since V1 also uses the same source data range [weekend_transaction_pattern.json:11]. Both V1 and V2 will produce weekly totals covering only Oct 1-Oct 6. This is identical behavior.

## 13. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|-------------|----------------|----------|
| Tier 2 (date injection + rounding fix External modules) | BR-2, BR-8, BR-9 | SQL cannot access `__maxEffectiveDate`; SQLite ROUND diverges from `Math.Round(ToEven)` |
| DataSourcing with hardcoded date range (AP10 retained) | BR-1, BR-7 | Weekly summaries need 7 days; framework cannot compute dynamic dates |
| DataSourcing columns: `["amount", "as_of"]` | BR-8 (decimal amounts) | AP4: [WeekendTransactionPatternProcessor.cs] only uses amount and as_of |
| SQL classification by day of week | BR-3 | [WeekendTransactionPatternProcessor.cs:41] |
| Two daily rows always output (Weekday + Weekend) | BR-4, BR-5 | [WeekendTransactionPatternProcessor.cs:55-71] |
| Sunday weekly summary rows | BR-6, BR-7 | [WeekendTransactionPatternProcessor.cs:74-119] |
| Banker's rounding in External module | BR-8 (W5) | [WeekendTransactionPatternProcessor.cs:59,67,107,115] `Math.Round(decimal, 2)` |
| as_of = maxDate formatted yyyy-MM-dd | BR-9 | [WeekendTransactionPatternProcessor.cs:25] |
| Empty transactions = empty output | BR-10 | [WeekendTransactionPatternProcessor.cs:19-23] |
| Trailer row count (2 or 4) | BR-11 | CsvFileWriter `{row_count}` = df.Count |
