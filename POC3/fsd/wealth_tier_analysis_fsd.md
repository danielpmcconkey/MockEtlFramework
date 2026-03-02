# WealthTierAnalysis -- Functional Specification Document

## 1. Job Summary

This job classifies customers into four wealth tiers (Bronze, Silver, Gold, Platinum) based on the sum of their account balances and investment values, then aggregates per-tier statistics -- customer count, total wealth, average wealth, and percentage of total customers. Output is a 4-row CSV (one per tier, always in fixed order) with a trailer line, written in Overwrite mode.

## 2. V2 Module Chain

**Tier: 1 -- Framework Only**

| Step | Module | Purpose |
|------|--------|---------|
| 1 | DataSourcing | Pull `accounts` (account_id, customer_id, current_balance) |
| 2 | DataSourcing | Pull `investments` (investment_id, customer_id, current_value) |
| 3 | DataSourcing | Pull `customers` (id) -- used only for empty-guard |
| 4 | Transformation | SQL computes wealth per customer, assigns tiers, aggregates stats |
| 5 | CsvFileWriter | Write output CSV with header and trailer |

**Tier 1 justification:** All business logic in this job -- summing balances, classifying tiers via CASE/WHEN, aggregating per tier, computing percentages, and rounding -- is expressible in SQLite SQL. The V1 External module (`WealthTierAnalyzer.cs`) does row-by-row iteration that maps directly to GROUP BY + CASE + SUM/COUNT/AVG. No procedural logic is required. This is a textbook AP3 elimination.

## 3. DataSourcing Config

### 3.1 accounts

| Property | Value |
|----------|-------|
| resultName | `accounts` |
| schema | `datalake` |
| table | `accounts` |
| columns | `account_id`, `customer_id`, `current_balance` |

Effective dates: injected via shared state (`__minEffectiveDate` / `__maxEffectiveDate`). No hardcoded dates in config.

### 3.2 investments

| Property | Value |
|----------|-------|
| resultName | `investments` |
| schema | `datalake` |
| table | `investments` |
| columns | `investment_id`, `customer_id`, `current_value` |

Effective dates: injected via shared state.

### 3.3 customers

| Property | Value |
|----------|-------|
| resultName | `customers` |
| schema | `datalake` |
| table | `customers` |
| columns | `id` |

Effective dates: injected via shared state. Only `id` is sourced -- `first_name` and `last_name` are eliminated per AP4. The customers DataFrame is used solely for the empty guard (BR-10).

**Note on AP4 elimination:** V1 sources `first_name` and `last_name` from customers [wealth_tier_analysis.json:24], but these columns are never referenced in the External module's output or calculation logic [WealthTierAnalyzer.cs:20-24, 30-47]. V2 drops them.

## 4. Transformation SQL

A single Transformation module produces the `output` DataFrame. The SQL must:

1. Compute total wealth per customer across all `as_of` dates in the effective range (BR-1)
2. Classify each customer into a tier using V1's actual thresholds: Bronze < 10000, Silver < 100000, Gold < 500000, Platinum >= 500000 (BR-2, see Wrinkle Replication section for BRD discrepancy)
3. Guard against empty customers table (BR-10)
4. Always produce exactly 4 rows, one per tier, even if a tier has 0 customers (BR-4)
5. Output tiers in fixed order: Bronze, Silver, Gold, Platinum (BR-5)
6. Use banker's rounding (ROUND) for total_wealth, avg_wealth, and pct_of_customers (BR-7, W5)
7. Set `as_of` to the max effective date (BR-9)

```sql
WITH customer_wealth AS (
    SELECT
        customer_id,
        SUM(current_balance) AS wealth
    FROM accounts
    GROUP BY customer_id

    UNION ALL

    SELECT
        customer_id,
        SUM(current_value) AS wealth
    FROM investments
    GROUP BY customer_id
),
total_wealth_per_customer AS (
    SELECT
        customer_id,
        SUM(wealth) AS total_wealth
    FROM customer_wealth
    GROUP BY customer_id
),
-- V1 behavior: only customers with at least one account or investment row
-- appear in the wealth calculation [WealthTierAnalyzer.cs:30-47,58]
tier_assignment AS (
    SELECT
        customer_id,
        total_wealth,
        CASE
            -- V1 thresholds [WealthTierAnalyzer.cs:62-65]:
            -- Bronze < 10000, Silver < 100000, Gold < 500000, Platinum >= 500000
            WHEN total_wealth < 10000 THEN 'Bronze'
            WHEN total_wealth < 100000 THEN 'Silver'
            WHEN total_wealth < 500000 THEN 'Gold'
            ELSE 'Platinum'
        END AS wealth_tier
    FROM total_wealth_per_customer
),
tier_stats AS (
    SELECT
        wealth_tier,
        COUNT(*) AS customer_count,
        SUM(total_wealth) AS raw_total_wealth,
        CASE WHEN COUNT(*) > 0 THEN SUM(total_wealth) * 1.0 / COUNT(*) ELSE 0 END AS raw_avg_wealth
    FROM tier_assignment
    GROUP BY wealth_tier
),
-- Ensure all 4 tiers always appear (BR-4), in fixed order (BR-5)
all_tiers AS (
    SELECT 'Bronze' AS wealth_tier, 1 AS sort_order
    UNION ALL SELECT 'Silver', 2
    UNION ALL SELECT 'Gold', 3
    UNION ALL SELECT 'Platinum', 4
),
total_customers AS (
    SELECT COUNT(*) AS cnt FROM total_wealth_per_customer
),
-- Empty guard: if the customers table is empty, produce 0 rows (BR-10)
customers_guard AS (
    SELECT COUNT(*) AS cnt FROM customers
)
SELECT
    a.wealth_tier,
    COALESCE(t.customer_count, 0) AS customer_count,
    -- W5: banker's rounding (MidpointRounding.ToEven) for total_wealth [WealthTierAnalyzer.cs:87]
    ROUND(COALESCE(t.raw_total_wealth, 0), 2) AS total_wealth,
    -- W5: banker's rounding for avg_wealth [WealthTierAnalyzer.cs:88]
    ROUND(COALESCE(t.raw_avg_wealth, 0), 2) AS avg_wealth,
    -- W5: banker's rounding for pct_of_customers [WealthTierAnalyzer.cs:79-80]
    -- Note: V1 uses ToEven here, NOT AwayFromZero (BRD BR-6 is incorrect; see V1 code line 80)
    CASE
        WHEN tc.cnt > 0 THEN ROUND(COALESCE(t.customer_count, 0) * 100.0 / tc.cnt, 2)
        ELSE 0
    END AS pct_of_customers,
    (SELECT MAX(as_of) FROM accounts) AS as_of
FROM all_tiers a
LEFT JOIN tier_stats t ON a.wealth_tier = t.wealth_tier
CROSS JOIN total_customers tc
WHERE (SELECT cnt FROM customers_guard) > 0
ORDER BY a.sort_order
```

**Important SQLite rounding note:** SQLite's `ROUND()` function uses banker's rounding (round half to even) by default, which matches V1's `MidpointRounding.ToEven` behavior [WealthTierAnalyzer.cs:80,87-88]. This is a fortunate alignment -- no special handling needed.

**Empty guard implementation (BR-10):** The `WHERE (SELECT cnt FROM customers_guard) > 0` clause ensures that when the customers table has zero rows, the entire query returns 0 rows. This matches V1's early return of an empty DataFrame [WealthTierAnalyzer.cs:20-24].

**as_of column (BR-9):** V1 reads `__maxEffectiveDate` from shared state [WealthTierAnalyzer.cs:26]. In V2, the DataSourcing module filters by effective date range, so `MAX(as_of) FROM accounts` yields the same value as `__maxEffectiveDate`. If accounts is empty but investments is not, we should use `MAX(as_of)` across all source tables. However, since the empty guard on customers would also likely trigger in that scenario, using `accounts` is consistent. An alternative would be to use a subquery across both tables:

```sql
(SELECT MAX(d) FROM (SELECT MAX(as_of) AS d FROM accounts UNION ALL SELECT MAX(as_of) FROM investments)) AS as_of
```

This is flagged as an open question (OQ-1).

## 5. Writer Config

| Property | Value | Evidence |
|----------|-------|----------|
| type | `CsvFileWriter` | [wealth_tier_analysis.json:32] |
| source | `output` | [wealth_tier_analysis.json:33] |
| outputFile | `Output/double_secret_curated/wealth_tier_analysis.csv` | V2 path convention |
| includeHeader | `true` | [wealth_tier_analysis.json:35] |
| trailerFormat | `TRAILER\|{row_count}\|{date}` | [wealth_tier_analysis.json:36] |
| writeMode | `Overwrite` | [wealth_tier_analysis.json:37] |
| lineEnding | `LF` | [wealth_tier_analysis.json:38] |

All writer params match V1 exactly except `outputFile`, which uses the V2 output directory.

## 6. Wrinkle Replication

### W5 -- Banker's Rounding

**Applies to:** `total_wealth`, `avg_wealth`, `pct_of_customers`

**V1 behavior:** All three monetary/percentage fields use `MidpointRounding.ToEven` (banker's rounding) [WealthTierAnalyzer.cs:80,87-88].

**V2 replication:** SQLite's `ROUND()` function uses banker's rounding by default. The SQL `ROUND(value, 2)` naturally produces the same output. No special handling required.

**BRD correction (BR-6, RESOLVED):** The BRD originally stated that `pct_of_customers` uses `MidpointRounding.AwayFromZero`. The BRD has been corrected to state `MidpointRounding.ToEven`, matching the V1 source code at line 80:
```csharp
Math.Round((decimal)count / totalCustomers * 100m, 2, MidpointRounding.ToEven)
```
The BRD and FSD are now consistent.

### No Other W-codes Apply

- W1 (Sunday skip): Not present in V1 code. No day-of-week checks.
- W2 (Weekend fallback): Not present.
- W3a/b/c (Boundary rows): Not present. Output is always exactly 4 rows.
- W4 (Integer division): Not present. V1 uses decimal division throughout.
- W6 (Double epsilon): Not present. V1 uses `decimal` for all monetary accumulation [WealthTierAnalyzer.cs:29,36,45].
- W7 (Trailer inflated count): Not present. V1 uses the framework's CsvFileWriter, not direct I/O. The trailer `{row_count}` token is substituted by the framework and correctly reflects output row count.
- W8 (Trailer stale date): Not present. The trailer `{date}` token is substituted by the framework from `__maxEffectiveDate`.
- W9 (Wrong writeMode): writeMode is Overwrite, which is appropriate for a snapshot output. Debatable, but this is the V1 behavior.
- W10 (Absurd numParts): Not applicable (CSV, not Parquet).
- W12 (Header every append): Not applicable (Overwrite mode).

## 7. Anti-Pattern Elimination

### AP3 -- Unnecessary External Module (ELIMINATED)

**V1 problem:** V1 uses a full C# External module (`WealthTierAnalyzer.cs`) for logic that is entirely expressible in SQL: summing values per customer, classifying with CASE/WHEN, aggregating with GROUP BY, and computing percentages [WealthTierAnalyzer.cs:6-97].

**V2 solution:** Replaced with a single Transformation (SQL) module. The entire pipeline is Tier 1: `DataSourcing -> Transformation -> CsvFileWriter`.

### AP4 -- Unused Columns (ELIMINATED)

**V1 problem:** The customers DataSourcing config includes `first_name` and `last_name` [wealth_tier_analysis.json:24], but these columns are never used in the output or any calculation [WealthTierAnalyzer.cs:18,20-24]. The customers DataFrame is only checked for emptiness.

**V2 solution:** customers DataSourcing sources only `id`. This is sufficient for the empty guard.

### AP6 -- Row-by-Row Iteration (ELIMINATED)

**V1 problem:** The External module iterates accounts and investments row-by-row with `foreach` loops to accumulate wealth per customer [WealthTierAnalyzer.cs:33-37,42-46], then iterates `wealthByCustomer` to assign tiers [WealthTierAnalyzer.cs:58-69].

**V2 solution:** Replaced with set-based SQL operations: `GROUP BY customer_id` for aggregation, `CASE WHEN` for tier assignment, `GROUP BY wealth_tier` for tier statistics.

### AP7 -- Magic Values (ELIMINATED)

**V1 problem:** Tier thresholds are bare literals: `10000m`, `100000m`, `500000m` [WealthTierAnalyzer.cs:62-65]. No documentation of what these thresholds represent.

**V2 solution:** The SQL includes inline comments documenting each threshold. Since SQL does not support named constants, the comments serve as documentation:
```sql
-- V1 thresholds [WealthTierAnalyzer.cs:62-65]:
-- Bronze < 10000, Silver < 100000, Gold < 500000, Platinum >= 500000
```
The threshold values remain identical for output equivalence.

### AP1 -- Dead-End Sourcing (PARTIALLY APPLICABLE)

**Analysis:** The `customers` table is sourced but its data is not used in the output. However, it IS used for the empty guard (BR-10) [WealthTierAnalyzer.cs:20-24]. This is not truly dead-end sourcing -- it serves a functional purpose, even if that purpose is debatable (see OQ-1). V2 retains the customers DataSourcing but strips unused columns (AP4 fix).

### AP2, AP5, AP8, AP9, AP10 -- Not Applicable

- AP2 (Duplicated logic): No cross-job duplication identified.
- AP5 (Asymmetric NULLs): Not present. Wealth defaults to 0 via `COALESCE`.
- AP8 (Complex SQL / unused CTEs): V2 SQL is purpose-built with no unused CTEs.
- AP9 (Misleading names): Job name accurately describes what it does.
- AP10 (Over-sourcing dates): V2 uses framework effective date injection. No date filtering in SQL.

## 8. Proofmark Config

```yaml
comparison_target: "wealth_tier_analysis"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

**Justification:**
- `reader: csv` -- V1 output is a CSV file [wealth_tier_analysis.json:32]
- `header_rows: 1` -- V1 config has `includeHeader: true` [wealth_tier_analysis.json:35]
- `trailer_rows: 1` -- V1 has `trailerFormat` AND uses `writeMode: Overwrite`, so the file has exactly one trailer at the end [wealth_tier_analysis.json:36-37]
- `threshold: 100.0` -- Byte-identical output required. No non-deterministic fields.
- No `columns.excluded` -- All output columns are deterministic.
- No `columns.fuzzy` -- All rounding is handled by SQLite's ROUND() which matches V1's banker's rounding. No double-precision epsilon issues (V1 uses `decimal`).

## 9. Open Questions

**OQ-1: customers table empty guard necessity**

The customers table is sourced solely for the empty guard [WealthTierAnalyzer.cs:20-24]. If the customers table is empty but accounts/investments have data, V1 produces an empty output. Whether this is intentional business logic or an over-cautious guard is unclear. V2 replicates this behavior for output equivalence. If a future iteration determines the guard is unnecessary, the customers DataSourcing and `WHERE (SELECT cnt FROM customers_guard) > 0` clause can be removed.

**OQ-2: as_of column source when accounts is empty but investments is not**

V1 reads `__maxEffectiveDate` directly from shared state [WealthTierAnalyzer.cs:26]. V2's SQL derives `as_of` from `MAX(as_of) FROM accounts`. If accounts has no rows for the effective date but investments does, V1 would still have a valid `as_of` (from shared state) while V2's subquery would return NULL. This edge case should be addressed by using a cross-table max:
```sql
(SELECT MAX(d) FROM (SELECT MAX(as_of) AS d FROM accounts UNION ALL SELECT MAX(as_of) FROM investments)) AS as_of
```
Or by using the Transformation module's access to `__maxEffectiveDate` if available in SQLite context. This needs verification during implementation.

**OQ-3: BRD BR-2 threshold discrepancy (RESOLVED)**

The BRD originally stated the Bronze threshold as `< $25,000`, but V1 source code uses `< 10000m` [WealthTierAnalyzer.cs:62]. The BRD has been corrected to state `< $10,000`. BRD and FSD are now consistent.

**OQ-4: BRD BR-6 rounding discrepancy (RESOLVED)**

The BRD originally stated `pct_of_customers` uses `MidpointRounding.AwayFromZero`, but V1 source code uses `MidpointRounding.ToEven` [WealthTierAnalyzer.cs:80]. The BRD has been corrected to state banker's rounding (ToEven). BRD and FSD are now consistent.
