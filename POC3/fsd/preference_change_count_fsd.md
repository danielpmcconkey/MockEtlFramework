# PreferenceChangeCount -- Functional Specification Document

## 1. Job Summary

**Job**: PreferenceChangeCountV2
**Config**: `preference_change_count_v2.json`
**Tier**: 1 (Framework Only) -- `DataSourcing -> Transformation (SQL) -> ParquetFileWriter`

This job produces a per-customer preference summary for each effective date. For each customer on each `as_of` date, it outputs the total count of preference rows, a flag indicating whether the customer has opted in to email marketing, and a flag indicating whether the customer has opted in to SMS marketing. Despite the name "change count", the job counts total preference rows per customer rather than tracking actual changes over time [BRD: Overview, Edge Case 3]. Output is Parquet.

**Tier Justification**: The V1 job already uses a Tier 1 chain (DataSourcing -> Transformation -> ParquetFileWriter) with no External module. The SQL transformation handles all business logic -- aggregation via COUNT(*) and MAX(CASE) expressions grouped by customer_id and as_of. The only V2 changes are eliminating dead-end sourcing, unused columns, and an unused CTE. Tier 1 remains the correct choice.

---

## 2. V2 Module Chain

```
DataSourcing (customer_preferences)
    -> Transformation (SQL: aggregate preference counts and opt-in flags)
        -> ParquetFileWriter (Output/double_secret_curated/preference_change_count/)
```

### Module 1: DataSourcing -- customer_preferences

| Property | Value |
|----------|-------|
| resultName | `customer_preferences` |
| schema | `datalake` |
| table | `customer_preferences` |
| columns | `customer_id`, `preference_type`, `opted_in` |

**Changes from V1:**

| Aspect | V1 | V2 | Reason |
|--------|----|----|--------|
| `preference_id` column | Sourced [preference_change_count.json:10] | **Removed** | AP4: Only used inside the dead RANK() window function (see AP8 below). Once the unused CTE is simplified, preference_id is no longer referenced. |
| `updated_date` column | Sourced [preference_change_count.json:10] | **Removed** | AP4: Never referenced in any SQL expression [BRD: BR-7]. Dead column. |

**Note**: The `as_of` column is NOT listed in the columns array. Per the framework's DataSourcing behavior [Architecture.md: DataSourcing], when `as_of` is not in the caller's column list, it is automatically appended to the SELECT clause and included in the output DataFrame. This matches V1 behavior -- the V1 config also omits `as_of` from the columns list, but the SQL references `cp.as_of` because DataSourcing injects it.

### Module 2: Transformation -- pref_counts

| Property | Value |
|----------|-------|
| resultName | `pref_counts` |
| sql | See Section 4 below |

### Module 3: ParquetFileWriter

| Property | Value | Evidence |
|----------|-------|----------|
| source | `pref_counts` | [preference_change_count.json:26] |
| outputDirectory | `Output/double_secret_curated/preference_change_count/` | V2 path convention |
| numParts | `1` | [preference_change_count.json:28] |
| writeMode | `Overwrite` | [preference_change_count.json:29] |

---

## 3. DataSourcing Config

### Table: customer_preferences

| Column | Type (DB) | Used In SQL | Purpose |
|--------|-----------|-------------|---------|
| customer_id | integer | GROUP BY key | Aggregation key -- one output row per customer per date |
| preference_type | varchar(25) | CASE WHEN conditions | Determines email/SMS opt-in flags |
| opted_in | boolean | CASE WHEN conditions | Determines opt-in flag value |
| as_of | date (auto-injected) | GROUP BY key | Effective date -- aggregation key |

**Effective date handling**: The V2 config omits `minEffectiveDate` and `maxEffectiveDate` from the DataSourcing module. The framework's `JobExecutorService` injects `__minEffectiveDate` and `__maxEffectiveDate` into shared state at runtime, and DataSourcing picks them up automatically to filter the `as_of` column. This is the standard framework pattern [Architecture.md: DataSourcing].

### NOT sourced in V2 (eliminated):

| Table/Column | V1 Reference | Elimination Reason |
|--------------|-------------|-------------------|
| `datalake.customers` (entire table) | [preference_change_count.json:14-18] | AP1: Dead-end sourcing. Table is loaded but never referenced in the SQL [BRD: BR-6]. |
| `customer_preferences.preference_id` | [preference_change_count.json:10] | AP4: Only used in the dead RANK() computation [BRD: BR-1]. Eliminated with AP8. |
| `customer_preferences.updated_date` | [preference_change_count.json:10] | AP4: Never referenced in any SQL expression [BRD: BR-7]. |

---

## 4. Transformation SQL

### V1 SQL (for reference)

```sql
WITH all_prefs AS (
    SELECT cp.customer_id, cp.preference_type, cp.opted_in, cp.as_of,
           RANK() OVER (PARTITION BY cp.customer_id, cp.preference_type ORDER BY cp.preference_id) AS rnk
    FROM customer_preferences cp
),
summary AS (
    SELECT customer_id,
           COUNT(*) AS preference_count,
           MAX(CASE WHEN preference_type = 'MARKETING_EMAIL' AND opted_in = 1 THEN 1 ELSE 0 END) AS has_email_opt_in,
           MAX(CASE WHEN preference_type = 'MARKETING_SMS' AND opted_in = 1 THEN 1 ELSE 0 END) AS has_sms_opt_in,
           as_of
    FROM all_prefs
    GROUP BY customer_id, as_of
)
SELECT s.customer_id, s.preference_count, s.has_email_opt_in, s.has_sms_opt_in, s.as_of
FROM summary s
```

### V2 SQL (simplified)

```sql
SELECT
    cp.customer_id,
    COUNT(*) AS preference_count,
    MAX(CASE WHEN cp.preference_type = 'MARKETING_EMAIL' AND cp.opted_in = 1 THEN 1 ELSE 0 END) AS has_email_opt_in,
    MAX(CASE WHEN cp.preference_type = 'MARKETING_SMS' AND cp.opted_in = 1 THEN 1 ELSE 0 END) AS has_sms_opt_in,
    cp.as_of
FROM customer_preferences cp
GROUP BY cp.customer_id, cp.as_of
```

### Design Rationale

The V1 SQL uses two CTEs (`all_prefs` and `summary`) where a single direct query suffices. The `all_prefs` CTE computes `RANK() OVER (PARTITION BY customer_id, preference_type ORDER BY preference_id) AS rnk`, but the `rnk` column is never referenced in the `summary` CTE or the final SELECT [BRD: BR-1]. The `all_prefs` CTE therefore serves only as a pass-through -- it adds no filtering or transformation that affects the output. The `summary` CTE performs the actual aggregation (COUNT, MAX CASE) on the same rows that `all_prefs` passes through unchanged.

The V2 SQL eliminates both CTEs and performs the aggregation directly on the `customer_preferences` table. This produces identical output because:

1. The `all_prefs` CTE does not filter any rows (no WHERE clause, no HAVING clause).
2. The `rnk` column computed by RANK() is never used downstream.
3. The `summary` CTE's GROUP BY operates on `customer_id` and `as_of` -- the same columns used in V2's GROUP BY.
4. The aggregate functions (COUNT(*), MAX(CASE...)) operate on the same row set in both versions.

**Column order**: The SELECT lists columns in the exact order of the V1 final SELECT: `customer_id`, `preference_count`, `has_email_opt_in`, `has_sms_opt_in`, `as_of`. This ensures the Parquet schema matches V1.

**opted_in comparison**: The V1 SQL compares `opted_in = 1`. In PostgreSQL, the source column is `boolean`, but DataSourcing loads data into the framework's DataFrame which passes through SQLite. In SQLite, boolean values are stored as integers (0/1), so `opted_in = 1` correctly matches `true` values. The V2 SQL preserves this comparison exactly.

---

## 5. Writer Config

| Property | Value | Matches V1? | Evidence |
|----------|-------|-------------|----------|
| Writer type | ParquetFileWriter | Yes | [preference_change_count.json:25] |
| source | `pref_counts` | Yes | [preference_change_count.json:26] |
| outputDirectory | `Output/double_secret_curated/preference_change_count/` | Path changed per V2 convention | V1: `Output/curated/preference_change_count/` [preference_change_count.json:27] |
| numParts | `1` | Yes | [preference_change_count.json:28] |
| writeMode | `Overwrite` | Yes | [preference_change_count.json:29] |

**Write mode note**: Overwrite mode means each effective date execution replaces the entire Parquet directory. For multi-day auto-advance runs, only the last effective date's output survives on disk. However, because the SQL groups by `as_of`, a single run covering multiple effective dates produces rows for all dates in the range [BRD: Write Mode Implications]. The framework's DataSourcing uses `__minEffectiveDate` and `__maxEffectiveDate` to pull data for the date range, and all dates' aggregated rows appear in the final output.

---

## 6. Wrinkle Replication

**No output-affecting wrinkles (W-codes) apply to this job.**

The BRD identifies no weekend fallback logic, no trailer, no rounding issues, no integer division, no hardcoded dates, no write mode anomalies, and no other W-code behaviors. The job performs straightforward COUNT/MAX aggregation with integer outputs. The write mode is Overwrite which is appropriate for a job that groups by `as_of` (all dates are represented in a single output). `numParts: 1` is reasonable for a per-customer summary dataset.

| W-Code | Applicable? | Rationale |
|--------|-------------|-----------|
| W1 (Sunday skip) | No | No day-of-week logic in SQL or config |
| W2 (Weekend fallback) | No | No date manipulation logic |
| W3a/b/c (Boundary rows) | No | No summary row injection |
| W4 (Integer division) | No | No division operations |
| W5 (Banker's rounding) | No | No rounding operations |
| W6 (Double epsilon) | No | No floating-point accumulation |
| W7 (Trailer inflated count) | No | No trailer |
| W8 (Trailer stale date) | No | No trailer |
| W9 (Wrong writeMode) | No | Overwrite is appropriate for this job's pattern [BRD: Write Mode Implications] |
| W10 (Absurd numParts) | No | numParts=1 is reasonable |
| W12 (Header every append) | No | Not an Append-mode CSV |

---

## 7. Anti-Pattern Elimination

### AP1: Dead-end sourcing -- ELIMINATED

**V1 behavior**: The V1 config sources `datalake.customers` with columns `id`, `prefix`, `first_name`, `last_name` [preference_change_count.json:14-18]. The customers DataFrame is loaded into shared state and registered as a SQLite table, but the Transformation SQL only references `customer_preferences` (aliased as `cp`). The customers table is never joined, filtered, or selected from [BRD: BR-6].

**V2 resolution**: The `customers` DataSourcing module is removed entirely from the V2 config. This eliminates unnecessary database I/O and memory consumption without affecting output.

### AP4: Unused columns -- ELIMINATED

**V1 behavior**: Two columns are sourced from `customer_preferences` but never used in the output-affecting SQL logic:
- `preference_id` [preference_change_count.json:10]: Only referenced inside the RANK() window function in the `all_prefs` CTE, which computes a `rnk` value that is never used downstream [BRD: BR-1].
- `updated_date` [preference_change_count.json:10]: Never referenced anywhere in the SQL [BRD: BR-7].

Additionally, ALL columns from the `customers` table (`id`, `prefix`, `first_name`, `last_name`) are unused [BRD: BR-6], but those are already covered by AP1 above.

**V2 resolution**: The V2 DataSourcing config sources only `customer_id`, `preference_type`, and `opted_in` from `customer_preferences`. The `preference_id` and `updated_date` columns are removed.

### AP8: Complex SQL / unused CTEs -- ELIMINATED

**V1 behavior**: The V1 SQL uses two CTEs where none are needed:
1. `all_prefs` CTE computes `RANK() OVER (PARTITION BY customer_id, preference_type ORDER BY preference_id) AS rnk` -- the `rnk` column is never referenced in the `summary` CTE or the final SELECT [BRD: BR-1, Edge Case 1]. The CTE passes through all rows unchanged (no WHERE, no HAVING).
2. `summary` CTE performs the actual aggregation. This could be the top-level SELECT.

**V2 resolution**: Both CTEs are eliminated. The V2 SQL performs the aggregation directly with a single SELECT ... GROUP BY statement. The RANK() window function is removed entirely.

### AP9: Misleading names -- DOCUMENTED (cannot rename)

**V1 behavior**: The job name "PreferenceChangeCount" implies tracking changes over time, but the job actually counts total preference rows per snapshot date. No change detection logic exists -- COUNT(*) counts all rows, not differences between dates [BRD: Edge Case 3].

**V2 resolution**: The V2 job name is `PreferenceChangeCountV2` per naming conventions. The output filename `preference_change_count` cannot be changed (output must match V1). This anti-pattern is documented here but cannot be eliminated without changing the job's identity.

---

## 8. Proofmark Config

```yaml
comparison_target: "preference_change_count"
reader: parquet
threshold: 100.0
```

### Rationale

| Setting | Value | Justification |
|---------|-------|---------------|
| reader | `parquet` | V1 and V2 both use ParquetFileWriter |
| threshold | `100.0` | All output columns are deterministic integer/date values from aggregation; no tolerance needed |
| Excluded columns | None | No non-deterministic fields identified in BRD [BRD: Non-Deterministic Fields: "None identified"] |
| Fuzzy columns | None | No floating-point operations, no rounding, no division. COUNT(*) and MAX(CASE) produce exact integer results. |

**Starting position**: Zero exclusions, zero fuzzy overrides. This is the strictest possible comparison. The output consists entirely of integer aggregates (`preference_count`, `has_email_opt_in`, `has_sms_opt_in`), an integer key (`customer_id`), and a date (`as_of`). There is no source of non-determinism or floating-point variance. If Phase D testing reveals discrepancies, they will be investigated and resolved before any overrides are added.

---

## 9. Open Questions

1. **What was the RANK() for?** The RANK() window function in the V1 `all_prefs` CTE is computed but never used [BRD: OQ-1]. It may have been intended for a different version of the query (e.g., to detect first vs. subsequent preference records, or to de-duplicate). Since it has no effect on the output, V2 removes it. If a future requirement surfaces that needs ranking, it can be added back.
   - **Impact on V2**: None. Removal is safe because the computed `rnk` value never flows to the output.
   - **Confidence**: HIGH -- the `rnk` column is provably unreferenced in V1's `summary` CTE and final SELECT.

2. **Why was `customers` sourced?** The V1 config loads the `customers` table but the SQL never references it [BRD: OQ-2]. Likely a leftover from a prior design where customer names were included in the output. V2 eliminates this dead-end source.
   - **Impact on V2**: None. Removal is safe because the customers table never contributes to the output DataFrame.
   - **Confidence**: HIGH -- the V1 SQL does not contain any reference to the customers table or its columns.

3. **Row ordering in Parquet output**: The V1 SQL has no ORDER BY clause. The output row order depends on SQLite's internal processing order for GROUP BY. V2 also omits ORDER BY to match this behavior. Parquet is a columnar format where row order is typically not semantically meaningful, but Proofmark may compare row-by-row. If comparison fails due to row ordering, an ORDER BY clause matching the GROUP BY columns (`customer_id, as_of`) may need to be added to both the V1 and V2 descriptions.
   - **Impact on V2**: Potentially requires adding ORDER BY if Proofmark comparison is order-sensitive.
   - **Confidence**: MEDIUM -- depends on Proofmark's comparison strategy for Parquet files.

---

## Traceability Matrix

| BRD Requirement | FSD Design Element | V2 Implementation |
|-----------------|-------------------|-------------------|
| BR-1: Dead RANK() computation | AP8 elimination: CTE and RANK() removed from V2 SQL | V2 SQL has no CTEs, no RANK() |
| BR-2: preference_count = COUNT(*) per customer per date | V2 SQL: `COUNT(*) AS preference_count` | Direct aggregation in SELECT |
| BR-3: has_email_opt_in = MAX(CASE MARKETING_EMAIL) | V2 SQL: `MAX(CASE WHEN cp.preference_type = 'MARKETING_EMAIL' AND cp.opted_in = 1 THEN 1 ELSE 0 END)` | Preserved exactly from V1 |
| BR-4: has_sms_opt_in = MAX(CASE MARKETING_SMS) | V2 SQL: `MAX(CASE WHEN cp.preference_type = 'MARKETING_SMS' AND cp.opted_in = 1 THEN 1 ELSE 0 END)` | Preserved exactly from V1 |
| BR-5: GROUP BY customer_id, as_of | V2 SQL: `GROUP BY cp.customer_id, cp.as_of` | Preserved exactly from V1 |
| BR-6: Customers table unused (dead-end source) | AP1 elimination: customers DataSourcing removed from V2 config | Not sourced in V2 |
| BR-7: updated_date column unused | AP4 elimination: updated_date removed from V2 DataSourcing columns | Not sourced in V2 |
| BRD Output Schema: 5 columns in specific order | V2 SQL SELECT column order: customer_id, preference_count, has_email_opt_in, has_sms_opt_in, as_of | Matches V1 output schema |
| BRD Writer: ParquetFileWriter | V2 uses ParquetFileWriter | Same writer type |
| BRD Writer: numParts=1 | V2 config: numParts=1 | Matches V1 |
| BRD Writer: writeMode=Overwrite | V2 config: writeMode=Overwrite | Matches V1 |

---

## V2 Job Config JSON

```json
{
  "jobName": "PreferenceChangeCountV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "customer_preferences",
      "schema": "datalake",
      "table": "customer_preferences",
      "columns": ["customer_id", "preference_type", "opted_in"]
    },
    {
      "type": "Transformation",
      "resultName": "pref_counts",
      "sql": "SELECT cp.customer_id, COUNT(*) AS preference_count, MAX(CASE WHEN cp.preference_type = 'MARKETING_EMAIL' AND cp.opted_in = 1 THEN 1 ELSE 0 END) AS has_email_opt_in, MAX(CASE WHEN cp.preference_type = 'MARKETING_SMS' AND cp.opted_in = 1 THEN 1 ELSE 0 END) AS has_sms_opt_in, cp.as_of FROM customer_preferences cp GROUP BY cp.customer_id, cp.as_of"
    },
    {
      "type": "ParquetFileWriter",
      "source": "pref_counts",
      "outputDirectory": "Output/double_secret_curated/preference_change_count/",
      "numParts": 1,
      "writeMode": "Overwrite"
    }
  ]
}
```

### Key differences from V1 config:

| Aspect | V1 | V2 | Reason |
|--------|----|----|--------|
| Job name | `PreferenceChangeCount` | `PreferenceChangeCountV2` | V2 naming convention |
| Customers DataSourcing | Present [preference_change_count.json:14-18] | **Removed** | AP1: Dead-end sourcing eliminated |
| preference_id column | Sourced [preference_change_count.json:10] | **Removed** | AP4: Only used in dead RANK() |
| updated_date column | Sourced [preference_change_count.json:10] | **Removed** | AP4: Never referenced in SQL |
| SQL CTEs | Two CTEs (all_prefs, summary) [preference_change_count.json:22] | **Eliminated** | AP8: Simplified to single SELECT |
| SQL RANK() | Computed but unused [preference_change_count.json:22] | **Removed** | AP8: Dead computation eliminated |
| Output path | `Output/curated/preference_change_count/` | `Output/double_secret_curated/preference_change_count/` | V2 output directory |
| Writer config | numParts=1, writeMode=Overwrite | Same | Output equivalence requirement |
