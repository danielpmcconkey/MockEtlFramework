# DailyWireVolume -- Functional Specification Document

## 1. Overview

DailyWireVolumeV2 aggregates wire transfer activity by date, producing a daily count and total dollar amount across the fixed date range 2024-10-01 through 2024-12-31. Output is a CSV file written in Append mode with LF line endings.

**Tier: 1 (Framework Only)**
`DataSourcing -> Transformation (SQL) -> CsvFileWriter`

**Justification:** V1 already uses a Tier 1 chain. All business logic (GROUP BY aggregation, rounding, ordering) is trivially expressible in SQL. No procedural logic, no cross-boundary date queries, no C# operations required. There is zero reason to escalate beyond Tier 1.

---

## 2. V2 Module Chain

### Module 1: DataSourcing
- **resultName:** `wire_transfers`
- **schema:** `datalake`
- **table:** `wire_transfers`
- **columns:** `["amount"]`
- **Effective dates:** Hard-coded to `minEffectiveDate: "2024-10-01"` and `maxEffectiveDate: "2024-12-31"`.
- **Note:** V1 hard-codes these dates (BR-1). This is normally an AP10 violation, but because the job's business requirement is to produce output spanning the full Q4 2024 range on every single execution (regardless of which effective date the executor injects), V2 must preserve the hard-coded dates to match V1 output. If V2 relied on executor-injected dates, each daily execution would only source that single day's data, producing a single-row aggregation per execution instead of the full quarter's daily breakdown. The hard-coded dates ARE the business logic here. See Anti-Pattern Analysis (Section 3) for full discussion.
- **Column reduction (AP4):** V1 sources `["wire_id", "customer_id", "direction", "amount", "status", "wire_timestamp"]` but the SQL only uses `amount` (via `SUM`) and the auto-appended `as_of` column (for GROUP BY, WHERE, and SELECT). The remaining five columns (`wire_id`, `customer_id`, `direction`, `status`, `wire_timestamp`) are never referenced. V2 sources only `["amount"]`. The `as_of` column is automatically appended by the DataSourcing module when not listed in `columns` (see `DataSourcing.cs:69-72`).

### Module 2: Transformation
- **resultName:** `daily_vol`
- **sql:** See Section 5 below.

### Module 3: CsvFileWriter
- **source:** `daily_vol`
- **outputFile:** `Output/double_secret_curated/daily_wire_volume.csv`
- **includeHeader:** `true`
- **writeMode:** `Append`
- **lineEnding:** `LF`
- **trailerFormat:** not specified (no trailer)

---

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles to Reproduce

| W-Code | Applies? | Analysis |
|--------|----------|----------|
| W1 (Sunday skip) | No | No day-of-week logic in this job. |
| W2 (Weekend fallback) | No | No day-of-week logic. |
| W3a/b/c (Boundary summaries) | No | No summary rows appended. |
| W4 (Integer division) | No | No division operations. |
| W5 (Banker's rounding) | No | `ROUND(SUM(amount), 2)` is the only rounding. SQLite ROUND uses standard arithmetic rounding, not banker's rounding. Both V1 and V2 pass through the same SQLite ROUND function. |
| W6 (Double epsilon) | No | `amount` is numeric from PostgreSQL, loaded into SQLite as REAL. Both V1 and V2 go through the same SQLite SUM/ROUND pipeline so epsilon behavior is identical. |
| W7 (Trailer inflated count) | No | No trailer in this job. |
| W8 (Trailer stale date) | No | No trailer in this job. |
| W9 (Wrong writeMode) | **Possibly** | V1 uses Append mode with a hard-coded date range covering the full quarter. Every execution appends the same full-quarter aggregation. This produces duplicate data on re-runs. Whether this is "wrong" is ambiguous, but it is V1's behavior and V2 must match it. `writeMode: "Append"` is preserved. |
| W10 (Absurd numParts) | No | Not a Parquet job. |
| W12 (Header every append) | No | The framework's CsvFileWriter only writes the header on the first write (when the file does not yet exist). On subsequent Append calls, the header is suppressed (see `CsvFileWriter.cs:47`: `if (_includeHeader && !append)`). V1 and V2 share this framework behavior. |

**Duplicate as_of column (BR-6):** The V1 SQL `SELECT as_of AS wire_date, ..., as_of` produces a duplicate column. The output contains both `wire_date` (aliased from `as_of`) and `as_of` as separate columns with identical values. This is an output-affecting quirk that V2 must reproduce. The V2 SQL preserves this exact SELECT list.

### Code-Quality Anti-Patterns to Eliminate

| AP Code | Applies? | V1 Problem | V2 Action |
|---------|----------|------------|-----------|
| AP1 (Dead-end sourcing) | No | Only one table sourced, and it is used. | N/A |
| AP2 (Duplicated logic) | No | No cross-job duplication identified. | N/A |
| AP3 (Unnecessary External) | No | V1 already uses Tier 1. | N/A |
| AP4 (Unused columns) | **Yes** | V1 sources `wire_id`, `customer_id`, `direction`, `status`, `wire_timestamp` -- none of which are referenced in the SQL transformation. Only `amount` and the auto-appended `as_of` are used. | **Eliminated.** V2 sources only `["amount"]`. The `as_of` column is automatically appended by DataSourcing. Evidence: [daily_wire_volume.json:10] lists 6 columns; [daily_wire_volume.json:17] SQL references only `amount`, `as_of`, and `COUNT(*)`. |
| AP5 (Asymmetric NULLs) | No | No NULL handling transformations. GROUP BY naturally excludes dates with no data. | N/A |
| AP6 (Row-by-row iteration) | No | No External module. | N/A |
| AP7 (Magic values) | No | No hardcoded thresholds or magic strings in the SQL beyond the date range, which is discussed under AP10. | N/A |
| AP8 (Complex SQL / unused CTEs) | **Yes** | The V1 SQL contains a redundant `WHERE as_of >= '2024-10-01' AND as_of <= '2024-12-31'` clause that duplicates the DataSourcing effective date filter. DataSourcing already constrains the data to exactly this date range at the PostgreSQL level. | **Eliminated.** V2 SQL removes the redundant WHERE clause. Since DataSourcing hard-codes the same date range (`minEffectiveDate: "2024-10-01"`, `maxEffectiveDate: "2024-12-31"`), the SQLite table `wire_transfers` already contains ONLY rows within that range. The WHERE clause filters nothing and can be safely removed without affecting output. Evidence: [daily_wire_volume.json:11-12] hard-codes dates; [DataSourcing.cs:74-78] applies `WHERE as_of >= @minDate AND as_of <= @maxDate` at query time. |
| AP9 (Misleading names) | No | Job name accurately describes output. | N/A |
| AP10 (Over-sourcing dates) | **Partially** | V1 hard-codes `minEffectiveDate` and `maxEffectiveDate` in the DataSourcing config, bypassing executor date injection. Normally this is AP10 and should be eliminated. However, the business requirement is to produce the full Q4 2024 aggregation on every execution. If we relied on executor-injected dates, each run would only cover a single day's effective date, producing a one-row output per execution instead of the full quarter summary. The hard-coded dates are the business logic, not an anti-pattern. | **Preserved with documentation.** The hard-coded dates remain because they define the job's semantic scope. The redundant SQL WHERE clause (the true AP10/AP8 symptom) is eliminated. Comment in FSD: "V1 hard-codes Q4 2024 date range in DataSourcing. This is intentional -- the job always produces the full-quarter aggregation regardless of executor effective date." |

**Summary:** Three anti-patterns identified. AP4 (unused columns) is fully eliminated by reducing from 6 sourced columns to 1. AP8 (redundant SQL WHERE clause) is fully eliminated by removing the redundant filter. AP10 is partially addressed -- the redundant SQL filter is removed, but the hard-coded DataSourcing dates are preserved because they are the business logic. The duplicate `as_of` column (BR-6) is an output-affecting quirk that is preserved for output equivalence.

---

## 4. Output Schema

| Column | Source Table.Column | Transformation | Notes |
|--------|-------------------|----------------|-------|
| wire_date | wire_transfers.as_of | Aliased from `as_of`, used as GROUP BY key | DATE. One row per distinct `as_of` date. |
| wire_count | Computed | `COUNT(*)` per `as_of` date | INTEGER. Counts all wire transfers regardless of status or direction (BR-4). |
| total_amount | Computed | `ROUND(SUM(amount), 2)` per `as_of` date | REAL. Sum of all wire amounts for the date, rounded to 2 decimal places (BR-3). |
| as_of | wire_transfers.as_of | Direct passthrough from GROUP BY | DATE. Duplicate of `wire_date` (BR-6). Preserved for output equivalence. |

**Output ordering:** Ascending by `as_of` (BR-5).

**Row count:** One row per distinct `as_of` date in the 2024-10-01 to 2024-12-31 range that has wire transfer data. Dates with zero wire transfers produce no output row.

---

## 5. SQL Design

**V1 SQL (for reference):**
```sql
SELECT as_of AS wire_date, COUNT(*) AS wire_count, ROUND(SUM(amount), 2) AS total_amount, as_of
FROM wire_transfers
WHERE as_of >= '2024-10-01' AND as_of <= '2024-12-31'
GROUP BY as_of
ORDER BY as_of
```

**V2 SQL:**
```sql
SELECT as_of AS wire_date,
       COUNT(*) AS wire_count,
       ROUND(SUM(amount), 2) AS total_amount,
       as_of
FROM wire_transfers
GROUP BY as_of
ORDER BY as_of
```

**Design notes:**
- The redundant `WHERE as_of >= '2024-10-01' AND as_of <= '2024-12-31'` is removed (AP8 elimination). DataSourcing already constrains the date range to 2024-10-01 through 2024-12-31 at the PostgreSQL level. The SQLite table `wire_transfers` contains ONLY rows in that range, making the WHERE clause a no-op.
- `as_of` appears twice in the SELECT: once aliased as `wire_date` and once as bare `as_of`. This duplicates the same value in the output (BR-6). Preserved for output equivalence. `// V1 quirk: as_of appears as both wire_date and as_of in the output. Duplicate preserved for output equivalence.`
- `COUNT(*)` counts all wire transfers regardless of `status` or `direction` (BR-4). No filtering is applied.
- `ROUND(SUM(amount), 2)` rounds the sum to 2 decimal places (BR-3). SQLite's ROUND uses standard arithmetic rounding.
- `GROUP BY as_of` produces one row per date; `ORDER BY as_of` sorts ascending (BR-5).
- The `wire_transfers` table name in SQL refers to the DataSourcing result registered in SQLite shared state, not the PostgreSQL table directly.

---

## 6. V2 Job Config

```json
{
  "jobName": "DailyWireVolumeV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "wire_transfers",
      "schema": "datalake",
      "table": "wire_transfers",
      "columns": ["amount"],
      "minEffectiveDate": "2024-10-01",
      "maxEffectiveDate": "2024-12-31"
    },
    {
      "type": "Transformation",
      "resultName": "daily_vol",
      "sql": "SELECT as_of AS wire_date, COUNT(*) AS wire_count, ROUND(SUM(amount), 2) AS total_amount, as_of FROM wire_transfers GROUP BY as_of ORDER BY as_of"
    },
    {
      "type": "CsvFileWriter",
      "source": "daily_vol",
      "outputFile": "Output/double_secret_curated/daily_wire_volume.csv",
      "includeHeader": true,
      "writeMode": "Append",
      "lineEnding": "LF"
    }
  ]
}
```

---

## 7. Writer Configuration

| Parameter | Value | Matches V1? |
|-----------|-------|-------------|
| Writer type | CsvFileWriter | Yes |
| source | `daily_vol` | Yes |
| outputFile | `Output/double_secret_curated/daily_wire_volume.csv` | Path changed to V2 output directory per project conventions |
| includeHeader | `true` | Yes |
| writeMode | `Append` | Yes |
| lineEnding | `LF` | Yes |
| trailerFormat | (not specified) | Yes -- V1 has no trailer |

**Write mode implications:** Append mode means each execution adds rows to the existing file. The header is written only on the first execution (when the file does not yet exist), per `CsvFileWriter.cs:47`. Since DataSourcing uses hard-coded dates spanning the full Q4 2024 range, every execution appends the SAME full-quarter aggregation. Running across the date range (2024-10-01 through 2024-12-31) produces 92 identical copies of the same ~60-90 data rows. This matches V1 behavior exactly.

---

## 8. Proofmark Config Design

**Starting position:** Zero exclusions, zero fuzzy overrides.

**Analysis of non-deterministic fields:**
- None identified in the BRD. All aggregations (`COUNT(*)`, `ROUND(SUM(amount), 2)`) are deterministic given the same input data.

**Analysis of floating-point concerns:**
- `total_amount` uses `ROUND(SUM(amount), 2)` in SQLite. Both V1 and V2 execute through the same SQLite Transformation pipeline. The `amount` values come from PostgreSQL `numeric` type, which DataSourcing loads via Npgsql. They are registered into SQLite as REAL. The SUM and ROUND operations in SQLite produce identical results for V1 and V2 because both use the exact same data path. No fuzzy matching needed unless comparison fails.

**CSV structure:**
- `includeHeader: true` -> `header_rows: 1`
- No trailer -> `trailer_rows: 0`
- Append mode: header is only at the very top of the file (first write). Subsequent appended blocks have no header. Using `header_rows: 1` correctly strips the single header row from the top.

**Proposed config:**

```yaml
comparison_target: "daily_wire_volume"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

**Rationale:**
- `reader: csv` -- V1 uses CsvFileWriter
- `header_rows: 1` -- V1 has `includeHeader: true`, header written once at file top
- `trailer_rows: 0` -- V1 has no trailer
- No excluded columns -- all fields are deterministic
- No fuzzy columns -- SQLite arithmetic is identical between V1 and V2; start strict per best practices
- `threshold: 100.0` -- require exact match

**Contingency:** If `total_amount` causes comparison failure due to floating-point representation differences, add:
```yaml
columns:
  fuzzy:
    - name: "total_amount"
      tolerance: 0.01
      tolerance_type: absolute
      reason: "SQLite REAL arithmetic on SUM(amount) may produce epsilon-level differences. [daily_wire_volume.json:17] [BRD BR-3]"
```

---

## 9. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|-------------|----------------|----------|
| Tier 1 (DataSourcing -> Transformation -> CsvFileWriter) | Entire BRD -- all logic is SQL-expressible | V1 config uses this exact chain [daily_wire_volume.json:4-27] |
| Hard-coded `minEffectiveDate: "2024-10-01"` / `maxEffectiveDate: "2024-12-31"` | BR-1: Hard-coded dates | [daily_wire_volume.json:11-12] |
| Redundant SQL WHERE removed | BR-2: Redundant SQL filter | [daily_wire_volume.json:17] vs [daily_wire_volume.json:11-12]; AP8 elimination |
| GROUP BY as_of with COUNT/SUM/ROUND | BR-3: GROUP BY as_of with aggregations | [daily_wire_volume.json:17] |
| No filter on status or direction | BR-4: All statuses included | [daily_wire_volume.json:17] -- no WHERE on status/direction |
| ORDER BY as_of ascending | BR-5: Results ordered by as_of | [daily_wire_volume.json:17] |
| Duplicate as_of in SELECT (wire_date + as_of) | BR-6: Duplicate as_of column | [daily_wire_volume.json:17] -- `SELECT as_of AS wire_date, ..., as_of` |
| CsvFileWriter with Append/LF/header/no trailer | BRD Writer Configuration | [daily_wire_volume.json:20-26] |
| Columns reduced from 6 to 1 (`amount` only) | AP4: Unused columns eliminated | [daily_wire_volume.json:10] sources 6 cols; SQL uses only `amount` and auto-appended `as_of` |
| Redundant WHERE clause removed from SQL | AP8: Complex SQL simplified | [daily_wire_volume.json:17] WHERE duplicates [daily_wire_volume.json:11-12] date range |
| Proofmark starts strict (no exclusions, no fuzzy) | BRD: No non-deterministic fields | All aggregations deterministic given same input |

---

## 10. External Module Design

**Not applicable.** This is a Tier 1 job. No External module is needed. All business logic is expressed in the Transformation SQL.
