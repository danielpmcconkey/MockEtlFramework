# TopHoldingsByValue — Functional Specification Document

## 1. Job Summary

TopHoldingsByValueV2 produces a ranked list of the top 20 securities by total held value across all holders for each effective date. It aggregates holdings by (security_id, as_of), joins with security metadata, ranks by total value descending via ROW_NUMBER(), filters to the top 20, assigns tier labels (Top 5 / Top 10 / Top 20), and writes the result to Parquet with Overwrite mode. The V1 job is already a clean Tier 1 framework pipeline (DataSourcing + Transformation + ParquetFileWriter) with no External module, so the V2 rewrite preserves the same module chain while eliminating unused columns and an unused CTE.

## 2. V2 Module Chain

**Tier: 1 (Framework Only)** -- `DataSourcing -> DataSourcing -> Transformation (SQL) -> ParquetFileWriter`

**Tier Justification:** V1 already uses Tier 1. All business logic is expressible in SQL: aggregation (SUM, COUNT, GROUP BY), window function (ROW_NUMBER), CASE expression, and filtering (WHERE rank <= 20). No procedural logic, no snapshot fallback, no cross-date-range queries outside the effective date window. There is zero reason to escalate to Tier 2 or Tier 3.

| Step | Module Type | Config Key | Details |
|------|------------|------------|---------|
| 1 | DataSourcing | `holdings` | schema=`datalake`, table=`holdings`, columns=`[security_id, current_value]`. Effective dates injected by executor. `as_of` auto-appended by DataSourcing. |
| 2 | DataSourcing | `securities` | schema=`datalake`, table=`securities`, columns=`[security_id, ticker, security_name, sector]`. Effective dates injected by executor. `as_of` auto-appended by DataSourcing. |
| 3 | Transformation | `top_holdings` | SQL aggregation, ranking, tier classification. See Section 4 for exact SQL. |
| 4 | ParquetFileWriter | -- | source=`top_holdings`, outputDirectory=`Output/double_secret_curated/top_holdings_by_value/`, numParts=50, writeMode=Overwrite |

### Key Design Decisions

- **Remove unused columns from holdings.** V1 sources 6 columns (`holding_id`, `investment_id`, `security_id`, `customer_id`, `quantity`, `current_value`) but the SQL only uses `security_id`, `current_value`, and the auto-appended `as_of`. The remaining 4 columns (`holding_id`, `investment_id`, `customer_id`, `quantity`) are never referenced (AP4). V2 sources only `security_id` and `current_value`.
- **Remove unused column from securities.** V1 sources 5 columns (`security_id`, `ticker`, `security_name`, `security_type`, `sector`) but `security_type` is never used in the SQL output (AP4). V2 sources only `security_id`, `ticker`, `security_name`, and `sector`.
- **Remove the unused CTE.** V1's SQL defines `unused_cte` which is never referenced by any downstream CTE or the final SELECT (AP8). V2 eliminates it entirely.
- **Preserve numParts=50.** Although 50 parts for at most 20 rows is absurd (W10), changing it would produce a structurally different output. V2 reproduces this exactly for output equivalence.
- **Preserve Overwrite writeMode.** V1 uses Overwrite, which means only the last effective date's output survives multi-day runs (W9 analysis -- see Section 6). V2 replicates this exactly.
- **Preserve the ELSE 'Other' dead branch.** The CASE statement includes `ELSE 'Other'` which can never execute because `WHERE r.rank <= 20` guarantees all rows fall into the Top 5/10/20 buckets. This dead code has no output effect and is retained for behavioral equivalence.

## 3. DataSourcing Config

### Holdings

| Property | Value |
|----------|-------|
| resultName | `holdings` |
| schema | `datalake` |
| table | `holdings` |
| columns | `["security_id", "current_value"]` |

**Effective date handling:** No `minEffectiveDate`/`maxEffectiveDate` in config. The executor injects these into shared state as `__minEffectiveDate` and `__maxEffectiveDate`. DataSourcing reads them automatically and filters `holdings.as_of` to the effective date range. The `as_of` column is auto-appended by DataSourcing since it is not in the explicit column list.

Evidence: [top_holdings_by_value.json:6-11] -- V1 sources holdings with no explicit date filter; [Architecture.md] -- DataSourcing auto-appends `as_of` and reads injected effective dates.

### Securities

| Property | Value |
|----------|-------|
| resultName | `securities` |
| schema | `datalake` |
| table | `securities` |
| columns | `["security_id", "ticker", "security_name", "sector"]` |

**Effective date handling:** Same as holdings -- executor-injected effective dates, `as_of` auto-appended.

Evidence: [top_holdings_by_value.json:13-18] -- V1 sources securities with no explicit date filter.

## 4. Transformation SQL

The V2 SQL is identical to V1 except for the removal of the `unused_cte` CTE (AP8).

```sql
WITH security_totals AS (
    SELECT
        h.security_id,
        SUM(h.current_value) AS total_held_value,
        COUNT(*) AS holder_count,
        h.as_of
    FROM holdings h
    GROUP BY h.security_id, h.as_of
),
ranked AS (
    SELECT
        st.security_id,
        s.ticker,
        s.security_name,
        s.sector,
        st.total_held_value,
        st.holder_count,
        st.as_of,
        ROW_NUMBER() OVER (ORDER BY st.total_held_value DESC) AS rank
    FROM security_totals st
    JOIN securities s
        ON st.security_id = s.security_id
        AND st.as_of = s.as_of
)
SELECT
    r.security_id,
    r.ticker,
    r.security_name,
    r.sector,
    r.total_held_value,
    r.holder_count,
    CASE
        WHEN r.rank <= 5 THEN 'Top 5'
        WHEN r.rank <= 10 THEN 'Top 10'
        WHEN r.rank <= 20 THEN 'Top 20'
        ELSE 'Other'
    END AS rank,
    r.as_of
FROM ranked r
WHERE r.rank <= 20
ORDER BY r.rank
```

**V1 SQL (for reference):** Identical except V1 includes between `security_totals` and `ranked`:
```sql
unused_cte AS (
    SELECT security_id, total_held_value
    FROM security_totals
    WHERE total_held_value > 0
),
```
This CTE is defined but never referenced. Its removal has zero effect on output.

Evidence: [top_holdings_by_value.json:22] -- V1 SQL; BRD BR-7 -- unused_cte is defined but never used (HIGH confidence).

### SQL Logic Breakdown

1. **security_totals CTE (BR-1):** Aggregates holdings per (security_id, as_of). Produces `total_held_value` (SUM of current_value) and `holder_count` (COUNT of rows).
2. **ranked CTE (BR-2, BR-3):** Joins security_totals with securities on (security_id, as_of) to enrich with ticker, security_name, sector. Assigns a rank via `ROW_NUMBER() OVER (ORDER BY st.total_held_value DESC)`.
3. **Final SELECT (BR-4, BR-5, BR-6):** Filters to rank <= 20, converts numeric rank to tier label via CASE, orders by numeric rank ascending.

### Non-Determinism Note (BR-11)

ROW_NUMBER() assigns unique rank positions even when securities have identical `total_held_value`. The tie-breaking order is non-deterministic in SQLite. This means:
- At tier boundaries (rank 5/6, 10/11, 20/21), tied securities may swap tier assignments between runs.
- At the rank 20 boundary, a tied security could be included or excluded non-deterministically.

However, since V1 and V2 run the same SQL against the same data in the same SQLite engine during the same executor invocation, the non-determinism is consistent within a single run. For Proofmark comparison (V1 output vs V2 output from separate runs), this could cause mismatches if ties exist at boundaries.

### Cross-Date Ranking Note (BR-8, Edge Case 3)

The ROW_NUMBER() is NOT partitioned by `as_of`. For multi-day effective date ranges, all (security_id, as_of) tuples across all dates compete for the same 20 rank positions. This means the output is NOT a per-date top 20 -- it is the top 20 across all dates combined. V2 replicates this behavior exactly.

## 5. Writer Config

| Property | V1 Value | V2 Value | Match? |
|----------|----------|----------|--------|
| type | ParquetFileWriter | ParquetFileWriter | YES |
| source | `top_holdings` | `top_holdings` | YES |
| outputDirectory | `Output/curated/top_holdings_by_value/` | `Output/double_secret_curated/top_holdings_by_value/` | Changed per V2 convention |
| numParts | 50 | 50 | YES -- W10 replicated for output equivalence |
| writeMode | Overwrite | Overwrite | YES |

Evidence: [top_holdings_by_value.json:24-30]

## 6. Wrinkle Replication

| W-code | Applicable? | V2 Handling |
|--------|------------|-------------|
| W10 (Absurd numParts) | **YES** | V2 preserves `numParts: 50` exactly. For a dataset producing at most 20 rows per date (and potentially fewer after cross-date ranking), most of the 50 part files will be empty. This is replicated for output structural equivalence. A comment in the V2 config or FSD notes this is excessive. Evidence: [top_holdings_by_value.json:28] -- `"numParts": 50`; BRD BR-10. |
| W1 (Sunday skip) | No | No day-of-week logic in V1. |
| W2 (Weekend fallback) | No | No date fallback logic in V1. |
| W3a/b/c (Boundary rows) | No | No summary row generation in V1. |
| W4 (Integer division) | No | No integer division in the SQL. SUM and COUNT operate on numeric types; the output includes the raw aggregated values. |
| W5 (Banker's rounding) | No | No rounding operations in V1. |
| W6 (Double epsilon) | No | No monetary accumulation in application code. SUM is computed by SQLite which uses IEEE 754 double internally, but this is identical between V1 and V2 since both use the same Transformation module. |
| W7 (Trailer inflated count) | No | Parquet writer, no trailers. |
| W8 (Trailer stale date) | No | Parquet writer, no trailers. |
| W9 (Wrong writeMode) | No | Overwrite mode may lose prior dates' output in multi-day runs, but this is V1's behavior. Whether it is "wrong" depends on intent -- the BRD notes it (BR-10, Write Mode Implications). V2 replicates Overwrite exactly. |
| W12 (Header every append) | No | Parquet writer, not CSV Append. |

## 7. Anti-Pattern Elimination

| AP-code | Identified? | V1 Problem | V2 Resolution |
|---------|------------|------------|---------------|
| **AP4** (Unused columns) | **YES** | V1 sources 4 unused columns from holdings: `holding_id`, `investment_id`, `customer_id`, `quantity`. V1 sources 1 unused column from securities: `security_type`. None of these appear in any CTE or the final SELECT. Evidence: [top_holdings_by_value.json:10] sources 6 columns; SQL references only `security_id`, `current_value`, and auto-appended `as_of` from holdings. [top_holdings_by_value.json:17] sources 5 columns; SQL references only `security_id`, `ticker`, `security_name`, `sector` from securities. | **Eliminated.** V2 DataSourcing for holdings requests only `[security_id, current_value]`. V2 DataSourcing for securities requests only `[security_id, ticker, security_name, sector]`. |
| **AP8** (Unused CTEs) | **YES** | V1 SQL defines `unused_cte AS (SELECT security_id, total_held_value FROM security_totals WHERE total_held_value > 0)` which is never referenced by the `ranked` CTE or the final SELECT. Evidence: [top_holdings_by_value.json:22] -- the CTE is defined but no subsequent SQL references it; BRD BR-7 (HIGH confidence). | **Eliminated.** V2 SQL removes `unused_cte` entirely. The remaining CTEs (`security_totals`, `ranked`) and the final SELECT are unchanged, producing identical output. |
| AP1 (Dead-end sourcing) | No | Both sourced tables (holdings, securities) are used in the SQL. |
| AP2 (Duplicated logic) | No | Not applicable within single-job scope. |
| AP3 (Unnecessary External) | No | V1 does not use an External module. Already Tier 1. |
| AP5 (Asymmetric NULLs) | No | No NULL coalescing or default-value logic in the SQL. |
| AP6 (Row-by-row iteration) | No | V1 uses SQL, not procedural code. |
| AP7 (Magic values) | No | The thresholds 5, 10, 20 in the CASE expression and the WHERE clause are the job's core business logic (defining tier boundaries), not unexplained magic values. Their meaning is self-evident from context ("Top 5", "Top 10", "Top 20"). |
| AP9 (Misleading names) | No | Job name "top_holdings_by_value" accurately describes the output. |
| AP10 (Over-sourcing dates) | No | V1 uses executor-injected effective dates via DataSourcing. No explicit date filtering in SQL. |

## 8. Proofmark Config

```yaml
comparison_target: "top_holdings_by_value"
reader: parquet
threshold: 100.0
```

**Excluded columns:** None.

**Fuzzy columns:** None.

**Rationale:** All output columns are deterministic given the same input data and the same SQLite engine. The aggregation (SUM, COUNT) and tier classification (CASE) produce exact values. There are no timestamps, UUIDs, or runtime-generated values.

**Risk: ROW_NUMBER() tie-breaking.** The BRD identifies a non-deterministic field risk: when multiple securities have identical `total_held_value`, ROW_NUMBER() assigns arbitrary positions among tied rows. If ties exist at tier boundaries (rank 5/6, 10/11) or at the inclusion boundary (rank 20/21), V1 and V2 could differ if run in separate SQLite sessions.

However, because V1 and V2 both use the framework's Transformation module (same SQLite in-memory engine, same data loaded in the same order from the same DataSourcing), the tie-breaking should be consistent between runs against the same effective dates. Start with strict comparison (threshold 100.0, no exclusions). If Proofmark fails on the `rank` column due to tie-breaking, escalate to:

```yaml
columns:
  fuzzy:
    - name: "rank"
      tolerance: 0
      tolerance_type: absolute
      reason: "ROW_NUMBER() tie-breaking is non-deterministic for equal total_held_value [BRD BR-11]"
```

Or, if entire rows are swapped at the boundary, consider excluding `rank` with reason documented. But do NOT pre-emptively exclude -- start strict and let Proofmark evidence drive any changes.

## 9. V2 Job Config

```json
{
  "jobName": "TopHoldingsByValueV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "holdings",
      "schema": "datalake",
      "table": "holdings",
      "columns": ["security_id", "current_value"]
    },
    {
      "type": "DataSourcing",
      "resultName": "securities",
      "schema": "datalake",
      "table": "securities",
      "columns": ["security_id", "ticker", "security_name", "sector"]
    },
    {
      "type": "Transformation",
      "resultName": "top_holdings",
      "sql": "WITH security_totals AS (SELECT h.security_id, SUM(h.current_value) AS total_held_value, COUNT(*) AS holder_count, h.as_of FROM holdings h GROUP BY h.security_id, h.as_of), ranked AS (SELECT st.security_id, s.ticker, s.security_name, s.sector, st.total_held_value, st.holder_count, st.as_of, ROW_NUMBER() OVER (ORDER BY st.total_held_value DESC) AS rank FROM security_totals st JOIN securities s ON st.security_id = s.security_id AND st.as_of = s.as_of) SELECT r.security_id, r.ticker, r.security_name, r.sector, r.total_held_value, r.holder_count, CASE WHEN r.rank <= 5 THEN 'Top 5' WHEN r.rank <= 10 THEN 'Top 10' WHEN r.rank <= 20 THEN 'Top 20' ELSE 'Other' END AS rank, r.as_of FROM ranked r WHERE r.rank <= 20 ORDER BY r.rank"
    },
    {
      "type": "ParquetFileWriter",
      "source": "top_holdings",
      "outputDirectory": "Output/double_secret_curated/top_holdings_by_value/",
      "numParts": 50,
      "writeMode": "Overwrite"
    }
  ]
}
```

### Differences from V1 Config

| Change | V1 | V2 | Reason |
|--------|----|----|--------|
| holdings columns | `[holding_id, investment_id, security_id, customer_id, quantity, current_value]` | `[security_id, current_value]` | AP4: 4 unused columns eliminated |
| securities columns | `[security_id, ticker, security_name, security_type, sector]` | `[security_id, ticker, security_name, sector]` | AP4: `security_type` unused, eliminated |
| SQL unused_cte | Present | Removed | AP8: CTE defined but never referenced |
| Output directory | `Output/curated/top_holdings_by_value/` | `Output/double_secret_curated/top_holdings_by_value/` | V2 convention |
| Job name | `TopHoldingsByValue` | `TopHoldingsByValueV2` | V2 naming convention |
| numParts | 50 | 50 | W10: preserved for output equivalence |
| writeMode | Overwrite | Overwrite | Preserved -- matches V1 |
| firstEffectiveDate | 2024-10-01 | 2024-10-01 | Preserved -- matches V1 |

## 10. Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| security_id | holdings.security_id (via security_totals CTE) | Aggregation key in GROUP BY | [top_holdings_by_value.json:22] |
| ticker | securities.ticker | Joined via security_id + as_of | [top_holdings_by_value.json:22] |
| security_name | securities.security_name | Joined via security_id + as_of | [top_holdings_by_value.json:22] |
| sector | securities.sector | Joined via security_id + as_of | [top_holdings_by_value.json:22] |
| total_held_value | SUM(holdings.current_value) per (security_id, as_of) | SQL SUM aggregation | [top_holdings_by_value.json:22] |
| holder_count | COUNT(*) per (security_id, as_of) | SQL COUNT aggregation | [top_holdings_by_value.json:22] |
| rank | Computed from ROW_NUMBER() position | Tier label: "Top 5", "Top 10", or "Top 20" via CASE | [top_holdings_by_value.json:22] |
| as_of | holdings.as_of (passed through CTEs) | Pass-through from source data | [top_holdings_by_value.json:22] |

**Column order:** security_id, ticker, security_name, sector, total_held_value, holder_count, rank, as_of. Matches V1 final SELECT column order.

**NULL handling:** No explicit NULL handling. If `securities.ticker`, `securities.security_name`, or `securities.sector` is NULL in the source, it passes through as NULL. Holdings with no matching security are excluded by the INNER JOIN.

## 11. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|-------------|-----------------|----------|
| Source holdings with only security_id, current_value | BR-1: aggregation uses security_id + current_value only | [top_holdings_by_value.json:22] -- SQL references only these columns |
| Source securities with only security_id, ticker, security_name, sector | BR-2: join enriches with ticker, security_name, sector | [top_holdings_by_value.json:22] -- SQL references only these columns |
| Remove unused columns (AP4) | BR-1, BR-2: only referenced columns needed | [top_holdings_by_value.json:10,17] vs SQL column references |
| Remove unused_cte (AP8) | BR-7: CTE defined but never referenced | [top_holdings_by_value.json:22] -- no reference to unused_cte after its definition |
| GROUP BY security_id, as_of | BR-1: aggregation per (security_id, as_of) | [top_holdings_by_value.json:22] |
| JOIN on security_id AND as_of | BR-2: securities joined on both keys | [top_holdings_by_value.json:22] |
| ROW_NUMBER() ORDER BY total_held_value DESC | BR-3: ranking by total value descending | [top_holdings_by_value.json:22] |
| WHERE rank <= 20 | BR-4: top 20 filter | [top_holdings_by_value.json:22] |
| CASE tier classification | BR-5: tier labels Top 5/10/20 | [top_holdings_by_value.json:22] |
| ORDER BY rank | BR-6: output ordered by numeric rank ascending | [top_holdings_by_value.json:22] |
| as_of pass-through | BR-8: as_of preserved from source | [top_holdings_by_value.json:22] |
| No explicit date filter in DataSourcing | BR-9: effective dates injected by executor | [top_holdings_by_value.json] -- no min/maxEffectiveDate fields |
| numParts=50 | BR-10, W10: replicated for output equivalence | [top_holdings_by_value.json:28] |
| ROW_NUMBER() non-determinism documented | BR-11: tie-breaking is non-deterministic | [top_holdings_by_value.json:22] -- no tiebreaker column |
| Overwrite writeMode | BRD Write Mode Implications | [top_holdings_by_value.json:29] |
| firstEffectiveDate=2024-10-01 | BRD source config | [top_holdings_by_value.json:2] |
| Strict Proofmark config (no exclusions) | BRD: no non-deterministic fields identified as needing exclusion | BRD Non-Deterministic Fields section |

## 12. Open Questions

1. **ROW_NUMBER() tie-breaking consistency across runs.** If securities have identical `total_held_value`, the rank assignment is non-deterministic. V1 and V2 run in separate SQLite sessions, so tied rows could receive different rank positions. If Proofmark comparison fails on the `rank` column, the fallback is to add a fuzzy or excluded column override -- but this should only be done with evidence from a failed comparison, not pre-emptively. (MEDIUM confidence -- depends on whether ties exist in the actual data.)

2. **Cross-date ranking behavior.** The ROW_NUMBER() window function has no `PARTITION BY as_of`, so multi-day runs rank all dates together. A security with high value on one date competes against all securities on all dates. This could mean some dates have zero representation in the top 20 output if another date dominates. V2 replicates this exactly (it may be V1's intent or a bug), but it is worth noting. (MEDIUM confidence -- BRD Edge Case 3.)

3. **Column name "rank" shadows SQL keyword.** The output column `rank` shadows SQLite's RANK window function name. This works in both V1 and V2 (SQLite allows it), but is a naming concern. V2 preserves the column name for output equivalence. (LOW confidence -- naming concern only, per BRD Open Question 4.)
