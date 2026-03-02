# PreferenceTrend -- Functional Specification Document

## 1. Job Summary

PreferenceTrendV2 tracks opt-in and opt-out counts across all preference types over time by aggregating `customer_preferences` rows grouped by `preference_type` and `as_of` date. Each execution appends new rows to a cumulative CSV file, building a historical record of how customer preference opt-in/opt-out counts change day over day. The output contains one row per preference type per date, with columns for `preference_type`, `opted_in_count`, `opted_out_count`, and `as_of`.

## 2. V2 Module Chain

**Tier: 1 (Framework Only)** -- `DataSourcing -> Transformation (SQL) -> CsvFileWriter`

**Tier Justification:** All business logic is a straightforward SQL aggregation (SUM of CASE expressions with GROUP BY). V1 already uses this exact Tier 1 pattern -- a single DataSourcing feeds a SQL Transformation, which feeds a CsvFileWriter. No External module exists in V1 for this job. There is no procedural logic, no complex date manipulation, no multi-table joins, and no SQLite-incompatible operations. Tier 1 is the only appropriate choice.

| Step | Module Type | Config Key | Details |
|------|------------|------------|---------|
| 1 | DataSourcing | `customer_preferences` | schema=`datalake`, table=`customer_preferences`, columns=`[preference_type, opted_in]`. Effective dates injected by executor. `as_of` auto-appended by DataSourcing. |
| 2 | Transformation | `pref_trend` | SQL aggregates opt-in/opt-out counts per preference_type and as_of. See Section 4. |
| 3 | CsvFileWriter | -- | source=`pref_trend`, outputFile=`Output/double_secret_curated/preference_trend.csv`, includeHeader=true, writeMode=Append, lineEnding=LF, no trailer |

### Key Design Decisions

- **Remove unused columns from DataSourcing.** V1 sources `[preference_id, customer_id, preference_type, opted_in]` but the Transformation SQL only references `preference_type`, `opted_in`, and `as_of` (which is auto-appended by DataSourcing regardless). `preference_id` and `customer_id` are never referenced in the SQL. V2 sources only `[preference_type, opted_in]`, eliminating AP4.
- **Same writer type and configuration.** V1 uses CsvFileWriter with Append mode, includeHeader=true, and LF line endings. V2 matches this exactly.
- **No SQL changes needed.** V1's SQL is clean, correct, and minimal. V2 reproduces it identically.

## 3. DataSourcing Config

| Property | Value |
|----------|-------|
| resultName | `customer_preferences` |
| schema | `datalake` |
| table | `customer_preferences` |
| columns | `["preference_type", "opted_in"]` |
| minEffectiveDate | Injected by executor via `__minEffectiveDate` shared state key |
| maxEffectiveDate | Injected by executor via `__maxEffectiveDate` shared state key |
| additionalFilter | (none) |

**Effective date handling:** DataSourcing automatically appends `as_of` to the column list when it is not explicitly included [DataSourcing.cs:69]. The executor injects `__minEffectiveDate` and `__maxEffectiveDate` into shared state before the pipeline runs [Architecture.md, JobExecutorService]. DataSourcing generates a WHERE clause: `WHERE as_of >= @minDate AND as_of <= @maxDate` [DataSourcing.cs:77-78], plus `ORDER BY as_of` [DataSourcing.cs:85]. For single-date auto-advance runs, `minDate == maxDate`, so exactly one date's worth of data is sourced per execution.

**Table schema (datalake.customer_preferences):**

| Column | Type | Used in V2? |
|--------|------|-------------|
| preference_id | integer | NO -- removed (AP4) |
| customer_id | integer | NO -- removed (AP4) |
| preference_type | varchar(25) | YES -- GROUP BY key |
| opted_in | boolean | YES -- aggregation target |
| updated_date | date | NO -- not sourced in V1 either |
| as_of | date | YES -- auto-appended by DataSourcing, GROUP BY key |

**Constraint:** `preference_type` is constrained to: `PAPER_STATEMENTS`, `E_STATEMENTS`, `MARKETING_EMAIL`, `MARKETING_SMS`, `PUSH_NOTIFICATIONS` [datalake.customer_preferences CHECK constraint].

## 4. Transformation SQL

The V2 SQL is functionally identical to V1. No changes are needed -- the SQL is clean and minimal.

```sql
SELECT
    cp.preference_type,
    SUM(CASE WHEN cp.opted_in = 1 THEN 1 ELSE 0 END) AS opted_in_count,
    SUM(CASE WHEN cp.opted_in = 0 THEN 1 ELSE 0 END) AS opted_out_count,
    cp.as_of
FROM customer_preferences cp
GROUP BY cp.preference_type, cp.as_of
```

### SQL Design Notes

1. **Boolean-to-integer mapping:** PostgreSQL stores `opted_in` as boolean, but DataSourcing fetches it into a DataFrame, and the Transformation module registers it in SQLite. In SQLite, boolean `true` maps to `1` and `false` maps to `0`, so the `CASE WHEN cp.opted_in = 1` and `CASE WHEN cp.opted_in = 0` expressions work correctly against the SQLite-registered data. This matches V1 exactly [preference_trend.json:15].

2. **GROUP BY:** Groups by `preference_type` and `as_of`. For a single-date auto-advance run, this produces exactly 5 rows (one per preference type). For a multi-date run, it produces 5 rows per date.

3. **No ORDER BY:** V1 SQL has no ORDER BY clause [BRD BR-4]. Row order depends on SQLite's GROUP BY implementation. V2 reproduces this exactly -- no ORDER BY is added.

4. **No WHERE filter:** Unlike EmailOptInRate (which filters to MARKETING_EMAIL only), this job includes all preference types [BRD Edge Case 5]. No WHERE clause is needed.

5. **No NULL handling:** The `opted_in` column is NOT NULL in the datalake schema. The CASE expressions cover both `= 1` (true) and `= 0` (false), which are exhaustive for the boolean domain. No NULL branch is needed.

6. **Transformation resultName:** `pref_trend` -- matches V1 [preference_trend.json:14].

## 5. Writer Config

| Property | V1 Value | V2 Value | Match? |
|----------|----------|----------|--------|
| type | CsvFileWriter | CsvFileWriter | YES |
| source | `pref_trend` | `pref_trend` | YES |
| outputFile | `Output/curated/preference_trend.csv` | `Output/double_secret_curated/preference_trend.csv` | Changed per V2 convention |
| includeHeader | true | true | YES |
| writeMode | Append | Append | YES |
| lineEnding | LF | LF | YES |
| trailerFormat | (absent / none) | (absent / none) | YES |

**Write mode implications:**

- **Append mode** means each execution opens the file in append mode and adds new data rows [CsvFileWriter.cs:42-43].
- **Header on first write only:** CsvFileWriter suppresses the header when appending to an existing file (`if (_includeHeader && !append)` [CsvFileWriter.cs:47]). The header is written only on the first execution when the file does not yet exist. Subsequent executions add data rows only.
- **Cumulative growth:** Over the full auto-advance date range (2024-10-01 through 2024-12-31), the file grows by ~5 rows per day (one per preference type), for a total of ~460 rows plus the header.
- **Re-run duplication:** No deduplication mechanism exists. Re-running for a previously processed date range appends duplicate rows [BRD Edge Case 3].

## 6. Wrinkle Replication

No output-affecting wrinkles (W-codes) apply to this job.

| W-code | Applicable? | Rationale |
|--------|------------|-----------|
| W1 (Sunday skip) | No | No day-of-week logic in V1 config or SQL. |
| W2 (Weekend fallback) | No | No date fallback logic. |
| W3a/b/c (Boundary rows) | No | No summary row generation. |
| W4 (Integer division) | No | No division operations. SUM produces integers, no rate calculation. |
| W5 (Banker's rounding) | No | No rounding operations. |
| W6 (Double epsilon) | No | No floating-point accumulation. Integer SUM only. |
| W7 (Trailer inflated count) | No | No trailer in V1. No External module. |
| W8 (Trailer stale date) | No | No trailer in V1. |
| W9 (Wrong writeMode) | No | Append mode is appropriate for a cumulative trend file. Each run adds new date-partitioned rows without destroying prior data. This is the correct semantic. |
| W10 (Absurd numParts) | No | CsvFileWriter, not Parquet. Single output file. |
| W12 (Header every append) | No | No External module. CsvFileWriter correctly suppresses header on append [CsvFileWriter.cs:47]. |

## 7. Anti-Pattern Elimination

| AP-code | Identified? | V1 Problem | V2 Resolution |
|---------|------------|------------|---------------|
| **AP4** (Unused columns) | **YES** | V1 sources `preference_id` and `customer_id` from `customer_preferences`, but the Transformation SQL never references either column. The SQL only uses `cp.preference_type`, `cp.opted_in`, and `cp.as_of`. Evidence: [preference_trend.json:10] sources `["preference_id", "customer_id", "preference_type", "opted_in"]`; [preference_trend.json:15] SQL references only `cp.preference_type`, `cp.opted_in`, `cp.as_of`. | **Eliminated.** V2 DataSourcing requests only `["preference_type", "opted_in"]`. `as_of` is auto-appended by DataSourcing [DataSourcing.cs:69-72]. |
| AP1 (Dead-end sourcing) | No | Only one table is sourced (`customer_preferences`) and it is used in the SQL. The unused columns are covered by AP4 above, not AP1 (which applies to entire unused tables). |
| AP2 (Duplicated logic) | No | Not applicable within this job's scope. |
| AP3 (Unnecessary External) | No | V1 does not use an External module. Already framework-only (Tier 1). |
| AP5 (Asymmetric NULLs) | No | No NULL coalescing logic. `opted_in` is NOT NULL per schema constraint. |
| AP6 (Row-by-row iteration) | No | V1 uses SQL aggregation, not C# iteration. |
| AP7 (Magic values) | No | No hardcoded thresholds or magic strings. The only literals are `1` and `0` for boolean comparison, which are self-documenting. |
| AP8 (Complex SQL / unused CTEs) | No | V1 SQL is a single SELECT with GROUP BY. No CTEs, no window functions, no subqueries. |
| AP9 (Misleading names) | No | "PreferenceTrend" accurately describes the job's output (trend of preference opt-in/opt-out counts over time). |
| AP10 (Over-sourcing dates) | No | V1 does not hardcode date filters in SQL. Effective dates are injected by the executor into DataSourcing, which applies the `WHERE as_of >= @minDate AND as_of <= @maxDate` filter at the source level. |

## 8. Proofmark Config

**Excluded columns:** None.

**Fuzzy columns:** None.

**Rationale:** All output columns are fully deterministic:
- `preference_type`: passthrough GROUP BY key from datalake. Constrained to 5 fixed values.
- `opted_in_count`: deterministic integer SUM of boolean CASE expression.
- `opted_out_count`: deterministic integer SUM of boolean CASE expression.
- `as_of`: passthrough date from datalake.

The BRD explicitly states: "Non-Deterministic Fields: None identified." There are no timestamps, random values, floating-point precision concerns, or execution-time-dependent outputs. Strict comparison at 100% threshold is appropriate.

**CSV-specific settings:** The file has a header row (written on the first execution only) and no trailer. Since this is an Append-mode file, the header appears once at the top. `header_rows: 1` skips it during comparison. `trailer_rows: 0` because there is no trailer.

```yaml
comparison_target: "preference_trend"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

## 9. Open Questions

1. **No ORDER BY in SQL:** The SQL lacks an ORDER BY clause. Row order within each appended batch depends on SQLite's GROUP BY implementation, which is not guaranteed to be stable across SQLite versions. However, since V1 also has no ORDER BY, and both V1 and V2 use the same SQLite engine within the same framework, the row order should be identical. If Proofmark comparison fails due to row ordering, an ORDER BY clause could be added to both V1's description and V2's implementation to stabilize the output, but this would need to be validated as not changing V1's actual output order first.
   - **Risk:** LOW -- SQLite's GROUP BY ordering is deterministic for the same data and engine version.
   - **Mitigation:** If Proofmark reports row-order mismatches, investigate and add `ORDER BY cp.preference_type, cp.as_of` to V2 SQL only if it matches V1's actual output order.

## 10. V2 Job Config

```json
{
  "jobName": "PreferenceTrendV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "customer_preferences",
      "schema": "datalake",
      "table": "customer_preferences",
      "columns": ["preference_type", "opted_in"]
    },
    {
      "type": "Transformation",
      "resultName": "pref_trend",
      "sql": "SELECT cp.preference_type, SUM(CASE WHEN cp.opted_in = 1 THEN 1 ELSE 0 END) AS opted_in_count, SUM(CASE WHEN cp.opted_in = 0 THEN 1 ELSE 0 END) AS opted_out_count, cp.as_of FROM customer_preferences cp GROUP BY cp.preference_type, cp.as_of"
    },
    {
      "type": "CsvFileWriter",
      "source": "pref_trend",
      "outputFile": "Output/double_secret_curated/preference_trend.csv",
      "includeHeader": true,
      "writeMode": "Append",
      "lineEnding": "LF"
    }
  ]
}
```

### Differences from V1 Config

| Change | V1 | V2 | Reason |
|--------|----|----|--------|
| customer_preferences columns | `["preference_id", "customer_id", "preference_type", "opted_in"]` | `["preference_type", "opted_in"]` | AP4: preference_id and customer_id never used in SQL |
| Transformation SQL | Identical | Identical | No SQL changes needed -- V1 SQL is clean |
| Output file | `Output/curated/preference_trend.csv` | `Output/double_secret_curated/preference_trend.csv` | V2 output convention |
| Job name | `PreferenceTrend` | `PreferenceTrendV2` | V2 naming convention |
| includeHeader | true | true | Unchanged |
| writeMode | Append | Append | Unchanged |
| lineEnding | LF | LF | Unchanged |
| trailerFormat | (absent) | (absent) | Unchanged |
| firstEffectiveDate | 2024-10-01 | 2024-10-01 | Unchanged |

## 11. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|-------------|-----------------|----------|
| Source customer_preferences with columns [preference_type, opted_in] | BR-1, BR-2: opted_in counts require preference_type and opted_in | [preference_trend.json:10-11] |
| Remove preference_id and customer_id from DataSourcing | AP4: columns never referenced in SQL | [preference_trend.json:10] columns vs [preference_trend.json:15] SQL |
| SUM(CASE WHEN opted_in = 1) AS opted_in_count | BR-1: count of opted-in rows per group | [preference_trend.json:15] |
| SUM(CASE WHEN opted_in = 0) AS opted_out_count | BR-2: count of opted-out rows per group | [preference_trend.json:15] |
| GROUP BY preference_type, as_of | BR-3: one row per preference type per date | [preference_trend.json:15] |
| No ORDER BY | BR-4: V1 SQL has no ORDER BY clause | [preference_trend.json:15] |
| writeMode=Append | BRD Writer Configuration | [preference_trend.json:22] |
| includeHeader=true | BRD Writer Configuration | [preference_trend.json:21] |
| lineEnding=LF | BRD Writer Configuration | [preference_trend.json:23] |
| No trailerFormat | BRD Writer Configuration: no trailer | [preference_trend.json:17-24] -- absent |
| firstEffectiveDate=2024-10-01 | V1 job config | [preference_trend.json:3] |
| Eliminate AP4 (preference_id, customer_id) | AP4 prescription: remove unused columns | [KNOWN_ANTI_PATTERNS.md:AP4] |
| No W-code wrinkles to replicate | BRD: no wrinkles identified | BRD analysis: no day-of-week logic, no division, no trailer, no External module |
| No Proofmark exclusions or fuzzy | BRD: no non-deterministic fields | BRD Non-Deterministic Fields section |
| Tier 1 module chain | All logic expressible in SQL | V1 is already Tier 1; no External module needed |

## 12. External Module Design

**Not applicable.** V2 uses Tier 1 (Framework Only) with a three-step chain: DataSourcing -> Transformation (SQL) -> CsvFileWriter. No External module is needed. V1 also does not use an External module for this job.
