# TopHoldingsByValue — Business Requirements Document

## Overview
Produces a ranked list of the top 20 securities by total held value across all holders. Uses a SQL Transformation with CTEs to aggregate holdings per security, join with security metadata, rank by total value, and categorize into tiers (Top 5, Top 10, Top 20). Includes an unused CTE (`unused_cte`) that has no effect on the output.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `top_holdings`
- **outputDirectory**: `Output/curated/top_holdings_by_value/`
- **numParts**: 50
- **writeMode**: Overwrite

Evidence: [top_holdings_by_value.json:24-30]

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.holdings | holding_id, investment_id, security_id, customer_id, quantity, current_value | Aggregated by security_id + as_of in SQL | [top_holdings_by_value.json:6-11] |
| datalake.securities | security_id, ticker, security_name, security_type, sector | Joined with aggregated holdings on security_id + as_of | [top_holdings_by_value.json:13-18] |

### Table Schemas (from database)

**holdings**: holding_id (integer), investment_id (integer), security_id (integer), customer_id (integer), quantity (numeric), cost_basis (numeric), current_value (numeric), as_of (date)

**securities**: security_id (integer), ticker (varchar), security_name (varchar), security_type (varchar), sector (varchar), exchange (varchar), as_of (date)

## Business Rules

BR-1: Holdings are aggregated per (security_id, as_of) to compute `total_held_value` (SUM of current_value) and `holder_count` (COUNT of rows).
- Confidence: HIGH
- Evidence: [top_holdings_by_value.json:22] — `SELECT h.security_id, SUM(h.current_value) AS total_held_value, COUNT(*) AS holder_count, h.as_of FROM holdings h GROUP BY h.security_id, h.as_of`

BR-2: Securities are joined to aggregated holdings on `security_id AND as_of` to enrich with ticker, security_name, and sector.
- Confidence: HIGH
- Evidence: [top_holdings_by_value.json:22] — `JOIN securities s ON st.security_id = s.security_id AND st.as_of = s.as_of`

BR-3: Securities are ranked by `total_held_value DESC` using `ROW_NUMBER() OVER (ORDER BY st.total_held_value DESC)`.
- Confidence: HIGH
- Evidence: [top_holdings_by_value.json:22] — `ROW_NUMBER() OVER (ORDER BY st.total_held_value DESC) AS rank`

BR-4: Only the top 20 securities (by rank) are included in the output.
- Confidence: HIGH
- Evidence: [top_holdings_by_value.json:22] — `WHERE r.rank <= 20`

BR-5: The output `rank` column is a tier classification, NOT the numeric rank:
- Rank 1-5 → "Top 5"
- Rank 6-10 → "Top 10"
- Rank 11-20 → "Top 20"
- Confidence: HIGH
- Evidence: [top_holdings_by_value.json:22] — `CASE WHEN r.rank <= 5 THEN 'Top 5' WHEN r.rank <= 10 THEN 'Top 10' WHEN r.rank <= 20 THEN 'Top 20' ELSE 'Other' END AS rank`

BR-6: Output is ordered by the numeric rank (ascending), which means descending by total_held_value.
- Confidence: HIGH
- Evidence: [top_holdings_by_value.json:22] — `ORDER BY r.rank` (the numeric ROW_NUMBER rank, applied before the CASE transformation in the output alias)

BR-7: An `unused_cte` exists in the SQL that filters security_totals to total_held_value > 0, but this CTE is never referenced by subsequent CTEs or the final SELECT.
- Confidence: HIGH
- Evidence: [top_holdings_by_value.json:22] — `unused_cte AS (SELECT security_id, total_held_value FROM security_totals WHERE total_held_value > 0)` — this CTE is defined but never used

BR-8: The `as_of` column is preserved from the source data (passed through the CTEs from holdings.as_of).
- Confidence: HIGH
- Evidence: [top_holdings_by_value.json:22] — `r.as_of` in final SELECT, originating from `h.as_of` in security_totals CTE

BR-9: Data is sourced for the effective date range injected by the executor (no explicit date filters in the job config).
- Confidence: HIGH
- Evidence: [top_holdings_by_value.json] — no minEffectiveDate/maxEffectiveDate in DataSourcing modules

BR-10: The numParts is set to 50, which is unusually high for a dataset that produces at most 20 rows per date. Most part files would be empty.
- Confidence: HIGH
- Evidence: [top_holdings_by_value.json:28] — `"numParts": 50` for a max 20-row output

BR-11: ROW_NUMBER() is used (not RANK() or DENSE_RANK()), so ties in total_held_value get different rank numbers. The tie-breaking order is non-deterministic in SQLite for equal values.
- Confidence: HIGH
- Evidence: [top_holdings_by_value.json:22] — `ROW_NUMBER() OVER (ORDER BY st.total_held_value DESC)`

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| security_id | holdings.security_id (via security_totals CTE) | Aggregation key | [top_holdings_by_value.json:22] |
| ticker | securities.ticker | Joined via security_id + as_of | [top_holdings_by_value.json:22] |
| security_name | securities.security_name | Joined via security_id + as_of | [top_holdings_by_value.json:22] |
| sector | securities.sector | Joined via security_id + as_of | [top_holdings_by_value.json:22] |
| total_held_value | SUM(holdings.current_value) per (security_id, as_of) | SQL SUM aggregation | [top_holdings_by_value.json:22] |
| holder_count | COUNT(*) per (security_id, as_of) | SQL COUNT aggregation | [top_holdings_by_value.json:22] |
| rank | Computed from ROW_NUMBER position | "Top 5", "Top 10", or "Top 20" tier label | [top_holdings_by_value.json:22] |
| as_of | holdings.as_of (via CTEs) | Pass-through | [top_holdings_by_value.json:22] |

## Non-Deterministic Fields

**rank**: When two or more securities have identical `total_held_value`, `ROW_NUMBER()` assigns arbitrary rank positions among them because the ORDER BY has no secondary sort column. This means the tier assignment (Top 5 vs. Top 10, etc.) and inclusion/exclusion at the rank 20 boundary could vary between runs for tied securities.

Evidence: [top_holdings_by_value.json:22] — `ROW_NUMBER() OVER (ORDER BY st.total_held_value DESC)` with no tiebreaker

## Write Mode Implications
- **Overwrite** mode: Each run replaces the entire Parquet output directory.
- For multi-day auto-advance runs, only the final effective date's output persists. Prior dates are overwritten.
- With 50 part files for at most 20 rows, most parts will be empty or contain 0-1 rows.

## Edge Cases

1. **Unused CTE**: The `unused_cte` definition has no effect on the query result. It is defined but never referenced. (HIGH confidence — SQL analysis)

2. **Ties in total_held_value**: ROW_NUMBER() assigns unique ranks even for ties. At the boundary (rank 20), a tied security could be included or excluded non-deterministically. (HIGH confidence — ROW_NUMBER() semantics)

3. **Multi-day effective date range**: The GROUP BY includes `as_of`, so each date produces its own set of security aggregates. The ROW_NUMBER() ranking is computed across ALL (security_id, as_of) tuples without partitioning by date, so rankings would mix dates. For example, if SecurityA has value 1M on Oct 1 and 900K on Oct 2, these would be separate rows competing for rank positions. (HIGH confidence — SQL analysis: no `PARTITION BY as_of` in the window function)

4. **50 part files for 20 rows**: The numParts of 50 means most part files will contain zero rows. With 20 output rows, at most 20 of the 50 parts will have data. (HIGH confidence — [top_holdings_by_value.json:28])

5. **Securities with no holdings**: These do not appear because the aggregation starts from the holdings table and joins securities. Securities with zero holdings are excluded. (HIGH confidence — SQL uses holdings as the base table)

6. **Holdings with no matching security**: If a holding's security_id has no match in the securities table for the same as_of, the INNER JOIN excludes it from the ranked results. (HIGH confidence — `JOIN securities s ON st.security_id = s.security_id AND st.as_of = s.as_of`)

7. **Fewer than 20 distinct securities**: If the data contains fewer than 20 unique (security_id, as_of) combinations, all rows are output with their respective tier labels. (HIGH confidence — `WHERE r.rank <= 20` would not filter any rows)

8. **"Other" tier**: The CASE statement includes an `ELSE 'Other'` branch, but since the WHERE clause filters to rank <= 20, no row can ever have the "Other" tier. This branch is dead code. (HIGH confidence — SQL analysis)

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Holdings aggregation per (security_id, as_of) | [top_holdings_by_value.json:22] — security_totals CTE |
| Securities join on security_id + as_of | [top_holdings_by_value.json:22] — ranked CTE |
| ROW_NUMBER ranking by total_held_value DESC | [top_holdings_by_value.json:22] — ranked CTE |
| Top 20 filter | [top_holdings_by_value.json:22] — final WHERE |
| Tier classification (Top 5/10/20) | [top_holdings_by_value.json:22] — CASE expression |
| Unused CTE | [top_holdings_by_value.json:22] — unused_cte definition |
| ORDER BY rank | [top_holdings_by_value.json:22] — final ORDER BY |
| Parquet output with 50 parts | [top_holdings_by_value.json:24-30] |
| as_of pass-through | [top_holdings_by_value.json:22] — r.as_of in SELECT |

## Open Questions

1. **Why unused_cte?**: A CTE is defined that filters security_totals to total_held_value > 0 but is never used. This may be a leftover from a prior version that intended to exclude zero-value securities before ranking. (HIGH confidence it is unused)

2. **Cross-date ranking**: The ROW_NUMBER() is not partitioned by as_of, so multi-day runs would rank all dates together rather than producing a per-date top 20. This may be intentional (absolute top 20 across all dates) or a bug (should rank per date). (MEDIUM confidence)

3. **numParts = 50**: Unusually high for a maximum 20-row output. This may be a configuration error or carried over from a different job's template. (MEDIUM confidence)

4. **Column name collision**: The output column `rank` shadows the SQL keyword RANK. While this works in SQLite (which allows it), it could cause confusion. The column name in the output represents a tier label, not a numeric rank. (LOW confidence — naming concern only)
