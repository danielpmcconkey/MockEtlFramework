# WireTransferDaily -- Functional Specification Document

## 1. Job Summary

WireTransferDailyV2 aggregates wire transfer activity by date, producing daily counts, total amounts, and average amounts per effective date. When the effective date falls on the last day of a month, a special "MONTHLY_TOTAL" summary row is appended. Output is a Parquet file (1 part, Overwrite mode) per effective date. V1 uses an unnecessary External module for logic that is fully expressible in SQL; V2 replaces it with a Tier 1 framework-only chain (DataSourcing -> Transformation -> ParquetFileWriter), eliminating AP1, AP3, AP4, and AP6 while faithfully reproducing the W3b month-end boundary behavior.

**Tier: 1 (Framework Only)**
`DataSourcing -> Transformation (SQL) -> ParquetFileWriter`

**Justification:** V1's External module (`WireTransferDailyProcessor.cs`) performs a GROUP BY aggregation with a conditional MONTHLY_TOTAL summary row. Both operations are expressible in SQL. The GROUP BY is trivial. The month-end detection can be achieved with SQLite date functions: `strftime('%d', MAX(as_of), '+1 day') = '01'` checks whether the max date in the sourced data is the last day of its month. Since DataSourcing constrains the data to the executor-injected effective date range, `MAX(as_of)` equals `__maxEffectiveDate` in normal operation. This eliminates the need for an External module entirely (AP3). No procedural logic, no shared-state access beyond what DataSourcing provides, no cross-boundary date queries required. Tier 1 is sufficient.

---

## 2. V2 Module Chain

### Module 1: DataSourcing
- **resultName:** `wire_transfers`
- **schema:** `datalake`
- **table:** `wire_transfers`
- **columns:** `["wire_id", "amount"]`
- **Effective dates:** Injected by executor at runtime via `__minEffectiveDate` / `__maxEffectiveDate`. No hard-coded dates.
- **Column reduction (AP4):** V1 sources `["wire_id", "customer_id", "account_id", "direction", "amount", "wire_timestamp", "status"]` (7 columns). The External module only uses `as_of` (auto-appended by DataSourcing) and `amount` (for SUM). V2 sources only `["wire_id", "amount"]`. The `wire_id` column is included solely because `COUNT(*)` needs to count rows -- alternatively `COUNT(*)` works on any column, but `wire_id` is the natural primary key and costs negligible overhead. The `as_of` column is automatically appended by DataSourcing when not listed in `columns`. Evidence: [WireTransferDailyProcessor.cs:34-35] accesses only `row["as_of"]` and `row["amount"]`.
- **Note:** V1 also sources an `accounts` table (AP1 dead-end sourcing) which is never referenced by the External module. V2 eliminates this entirely. Evidence: [WireTransferDailyProcessor.cs] contains no reference to `accounts`; [wire_transfer_daily.json:14-18] sources it but it's unused; [BRD BR-6].

### Module 2: Transformation
- **resultName:** `output`
- **sql:** See Section 4 below.

### Module 3: ParquetFileWriter
- **source:** `output`
- **outputDirectory:** `Output/double_secret_curated/wire_transfer_daily/`
- **numParts:** 1
- **writeMode:** `Overwrite`

---

## 3. DataSourcing Config

### Table: wire_transfers

| Property | Value |
|----------|-------|
| resultName | `wire_transfers` |
| schema | `datalake` |
| table | `wire_transfers` |
| columns | `["wire_id", "amount"]` |
| minEffectiveDate | (not specified -- injected by executor) |
| maxEffectiveDate | (not specified -- injected by executor) |
| additionalFilter | (not specified) |

**Effective date handling:** The executor injects `__minEffectiveDate` and `__maxEffectiveDate` into shared state before each pipeline run. DataSourcing reads these keys and applies `WHERE as_of >= @minDate AND as_of <= @maxDate` at the PostgreSQL level. With daily gap-fill, both dates are the same single date, so the sourced data contains exactly one `as_of` date per execution. The `as_of` column is auto-appended by DataSourcing.

**Eliminated table: accounts.** V1 sources `datalake.accounts` with columns `["account_id", "customer_id", "account_type"]` but the External module never references this DataFrame. This is AP1 (dead-end sourcing). V2 does not source `accounts` at all. Evidence: [WireTransferDailyProcessor.cs] -- no reference to `accounts` DataFrame; [BRD BR-6].

---

## 4. Transformation SQL

**V1 approach (for reference):** The V1 External module (`WireTransferDailyProcessor.cs`) performs:
1. Row-by-row iteration over `wire_transfers` rows, grouping by `as_of` into a `Dictionary<object, (int count, decimal total)>` (AP6)
2. For each group: compute `wire_count`, `total_amount` (rounded to 2dp), `avg_amount` (rounded to 2dp)
3. If `__maxEffectiveDate` is the last day of its month, append a MONTHLY_TOTAL summary row

**V2 SQL:**

```sql
SELECT
    as_of AS wire_date,
    COUNT(*) AS wire_count,
    ROUND(SUM(amount), 2) AS total_amount,
    ROUND(SUM(amount) / COUNT(*), 2) AS avg_amount,
    as_of
FROM wire_transfers
WHERE as_of IS NOT NULL
GROUP BY as_of

UNION ALL

SELECT
    'MONTHLY_TOTAL' AS wire_date,
    COUNT(*) AS wire_count,
    ROUND(SUM(amount), 2) AS total_amount,
    ROUND(SUM(amount) / COUNT(*), 2) AS avg_amount,
    MAX(as_of) AS as_of
FROM wire_transfers
WHERE as_of IS NOT NULL
  AND strftime('%d', MAX(as_of), '+1 day') = '01'
HAVING COUNT(*) > 0
  AND strftime('%d', MAX(as_of), '+1 day') = '01'
```

**Design notes:**

1. **UNION ALL structure:** The first SELECT produces daily aggregation rows (one per `as_of` date). The second SELECT conditionally produces a single MONTHLY_TOTAL summary row. This replaces the V1 External module's two-phase logic (AP3 elimination).

2. **Month-end detection:** `strftime('%d', MAX(as_of), '+1 day') = '01'` checks whether adding one day to the max date results in day '01' (i.e., the first of the next month). This is equivalent to V1's `maxDate.Day == DateTime.DaysInMonth(maxDate.Year, maxDate.Month)`. It correctly handles variable-length months and leap years. Evidence: [WireTransferDailyProcessor.cs:65]. Note: the `WHERE` clause with `strftime` on `MAX(as_of)` won't work as an aggregate in WHERE -- the actual filtering happens in the `HAVING` clause. The WHERE clause filters out NULL `as_of` rows (BR-8 replication), and the HAVING clause performs both the count check (BR-7: no MONTHLY_TOTAL on empty input) and the month-end check (BR-4/BR-5: W3b).

3. **`MAX(as_of)` as proxy for `__maxEffectiveDate`:** In normal executor operation with daily gap-fill, DataSourcing constrains data to a single effective date, so `MAX(as_of)` equals `__maxEffectiveDate`. For multi-day ranges, `MAX(as_of)` still equals `__maxEffectiveDate` because DataSourcing filters `as_of <= __maxEffectiveDate`. Evidence: [Architecture.md] executor gap-fills one day at a time; [DataSourcing.cs] applies `WHERE as_of >= @minDate AND as_of <= @maxDate`.

4. **NULL as_of filtering:** `WHERE as_of IS NOT NULL` replicates V1's `if (asOf == null) continue;` behavior (BR-8). DataSourcing's PostgreSQL-level filter `as_of >= @minDate` already excludes NULLs (NULL comparisons return false), so the explicit IS NOT NULL is defensive but harmless.

5. **`avg_amount` computation:** `ROUND(SUM(amount) / COUNT(*), 2)` computes the average as total/count rounded to 2 decimal places. V1 uses `Math.Round(totalAmount / wireCount, 2)` with the default `MidpointRounding.ToEven` (banker's rounding). SQLite ROUND uses standard arithmetic rounding (round half away from zero). For this dataset (amounts in range ~1012-49959, counts ~35-62 per date), the probability of hitting an exact midpoint (x.xx5000...) is negligible. If a comparison mismatch arises from this difference, it will be addressed in the resolution phase. Evidence: [WireTransferDailyProcessor.cs:52, 75].

6. **`as_of` output column:** Both daily rows and MONTHLY_TOTAL rows include `as_of` as the final column. For daily rows, `as_of` equals `wire_date` (BR-3). For MONTHLY_TOTAL, `as_of` is `MAX(as_of)` which equals `__maxEffectiveDate` (BR-5). Evidence: [WireTransferDailyProcessor.cs:56, 76].

7. **Mixed-type `wire_date` column:** Daily rows have `wire_date` set to a date value (from `as_of`). The MONTHLY_TOTAL row has `wire_date` set to the string literal `'MONTHLY_TOTAL'`. SQLite's dynamic typing handles this naturally. The resulting Parquet column will contain mixed types, matching V1 behavior. Evidence: [WireTransferDailyProcessor.cs:55, 72]; [BRD Edge Case 2].

8. **Row ordering:** V1 iterates a `Dictionary<object, ...>` whose order is non-deterministic (BRD Edge Case 6). However, with daily gap-fill producing a single-day effective range, there is exactly one daily group -- so ordering is irrelevant for daily rows. The MONTHLY_TOTAL row is always appended after daily rows in V1, and the UNION ALL structure ensures the same ordering in V2. For multi-day ranges, V1's dictionary iteration order is insertion order (C# Dictionary preserves insertion order for small collections), which follows the row scan order of `wireTransfers.Rows`. V2's `GROUP BY as_of` with no explicit `ORDER BY` in the first SELECT leaves ordering to SQLite, which typically returns groups in encounter order. This should produce equivalent results. If Proofmark detects ordering mismatches, an explicit `ORDER BY` can be added.

**Revised SQL (correcting the aggregate-in-WHERE issue):**

The `strftime` on `MAX(as_of)` cannot appear in a WHERE clause (it's an aggregate). The correct approach:

```sql
SELECT
    as_of AS wire_date,
    COUNT(*) AS wire_count,
    ROUND(SUM(amount), 2) AS total_amount,
    ROUND(SUM(amount) / COUNT(*), 2) AS avg_amount,
    as_of
FROM wire_transfers
WHERE as_of IS NOT NULL
GROUP BY as_of

UNION ALL

SELECT
    'MONTHLY_TOTAL' AS wire_date,
    COUNT(*) AS wire_count,
    ROUND(SUM(amount), 2) AS total_amount,
    ROUND(SUM(amount) / COUNT(*), 2) AS avg_amount,
    MAX(as_of) AS as_of
FROM wire_transfers
WHERE as_of IS NOT NULL
HAVING COUNT(*) > 0
  AND strftime('%d', MAX(as_of), '+1 day') = '01'
```

The second SELECT uses no GROUP BY (aggregates the entire table), with the HAVING clause performing both the emptiness guard (`COUNT(*) > 0`) and month-end check. When the HAVING condition is false, the second SELECT produces zero rows -- the MONTHLY_TOTAL row is not emitted. This is the final SQL.

---

## 5. Writer Config

| Parameter | Value | Matches V1? |
|-----------|-------|-------------|
| Writer type | ParquetFileWriter | Yes |
| source | `output` | Yes |
| outputDirectory | `Output/double_secret_curated/wire_transfer_daily/` | Path changed to V2 output directory per project conventions |
| numParts | `1` | Yes |
| writeMode | `Overwrite` | Yes |

**Write mode implications:** Overwrite mode means each effective date run replaces the entire output directory. In multi-day gap-fill scenarios, only the last day's output survives. The MONTHLY_TOTAL row only appears if the final effective date in the gap-fill is a month-end date. This matches V1 behavior exactly. Evidence: [wire_transfer_daily.json:25-29]; [BRD Write Mode Implications].

---

## 6. Wrinkle Replication

| W-Code | Applies? | V2 Replication Strategy |
|--------|----------|------------------------|
| W1 (Sunday skip) | No | No day-of-week logic in this job. |
| W2 (Weekend fallback) | No | No weekend date manipulation. |
| W3a (End-of-week boundary) | No | No weekly summary rows. |
| **W3b (End-of-month boundary)** | **Yes** | **Replicated.** The UNION ALL's second SELECT conditionally produces a MONTHLY_TOTAL row when `strftime('%d', MAX(as_of), '+1 day') = '01'`. This is the clean SQL equivalent of V1's `if (maxDate.Day == DateTime.DaysInMonth(maxDate.Year, maxDate.Month))`. The summary row contains: `wire_date = 'MONTHLY_TOTAL'`, `wire_count = COUNT(*)` (sum of all wires in effective range), `total_amount = ROUND(SUM(amount), 2)`, `avg_amount = ROUND(SUM(amount) / COUNT(*), 2)`, `as_of = MAX(as_of)`. Evidence: [WireTransferDailyProcessor.cs:65-77]; [BRD BR-4, BR-5]. |
| W3c (End-of-quarter boundary) | No | No quarterly summary rows. |
| W4 (Integer division) | No | No integer division in this job. |
| W5 (Banker's rounding) | **Possibly** | V1 uses `Math.Round(..., 2)` which defaults to `MidpointRounding.ToEven` (banker's rounding). SQLite ROUND uses arithmetic rounding (round half away from zero). For the data ranges involved (~1012-49959 amounts, ~35-62 wires/day), exact midpoints are statistically improbable. Monitor via Proofmark. If a mismatch occurs, escalate to Tier 2 with a minimal External module that applies banker's rounding. |
| W6 (Double epsilon) | No | V1 uses `decimal` for amount accumulation (`Convert.ToDecimal`), not `double`. No floating-point epsilon issues. Evidence: [WireTransferDailyProcessor.cs:35, 40, 43]. |
| W7 (Trailer inflated count) | No | No trailer. Output is Parquet. |
| W8 (Trailer stale date) | No | No trailer. |
| W9 (Wrong writeMode) | No | Overwrite is appropriate for this job's output pattern (one Parquet directory per date). |
| W10 (Absurd numParts) | No | numParts is 1, which is reasonable. |
| W12 (Header every append) | No | Not a CSV job; not Append mode. |

---

## 7. Anti-Pattern Elimination

| AP-Code | Applies? | V1 Problem | V2 Action |
|---------|----------|------------|-----------|
| **AP1 (Dead-end sourcing)** | **Yes** | V1 sources `datalake.accounts` with columns `["account_id", "customer_id", "account_type"]` but the External module never references the `accounts` DataFrame. | **Eliminated.** V2 does not source `accounts` at all. Evidence: [WireTransferDailyProcessor.cs] -- no reference to `accounts`; [wire_transfer_daily.json:14-18]; [BRD BR-6]. |
| AP2 (Duplicated logic) | No | No cross-job duplication identified. | N/A |
| **AP3 (Unnecessary External)** | **Yes** | V1 uses an External module (`WireTransferDailyProcessor.cs`) for GROUP BY aggregation and conditional summary row logic that is expressible in SQL. | **Eliminated.** V2 uses Tier 1: DataSourcing -> Transformation (SQL) -> ParquetFileWriter. The GROUP BY aggregation is trivial SQL. The month-end MONTHLY_TOTAL row is handled via UNION ALL with a HAVING clause using SQLite date functions. Evidence: [WireTransferDailyProcessor.cs:31-44] is a GROUP BY; [WireTransferDailyProcessor.cs:65-77] is a conditional append. |
| **AP4 (Unused columns)** | **Yes** | V1 sources 7 columns (`wire_id`, `customer_id`, `account_id`, `direction`, `amount`, `wire_timestamp`, `status`) but only uses `as_of` (auto-appended) and `amount`. | **Eliminated.** V2 sources only `["wire_id", "amount"]`. The remaining 5 columns (`customer_id`, `account_id`, `direction`, `wire_timestamp`, `status`) are removed. Evidence: [wire_transfer_daily.json:10]; [WireTransferDailyProcessor.cs:34-35] accesses only `row["as_of"]` and `row["amount"]`. |
| AP5 (Asymmetric NULLs) | No | NULL `as_of` is silently skipped (consistent behavior). | N/A -- V2's `WHERE as_of IS NOT NULL` replicates this. |
| **AP6 (Row-by-row iteration)** | **Yes** | V1 uses `foreach` over `wireTransfers.Rows` with a `Dictionary<object, ...>` to accumulate group totals. | **Eliminated.** V2 uses SQL `GROUP BY` and aggregate functions (`COUNT`, `SUM`, `ROUND`). Set-based operation replaces procedural iteration. Evidence: [WireTransferDailyProcessor.cs:31-44]. |
| AP7 (Magic values) | No | No hardcoded thresholds. The string `"MONTHLY_TOTAL"` is a well-documented output value, not a magic value. | N/A |
| AP8 (Complex SQL / unused CTEs) | No | V1 has no SQL (it uses an External module). V2's SQL is straightforward with no unused CTEs. | N/A |
| AP9 (Misleading names) | No | Job name accurately describes output. | N/A |
| AP10 (Over-sourcing dates) | No | V1 relies on executor-injected effective dates (no hard-coded date range in config, no redundant SQL WHERE on dates). V2 preserves this pattern. | N/A |

**Summary:** Four anti-patterns identified and eliminated:
- **AP1**: Removed dead-end `accounts` table sourcing
- **AP3**: Replaced unnecessary External module with Tier 1 SQL
- **AP4**: Reduced from 7 sourced columns to 2 (plus auto-appended `as_of`)
- **AP6**: Replaced row-by-row `foreach` iteration with SQL GROUP BY

---

## 8. Proofmark Config

**Starting position:** Zero exclusions, zero fuzzy overrides.

**Analysis of non-deterministic fields:**
- None identified. All aggregations (`COUNT(*)`, `ROUND(SUM(amount), 2)`, `ROUND(SUM(amount)/COUNT(*), 2)`) are deterministic given the same input data. The `__maxEffectiveDate` fallback to `DateTime.Today` (BR-9) is only used when the executor doesn't inject the date, which never happens in normal operation.

**Analysis of floating-point concerns:**
- `total_amount` and `avg_amount` use `ROUND(SUM/division, 2)` in SQLite. V1 uses `Math.Round(..., 2)` with `decimal` arithmetic. SQLite uses IEEE 754 double-precision floating point. The `amount` values from PostgreSQL are `numeric` type, loaded into SQLite as REAL (double). There is a theoretical risk of epsilon-level differences between V1's `decimal` arithmetic and V2's SQLite `double` arithmetic, but `ROUND(..., 2)` should normalize small differences. Start strict; escalate to fuzzy if comparison fails.
- Rounding mode: V1's `Math.Round` defaults to banker's rounding (`MidpointRounding.ToEven`). SQLite ROUND uses arithmetic rounding. Midpoint values (exactly x.xx5) would round differently. This is unlikely with the data distribution but possible. Start strict; if comparison fails on rounding, add fuzzy tolerance.

**Proposed config:**

```yaml
comparison_target: "wire_transfer_daily"
reader: parquet
threshold: 100.0
```

**Rationale:**
- `reader: parquet` -- V1 and V2 both use ParquetFileWriter
- No excluded columns -- all fields are deterministic
- No fuzzy columns -- start strict per best practices; decimal vs. double rounding differences are unlikely to manifest given the data ranges
- `threshold: 100.0` -- require exact match

**Contingency:** If `total_amount` or `avg_amount` causes comparison failure due to decimal-vs-double arithmetic or rounding mode differences, add:
```yaml
columns:
  fuzzy:
    - name: "total_amount"
      tolerance: 0.01
      tolerance_type: absolute
      reason: "V1 uses C# decimal arithmetic with Math.Round (banker's rounding); V2 uses SQLite double arithmetic with ROUND (arithmetic rounding). Tolerance covers both epsilon and midpoint rounding differences. [WireTransferDailyProcessor.cs:51,58]"
    - name: "avg_amount"
      tolerance: 0.01
      tolerance_type: absolute
      reason: "Same decimal-vs-double and rounding mode concern as total_amount. [WireTransferDailyProcessor.cs:52,75]"
```

If fuzzy tolerance is insufficient (differences exceed 0.01), escalate to Tier 2 with a minimal External module that applies `Math.Round(..., 2, MidpointRounding.ToEven)` to the aggregated values.

---

## 9. Open Questions

1. **Rounding mode divergence (W5 risk):** V1's `Math.Round` uses banker's rounding by default. SQLite's ROUND uses arithmetic rounding. For the observed data ranges (amounts ~1012-49959, ~35-62 wires/day), hitting an exact midpoint (x.xx5000...) in `total_amount` or `avg_amount` is statistically unlikely but not impossible. If Proofmark detects a mismatch, resolution options are: (a) add fuzzy tolerance to Proofmark config, or (b) escalate to Tier 2 with a minimal External module for rounding.
   - Confidence: MEDIUM -- depends on actual data values across the 2024-10-01 to 2024-12-31 range
   - Risk: LOW -- amounts are integers or have few decimal places in practice

2. **Row ordering in multi-day effective ranges:** With daily gap-fill, each run processes a single date (one daily row + optional MONTHLY_TOTAL), so ordering is trivially correct. If the executor ever processes a multi-day range, V1's Dictionary iteration order may differ from V2's SQL GROUP BY order. Since Overwrite mode means only the last run's output survives, this is only relevant for the final effective date's run.
   - Confidence: HIGH -- single-day gap-fill makes this moot in practice
   - Risk: NEGLIGIBLE

3. **`wire_id` column necessity:** V2 sources `wire_id` alongside `amount`. Strictly speaking, `COUNT(*)` does not require `wire_id` -- it counts all rows regardless of column content. Sourcing only `["amount"]` would be even leaner. However, `wire_id` has negligible overhead and provides a natural row identifier if debugging is needed.
   - Confidence: HIGH -- functional impact is zero
   - Risk: NONE

---

## 10. V2 Job Config

```json
{
  "jobName": "WireTransferDailyV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "wire_transfers",
      "schema": "datalake",
      "table": "wire_transfers",
      "columns": ["wire_id", "amount"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT as_of AS wire_date, COUNT(*) AS wire_count, ROUND(SUM(amount), 2) AS total_amount, ROUND(SUM(amount) / COUNT(*), 2) AS avg_amount, as_of FROM wire_transfers WHERE as_of IS NOT NULL GROUP BY as_of UNION ALL SELECT 'MONTHLY_TOTAL' AS wire_date, COUNT(*) AS wire_count, ROUND(SUM(amount), 2) AS total_amount, ROUND(SUM(amount) / COUNT(*), 2) AS avg_amount, MAX(as_of) AS as_of FROM wire_transfers WHERE as_of IS NOT NULL HAVING COUNT(*) > 0 AND strftime('%d', MAX(as_of), '+1 day') = '01'"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/wire_transfer_daily/",
      "numParts": 1,
      "writeMode": "Overwrite"
    }
  ]
}
```

---

## 11. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|-------------|----------------|----------|
| Tier 1 (DataSourcing -> Transformation -> ParquetFileWriter) | BR-1 through BR-5: all logic is SQL-expressible | [WireTransferDailyProcessor.cs] -- GROUP BY + conditional append |
| Remove `accounts` DataSourcing | BR-6: accounts sourced but unused | [WireTransferDailyProcessor.cs] -- no reference to accounts; AP1 |
| Source only `["wire_id", "amount"]` | BR-1, BR-2: only as_of and amount are used | [WireTransferDailyProcessor.cs:34-35]; AP4 |
| Executor-injected effective dates (no hard-coding) | BR-9: maxEffectiveDate from shared state | [WireTransferDailyProcessor.cs:17-19]; [wire_transfer_daily.json] -- no date fields in DataSourcing |
| `WHERE as_of IS NOT NULL` | BR-8: null as_of silently skipped | [WireTransferDailyProcessor.cs:37] |
| GROUP BY as_of with COUNT/SUM/ROUND | BR-1, BR-2: group by date, aggregate count/total/avg | [WireTransferDailyProcessor.cs:31-52] |
| `wire_date = as_of`, `as_of = as_of` (same value) | BR-3: wire_date and as_of both set to group key | [WireTransferDailyProcessor.cs:55-56] |
| UNION ALL with HAVING for MONTHLY_TOTAL | BR-4, BR-5: month-end summary row (W3b) | [WireTransferDailyProcessor.cs:65-77] |
| `HAVING COUNT(*) > 0` | BR-7: no MONTHLY_TOTAL on empty input | [WireTransferDailyProcessor.cs:21-25] -- returns before monthly check |
| ParquetFileWriter, 1 part, Overwrite | BRD Writer Configuration | [wire_transfer_daily.json:25-29] |
| SQL GROUP BY replaces External module | AP3: unnecessary External eliminated | [WireTransferDailyProcessor.cs] -- all logic is aggregation |
| SQL set operations replace foreach | AP6: row-by-row iteration eliminated | [WireTransferDailyProcessor.cs:32-44] -- foreach loop |
| Proofmark: strict parquet, 100% threshold | BRD: no non-deterministic fields | All aggregations deterministic |
