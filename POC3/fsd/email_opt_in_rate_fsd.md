# EmailOptInRate -- Functional Specification Document

## 1. Overview

EmailOptInRateV2 calculates the email marketing opt-in rate per customer segment by joining customer preferences, customer-segment mappings, and segment definitions, filtering to MARKETING_EMAIL preference type, and producing per-segment/per-date aggregates. Output is Parquet with one part file, overwritten each run.

**Tier: 1 (Framework Only)** -- `DataSourcing -> Transformation (SQL) -> ParquetFileWriter`

**Tier Justification:** All business logic (filtering, joining, aggregation, integer division) is expressible in standard SQL. V1 already uses this exact Tier 1 pattern -- DataSourcing modules feed a SQL Transformation, which feeds a ParquetFileWriter. No External module exists in V1 for this job. The only change is removing the dead-end `phone_numbers` data source and reproducing the integer division wrinkle cleanly in SQL.

## 2. V2 Module Chain

| Step | Module Type | Config Key | Details |
|------|------------|------------|---------|
| 1 | DataSourcing | `customer_preferences` | schema=`datalake`, table=`customer_preferences`, columns=`[preference_id, customer_id, preference_type, opted_in]`. Effective dates injected by executor. `as_of` auto-appended by DataSourcing. |
| 2 | DataSourcing | `customers_segments` | schema=`datalake`, table=`customers_segments`, columns=`[customer_id, segment_id]`. Effective dates injected by executor. `as_of` auto-appended by DataSourcing. |
| 3 | DataSourcing | `segments` | schema=`datalake`, table=`segments`, columns=`[segment_id, segment_name]`. Effective dates injected by executor. `as_of` auto-appended by DataSourcing. |
| 4 | Transformation | `email_opt_in` | SQL joins the three tables, filters to MARKETING_EMAIL, aggregates per segment/date, computes integer-division opt_in_rate. See Section 5. |
| 5 | ParquetFileWriter | -- | source=`email_opt_in`, outputDirectory=`Output/double_secret_curated/email_opt_in_rate/`, numParts=1, writeMode=Overwrite |

### Key Design Decisions

- **Remove `phone_numbers` data source.** V1 sources `datalake.phone_numbers` but the Transformation SQL never references it (BR-7, AP1). V2 eliminates this dead-end source entirely.
- **Remove unused columns from `customer_preferences`.** V1 sources `preference_id` which is never referenced in the SQL. However, `preference_id` IS in V1's column list [email_opt_in_rate.json:10], and while it is not selected in the SQL output, it is available to the SQL via the SQLite table registration. Since SQLite only registers sourced columns, removing `preference_id` from DataSourcing does not affect the SQL query (which never references it). V2 removes `preference_id` to eliminate AP4.
- **Preserve integer division in SQL.** V1 uses `CAST(... AS INTEGER) / CAST(... AS INTEGER)` for `opt_in_rate`, which produces integer division yielding only 0 or 1 (W4). V2 reproduces this exact behavior in SQL with a comment documenting it as a V1 bug replicated for output equivalence.
- **Same writer type and configuration.** V1 uses ParquetFileWriter with numParts=1 and writeMode=Overwrite. V2 matches this exactly.

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (W-codes)

| W-code | Applicable? | Rationale |
|--------|------------|-----------|
| **W4 (Integer division)** | **YES** | `opt_in_rate` uses `CAST(SUM(...) AS INTEGER) / CAST(COUNT(*) AS INTEGER)` which truncates to 0 or 1. Evidence: [email_opt_in_rate.json:36]. |
| W1 (Sunday skip) | No | No day-of-week logic. |
| W2 (Weekend fallback) | No | No date fallback logic. |
| W3a/b/c (Boundary rows) | No | No summary row generation. |
| W5 (Banker's rounding) | No | No rounding operations (integer division truncates, does not round). |
| W6 (Double epsilon) | No | No floating-point accumulation -- integer aggregates only. |
| W7 (Trailer inflated count) | No | Parquet writer, no trailers. |
| W8 (Trailer stale date) | No | Parquet writer, no trailers. |
| W9 (Wrong writeMode) | No | Overwrite for a rate calculation that includes `as_of` in GROUP BY is a reasonable choice. The BRD notes that in auto-advance mode only the last day's output persists, but a single run covering multiple dates does include all dates in the result. This is debatable but matches V1 exactly. |
| W10 (Absurd numParts) | No | numParts=1 is appropriate for a small aggregate result set. |
| W12 (Header every append) | No | Parquet writer, not CSV append. |

**W4 Handling:** The KNOWN_ANTI_PATTERNS.md prescription for W4 says: "do NOT use integer division in your code. Instead, cast to decimal, compute the correct value, then explicitly truncate." However, this prescription targets C# code in External modules. Since V2 uses pure SQL in a Transformation module (not C#), and SQLite's `CAST(... AS INTEGER) / CAST(... AS INTEGER)` natively produces the identical truncation behavior as V1, the cleanest approach is to keep the same SQL expression. The SQL itself is the documentation -- the integer types in the CAST make the truncation explicit and visible. A SQL comment documents the wrinkle. An alternative approach of computing a decimal result and then truncating in SQL (e.g., `CAST(... AS REAL)` then some truncation function) would be more complex and fragile for no output benefit, since SQLite integer division already produces the exact V1 result.

### Code-Quality Anti-Patterns (AP-codes)

| AP-code | Identified? | V1 Problem | V2 Resolution |
|---------|------------|------------|---------------|
| **AP1** (Dead-end sourcing) | **YES** | V1 sources `datalake.phone_numbers` but the SQL never references it. Evidence: [email_opt_in_rate.json:27-30] sources phone_numbers; [email_opt_in_rate.json:36] SQL has no `phone_numbers` reference. | **Eliminated.** V2 does not source `phone_numbers`. |
| **AP4** (Unused columns) | **YES** | V1 sources `preference_id` from `customer_preferences` but the SQL never selects or filters on it. Evidence: [email_opt_in_rate.json:10] sources `preference_id`; [email_opt_in_rate.json:36] SQL references only `cp.preference_type`, `cp.opted_in`, `cp.customer_id`, `cp.as_of`. | **Eliminated.** V2 DataSourcing for `customer_preferences` requests only `[customer_id, preference_type, opted_in]`. |
| AP2 (Duplicated logic) | No | Not applicable. |
| AP3 (Unnecessary External) | No | V1 does not use an External module. Already framework-only. |
| AP5 (Asymmetric NULLs) | No | No NULL coalescing logic in V1 SQL. |
| AP6 (Row-by-row iteration) | No | V1 uses SQL aggregation, not C# iteration. |
| AP7 (Magic values) | No | The only literal is `'MARKETING_EMAIL'` which is a filter value, not a threshold. It is self-documenting in the SQL WHERE clause. |
| AP8 (Complex SQL / unused CTEs) | No | V1 SQL has no CTEs or unused window functions. The SQL is straightforward: one SELECT with JOINs, WHERE, GROUP BY. |
| AP9 (Misleading names) | No | "EmailOptInRate" accurately describes the job's output (opt-in rates for email preferences). |
| AP10 (Over-sourcing dates) | No | V1 does not hardcode date filters in SQL -- effective dates are injected by the executor into DataSourcing. The SQL GROUP BY includes `as_of` which is the date dimension. |

## 4. Output Schema

| Column | Source Table | Source Column | Transformation | Evidence |
|--------|-------------|---------------|---------------|----------|
| segment_name | datalake.segments | segment_name | JOIN through customers_segments to resolve segment_id | [email_opt_in_rate.json:36] `s.segment_name` |
| opted_in_count | datalake.customer_preferences | opted_in | `SUM(CASE WHEN cp.opted_in = 1 THEN 1 ELSE 0 END)` | [email_opt_in_rate.json:36] |
| total_count | datalake.customer_preferences | (all rows) | `COUNT(*)` of MARKETING_EMAIL rows per segment/date | [email_opt_in_rate.json:36] |
| opt_in_rate | Derived | opted_in_count, total_count | Integer division: `CAST(SUM(...) AS INTEGER) / CAST(COUNT(*) AS INTEGER)` -- always 0 or 1 (W4) | [email_opt_in_rate.json:36] |
| as_of | datalake.customer_preferences | as_of | GROUP BY key, passthrough | [email_opt_in_rate.json:36] `cp.as_of` |

**Column order:** segment_name, opted_in_count, total_count, opt_in_rate, as_of. This matches the SELECT order in V1's SQL [email_opt_in_rate.json:36].

**NULL handling:** No explicit NULL handling. All JOINs are INNER, so rows without matching segments or preferences are excluded. `opted_in` values of NULL would be treated as non-1 by the CASE expression (falling to ELSE 0), and would be counted by COUNT(*). This matches V1 behavior.

**Empty input:** If no rows match the `WHERE preference_type = 'MARKETING_EMAIL'` filter, the result is an empty DataFrame. ParquetFileWriter writes an empty Parquet part file. This matches V1 behavior.

## 5. SQL Design

The V2 SQL is functionally identical to V1. The only change is the addition of a comment documenting the W4 integer division wrinkle.

```sql
SELECT
    s.segment_name,
    SUM(CASE WHEN cp.opted_in = 1 THEN 1 ELSE 0 END) AS opted_in_count,
    COUNT(*) AS total_count,
    /* W4: V1 bug -- integer division truncates to 0 or 1. Replicated for output equivalence. */
    CAST(SUM(CASE WHEN cp.opted_in = 1 THEN 1 ELSE 0 END) AS INTEGER) / CAST(COUNT(*) AS INTEGER) AS opt_in_rate,
    cp.as_of
FROM customer_preferences cp
JOIN customers_segments cs ON cp.customer_id = cs.customer_id
JOIN segments s ON cs.segment_id = s.segment_id
WHERE cp.preference_type = 'MARKETING_EMAIL'
GROUP BY s.segment_name, cp.as_of
```

### SQL Design Notes

1. **JOIN semantics:** All JOINs are INNER (the default `JOIN` keyword in SQL). Customers without a segment mapping in `customers_segments` are excluded. This matches V1 exactly (BR-6).

2. **Integer division (W4):** SQLite's `CAST(x AS INTEGER) / CAST(y AS INTEGER)` performs integer division, truncating toward zero. Since `opted_in_count <= total_count`, the rate is always 0 (when not all opted in) or 1 (when all opted in). This is a V1 bug but must be replicated for output equivalence.

3. **GROUP BY:** Groups by `segment_name` and `as_of`. This produces one row per segment per effective date. Matches V1 exactly (BR-5).

4. **MARKETING_EMAIL filter:** The WHERE clause filters to only `preference_type = 'MARKETING_EMAIL'`. All other preference types are excluded (BR-1).

5. **Multi-segment customers:** A customer appearing in `customers_segments` multiple times (different segment_ids) contributes their preference count to each segment independently. This is the natural behavior of the INNER JOIN and matches V1 (BRD Edge Case 2).

6. **No reference to phone_numbers:** The SQL intentionally does not reference the `phone_numbers` table, which V1 sources but never uses. Since V2 does not source `phone_numbers` at all (AP1 eliminated), this is consistent.

## 6. V2 Job Config

```json
{
  "jobName": "EmailOptInRateV2",
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
      "type": "DataSourcing",
      "resultName": "customers_segments",
      "schema": "datalake",
      "table": "customers_segments",
      "columns": ["customer_id", "segment_id"]
    },
    {
      "type": "DataSourcing",
      "resultName": "segments",
      "schema": "datalake",
      "table": "segments",
      "columns": ["segment_id", "segment_name"]
    },
    {
      "type": "Transformation",
      "resultName": "email_opt_in",
      "sql": "SELECT s.segment_name, SUM(CASE WHEN cp.opted_in = 1 THEN 1 ELSE 0 END) AS opted_in_count, COUNT(*) AS total_count, CAST(SUM(CASE WHEN cp.opted_in = 1 THEN 1 ELSE 0 END) AS INTEGER) / CAST(COUNT(*) AS INTEGER) AS opt_in_rate, cp.as_of FROM customer_preferences cp JOIN customers_segments cs ON cp.customer_id = cs.customer_id JOIN segments s ON cs.segment_id = s.segment_id WHERE cp.preference_type = 'MARKETING_EMAIL' GROUP BY s.segment_name, cp.as_of"
    },
    {
      "type": "ParquetFileWriter",
      "source": "email_opt_in",
      "outputDirectory": "Output/double_secret_curated/email_opt_in_rate/",
      "numParts": 1,
      "writeMode": "Overwrite"
    }
  ]
}
```

### Differences from V1 Config

| Change | V1 | V2 | Reason |
|--------|----|----|--------|
| phone_numbers DataSourcing | Present [email_opt_in_rate.json:27-30] | Removed | AP1: dead-end sourcing -- SQL never references phone_numbers |
| customer_preferences columns | `[preference_id, customer_id, preference_type, opted_in]` | `[customer_id, preference_type, opted_in]` | AP4: preference_id never used in SQL |
| Transformation SQL | Identical logic | Identical logic | No SQL changes needed -- V1 SQL is clean |
| Output directory | `Output/curated/email_opt_in_rate/` | `Output/double_secret_curated/email_opt_in_rate/` | V2 convention |
| Job name | `EmailOptInRate` | `EmailOptInRateV2` | V2 naming convention |
| numParts | 1 | 1 | Unchanged |
| writeMode | Overwrite | Overwrite | Unchanged |

## 7. Writer Configuration

| Property | V1 Value | V2 Value | Match? |
|----------|----------|----------|--------|
| type | ParquetFileWriter | ParquetFileWriter | YES |
| source | `email_opt_in` | `email_opt_in` | YES |
| numParts | 1 | 1 | YES |
| writeMode | Overwrite | Overwrite | YES |
| outputDirectory | `Output/curated/email_opt_in_rate/` | `Output/double_secret_curated/email_opt_in_rate/` | Changed per V2 convention |

**Write mode implications (BRD):** Overwrite mode means each execution replaces the entire Parquet directory. In auto-advance mode across multiple dates, only the final date's output persists on disk. However, since the GROUP BY includes `as_of`, a single-date run produces one row per segment for that date. This matches V1 exactly.

## 8. Proofmark Config Design

**Excluded columns:** None.

**Fuzzy columns:** None.

**Rationale:** All output columns are deterministic:
- `segment_name`: passthrough from datalake reference data.
- `opted_in_count`: deterministic aggregate (SUM of CASE).
- `total_count`: deterministic aggregate (COUNT).
- `opt_in_rate`: deterministic integer division of two deterministic integers.
- `as_of`: passthrough date from datalake.

The BRD explicitly states "Non-Deterministic Fields: None identified." There are no timestamps, random values, or floating-point precision concerns. The integer division (W4) produces exact integer results (0 or 1) with no epsilon issues. Strict comparison at 100% threshold is appropriate.

**Proofmark config:**
```yaml
comparison_target: "email_opt_in_rate"
reader: parquet
threshold: 100.0
```

## 9. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|-------------|-----------------|----------|
| Source customer_preferences with columns [customer_id, preference_type, opted_in] | BR-1: MARKETING_EMAIL filter needs preference_type and opted_in; BR-2: opted_in=1 count; BR-6: customer_id for JOIN | [email_opt_in_rate.json:9-11] |
| Source customers_segments with columns [customer_id, segment_id] | BR-6: customers linked to segments via junction table | [email_opt_in_rate.json:15-17] |
| Source segments with columns [segment_id, segment_name] | BR-5, BR-6: GROUP BY segment_name requires segment name resolution | [email_opt_in_rate.json:21-23] |
| Remove phone_numbers DataSourcing | BR-7: phone_numbers sourced but never referenced in SQL | [email_opt_in_rate.json:27-30] vs SQL at line 36 |
| Remove preference_id from customer_preferences columns | AP4: preference_id never referenced in SQL | [email_opt_in_rate.json:10] vs [email_opt_in_rate.json:36] |
| WHERE preference_type = 'MARKETING_EMAIL' | BR-1: only MARKETING_EMAIL preferences included | [email_opt_in_rate.json:36] |
| SUM(CASE WHEN opted_in = 1) AS opted_in_count | BR-2: count of opted-in rows per segment | [email_opt_in_rate.json:36] |
| COUNT(*) AS total_count | BR-3: total MARKETING_EMAIL rows per segment | [email_opt_in_rate.json:36] |
| Integer division for opt_in_rate | BR-4, W4: CAST AS INTEGER / CAST AS INTEGER truncates to 0 or 1 | [email_opt_in_rate.json:36] |
| GROUP BY segment_name, as_of | BR-5: results grouped by segment and date | [email_opt_in_rate.json:36] |
| INNER JOINs throughout | BR-6: customers without segments excluded | [email_opt_in_rate.json:36] |
| numParts=1 | BRD Writer Configuration | [email_opt_in_rate.json:42] |
| writeMode=Overwrite | BRD Writer Configuration | [email_opt_in_rate.json:43] |
| firstEffectiveDate=2024-10-01 | V1 job config | [email_opt_in_rate.json:3] |
| Eliminate AP1 (phone_numbers) | BR-7 + AP1 prescription | Remove unused DataSourcing entries |
| Eliminate AP4 (preference_id) | AP4 prescription | Remove unused columns from DataSourcing |
| Reproduce W4 (integer division) | BR-4 + W4 prescription | Keep integer division in SQL for output equivalence |
| No Proofmark exclusions or fuzzy | BRD: no non-deterministic fields | BRD Non-Deterministic Fields section |

## 10. External Module Design

**Not applicable.** V2 uses Tier 1 (Framework Only) with a three-step chain: DataSourcing (x3) -> Transformation (SQL) -> ParquetFileWriter. No External module is needed. V1 also does not use an External module for this job.
