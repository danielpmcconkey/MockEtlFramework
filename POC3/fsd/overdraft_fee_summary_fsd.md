# OverdraftFeeSummary — Functional Specification Document

## 1. Job Summary

The `OverdraftFeeSummary` job aggregates overdraft event data from the `datalake.overdraft_events` table, grouping by fee waiver status (`fee_waived`) and snapshot date (`as_of`). For each group it computes total fees, event count, and average fee -- all rounded to two decimal places. Output is a single CSV file with header, no trailer, written in Overwrite mode with LF line endings. The V1 implementation contains an unused CTE with `ROW_NUMBER()` and sources several columns never referenced in the SQL; V2 eliminates both anti-patterns while producing byte-identical output.

## 2. V2 Module Chain

**Tier: 1 — Framework Only (DEFAULT)**

| Step | Module | Purpose |
|------|--------|---------|
| 1 | DataSourcing | Pull `fee_amount`, `fee_waived` from `datalake.overdraft_events` for the executor-injected effective date range. The `as_of` column is appended automatically by the framework. |
| 2 | Transformation | SQL aggregation: GROUP BY `fee_waived`, `as_of`; compute `total_fees`, `event_count`, `avg_fee`. |
| 3 | CsvFileWriter | Write the `fee_summary` DataFrame to CSV. |

**Tier justification:** All business logic is expressible as a single SQL aggregation query. No procedural logic, no multi-table joins requiring snapshot boundary management, no calculations outside SQL's capability. Tier 1 is the correct and sufficient choice. The V1 job already uses Tier 1 (DataSourcing + Transformation + CsvFileWriter); V2 continues this pattern with a cleaner SQL query.

## 3. DataSourcing Config

**Source table:** `datalake.overdraft_events`

| Column | Type | Used In SQL | Justification |
|--------|------|-------------|---------------|
| `fee_amount` | numeric | Yes | SUM, AVG aggregation targets |
| `fee_waived` | boolean | Yes | GROUP BY key, ORDER BY key |

**Columns NOT sourced (eliminated per AP4):**
- `overdraft_id` — only used in V1's dead `ROW_NUMBER()` CTE (see AP8). Not needed in V2.
- `account_id` — sourced by V1 but never referenced in any SQL expression. Dead-end per AP4.
- `customer_id` — sourced by V1 but never referenced in any SQL expression. Dead-end per AP4.
- `overdraft_amount` — sourced by V1 but never referenced in any SQL expression. Dead-end per AP4.
- `event_timestamp` — sourced by V1 but never referenced in any SQL expression. Dead-end per AP4.

**Effective date handling:** No hardcoded dates. The framework's DataSourcing module reads `__minEffectiveDate` and `__maxEffectiveDate` from shared state (injected by `JobExecutorService` at runtime) and filters `WHERE as_of >= @minDate AND as_of <= @maxDate`. The `as_of` column is automatically appended to the result DataFrame since it is not in the explicit column list.

**Evidence:** [overdraft_fee_summary.json:4-11] V1 sources 7 columns; comparison with [overdraft_fee_summary.json:15] SQL shows only `fee_amount`, `fee_waived`, and `as_of` are referenced in the outer query. `overdraft_id` is used only in the dead `ROW_NUMBER()` CTE.

## 4. Transformation SQL

```sql
SELECT
    oe.fee_waived,
    ROUND(SUM(oe.fee_amount), 2) AS total_fees,
    COUNT(*) AS event_count,
    ROUND(AVG(oe.fee_amount), 2) AS avg_fee,
    oe.as_of
FROM overdraft_events oe
GROUP BY oe.fee_waived, oe.as_of
ORDER BY oe.fee_waived
```

**Key differences from V1 SQL:**
- The `WITH all_events AS (... ROW_NUMBER() OVER (...) AS rn ...)` CTE is removed. The `rn` column was computed but never referenced in V1's outer query. Removing it does not change output. (Eliminates AP8.)
- The outer query no longer selects from the CTE alias `ae`; it selects directly from `overdraft_events oe`. The table alias changes from `ae` to `oe` for clarity but this has no output effect.

**Output equivalence argument:** The V1 CTE `all_events` performs a direct passthrough of all rows from `overdraft_events` with an additional `rn` column. The outer query's `GROUP BY ae.fee_waived, ae.as_of` with `SUM(ae.fee_amount)`, `COUNT(*)`, `AVG(ae.fee_amount)` operates on the exact same row set regardless of whether the CTE exists. The `rn` column is never referenced outside the CTE. Therefore, removing the CTE produces identical grouping, aggregation, and output.

**SQLite compatibility notes:**
- `ROUND()` in SQLite behaves identically to PostgreSQL for 2-decimal rounding of numeric values.
- `fee_waived` is a boolean stored as INTEGER (0/1) in SQLite per the Transformation module's `ToSqliteValue` mapping (`bool b => b ? 1 : 0`). The V1 output will also render these as `0`/`1` (or `False`/`True` depending on DataFrame serialization). This is framework behavior, not job-specific — V2 inherits the same mapping.
- `ORDER BY oe.fee_waived` sorts `false` (0) before `true` (1) in SQLite, matching V1 behavior per BR-6.

## 5. Writer Config

| Parameter | Value | Evidence |
|-----------|-------|----------|
| **type** | CsvFileWriter | [overdraft_fee_summary.json:18] |
| **source** | `fee_summary` | [overdraft_fee_summary.json:19] |
| **outputFile** | `Output/double_secret_curated/overdraft_fee_summary.csv` | V2 convention per BLUEPRINT.md |
| **includeHeader** | `true` | [overdraft_fee_summary.json:21] |
| **writeMode** | `Overwrite` | [overdraft_fee_summary.json:22] |
| **lineEnding** | `LF` | [overdraft_fee_summary.json:23] |
| **trailerFormat** | not configured | [BRD: Writer Configuration] confirms no trailer |

All writer parameters match V1 exactly, except the output path which changes from `Output/curated/` to `Output/double_secret_curated/` per the V2 output convention.

## 6. Wrinkle Replication

No output-affecting wrinkles (W-codes) apply to this job.

**W-codes evaluated and ruled out:**

| W-code | Applicability | Rationale |
|--------|---------------|-----------|
| W1 (Sunday skip) | No | No day-of-week logic in V1 config or SQL. |
| W2 (Weekend fallback) | No | No weekend date logic in V1. |
| W3a/b/c (Boundary rows) | No | No summary row generation logic. |
| W4 (Integer division) | No | All arithmetic uses `ROUND(SUM(...))` and `ROUND(AVG(...))` on `numeric` type. No integer division. |
| W5 (Banker's rounding) | No | `ROUND()` in SQLite uses arithmetic rounding (away from zero at .5). No `MidpointRounding.ToEven` involved. |
| W6 (Double epsilon) | No | No double-precision accumulation. All values flow through SQLite's `ROUND()` to 2 decimals. |
| W7 (Trailer inflated count) | No | No trailer configured. |
| W8 (Trailer stale date) | No | No trailer configured. |
| W9 (Wrong writeMode) | No | Overwrite is the configured mode. The BRD notes that multi-day runs lose prior days' data (EC-3), but this is V1's intentional behavior, not a wrinkle — it is simply how Overwrite mode works. |
| W10 (Absurd numParts) | No | Not a Parquet writer. |
| W12 (Header every append) | No | Not in Append mode. |

## 7. Anti-Pattern Elimination

### AP4: Unused Columns — ELIMINATED

**V1 problem:** DataSourcing sources 7 columns (`overdraft_id`, `account_id`, `customer_id`, `overdraft_amount`, `fee_amount`, `fee_waived`, `event_timestamp`). Of these, only `fee_amount` and `fee_waived` are used in the outer aggregation query. `overdraft_id` is used only in the dead `ROW_NUMBER()` CTE. The remaining 4 columns (`account_id`, `customer_id`, `overdraft_amount`, `event_timestamp`) are never referenced anywhere.

**V2 fix:** DataSourcing sources only `fee_amount` and `fee_waived`. The `as_of` column is appended automatically by the framework since it is not in the explicit column list.

**Evidence:** [overdraft_fee_summary.json:10] sources 7 columns; [overdraft_fee_summary.json:15] SQL outer SELECT uses only `fee_amount`, `fee_waived`, `as_of`. Cross-reference confirms `account_id`, `customer_id`, `overdraft_amount`, `event_timestamp` are dead.

### AP8: Unused CTE / Complex SQL — ELIMINATED

**V1 problem:** The SQL uses a CTE (`all_events`) that computes `ROW_NUMBER() OVER (PARTITION BY as_of ORDER BY overdraft_id) AS rn`. The `rn` column is never referenced in the outer query. This is dead computation — likely a leftover from a prior iteration.

**V2 fix:** The CTE is removed entirely. The outer query selects directly from `overdraft_events`. This simplifies the SQL without affecting output.

**Evidence:** [overdraft_fee_summary.json:15] CTE defines `rn` but outer SELECT lists `ae.fee_waived`, `SUM(ae.fee_amount)`, `COUNT(*)`, `AVG(ae.fee_amount)`, `ae.as_of` — no reference to `rn`.

### AP-codes evaluated and not applicable:

| AP-code | Applicability | Rationale |
|---------|---------------|-----------|
| AP1 (Dead-end sourcing) | Covered by AP4 above | The unused columns are the dead-end sourcing. |
| AP2 (Duplicated logic) | Noted | BRD OQ-2 notes similarity to FeeWaiverAnalysis. Cannot fix cross-job duplication within a single job's scope. Documented here. |
| AP3 (Unnecessary External) | No | V1 already uses framework-only modules (Tier 1). |
| AP5 (Asymmetric NULLs) | No | No NULL coalescing in V1. `fee_amount` has no NULLs in the data (min=0.00). Standard SQL NULL semantics apply uniformly. |
| AP6 (Row-by-row iteration) | No | No External module, no C# iteration. Pure SQL. |
| AP7 (Magic values) | No | No hardcoded thresholds or magic strings in the SQL. |
| AP9 (Misleading names) | No | Job name accurately describes its output (overdraft fee summary). |
| AP10 (Over-sourcing dates) | No | V1 already uses framework date injection (no hardcoded dates, no WHERE clause on dates in the SQL). DataSourcing handles date filtering at the source. |

## 8. Proofmark Config

```yaml
comparison_target: "overdraft_fee_summary"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

**Justification for strict comparison (zero exclusions, zero fuzzy):**

- **No non-deterministic fields** identified in the BRD (Section: "Non-Deterministic Fields: None identified").
- **No trailer** to exclude.
- **All output columns** (`fee_waived`, `total_fees`, `event_count`, `avg_fee`, `as_of`) are deterministic aggregations of source data with explicit `ROUND()` to 2 decimals.
- **No floating-point accumulation issues** (W6 not applicable) — values pass through SQLite `ROUND()` which produces deterministic text output.
- 100.0 threshold: every row must match exactly.

## 9. Open Questions

**OQ-1: Cross-job duplication with FeeWaiverAnalysis.**
The BRD (OQ-2) notes that this job and `FeeWaiverAnalysis` produce similar outputs (both group by `fee_waived` + `as_of`). Key differences: this job does not join to accounts, does not COALESCE NULLs, and has the (now removed) `ROW_NUMBER` CTE. This is documented per AP2 but cannot be resolved within a single job's scope. No action required for V2 output equivalence.

**OQ-2: Boolean representation in CSV output.**
The `fee_waived` column is boolean. The Transformation module converts booleans to SQLite INTEGER (0/1) via `ToSqliteValue`. When SQLite returns these values, they come back as `long` integers. The CsvFileWriter calls `ToString()` on each value, so `fee_waived` will render as `0` or `1` in the CSV. V1 uses the same framework pipeline, so the rendering is identical. No risk to output equivalence, but worth verifying during Proofmark comparison.

**OQ-3: Overwrite mode on multi-day auto-advance.**
With `writeMode: Overwrite`, only the last effective date's output survives when running across multiple dates. This is V1 behavior (EC-3 in BRD). V2 replicates this exactly by using the same `writeMode: Overwrite`. The Proofmark comparison will only see the final date's output, which is the expected state.
