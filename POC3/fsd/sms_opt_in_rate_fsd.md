# SmsOptInRate -- Functional Specification Document

## 1. Job Summary

SmsOptInRateV2 calculates the SMS marketing opt-in rate per customer segment by joining customer preferences, customer-segment mappings, and segment definitions, filtering to `MARKETING_SMS` preference type, and producing per-segment/per-date aggregates with an integer-division opt-in rate. Output is Parquet with one part file, overwritten each run. This job is the structural twin of EmailOptInRate, differing only in the preference type filter value.

## 2. V2 Module Chain

**Tier: 1 (Framework Only)** -- `DataSourcing -> Transformation (SQL) -> ParquetFileWriter`

**Tier Justification:** All business logic (filtering, joining, aggregation, integer division) is fully expressible in standard SQL. V1 already uses this exact Tier 1 pattern -- three DataSourcing modules feed a SQL Transformation, which feeds a ParquetFileWriter. No External module exists in V1 for this job. No operation requires procedural C# logic. Tier 1 is the correct and simplest choice.

| Step | Module Type | resultName / Config Key | Details |
|------|------------|-------------------------|---------|
| 1 | DataSourcing | `customer_preferences` | schema=`datalake`, table=`customer_preferences`, columns=`[customer_id, preference_type, opted_in]`. Effective dates injected by executor. `as_of` auto-appended by DataSourcing. |
| 2 | DataSourcing | `customers_segments` | schema=`datalake`, table=`customers_segments`, columns=`[customer_id, segment_id]`. Effective dates injected by executor. `as_of` auto-appended by DataSourcing. |
| 3 | DataSourcing | `segments` | schema=`datalake`, table=`segments`, columns=`[segment_id, segment_name]`. Effective dates injected by executor. `as_of` auto-appended by DataSourcing. |
| 4 | Transformation | `sms_opt_in` | SQL joins the three tables, filters to `MARKETING_SMS`, aggregates per segment/date, computes integer-division `opt_in_rate`. See Section 4. |
| 5 | ParquetFileWriter | -- | source=`sms_opt_in`, outputDirectory=`Output/double_secret_curated/sms_opt_in_rate/`, numParts=1, writeMode=Overwrite |

### Key Design Decisions

- **Remove `preference_id` from `customer_preferences` DataSourcing.** V1 sources `preference_id` [sms_opt_in_rate.json:10] but the Transformation SQL never references it. V2 eliminates this unused column (AP4).
- **No dead-end data sources to remove.** Unlike EmailOptInRate, V1's sms_opt_in_rate does NOT source any unused tables. All three DataSourcing entries (customer_preferences, customers_segments, segments) are referenced in the SQL. Evidence: [sms_opt_in_rate.json] -- only 3 DataSourcing modules, all used in SQL JOINs. BRD Edge Case 5 confirms this.
- **Preserve integer division in SQL (W4).** V1 uses `CAST(... AS INTEGER) / CAST(... AS INTEGER)` for `opt_in_rate`, which produces integer division yielding only 0 or 1. V2 reproduces this exact behavior in SQL with a comment documenting it as a V1 bug replicated for output equivalence.
- **Same writer type and configuration.** V1 uses ParquetFileWriter with numParts=1 and writeMode=Overwrite [sms_opt_in_rate.json:32-37]. V2 matches this exactly, differing only in output directory path.

## 3. DataSourcing Config

### Table: `customer_preferences`

| Property | Value |
|----------|-------|
| schema | `datalake` |
| table | `customer_preferences` |
| columns | `[customer_id, preference_type, opted_in]` |
| resultName | `customer_preferences` |
| Effective dates | Injected by executor via shared state (`__minEffectiveDate`, `__maxEffectiveDate`) |
| `as_of` handling | Auto-appended by DataSourcing (not in columns list) |

**V1 diff:** V1 also sources `preference_id` [sms_opt_in_rate.json:10]. V2 removes it because the SQL never references `preference_id` (AP4 elimination).

### Table: `customers_segments`

| Property | Value |
|----------|-------|
| schema | `datalake` |
| table | `customers_segments` |
| columns | `[customer_id, segment_id]` |
| resultName | `customers_segments` |
| Effective dates | Injected by executor via shared state |
| `as_of` handling | Auto-appended by DataSourcing (not in columns list) |

**V1 match:** Identical to V1 [sms_opt_in_rate.json:16-17]. No changes needed.

### Table: `segments`

| Property | Value |
|----------|-------|
| schema | `datalake` |
| table | `segments` |
| columns | `[segment_id, segment_name]` |
| resultName | `segments` |
| Effective dates | Injected by executor via shared state |
| `as_of` handling | Auto-appended by DataSourcing (not in columns list) |

**V1 match:** Identical to V1 [sms_opt_in_rate.json:21-23]. No changes needed.

## 4. Transformation SQL

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
WHERE cp.preference_type = 'MARKETING_SMS'
GROUP BY s.segment_name, cp.as_of
```

### SQL Design Notes

1. **JOIN semantics:** All JOINs are INNER (the default `JOIN` keyword). Customers without a segment mapping in `customers_segments` are excluded. This matches V1 exactly (BR-6). Evidence: [sms_opt_in_rate.json:28] -- `JOIN customers_segments cs ON cp.customer_id = cs.customer_id JOIN segments s ON cs.segment_id = s.segment_id`.

2. **Integer division (W4):** SQLite's `CAST(x AS INTEGER) / CAST(y AS INTEGER)` performs integer division, truncating toward zero. Since `opted_in_count <= total_count`, the rate is always 0 (when not all opted in) or 1 (when all opted in). This is a V1 bug (same as EmailOptInRate) but must be replicated for output equivalence. Evidence: [sms_opt_in_rate.json:28] -- `CAST(SUM(...) AS INTEGER) / CAST(COUNT(*) AS INTEGER)`.

3. **MARKETING_SMS filter:** The WHERE clause filters to only `preference_type = 'MARKETING_SMS'`. All other preference types (MARKETING_EMAIL, E_STATEMENTS, PAPER_STATEMENTS, PUSH_NOTIFICATIONS) are excluded (BR-1). Evidence: [sms_opt_in_rate.json:28].

4. **GROUP BY:** Groups by `segment_name` and `as_of`. Produces one row per segment per effective date (BR-5). Evidence: [sms_opt_in_rate.json:28] -- `GROUP BY s.segment_name, cp.as_of`.

5. **Multi-segment customers:** A customer appearing in `customers_segments` multiple times (different `segment_id` values) contributes their preference count to each segment independently. This is the natural behavior of the INNER JOIN and matches V1 (BRD Edge Case 2).

6. **NULL handling:** No explicit NULL handling needed. All JOINs are INNER, so rows without matching segment mappings are excluded. The `opted_in` column is boolean in PostgreSQL; if a NULL value were present, the `CASE WHEN cp.opted_in = 1` expression would evaluate to the ELSE branch (0), and `COUNT(*)` would still count the row. This matches V1 behavior.

7. **Empty result:** If no rows match the MARKETING_SMS filter, the query returns an empty result set. The ParquetFileWriter will write an empty Parquet part file. This matches V1 behavior (BRD Edge Case 3).

## 5. Writer Config

| Property | V1 Value | V2 Value | Match? |
|----------|----------|----------|--------|
| type | ParquetFileWriter | ParquetFileWriter | YES |
| source | `sms_opt_in` | `sms_opt_in` | YES |
| outputDirectory | `Output/curated/sms_opt_in_rate/` | `Output/double_secret_curated/sms_opt_in_rate/` | Changed per V2 convention |
| numParts | 1 | 1 | YES |
| writeMode | Overwrite | Overwrite | YES |

**Write mode implications:** Overwrite mode means each execution replaces the entire Parquet directory. In auto-advance mode across multiple dates, only the final date's output persists on disk. Since the GROUP BY includes `as_of`, a single-date run produces one row per segment for that date. This matches V1 exactly. Evidence: [sms_opt_in_rate.json:36] `"writeMode": "Overwrite"`.

## 6. Wrinkle Replication

### W4 -- Integer Division

| Aspect | Detail |
|--------|--------|
| **V1 Behavior** | `opt_in_rate` computed as `CAST(SUM(...) AS INTEGER) / CAST(COUNT(*) AS INTEGER)`, which truncates to 0 or 1. Unless every customer in a segment opted in, the rate is 0. |
| **BRD Evidence** | BR-4: "Opt-in rate is calculated as integer division." BRD Edge Case 1: "Integer division produces 0 or 1 only." [sms_opt_in_rate.json:28] |
| **V2 Replication** | V2 preserves the identical SQL expression. SQLite integer division natively produces the same truncation as V1. The SQL includes a `/* W4: ... */` comment documenting the wrinkle. |
| **KNOWN_ANTI_PATTERNS.md Prescription** | W4 prescribes using `Math.Truncate` in C# instead of integer division. This applies to External module C# code. For pure SQL in a Transformation module, keeping the SQL integer division expression is cleaner and produces identical results without introducing unnecessary complexity. The CAST expressions in SQL make the truncation explicit and visible. |

### Other W-codes -- Not Applicable

| W-code | Applicable? | Rationale |
|--------|------------|-----------|
| W1 (Sunday skip) | No | No day-of-week logic in V1 |
| W2 (Weekend fallback) | No | No date fallback logic |
| W3a/b/c (Boundary rows) | No | No summary row generation |
| W5 (Banker's rounding) | No | No rounding operations; integer division truncates, does not round |
| W6 (Double epsilon) | No | No floating-point accumulation; integer aggregates only |
| W7 (Trailer inflated count) | No | Parquet writer, no trailers |
| W8 (Trailer stale date) | No | Parquet writer, no trailers |
| W9 (Wrong writeMode) | No | Overwrite for a rate calculation with `as_of` in GROUP BY is reasonable; matches V1 |
| W10 (Absurd numParts) | No | numParts=1 is appropriate for a small aggregate result set |
| W12 (Header every append) | No | Parquet writer, not CSV append |

## 7. Anti-Pattern Elimination

| AP-code | Identified? | V1 Problem | V2 Resolution |
|---------|------------|------------|---------------|
| **AP4** (Unused columns) | **YES** | V1 sources `preference_id` from `customer_preferences` [sms_opt_in_rate.json:10] but the SQL never selects or filters on it [sms_opt_in_rate.json:28]. | **Eliminated.** V2 DataSourcing for `customer_preferences` requests only `[customer_id, preference_type, opted_in]`. |
| AP1 (Dead-end sourcing) | No | Unlike EmailOptInRate, V1 does NOT source any unused tables. All three sourced tables are referenced in the SQL. BRD Edge Case 5 confirms: "this job does NOT source phone_numbers." | N/A -- no dead-end sources to remove. |
| AP2 (Duplicated logic) | No | Not applicable at single-job scope. The BRD notes this job is a "structural twin" of EmailOptInRate (BRD Edge Case 4) with only the filter value changed. This is cross-job duplication but cannot be eliminated within a single job's scope (AP2 prescription). | Documented. Cannot fix within single-job scope. |
| AP3 (Unnecessary External) | No | V1 does not use an External module. Already framework-only. | N/A |
| AP5 (Asymmetric NULLs) | No | No NULL coalescing logic in V1 SQL. | N/A |
| AP6 (Row-by-row iteration) | No | V1 uses SQL aggregation, not C# iteration. | N/A |
| AP7 (Magic values) | No | The only literal is `'MARKETING_SMS'` which is a filter value, not a threshold. It is self-documenting in the SQL WHERE clause. | N/A |
| AP8 (Complex SQL / unused CTEs) | No | V1 SQL has no CTEs or unused window functions. The SQL is straightforward: one SELECT with JOINs, WHERE, GROUP BY. | N/A |
| AP9 (Misleading names) | No | "SmsOptInRate" accurately describes the job's output (opt-in rates for SMS preferences). | N/A |
| AP10 (Over-sourcing dates) | No | V1 does not hardcode date filters in SQL. Effective dates are injected by the executor into DataSourcing. The SQL GROUP BY includes `as_of` as the date dimension. | N/A |

## 8. Proofmark Config

**Excluded columns:** None.

**Fuzzy columns:** None.

**Rationale:** All output columns are deterministic:
- `segment_name`: passthrough from datalake reference data.
- `opted_in_count`: deterministic aggregate (SUM of CASE).
- `total_count`: deterministic aggregate (COUNT).
- `opt_in_rate`: deterministic integer division of two deterministic integers -- always exactly 0 or 1, no floating-point epsilon.
- `as_of`: passthrough date from datalake.

The BRD explicitly states "Non-Deterministic Fields: None identified." There are no timestamps, random values, or floating-point precision concerns. The integer division (W4) produces exact integer results with no epsilon issues. Strict comparison at 100% threshold is appropriate.

```yaml
comparison_target: "sms_opt_in_rate"
reader: parquet
threshold: 100.0
```

## 9. Open Questions

1. **Integer division bug -- intentional or accidental?** Same as EmailOptInRate. The `opt_in_rate` will always be 0 or 1 due to integer division. This is almost certainly a bug (you'd want `CAST(... AS REAL)` for a meaningful rate), but V2 must reproduce it for output equivalence. Confidence: HIGH that it is a bug, HIGH that we must replicate it (W4).

2. **Cross-table `as_of` alignment during multi-date runs.** The V1 SQL joins `customer_preferences`, `customers_segments`, and `segments` on `customer_id` and `segment_id` without filtering on `as_of` alignment between tables. In auto-advance mode (min == max effective date per run), all three tables contain data for a single `as_of`, so cross-date cartesian products cannot occur. However, if the job were ever run with a multi-day date range (min != max), rows from different dates could join incorrectly. V2 replicates V1's exact join conditions. This is a latent risk in V1's design, not a V2 concern -- the executor runs one date at a time. Confidence: HIGH.

## Appendix: V2 Job Config

```json
{
  "jobName": "SmsOptInRateV2",
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
      "resultName": "sms_opt_in",
      "sql": "SELECT s.segment_name, SUM(CASE WHEN cp.opted_in = 1 THEN 1 ELSE 0 END) AS opted_in_count, COUNT(*) AS total_count, CAST(SUM(CASE WHEN cp.opted_in = 1 THEN 1 ELSE 0 END) AS INTEGER) / CAST(COUNT(*) AS INTEGER) AS opt_in_rate, cp.as_of FROM customer_preferences cp JOIN customers_segments cs ON cp.customer_id = cs.customer_id JOIN segments s ON cs.segment_id = s.segment_id WHERE cp.preference_type = 'MARKETING_SMS' GROUP BY s.segment_name, cp.as_of"
    },
    {
      "type": "ParquetFileWriter",
      "source": "sms_opt_in",
      "outputDirectory": "Output/double_secret_curated/sms_opt_in_rate/",
      "numParts": 1,
      "writeMode": "Overwrite"
    }
  ]
}
```

### Differences from V1 Config

| Change | V1 | V2 | Reason |
|--------|----|----|--------|
| customer_preferences columns | `[preference_id, customer_id, preference_type, opted_in]` | `[customer_id, preference_type, opted_in]` | AP4: `preference_id` never used in SQL |
| Output directory | `Output/curated/sms_opt_in_rate/` | `Output/double_secret_curated/sms_opt_in_rate/` | V2 convention |
| Job name | `SmsOptInRate` | `SmsOptInRateV2` | V2 naming convention |
| Transformation SQL | Identical logic | Identical logic | No SQL changes needed -- V1 SQL is clean |
| numParts | 1 | 1 | Unchanged |
| writeMode | Overwrite | Overwrite | Unchanged |
| firstEffectiveDate | 2024-10-01 | 2024-10-01 | Unchanged |

## Appendix: Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|-------------|-----------------|----------|
| Source customer_preferences with columns [customer_id, preference_type, opted_in] | BR-1: MARKETING_SMS filter needs preference_type; BR-2: opted_in=1 count needs opted_in; BR-6: customer_id for JOIN | [sms_opt_in_rate.json:9-11] |
| Source customers_segments with columns [customer_id, segment_id] | BR-6: customers linked to segments via junction table | [sms_opt_in_rate.json:16-17] |
| Source segments with columns [segment_id, segment_name] | BR-5, BR-6: GROUP BY segment_name requires segment name resolution | [sms_opt_in_rate.json:21-23] |
| Remove preference_id from customer_preferences columns | AP4: preference_id never referenced in SQL | [sms_opt_in_rate.json:10] vs SQL at [sms_opt_in_rate.json:28] |
| No dead-end sources to remove | BRD Edge Case 5: "this job does NOT source phone_numbers" | [sms_opt_in_rate.json] -- only 3 DataSourcing modules |
| WHERE preference_type = 'MARKETING_SMS' | BR-1: only MARKETING_SMS preferences included | [sms_opt_in_rate.json:28] |
| SUM(CASE WHEN opted_in = 1) AS opted_in_count | BR-2: count of opted-in rows per segment | [sms_opt_in_rate.json:28] |
| COUNT(*) AS total_count | BR-3: total MARKETING_SMS rows per segment | [sms_opt_in_rate.json:28] |
| Integer division for opt_in_rate (W4) | BR-4: CAST AS INTEGER / CAST AS INTEGER truncates to 0 or 1 | [sms_opt_in_rate.json:28] |
| GROUP BY segment_name, as_of | BR-5: results grouped by segment and date | [sms_opt_in_rate.json:28] |
| INNER JOINs throughout | BR-6: customers without segments excluded | [sms_opt_in_rate.json:28] |
| ParquetFileWriter, numParts=1 | BRD Writer Configuration | [sms_opt_in_rate.json:35] |
| writeMode=Overwrite | BRD Writer Configuration | [sms_opt_in_rate.json:36] |
| firstEffectiveDate=2024-10-01 | V1 job config | [sms_opt_in_rate.json:3] |
| Eliminate AP4 (preference_id) | AP4 prescription: remove unused columns | KNOWN_ANTI_PATTERNS.md AP4 |
| Reproduce W4 (integer division) | W4 prescription: replicate truncation for output equivalence | KNOWN_ANTI_PATTERNS.md W4 |
| No Proofmark exclusions or fuzzy | BRD: "Non-Deterministic Fields: None identified" | BRD Non-Deterministic Fields section |
