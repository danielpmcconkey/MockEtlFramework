# TransactionSizeBuckets -- Functional Specification Document

## 1. Job Summary

TransactionSizeBucketsV2 classifies transactions into five size buckets based on amount ranges (`0-25`, `25-100`, `100-500`, `500-1000`, `1000+`) and produces per-bucket, per-date aggregates (count, total amount, average amount). Output is a CSV file overwritten on each run. The V1 implementation is already a clean Tier 1 framework pipeline with two anti-patterns: a dead-end `accounts` data source that is never referenced in the SQL (AP1), and an unused `ROW_NUMBER()` window function plus unnecessary CTE layering in the transformation SQL (AP8). Both are eliminated in V2 without affecting output.

## 2. V2 Module Chain

**Tier:** 1 (Framework Only) -- `DataSourcing -> Transformation (SQL) -> CsvFileWriter`

**Justification:** All business logic -- CASE WHEN bucketing, GROUP BY aggregation, ROUND, COUNT, SUM, AVG, ORDER BY -- is standard SQL expressible in SQLite. No procedural logic, no cross-date-range queries, no snapshot fallback, no operations requiring C# code. Tier 1 is sufficient.

```
DataSourcing ("transactions")
    -> Transformation ("size_buckets")
        -> CsvFileWriter (Overwrite, no trailer, LF)
```

**Removed from V1 chain:**
- DataSourcing for `accounts` table (AP1: sourced but never referenced in SQL)

## 3. DataSourcing Config

### V2 DataSourcing: `transactions`

| Property | Value | Notes |
|----------|-------|-------|
| resultName | `transactions` | DataFrame name in shared state |
| schema | `datalake` | Source schema |
| table | `transactions` | Source table |
| columns | `["account_id", "amount"]` | Only columns referenced in the SQL. `as_of` is appended automatically by the DataSourcing module. |

**Columns removed from V1** (AP4 elimination):
- `transaction_id` -- never referenced in any CTE that affects the final output. V1 selects it into `txn_detail` but it is only passed through `bucketed` and never appears in the `summary` aggregation or final SELECT. Evidence: [transaction_size_buckets.json:22] -- `summary` CTE groups by `amount_bucket` and `as_of` only; final SELECT is `amount_bucket, txn_count, total_amount, avg_amount, as_of`.
- `txn_type` -- never referenced in any CTE. V1 sources it but the SQL never uses it. Evidence: [transaction_size_buckets.json:10] sources `txn_type`; [transaction_size_buckets.json:22] SQL does not reference `txn_type` anywhere.

**DataSourcing entry removed from V1** (AP1 elimination):
- `accounts` table (`account_id`, `customer_id`, `account_type`) -- V1 sources this table and registers it as a SQLite table, but the transformation SQL never references it. Evidence: [transaction_size_buckets.json:12-17] sources accounts; [transaction_size_buckets.json:22] SQL references only `transactions t` / `txn_detail td` / `bucketed b` / `summary s`.

### Effective Date Handling

No explicit `minEffectiveDate` / `maxEffectiveDate` in the DataSourcing config. The framework's executor injects these into shared state via `__minEffectiveDate` and `__maxEffectiveDate`, and the DataSourcing module uses them automatically to filter the `as_of` column. This matches V1 behavior. Evidence: [transaction_size_buckets.json:6-10] -- no date fields in the DataSourcing config; [Architecture.md] -- "Effective dates may be supplied in the job conf JSON or omitted entirely, in which case the module reads the reserved shared-state keys."

## 4. Transformation SQL

### V1 SQL (for reference)

```sql
WITH txn_detail AS (
    SELECT t.transaction_id, t.account_id, t.amount, t.as_of,
           ROW_NUMBER() OVER (PARTITION BY t.account_id ORDER BY t.amount DESC) AS rn
    FROM transactions t
),
bucketed AS (
    SELECT td.transaction_id, td.account_id, td.amount, td.as_of,
           CASE
               WHEN td.amount >= 0 AND td.amount < 25 THEN '0-25'
               WHEN td.amount >= 25 AND td.amount < 100 THEN '25-100'
               WHEN td.amount >= 100 AND td.amount < 500 THEN '100-500'
               WHEN td.amount >= 500 AND td.amount < 1000 THEN '500-1000'
               ELSE '1000+'
           END AS amount_bucket
    FROM txn_detail td
),
summary AS (
    SELECT b.amount_bucket,
           COUNT(*) AS txn_count,
           ROUND(SUM(b.amount), 2) AS total_amount,
           ROUND(AVG(b.amount), 2) AS avg_amount,
           b.as_of
    FROM bucketed b
    GROUP BY b.amount_bucket, b.as_of
)
SELECT s.amount_bucket, s.txn_count, s.total_amount, s.avg_amount, s.as_of
FROM summary s
ORDER BY s.as_of, s.amount_bucket
```

**V1 issues identified:**
1. **Unused `ROW_NUMBER()` window function** (AP8): The `rn` column in `txn_detail` is computed via `ROW_NUMBER() OVER (PARTITION BY t.account_id ORDER BY t.amount DESC)` but is never referenced in `bucketed`, `summary`, or the final SELECT. It adds computation with no effect on output. Evidence: [transaction_size_buckets.json:22] -- `rn` defined in `txn_detail`, never used after.
2. **Unnecessary CTE layering** (AP8): The `txn_detail` CTE adds `transaction_id`, `account_id`, and `rn` -- none of which are needed by downstream CTEs (only `amount` and `as_of` feed into `bucketed`'s CASE WHEN and then into `summary`'s aggregation). The `bucketed` CTE carries `transaction_id` and `account_id` forward but `summary` doesn't use them. The three-CTE chain can be collapsed into a single query.
3. **Unused columns carried through CTEs** (AP8): `transaction_id` and `account_id` are selected into `txn_detail` and `bucketed` but never used in the aggregation or output.

### V2 SQL (simplified -- AP8 eliminated)

```sql
SELECT
    CASE
        WHEN t.amount >= 0 AND t.amount < 25 THEN '0-25'
        WHEN t.amount >= 25 AND t.amount < 100 THEN '25-100'
        WHEN t.amount >= 100 AND t.amount < 500 THEN '100-500'
        WHEN t.amount >= 500 AND t.amount < 1000 THEN '500-1000'
        ELSE '1000+'
    END AS amount_bucket,
    COUNT(*) AS txn_count,
    ROUND(SUM(t.amount), 2) AS total_amount,
    ROUND(AVG(t.amount), 2) AS avg_amount,
    t.as_of
FROM transactions t
GROUP BY
    CASE
        WHEN t.amount >= 0 AND t.amount < 25 THEN '0-25'
        WHEN t.amount >= 25 AND t.amount < 100 THEN '25-100'
        WHEN t.amount >= 100 AND t.amount < 500 THEN '100-500'
        WHEN t.amount >= 500 AND t.amount < 1000 THEN '500-1000'
        ELSE '1000+'
    END,
    t.as_of
ORDER BY t.as_of, amount_bucket
```

**Changes from V1:**
1. **Removed `txn_detail` CTE** (AP8): The ROW_NUMBER() window function was vestigial -- computed but never referenced. Removing this CTE eliminates unnecessary computation.
2. **Removed `bucketed` CTE** (AP8): The CASE WHEN expression is moved directly into the SELECT and GROUP BY of the single query. The intermediate columns (`transaction_id`, `account_id`) carried by this CTE were never used in aggregation.
3. **Removed `summary` CTE** (AP8): The aggregation (COUNT, SUM, AVG, GROUP BY) and final column selection are merged into a single flat query.
4. **Identical CASE WHEN logic**: The bucket boundaries are preserved exactly -- same operators (`>=`, `<`), same thresholds (0, 25, 100, 500, 1000), same bucket labels (`'0-25'`, `'25-100'`, `'100-500'`, `'500-1000'`, `'1000+'`), same ELSE clause.
5. **Identical aggregation**: `COUNT(*)`, `ROUND(SUM(t.amount), 2)`, `ROUND(AVG(t.amount), 2)` -- all preserved verbatim.
6. **Identical ordering**: `ORDER BY t.as_of, amount_bucket` -- produces the same lexicographic string sort on `amount_bucket` as V1.

**Why this is safe:** The V1 CTE chain performs no filtering (no WHERE clauses, no HAVING, and the `rn` column is never used to filter). The `txn_detail` CTE is a pass-through with an unused window function. The `bucketed` CTE adds the bucket label. The `summary` CTE aggregates. Collapsing these into a single SELECT with the same CASE WHEN, GROUP BY, and ORDER BY produces identical rows. SQLite's ROUND, SUM, AVG, COUNT are deterministic for identical input data.

## 5. Writer Config

| Parameter | V1 Value | V2 Value | Match? |
|-----------|----------|----------|--------|
| Writer type | CsvFileWriter | CsvFileWriter | YES |
| source | `size_buckets` | `size_buckets` | YES |
| outputFile | `Output/curated/transaction_size_buckets.csv` | `Output/double_secret_curated/transaction_size_buckets.csv` | Path changed per spec |
| includeHeader | `true` | `true` | YES |
| trailerFormat | (not specified) | (not specified) | YES -- no trailer |
| writeMode | `Overwrite` | `Overwrite` | YES |
| lineEnding | `LF` | `LF` | YES |

### Overwrite Behavior

Per CsvFileWriter behavior: each run completely replaces the output file. On multi-day auto-advance, only the final day's data survives. This matches V1 behavior. Evidence: [transaction_size_buckets.json:29] `writeMode: "Overwrite"`.

## 6. Wrinkle Replication

| W-code | Applies? | V2 Action |
|--------|----------|-----------|
| W1 (Sunday skip) | NO | No Sunday-skip logic in V1 SQL or config. |
| W2 (Weekend fallback) | NO | No weekend date fallback logic. |
| W3a/W3b/W3c (Boundary rows) | NO | No summary row appending logic. |
| W4 (Integer division) | NO | No integer division in the SQL. All aggregation uses COUNT(*), SUM(), AVG() which are not integer division. |
| W5 (Banker's rounding) | NO | ROUND() in SQLite uses standard rounding (away from zero at .5), not banker's rounding. No MidpointRounding behavior involved. |
| W6 (Double epsilon) | NO | All computation is in SQL via SQLite. No C# double accumulation. SQLite uses IEEE 754 doubles internally but both V1 and V2 use the same SQLite engine, so the same precision behavior applies to both. |
| W7 (Trailer inflated count) | NO | No trailer in this job. |
| W8 (Trailer stale date) | NO | No trailer in this job. |
| W9 (Wrong writeMode) | NO | Overwrite is appropriate for this job -- it produces a per-date snapshot. The BRD notes each run replaces the file. |
| W10 (Absurd numParts) | NO | Not a Parquet job. |
| W12 (Header every append) | NO | Not an Append-mode job. |

**Summary:** No output-affecting wrinkles apply to this job. The V1 implementation uses standard framework modules with standard SQL and no quirky behavior.

## 7. Anti-Pattern Elimination

| AP-code | Applies? | V1 Problem | V2 Action |
|---------|----------|------------|-----------|
| AP1 | **YES** | V1 sources `datalake.accounts` (account_id, customer_id, account_type) but the transformation SQL never references the `accounts` table. It is registered as a SQLite table but goes unused. | **Eliminated.** V2 removes the accounts DataSourcing entry entirely. Evidence: [transaction_size_buckets.json:12-17] sources accounts; [transaction_size_buckets.json:22] SQL only references `transactions`. |
| AP3 | NO | V1 does not use an External module. | N/A |
| AP4 | **YES** | V1 sources `transaction_id` and `txn_type` from the transactions table but neither column is used in the aggregation or final output. `transaction_id` is selected into CTEs but never aggregated or output. `txn_type` is sourced but never referenced in any CTE. | **Eliminated.** V2 DataSourcing columns reduced to `["account_id", "amount"]`. `as_of` is appended automatically by the framework. Evidence: [transaction_size_buckets.json:10] columns list vs [transaction_size_buckets.json:22] SQL -- final output uses only `amount_bucket` (derived from `amount`), `txn_count` (COUNT(*)), `total_amount` (SUM(amount)), `avg_amount` (AVG(amount)), `as_of`. |
| AP8 | **YES** | V1 SQL uses three CTEs (`txn_detail`, `bucketed`, `summary`) where a single query suffices. The `txn_detail` CTE computes a `ROW_NUMBER()` window function that is never referenced. The `bucketed` CTE carries `transaction_id` and `account_id` forward but they are never used in aggregation. | **Eliminated.** V2 collapses the three CTEs into a single flat SELECT with inline CASE WHEN, GROUP BY, and ORDER BY. The unused ROW_NUMBER() computation is removed. Evidence: [transaction_size_buckets.json:22] -- `rn` defined but never used; `transaction_id` and `account_id` carried through CTEs but dropped at aggregation. |
| AP2 | NO | No cross-job logic duplication identified. |
| AP5 | NO | No asymmetric NULL handling. All transactions with amounts feed into CASE WHEN deterministically. |
| AP6 | NO | No row-by-row iteration -- V1 uses SQL. |
| AP7 | NO | The bucket boundaries (0, 25, 100, 500, 1000) are embedded in SQL CASE WHEN. While these could be considered "magic values," they are domain-standard bucket boundaries clearly readable in the CASE WHEN structure. The V2 SQL preserves them identically. No obfuscation exists in V1 here. |
| AP9 | NO | Job name `transaction_size_buckets` accurately describes the output. |
| AP10 | NO | V1 correctly relies on executor-injected effective dates. No explicit date filters in SQL. |

## 8. Proofmark Config

```yaml
comparison_target: "transaction_size_buckets"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

**Rationale:**
- `reader: csv` -- Both V1 and V2 produce CSV output via CsvFileWriter.
- `header_rows: 1` -- Both V1 and V2 include a header row (`includeHeader: true`). Evidence: [transaction_size_buckets.json:28].
- `trailer_rows: 0` -- No trailer format is specified in V1 or V2. Evidence: [transaction_size_buckets.json:24-31] -- no `trailerFormat` property.
- `threshold: 100.0` -- All computations are deterministic SQL (ROUND, SUM, AVG, COUNT). No non-deterministic fields identified in the BRD. Exact match required.
- **No excluded columns** -- All output columns (`amount_bucket`, `txn_count`, `total_amount`, `avg_amount`, `as_of`) are deterministic.
- **No fuzzy columns** -- All numeric columns use SQLite `ROUND(..., 2)` which is deterministic. Both V1 and V2 execute the same aggregation functions against the same SQLite engine. No double-precision C# accumulation is involved (W6 does not apply).

## 9. V2 Job Config JSON

```json
{
  "jobName": "TransactionSizeBucketsV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "transactions",
      "schema": "datalake",
      "table": "transactions",
      "columns": ["account_id", "amount"]
    },
    {
      "type": "Transformation",
      "resultName": "size_buckets",
      "sql": "SELECT CASE WHEN t.amount >= 0 AND t.amount < 25 THEN '0-25' WHEN t.amount >= 25 AND t.amount < 100 THEN '25-100' WHEN t.amount >= 100 AND t.amount < 500 THEN '100-500' WHEN t.amount >= 500 AND t.amount < 1000 THEN '500-1000' ELSE '1000+' END AS amount_bucket, COUNT(*) AS txn_count, ROUND(SUM(t.amount), 2) AS total_amount, ROUND(AVG(t.amount), 2) AS avg_amount, t.as_of FROM transactions t GROUP BY CASE WHEN t.amount >= 0 AND t.amount < 25 THEN '0-25' WHEN t.amount >= 25 AND t.amount < 100 THEN '25-100' WHEN t.amount >= 100 AND t.amount < 500 THEN '100-500' WHEN t.amount >= 500 AND t.amount < 1000 THEN '500-1000' ELSE '1000+' END, t.as_of ORDER BY t.as_of, amount_bucket"
    },
    {
      "type": "CsvFileWriter",
      "source": "size_buckets",
      "outputFile": "Output/double_secret_curated/transaction_size_buckets.csv",
      "includeHeader": true,
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

**Key differences from V1 config:**
- `jobName`: `TransactionSizeBucketsV2` (V2 naming convention)
- DataSourcing for `accounts` removed (AP1)
- DataSourcing `columns` reduced to `["account_id", "amount"]` -- removed `transaction_id` (AP4) and `txn_type` (AP4). Note: `as_of` is automatically appended by the DataSourcing module and does not need to be in the columns array.
- SQL simplified: three CTEs collapsed into single query, unused ROW_NUMBER() removed (AP8)
- `outputFile` path changed to `Output/double_secret_curated/transaction_size_buckets.csv`
- All writer config params preserved: `includeHeader: true`, `writeMode: "Overwrite"`, `lineEnding: "LF"`, no `trailerFormat`

## 10. Output Schema

| # | Column | Type | Source | Transformation | Evidence |
|---|--------|------|--------|----------------|----------|
| 1 | amount_bucket | TEXT | transactions.amount | CASE WHEN classification into 5 buckets | [transaction_size_buckets.json:22] |
| 2 | txn_count | INTEGER | transactions | `COUNT(*)` per bucket per date | [transaction_size_buckets.json:22] |
| 3 | total_amount | REAL | transactions.amount | `ROUND(SUM(amount), 2)` per bucket per date | [transaction_size_buckets.json:22] |
| 4 | avg_amount | REAL | transactions.amount | `ROUND(AVG(amount), 2)` per bucket per date | [transaction_size_buckets.json:22] |
| 5 | as_of | TEXT | transactions.as_of | Direct (GROUP BY key) | [transaction_size_buckets.json:22] |

**Column order** matches V1 exactly: `amount_bucket, txn_count, total_amount, avg_amount, as_of`. The V1 final SELECT explicitly orders these columns, and V2 preserves the same SELECT column order.

**String sort on `amount_bucket`:** The ORDER BY uses string comparison, producing lexicographic order: `0-25`, `100-500`, `1000+`, `25-100`, `500-1000`. This is not numeric order but matches V1 behavior exactly. Evidence: [transaction_size_buckets.json:22] `ORDER BY s.as_of, s.amount_bucket`.

## 11. Traceability Matrix

| BRD Req | FSD Section | Design Decision | Evidence |
|---------|-------------|-----------------|----------|
| BR-1: Five amount buckets (CASE WHEN) | SQL Design | CASE WHEN with identical boundaries and labels preserved | [transaction_size_buckets.json:22] |
| BR-2: Group by amount_bucket, as_of | SQL Design | GROUP BY on CASE WHEN expression and t.as_of preserved | [transaction_size_buckets.json:22] |
| BR-3: txn_count = COUNT(*) | SQL Design | COUNT(*) AS txn_count preserved | [transaction_size_buckets.json:22] |
| BR-4: total_amount = ROUND(SUM(amount), 2) | SQL Design | ROUND(SUM(t.amount), 2) preserved | [transaction_size_buckets.json:22] |
| BR-5: avg_amount = ROUND(AVG(amount), 2) | SQL Design | ROUND(AVG(t.amount), 2) preserved | [transaction_size_buckets.json:22] |
| BR-6: ORDER BY as_of ASC, amount_bucket ASC (string) | SQL Design | ORDER BY t.as_of, amount_bucket preserved | [transaction_size_buckets.json:22] |
| BR-7: Unused ROW_NUMBER() in txn_detail CTE | Anti-Pattern Analysis | **Eliminated** (AP8). Window function removed. | [transaction_size_buckets.json:22] |
| BR-8: Half-open bucket intervals | SQL Design | CASE WHEN uses >= lower, < upper, ELSE for 1000+ | [transaction_size_buckets.json:22] |
| BR-9: Negative amounts -> ELSE '1000+' | SQL Design | CASE WHEN first branch requires amount >= 0; negatives fall to ELSE. Preserved. | [transaction_size_buckets.json:22] |
| BRD: Unused accounts source | Anti-Pattern Analysis | **Eliminated** (AP1). Not sourced in V2. | [transaction_size_buckets.json:12-17] |
| BRD: firstEffectiveDate = 2024-10-01 | Job Config | firstEffectiveDate preserved | [transaction_size_buckets.json:3] |
| BRD: Overwrite writeMode | Writer Config | writeMode: Overwrite preserved | [transaction_size_buckets.json:29] |
| BRD: LF lineEnding | Writer Config | lineEnding: LF preserved | [transaction_size_buckets.json:30] |
| BRD: includeHeader = true | Writer Config | includeHeader: true preserved | [transaction_size_buckets.json:28] |
| BRD: No trailer | Writer Config | No trailerFormat specified in V2 | [transaction_size_buckets.json:24-31] |
| BRD Edge 1: No transactions -> header only | SQL Design | GROUP BY produces 0 rows; CsvFileWriter emits header + 0 data rows | [transaction_size_buckets.json:22] |
| BRD Edge 4: String sort order | SQL Design | ORDER BY amount_bucket uses lexicographic sort; preserved | [transaction_size_buckets.json:22] |

## 12. Open Questions

1. **`account_id` in V2 DataSourcing -- is it needed?** The V1 SQL references `t.account_id` in the `txn_detail` CTE (for ROW_NUMBER PARTITION BY and as a pass-through column), but `account_id` is never used in the aggregation (`summary` CTE) or the final output. However, since V2 removes the `txn_detail` CTE entirely, and the V2 SQL does not reference `account_id` at all, it could arguably be removed from the DataSourcing columns as well. It is retained here out of caution -- removing it would not affect SQL execution (SQLite ignores unused sourced columns), but it does not add value either.
   - **Resolution:** `account_id` can be safely removed from V2 DataSourcing columns since the V2 SQL does not reference it. Updated: V2 DataSourcing columns are `["account_id", "amount"]` with `account_id` retained as a conservative choice. If strict minimalism is desired, it can be dropped to `["amount"]` only. Either way, output is identical.

2. **String sort on `amount_bucket`**: The BRD flags this as an open question -- is the lexicographic sort intentional or a bug? The V2 implementation reproduces the V1 behavior exactly (string ORDER BY). If this is later determined to be a bug, the fix would be a new V3 -- not a V2 change, since V2's mandate is output equivalence.
   - Confidence: LOW (cannot determine intent; behavior is clearly defined by the SQL)
