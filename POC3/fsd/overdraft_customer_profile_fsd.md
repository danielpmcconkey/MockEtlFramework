# OverdraftCustomerProfile — Functional Specification Document

## 1. Job Summary

The `overdraft_customer_profile` job builds a per-customer overdraft profile for a single effective date. It sources overdraft events and customer data from the datalake, applies weekend fallback logic (W2: Saturday/Sunday map to the preceding Friday), filters overdraft events to the target date, groups by customer with count/sum/average aggregation, enriches with customer name via a LEFT JOIN, and writes the result as a single-part Parquet file in Overwrite mode. V2 replaces the V1 External module with a pure framework chain (Tier 1): DataSourcing pulls data, SQL Transformation handles all business logic (weekend fallback, filtering, joining, grouping, aggregation), and ParquetFileWriter produces the output.

---

## 2. V2 Module Chain

### Tier Selection: Tier 1 — Framework Only (DEFAULT)

`DataSourcing → Transformation (SQL) → ParquetFileWriter`

**Tier justification:** All V1 business logic can be expressed in a single SQL statement within the Transformation module:

- **Weekend fallback (W2):** Computable in SQL via `strftime('%w', as_of)` to detect day-of-week and `date()` to adjust. Since the executor runs one effective date at a time (min=max), every row in the sourced data shares the same `as_of` value, which IS the effective date. No need to access `__maxEffectiveDate` from shared state.
- **Date filtering (BR-2):** `WHERE` clause in SQL.
- **Customer lookup (BR-3):** `LEFT JOIN` with `MAX(as_of)` subquery.
- **Grouping/aggregation (BR-4, BR-5):** `GROUP BY`, `COUNT`, `SUM`, `ROUND`.
- **Missing customer fallback (BR-8):** `COALESCE` with empty string defaults.

The V1 External module (`OverdraftCustomerProfileProcessor.cs`, 103 lines of procedural C#) is a textbook AP3 violation — row-by-row iteration (AP6) for logic that belongs in SQL.

**Rounding risk note (W5):** V1 uses `Math.Round(value, 2)` which defaults to `MidpointRounding.ToEven` (banker's rounding). SQLite's `ROUND(value, 2)` uses round-half-away-from-zero. These differ only when the third decimal is exactly 5 (e.g., 123.445). For this dataset, where `avg_overdraft = SUM(overdraft_amount) / COUNT(*)` over small sets of monetary values, the probability of hitting an exact midpoint is low. If Proofmark detects a midpoint rounding mismatch, escalate to Tier 2 (see Section 9, Open Question #2).

### Module Chain

```
DataSourcing (overdraft_events)
  → DataSourcing (customers)
  → Transformation (SQL: filter, join, group, aggregate)
  → ParquetFileWriter (Parquet, 1 part, Overwrite)
```

---

## 3. DataSourcing Config

### Table 1: overdraft_events

| Property | Value |
|----------|-------|
| resultName | `overdraft_events` |
| schema | `datalake` |
| table | `overdraft_events` |
| columns | `["customer_id", "overdraft_amount"]` |

**Changes from V1:**
- **Removed columns:** `overdraft_id`, `account_id`, `fee_amount`, `fee_waived`, `event_timestamp` — none are referenced by V1's External module processing logic. Evidence: [OverdraftCustomerProfileProcessor.cs:42-96] only `customer_id`, `overdraft_amount`, and `as_of` are accessed. This eliminates AP4.
- **as_of column:** Automatically appended by DataSourcing since it is not in the columns list.
- **Effective dates:** Injected at runtime by the executor via `__minEffectiveDate` / `__maxEffectiveDate`. No hardcoded dates (BR-10).

### Table 2: customers

| Property | Value |
|----------|-------|
| resultName | `customers` |
| schema | `datalake` |
| table | `customers` |
| columns | `["id", "first_name", "last_name"]` |

**Changes from V1:**
- **Removed columns:** `prefix`, `suffix`, `birthdate` — sourced in V1 but never used. Evidence: [OverdraftCustomerProfileProcessor.cs:58-59] only `first_name` and `last_name` are extracted; [overdraft_customer_profile.json:17] V1 sources `prefix`, `suffix`, `birthdate`. This eliminates AP4.
- **as_of column:** Automatically appended by DataSourcing.
- **Effective dates:** Injected at runtime.

### Table 3: accounts — REMOVED

V1 sources `datalake.accounts` (`account_id`, `customer_id`, `account_type`, `account_status`) but the External module never accesses `sharedState["accounts"]`. Evidence: [OverdraftCustomerProfileProcessor.cs:32] comment `AP1: accounts sourced but never used (dead-end)`; [overdraft_customer_profile.json:20-25] config entry with no downstream consumer. This eliminates AP1.

**V2 does NOT source the accounts table.**

---

## 4. Transformation SQL

**resultName:** `output`

```sql
-- W2: Weekend fallback — Saturday->Friday, Sunday->Friday, weekdays->as-is
-- strftime('%w', date) returns: 0=Sunday, 1=Monday, ..., 6=Saturday
-- With single-day execution (min=max effective date), all rows share the same as_of,
-- which IS the effective date. LIMIT 1 extracts it for the weekend adjustment.
WITH effective AS (
    SELECT
        CASE CAST(strftime('%w', as_of) AS INTEGER)
            WHEN 6 THEN date(as_of, '-1 day')   -- Saturday -> Friday
            WHEN 0 THEN date(as_of, '-2 days')   -- Sunday -> Friday
            ELSE as_of
        END AS target_date,
        as_of AS source_date
    FROM overdraft_events
    LIMIT 1
),
-- BR-3: Customer lookup — use latest name per customer id.
-- With single-day effective dates, there is at most one row per customer per as_of.
-- GROUP BY id + HAVING as_of = MAX(as_of) selects the latest snapshot per customer.
-- COALESCE handles missing customers (BR-8): defaults to empty string.
customer_lookup AS (
    SELECT
        id,
        first_name,
        last_name
    FROM customers
    GROUP BY id
    HAVING as_of = MAX(as_of)
),
-- BR-2: Filter overdraft events to target date only.
-- On weekdays, target_date == source_date (same as_of), so all rows match.
-- On weekends, target_date is Friday but source_date is Sat/Sun — zero rows match.
-- This replicates V1's empty-output behavior on weekends (EC-1).
filtered_events AS (
    SELECT oe.customer_id, oe.overdraft_amount
    FROM overdraft_events oe, effective e
    WHERE oe.as_of = e.target_date
),
-- BR-4: Group by customer, compute count and total
aggregated AS (
    SELECT
        customer_id,
        COUNT(*) AS overdraft_count,
        SUM(overdraft_amount) AS total_overdraft_amount
    FROM filtered_events
    GROUP BY customer_id
)
-- Final output: join with customer names, compute average, add as_of
SELECT
    a.customer_id,
    COALESCE(c.first_name, '') AS first_name,       -- BR-8: default empty string
    COALESCE(c.last_name, '') AS last_name,          -- BR-8: default empty string
    a.overdraft_count,
    a.total_overdraft_amount,
    -- BR-5: average overdraft, rounded to 2 decimal places
    -- W5: SQLite ROUND uses half-away-from-zero; V1 uses MidpointRounding.ToEven.
    -- Difference only at exact midpoints (X.XX5). See rounding risk note in Section 2.
    ROUND(a.total_overdraft_amount * 1.0 / a.overdraft_count, 2) AS avg_overdraft,
    -- BR-9: as_of is the target date (after weekend fallback) as yyyy-MM-dd string
    e.target_date AS as_of
FROM aggregated a
LEFT JOIN customer_lookup c ON a.customer_id = c.id
CROSS JOIN effective e
```

### SQL Design Notes

1. **Weekend fallback (W2):** The `effective` CTE extracts one row from `overdraft_events` to determine the source `as_of` date, then computes the weekend-adjusted `target_date`. Since the executor runs one day at a time (min=max), all rows share the same `as_of`, so `LIMIT 1` is safe. On weekdays, `target_date == source_date` and all rows pass the filter. On weekends, `target_date` shifts to Friday but no rows have `as_of = Friday` (they all have Saturday or Sunday), producing empty output — exactly matching V1 behavior (EC-1).

2. **Empty result on no data (EC-2):** If `overdraft_events` has zero rows for the effective date, the `effective` CTE is empty, all downstream CTEs produce zero rows, and the output is an empty DataFrame. This matches V1's early-return behavior at [OverdraftCustomerProfileProcessor.cs:35-39].

3. **Customer lookup (BR-3):** The `customer_lookup` CTE groups by `id` and selects the row with `MAX(as_of)`, replicating V1's dictionary-overwrite semantics where the last-loaded row (latest `as_of`) wins. With single-day sourcing, there is typically one row per customer, making this a degenerate case. If multiple `as_of` dates were present (wider date range), this selects the latest name per customer, matching V1 behavior. Evidence: [OverdraftCustomerProfileProcessor.cs:54-61].

4. **Missing customer fallback (BR-8):** The `LEFT JOIN` with `COALESCE(..., '')` ensures customers not found in the lookup produce empty strings for both `first_name` and `last_name`, matching V1. Evidence: [OverdraftCustomerProfileProcessor.cs:81-82].

5. **Decimal precision (EC-5):** V1 uses `decimal` arithmetic. In the Transformation module, `decimal` values map to SQLite `REAL` (double) via `GetSqliteType` at [Transformation.cs:98-104]. For the typical overdraft amounts in this dataset (monetary values with 2 decimal places and small group sizes), double precision is sufficient to produce identical results at 2 decimal places.

6. **Row ordering:** V1 iterates a `Dictionary<int, ...>` which does not guarantee order. Parquet files are unordered by nature. No explicit `ORDER BY` is needed.

---

## 5. Writer Config

| Parameter | V1 Value | V2 Value | Match? |
|-----------|----------|----------|--------|
| Writer type | `ParquetFileWriter` | `ParquetFileWriter` | Yes |
| source | `output` | `output` | Yes |
| outputDirectory | `Output/curated/overdraft_customer_profile/` | `Output/double_secret_curated/overdraft_customer_profile/` | Path change per V2 convention |
| numParts | `1` | `1` | Yes |
| writeMode | `Overwrite` | `Overwrite` | Yes |

**Write mode note:** Overwrite mode means each execution replaces the output directory. On multi-day auto-advance, only the final effective date's output survives (EC-6). This is V1's intentional behavior and is preserved as-is. No W9 applies — Overwrite is the correct mode for this job since only one date's snapshot is meaningful at a time.

---

## 6. Wrinkle Replication

### W2 — Weekend Fallback (REPLICATED)

| Aspect | Detail |
|--------|--------|
| V1 behavior | If `__maxEffectiveDate` is Saturday, uses Friday's date (maxDate - 1). If Sunday, uses Friday's date (maxDate - 2). Weekdays use the date as-is. |
| V1 evidence | [OverdraftCustomerProfileProcessor.cs:21-23] `if (maxDate.DayOfWeek == DayOfWeek.Saturday) targetDate = maxDate.AddDays(-1);` |
| V2 replication | SQL `CASE` on `strftime('%w', as_of)` in the `effective` CTE. `WHEN 6 THEN date(as_of, '-1 day')` (Saturday → Friday). `WHEN 0 THEN date(as_of, '-2 days')` (Sunday → Friday). `ELSE as_of` (weekdays unchanged). |
| Output impact | On weekdays: events are filtered to the effective date and output is produced normally. On weekends: target_date shifts to Friday but no sourced rows have that `as_of`, producing empty output. The `as_of` output column reflects the adjusted target date. |

### W5 — Banker's Rounding (RISK ACKNOWLEDGED)

| Aspect | Detail |
|--------|--------|
| V1 behavior | `Math.Round(totalAmount / count, 2)` defaults to `MidpointRounding.ToEven` (banker's rounding). |
| V1 evidence | [OverdraftCustomerProfileProcessor.cs:85] `Math.Round(kvp.Value.totalAmount / kvp.Value.count, 2)` |
| V2 approach | SQLite `ROUND(value, 2)` uses round-half-away-from-zero. Difference only at exact midpoints (X.XX5). Starting with Tier 1 and validating via Proofmark. If a midpoint mismatch is detected, escalate to Tier 2 with a minimal External module for rounding (see Section 9, Open Question #2). |

### W-codes NOT applicable

| W-code | Why not applicable |
|--------|--------------------|
| W1 (Sunday skip) | Job does not skip Sundays — it applies weekend fallback (W2) instead. |
| W3a/b/c (Boundary rows) | No summary rows appended. |
| W4 (Integer division) | Division uses decimal types. |
| W6 (Double epsilon) | V1 uses `decimal`, not `double`, for monetary accumulation. |
| W7 (Trailer inflated count) | No trailer — Parquet output. |
| W8 (Trailer stale date) | No trailer. |
| W9 (Wrong writeMode) | Overwrite is correct for single-snapshot output. |
| W10 (Absurd numParts) | numParts = 1, which is appropriate. |
| W12 (Header every append) | No CSV append. |

---

## 7. Anti-Pattern Elimination

### AP1 — Dead-End Sourcing (ELIMINATED)

| Aspect | Detail |
|--------|--------|
| V1 problem | V1 sources `datalake.accounts` (`account_id`, `customer_id`, `account_type`, `account_status`) but the External module never accesses `sharedState["accounts"]`. |
| V1 evidence | [overdraft_customer_profile.json:20-25] accounts DataSourcing config; [OverdraftCustomerProfileProcessor.cs:32] comment `AP1: accounts sourced but never used (dead-end)` |
| V2 fix | The `accounts` DataSourcing entry is removed from the V2 job config. V2 sources only `overdraft_events` and `customers`. |

### AP3 — Unnecessary External Module (ELIMINATED)

| Aspect | Detail |
|--------|--------|
| V1 problem | V1 uses a full External module (103 lines of procedural C#) for logic entirely expressible in SQL: date filtering, dictionary-based customer lookup, foreach-loop grouping, and manual aggregation. |
| V1 evidence | [OverdraftCustomerProfileProcessor.cs:1-103] entire module; all operations (filter, join, group, aggregate) are standard SQL patterns |
| V2 fix | Replaced with Tier 1 (framework only). A single SQL Transformation handles all business logic. No External module needed. |

### AP4 — Unused Columns (ELIMINATED)

| Aspect | Detail |
|--------|--------|
| V1 problem | V1 sources columns never used in processing: `overdraft_id`, `account_id`, `fee_amount`, `fee_waived`, `event_timestamp` from overdraft_events; `prefix`, `suffix`, `birthdate` from customers. |
| V1 evidence | [overdraft_customer_profile.json:10-11] overdraft_events columns; [overdraft_customer_profile.json:17] customers columns; [OverdraftCustomerProfileProcessor.cs:42-96] only `customer_id`, `overdraft_amount`, `as_of` used from events; only `id`, `first_name`, `last_name` used from customers |
| V2 fix | V2 DataSourcing configs include only the columns actually needed: `customer_id`, `overdraft_amount` for overdraft_events; `id`, `first_name`, `last_name` for customers. (`as_of` is auto-appended by the framework.) |

### AP6 — Row-by-Row Iteration (ELIMINATED)

| Aspect | Detail |
|--------|--------|
| V1 problem | V1 uses three `foreach` loops: one to build a customer lookup dictionary, one to accumulate per-customer overdraft counts and totals, and one to construct output rows. |
| V1 evidence | [OverdraftCustomerProfileProcessor.cs:55-61] customer lookup loop; [OverdraftCustomerProfileProcessor.cs:65-75] aggregation loop; [OverdraftCustomerProfileProcessor.cs:78-98] output construction loop |
| V2 fix | All logic expressed as a single SQL query using `LEFT JOIN`, `GROUP BY`, `COUNT`, `SUM`, `ROUND`, and `COALESCE`. Zero row-by-row iteration. |

### AP-codes NOT applicable

| AP-code | Why not applicable |
|---------|--------------------|
| AP2 (Duplicated logic) | No evidence of cross-job duplication for this job's specific output. |
| AP5 (Asymmetric NULLs) | NULL handling is consistent — missing customer defaults to empty strings for both `first_name` and `last_name`. |
| AP7 (Magic values) | No hardcoded thresholds or magic values. |
| AP8 (Complex SQL / unused CTEs) | V1 uses no SQL (all C#). V2 SQL is straightforward with all CTEs consumed. |
| AP9 (Misleading names) | Job name accurately describes what it produces. |
| AP10 (Over-sourcing dates) | V1 already uses executor-injected effective dates; V2 continues this. |

---

## 8. Proofmark Config

```yaml
comparison_target: "overdraft_customer_profile"
reader: parquet
threshold: 100.0
```

**Rationale for strict configuration (zero exclusions, zero fuzzy):**

- **No non-deterministic fields:** The BRD confirms output is fully deterministic given the same source data and effective date. Evidence: [BRD: Non-Deterministic Fields] "None identified."
- **No floating-point accumulation concern:** V1 uses `decimal` (not `double`) for monetary arithmetic (EC-5). The SQLite REAL conversion introduces double precision, but for the magnitudes and counts in this dataset, double is sufficient at 2 decimal places.
- **Rounding (W5):** Starting strict. If Proofmark detects a midpoint rounding difference in `avg_overdraft`, the correct response is to escalate to Tier 2 (see Open Question #2), NOT to add a fuzzy tolerance. The goal is to fix the code to match V1 output, not mask the difference.

---

## 9. Open Questions

1. **Customer lookup ordering (BR-3) — Confidence: MEDIUM.** V1's dictionary-overwrite semantics depend on the iteration order of `customers.Rows`, which depends on the order PostgreSQL returns rows from the DataSourcing query. The V2 SQL uses `GROUP BY id HAVING as_of = MAX(as_of)` to deterministically select the latest snapshot per customer. If V1's PostgreSQL row order does not follow `as_of` ascending order (e.g., due to concurrent inserts or table reorganization), the selected customer name could differ. With single-day sourcing (min=max), there is only one row per customer, so this is a non-issue in practice. Mitigation: Proofmark comparison will detect any mismatch.

2. **SQLite ROUND vs. C# Math.Round (W5) — Confidence: MEDIUM.** V1 uses `Math.Round(value, 2)` which defaults to `MidpointRounding.ToEven` (banker's rounding). SQLite's `ROUND(value, 2)` uses round-half-away-from-zero. These differ only when the third decimal is exactly 5 (e.g., 123.445 rounds to 123.44 in C# but 123.45 in SQLite). For this dataset, the probability is low but non-zero. **Escalation path if detected:** Promote to Tier 2 by inserting a minimal External module (`OverdraftCustomerProfileV2Processor.cs`) between the Transformation and ParquetFileWriter. The SQL would output an unrounded `avg_overdraft_raw` column, and the External module would apply `Math.Round(avg_overdraft_raw, 2, MidpointRounding.ToEven)` to produce the final `avg_overdraft`, then drop the raw column. This keeps the External module minimal (rounding only) while all filtering, joining, and grouping remain in SQL.

3. **Empty DataFrame schema on zero events (EC-2) — Confidence: HIGH.** V1 explicitly constructs a zero-row DataFrame with the output column list when no events match. The SQL Transformation produces an empty result set when no rows pass filtering, and the column names come from SELECT aliases. This should match, but if the Transformation module handles empty results differently (e.g., returning no columns), this would need investigation.

---

## Appendix: V2 Job Config JSON

```json
{
  "jobName": "OverdraftCustomerProfileV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "overdraft_events",
      "schema": "datalake",
      "table": "overdraft_events",
      "columns": ["customer_id", "overdraft_amount"]
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
      "sql": "WITH effective AS (SELECT CASE CAST(strftime('%w', as_of) AS INTEGER) WHEN 6 THEN date(as_of, '-1 day') WHEN 0 THEN date(as_of, '-2 days') ELSE as_of END AS target_date, as_of AS source_date FROM overdraft_events LIMIT 1), customer_lookup AS (SELECT id, first_name, last_name FROM customers GROUP BY id HAVING as_of = MAX(as_of)), filtered_events AS (SELECT oe.customer_id, oe.overdraft_amount FROM overdraft_events oe, effective e WHERE oe.as_of = e.target_date), aggregated AS (SELECT customer_id, COUNT(*) AS overdraft_count, SUM(overdraft_amount) AS total_overdraft_amount FROM filtered_events GROUP BY customer_id) SELECT a.customer_id, COALESCE(c.first_name, '') AS first_name, COALESCE(c.last_name, '') AS last_name, a.overdraft_count, a.total_overdraft_amount, ROUND(a.total_overdraft_amount * 1.0 / a.overdraft_count, 2) AS avg_overdraft, e.target_date AS as_of FROM aggregated a LEFT JOIN customer_lookup c ON a.customer_id = c.id CROSS JOIN effective e"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/overdraft_customer_profile/",
      "numParts": 1,
      "writeMode": "Overwrite"
    }
  ]
}
```

---

## Appendix: Traceability Matrix

| BRD Requirement | FSD Section | V2 Implementation |
|-----------------|-------------|-------------------|
| BR-1: Weekend fallback | Sec 4 (effective CTE), Sec 6 (W2) | SQL `CASE` on `strftime('%w', as_of)` computes target_date |
| BR-2: Filter to target date | Sec 4 (filtered_events CTE) | SQL `WHERE oe.as_of = e.target_date` |
| BR-3: Customer lookup (last-seen) | Sec 4 (customer_lookup CTE) | SQL `GROUP BY id HAVING as_of = MAX(as_of)` |
| BR-4: Group by customer | Sec 4 (aggregated CTE) | SQL `GROUP BY customer_id` with `COUNT(*)` and `SUM(overdraft_amount)` |
| BR-5: Average calculation (round 2dp) | Sec 4 (final SELECT) | SQL `ROUND(total * 1.0 / count, 2)` |
| BR-6: Dead-end accounts table | Sec 7 (AP1) | **Eliminated.** V2 does not source accounts. |
| BR-7: Unused customer columns | Sec 7 (AP4) | **Eliminated.** V2 sources only id, first_name, last_name. |
| BR-8: Missing customer fallback | Sec 4 (final SELECT) | SQL `LEFT JOIN` + `COALESCE(c.first_name, '')` |
| BR-9: as_of from target date | Sec 4 (final SELECT) | SQL `e.target_date AS as_of` (already yyyy-MM-dd from `date()`) |
| BR-10: Effective dates injected | Sec 3 (DataSourcing) | No hardcoded dates; executor injects at runtime |
| EC-1: Weekend fallback empty | Sec 4 (SQL Design Notes #1) | On weekends, filtered_events produces zero rows |
| EC-2: No events on target date | Sec 4 (SQL Design Notes #2) | Empty overdraft_events → empty effective CTE → zero output rows |
| EC-3: Dead-end data sources | Sec 7 (AP1, AP4) | **Eliminated.** No unused tables or columns. |
| EC-4: Customer name staleness | Sec 4 (customer_lookup CTE) | `MAX(as_of)` selects latest name; single-day sourcing = one row per customer |
| EC-5: Decimal precision | Sec 4 (SQL Design Notes #5) | Double precision via SQLite REAL sufficient for this dataset |
| EC-6: Overwrite multi-day | Sec 5 (Writer Config) | Preserved. V2 uses Overwrite — only last day survives. |
