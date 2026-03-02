# TopHoldingsByValue -- Test Plan

## Job Overview

TopHoldingsByValue produces a Parquet dataset containing the top 20 securities by total held value, ranked with ROW_NUMBER() and classified into tier labels (Top 5 / Top 10 / Top 20). Tier 1 framework-only job (DataSourcing + Transformation + ParquetFileWriter). V2 eliminates unused columns from both source tables (AP4) and removes an unused CTE (AP8), while preserving numParts=50 (W10) and Overwrite writeMode for output equivalence.

---

## Test Cases

### Happy Path

| ID | Description | Expected Behavior | Traces To |
|----|-------------|-------------------|-----------|
| TH-HP-01 | V2 output matches V1 for full date range (2024-10-01 through 2024-12-31) | Proofmark comparison returns exit code 0 (PASS) with threshold 100.0. All rows match exactly between `Output/curated/top_holdings_by_value/` and `Output/double_secret_curated/top_holdings_by_value/`. | BR-1 through BR-11; FSD Section 4 |
| TH-HP-02 | Holdings correctly aggregated per (security_id, as_of) | Each unique (security_id, as_of) pair produces one row with `total_held_value` = SUM(current_value) and `holder_count` = COUNT(*) from holdings. | BR-1; FSD Section 4 (security_totals CTE) |
| TH-HP-03 | Securities metadata joined on (security_id, as_of) | Output contains ticker, security_name, and sector from the securities table, joined on both security_id and as_of. Securities with no matching holdings are excluded (inner join from holdings side). | BR-2; FSD Section 4 (ranked CTE) |
| TH-HP-04 | ROW_NUMBER() ranks by total_held_value descending | Securities are ranked from highest to lowest total held value. Each row receives a unique numeric rank (no ties). | BR-3; FSD Section 4 |
| TH-HP-05 | Only top 20 rows in output | WHERE clause filters to rank <= 20. Output contains at most 20 rows per effective date (fewer if fewer than 20 securities exist). | BR-4; FSD Section 4 |
| TH-HP-06 | Tier classification labels correctly applied | Ranks 1-5 labeled "Top 5", ranks 6-10 labeled "Top 10", ranks 11-20 labeled "Top 20". The output `rank` column contains these string labels, not numeric values. | BR-5; FSD Section 4 (CASE expression) |
| TH-HP-07 | Output ordered by numeric rank ascending | Rows are ordered from rank 1 (highest value) to rank 20 (lowest among top 20), which means descending by total_held_value. | BR-6; FSD Section 4 |
| TH-HP-08 | as_of column preserved from source data | The as_of column passes through from holdings.as_of via the CTEs and appears in the final output. | BR-8; FSD Section 4 |

### Writer Configuration Verification

| ID | Description | Expected Behavior | Traces To |
|----|-------------|-------------------|-----------|
| TH-WC-01 | Output is Parquet at correct V2 path | Parquet part files exist in `Output/double_secret_curated/top_holdings_by_value/`. | FSD Section 5 |
| TH-WC-02 | numParts = 50 | V2 produces exactly 50 part files, matching V1. Most part files will be empty or contain 0-1 rows given the max 20-row output. | BR-10; FSD Section 5; W10 |
| TH-WC-03 | Overwrite mode replaces output each run | After the full date range completes, only the last effective date's output persists. Prior dates' Parquet files are overwritten. | FSD Section 5 (`writeMode: Overwrite`); BRD Write Mode Implications |
| TH-WC-04 | Output column order matches V1 | Columns appear in order: security_id, ticker, security_name, sector, total_held_value, holder_count, rank, as_of. | FSD Section 10 |

### Edge Cases

| ID | Description | Expected Behavior | Traces To |
|----|-------------|-------------------|-----------|
| TH-EC-01 | ROW_NUMBER() tie-breaking for equal total_held_value | When two or more securities have identical total_held_value, ROW_NUMBER() assigns arbitrary distinct ranks among them. The tie-breaking is non-deterministic in SQLite. V1 and V2 should produce the same order when running against the same data in the same SQLite engine, but if Proofmark fails on this, the `rank` column may need fuzzy treatment. | BR-11; FSD Section 4 (Non-Determinism Note); BRD Edge Case 2 |
| TH-EC-02 | Cross-date ranking without PARTITION BY | ROW_NUMBER() is NOT partitioned by as_of. On multi-day effective date ranges, all (security_id, as_of) tuples across all dates compete for the top 20. Some dates may have zero representation. V2 replicates this behavior exactly. | BR-8; FSD Section 4 (Cross-Date Ranking Note); BRD Edge Case 3 |
| TH-EC-03 | "Other" tier label is dead code | The CASE expression includes `ELSE 'Other'` but `WHERE rank <= 20` guarantees all rows fall into Top 5/10/20. No output row should ever have rank = "Other". V2 preserves the dead branch for behavioral equivalence. | BR-5; FSD Section 2 (Key Design Decisions); BRD Edge Case 8 |
| TH-EC-04 | Fewer than 20 distinct securities | If the data contains fewer than 20 unique (security_id, as_of) combinations, all rows are output with their appropriate tier labels. The WHERE clause does not filter any rows. | BRD Edge Case 7 |
| TH-EC-05 | Securities with no holdings excluded | Securities that have no rows in the holdings table for a given as_of are excluded because the aggregation starts from holdings (inner join). | BRD Edge Case 5 |
| TH-EC-06 | Holdings with no matching security excluded | If a holding's security_id has no match in the securities table for the same as_of, the inner join in the ranked CTE excludes it from results. | BRD Edge Case 6 |
| TH-EC-07 | 50 part files for <= 20 rows | Most of the 50 Parquet parts will be empty. Proofmark must handle empty part files correctly. Structural match is verified by the comparison. | BR-10; W10; BRD Edge Case 4 |
| TH-EC-08 | NULL values in securities metadata pass through | If ticker, security_name, or sector is NULL in the securities table, the NULL passes through to the output (no COALESCE or default substitution in the SQL). | FSD Section 10 (NULL handling) |

### Anti-Pattern Elimination Verification

| ID | Description | Expected Behavior | Traces To |
|----|-------------|-------------------|-----------|
| TH-AP-01 | AP4: Unused holdings columns removed | V2 DataSourcing for holdings sources only `["security_id", "current_value"]`. The 4 unused columns (`holding_id`, `investment_id`, `customer_id`, `quantity`) are absent from the V2 config. | FSD Section 7 (AP4); KNOWN_ANTI_PATTERNS AP4 |
| TH-AP-02 | AP4: Unused securities column removed | V2 DataSourcing for securities sources only `["security_id", "ticker", "security_name", "sector"]`. The unused `security_type` column is absent from the V2 config. | FSD Section 7 (AP4); KNOWN_ANTI_PATTERNS AP4 |
| TH-AP-03 | AP8: Unused CTE removed from SQL | V2 SQL does not contain the `unused_cte` CTE. Only `security_totals` and `ranked` CTEs remain. Output is unaffected because the removed CTE was never referenced. | BR-7; FSD Section 4, Section 7 (AP8); KNOWN_ANTI_PATTERNS AP8 |
| TH-AP-04 | AP4/AP8 elimination does not change output | Despite removing unused columns and the unused CTE, Proofmark comparison PASS confirms no output difference. The SQL logic referencing remaining columns/CTEs is unchanged. | FSD Section 7 |
| TH-AP-05 | No unnecessary External module | V2 uses Tier 1 (DataSourcing + Transformation + ParquetFileWriter) with no External module, matching V1's architecture. | FSD Section 2 (Tier justification); KNOWN_ANTI_PATTERNS AP3 |
| TH-AP-06 | No dead-end sourcing | Both sourced tables (holdings, securities) are consumed by the Transformation SQL. No unused DataSourcing entries exist. | FSD Section 7 (AP1 check); KNOWN_ANTI_PATTERNS AP1 |

### Wrinkle Replication Verification

| ID | Description | Expected Behavior | Traces To |
|----|-------------|-------------------|-----------|
| TH-WR-01 | W10: numParts=50 preserved | V2 config specifies `"numParts": 50` to match V1's excessive part count. Output structure (50 part files) is identical. | BR-10; FSD Section 6 (W10); KNOWN_ANTI_PATTERNS W10 |
| TH-WR-02 | Overwrite writeMode preserved | V2 uses `"writeMode": "Overwrite"` matching V1. Only the final date's output survives multi-day runs. | FSD Section 6 (W9 analysis); FSD Section 5 |

### Proofmark Comparison Expectations

| ID | Description | Expected Behavior | Traces To |
|----|-------------|-------------------|-----------|
| TH-PM-01 | Proofmark config uses reader `parquet` | Config at `POC3/proofmark_configs/top_holdings_by_value.yaml` specifies `reader: parquet`. | FSD Section 8 |
| TH-PM-02 | Proofmark config has `threshold: 100.0` | All rows must match exactly. Start strict per Proofmark best practices. | FSD Section 8; CONFIG_GUIDE |
| TH-PM-03 | No excluded or fuzzy columns initially | All columns (security_id, ticker, security_name, sector, total_held_value, holder_count, rank, as_of) are strictly compared. No pre-emptive exclusions. | FSD Section 8 |
| TH-PM-04 | Proofmark exit code 0 for full date range run | After running both V1 and V2 for 2024-10-01 through 2024-12-31, comparison produces PASS. | FSD Section 8; BLUEPRINT Phase D |
| TH-PM-05 | Fallback: fuzzy rank column if ROW_NUMBER() tie-breaking differs | If Proofmark fails specifically on the `rank` column due to tied total_held_value producing different tier labels between V1 and V2, escalate to adding a fuzzy override on the `rank` column with documented evidence from the Proofmark failure report. Do NOT pre-emptively exclude. | BR-11; FSD Section 8 (Risk note); FSD Open Question 1 |

---

## Proofmark Config (Expected)

```yaml
comparison_target: "top_holdings_by_value"
reader: parquet
threshold: 100.0
```

**Escalation config (only if TH-PM-05 triggers):**

```yaml
comparison_target: "top_holdings_by_value"
reader: parquet
threshold: 100.0
columns:
  fuzzy:
    - name: "rank"
      tolerance: 0
      tolerance_type: absolute
      reason: "ROW_NUMBER() tie-breaking is non-deterministic for equal total_held_value [BRD BR-11]"
```
