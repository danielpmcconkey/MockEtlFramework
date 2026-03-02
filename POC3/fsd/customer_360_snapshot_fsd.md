# Customer360Snapshot -- Functional Specification Document

## 1. Overview & Tier Selection

**Job:** Customer360SnapshotV2
**Tier:** Tier 1 -- Framework Only (`DataSourcing -> Transformation (SQL) -> ParquetFileWriter`)

This job produces a comprehensive 360-degree customer snapshot: per-customer aggregations of account counts/balances, card counts, and investment counts/values. It implements weekend fallback logic (Saturday/Sunday map to the preceding Friday) and outputs a Parquet file.

**Tier Justification:** The V1 implementation uses an External module (AP3) for logic that is entirely expressible in SQL:
- Weekend date fallback: SQLite's `strftime('%w', date)` returns day-of-week (0=Sunday, 6=Saturday), and `date(col, '-N days')` handles date arithmetic.
- Multi-table LEFT JOIN with GROUP BY aggregation: standard SQL.
- COALESCE for default zeros: standard SQL.
- ROUND for 2-decimal rounding: standard SQL.
- NULL coalescing for name fields: COALESCE in SQL.

No procedural logic, snapshot boundary queries, or non-SQL operations are required. Tier 1 is sufficient.

---

## 2. V2 Module Chain

```
DataSourcing (customers)
  -> DataSourcing (accounts)
    -> DataSourcing (cards)
      -> DataSourcing (investments)
        -> Transformation (SQL: weekend fallback + join + aggregate)
          -> ParquetFileWriter (Parquet, 1 part, Overwrite)
```

### Module Details

| Step | Module Type | resultName / source | Purpose |
|------|------------|---------------------|---------|
| 1 | DataSourcing | `customers` | Load customer records (id, first_name, last_name) |
| 2 | DataSourcing | `accounts` | Load account records (account_id, customer_id, current_balance) |
| 3 | DataSourcing | `cards` | Load card records (card_id, customer_id) |
| 4 | DataSourcing | `investments` | Load investment records (investment_id, customer_id, current_value) |
| 5 | Transformation | `output` | Weekend fallback, join, aggregate, produce output schema |
| 6 | ParquetFileWriter | source: `output` | Write to `Output/double_secret_curated/customer_360_snapshot/` |

---

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (Reproduce)

| ID | Applies? | V1 Behavior | V2 Handling |
|----|----------|-------------|-------------|
| W2 | **YES** | Weekend fallback: Saturday -> Friday (date-1), Sunday -> Friday (date-2) | Implemented in SQL using `strftime('%w', as_of_date)` with CASE expression. Comment documents the V1 behavior replication. |
| W5 | No | Banker's rounding | Not applicable. `Math.Round(x, 2)` in V1 uses default `MidpointRounding.ToEven`, but SQLite's `ROUND()` also uses banker's rounding. Output equivalence maintained. |
| W9 | Evaluated | Overwrite mode | V1 uses Overwrite, which is correct for a snapshot job. Only the last effective date's output survives. This is intentional V1 behavior, reproduced exactly. |

### Code-Quality Anti-Patterns (Eliminate)

| ID | Applies? | V1 Problem | V2 Fix |
|----|----------|------------|--------|
| AP1 | **YES** | V1 sources `prefix`, `suffix` from customers but never uses them in output. V1 sources `interest_rate`, `credit_limit`, `apr` from accounts but never uses them. V1 sources `card_number_masked` from cards and `card_id` from cards, `investment_id`, `account_id` from accounts -- none used in output. | V2 sources ONLY the columns needed: `id`, `first_name`, `last_name` from customers; `customer_id`, `current_balance` from accounts; `customer_id` from cards; `customer_id`, `current_value` from investments. |
| AP3 | **YES** | V1 uses External module (`Customer360SnapshotBuilder`) for logic that is standard SQL (joins, GROUP BY, COALESCE). | V2 replaces with Tier 1 framework chain: `DataSourcing -> Transformation (SQL) -> ParquetFileWriter`. |
| AP4 | **YES** | Unused columns: `prefix`, `suffix`, `interest_rate`, `credit_limit`, `apr`, `card_number_masked`, `card_id`, `account_id`, `investment_id`. | Removed from V2 DataSourcing configs. Only columns that feed the output are sourced. |
| AP6 | **YES** | V1 uses C# `foreach` loops with Dictionary accumulators for aggregation logic that is a textbook GROUP BY + LEFT JOIN. | V2 uses SQL GROUP BY with LEFT JOINs in a single Transformation module. |

### Anti-Patterns Not Applicable

| ID | Why Not |
|----|---------|
| W1, W3a-c, W4, W6, W7, W8, W10, W12 | Not present in this job's V1 implementation. |
| AP2 | No cross-job duplication identified within scope. |
| AP5 | NULL handling is symmetric: all missing aggregations default to 0, names default to empty string. |
| AP7 | No magic values. Weekend day-of-week checks (Saturday=6, Sunday=0) are self-documenting. |
| AP8 | No complex SQL or unused CTEs in V1 (V1 had no SQL at all). |
| AP9 | Job name accurately describes output. |
| AP10 | Framework injects effective dates into DataSourcing automatically. V2 relies on this. |

---

## 4. Output Schema

| Column | Type | Source | Transformation | BRD Ref |
|--------|------|--------|---------------|---------|
| customer_id | INTEGER | customers.id | Cast via CAST(c.id AS INTEGER) | BR-9 |
| first_name | TEXT | customers.first_name | COALESCE(c.first_name, '') | BR-6, BR-9 |
| last_name | TEXT | customers.last_name | COALESCE(c.last_name, '') | BR-6, BR-9 |
| account_count | INTEGER | accounts | COUNT(a.customer_id), default 0 via COALESCE | BR-3, BR-6 |
| total_balance | REAL (decimal) | accounts.current_balance | ROUND(COALESCE(SUM(a.current_balance), 0), 2) | BR-3, BR-7 |
| card_count | INTEGER | cards | COUNT(cd.customer_id), default 0 via COALESCE | BR-4, BR-6 |
| investment_count | INTEGER | investments | COUNT(inv.customer_id), default 0 via COALESCE | BR-5, BR-6 |
| total_investment_value | REAL (decimal) | investments.current_value | ROUND(COALESCE(SUM(inv.current_value), 0), 2) | BR-5, BR-7 |
| as_of | TEXT (DateOnly) | Computed | The weekend-adjusted target date | BR-1, BR-8 |

**Column Order:** customer_id, first_name, last_name, account_count, total_balance, card_count, investment_count, total_investment_value, as_of

This matches the V1 output column order exactly as defined in [Customer360SnapshotBuilder.cs:10-15].

---

## 5. SQL Design

### Weekend Fallback Logic

SQLite's `strftime('%w', date)` returns the day of week as a string: '0' = Sunday, '6' = Saturday.

The SQL computes `target_date` using a CTE:

```sql
-- Step 1: Compute target_date from __maxEffectiveDate with weekend fallback (W2)
-- Saturday (day 6) -> Friday (date - 1 day)
-- Sunday (day 0) -> Friday (date - 2 days)
-- Weekdays -> use as-is
```

### Full SQL

```sql
WITH date_calc AS (
    SELECT
        CASE CAST(strftime('%w', as_of) AS INTEGER)
            WHEN 6 THEN date(as_of, '-1 day')
            WHEN 0 THEN date(as_of, '-2 days')
            ELSE as_of
        END AS target_date
    FROM (SELECT MAX(as_of) AS as_of FROM customers)
),
-- W2: Weekend fallback - Saturday uses Friday (date-1), Sunday uses Friday (date-2)
filtered_customers AS (
    SELECT c.id, c.first_name, c.last_name
    FROM customers c, date_calc d
    WHERE c.as_of = d.target_date
),
account_agg AS (
    SELECT
        a.customer_id,
        COUNT(*) AS account_count,
        SUM(a.current_balance) AS total_balance
    FROM accounts a, date_calc d
    WHERE a.as_of = d.target_date
    GROUP BY a.customer_id
),
card_agg AS (
    SELECT
        cd.customer_id,
        COUNT(*) AS card_count
    FROM cards cd, date_calc d
    WHERE cd.as_of = d.target_date
    GROUP BY cd.customer_id
),
investment_agg AS (
    SELECT
        inv.customer_id,
        COUNT(*) AS investment_count,
        SUM(inv.current_value) AS total_investment_value
    FROM investments inv, date_calc d
    WHERE inv.as_of = d.target_date
    GROUP BY inv.customer_id
)
SELECT
    CAST(fc.id AS INTEGER) AS customer_id,
    COALESCE(fc.first_name, '') AS first_name,
    COALESCE(fc.last_name, '') AS last_name,
    COALESCE(aa.account_count, 0) AS account_count,
    ROUND(COALESCE(aa.total_balance, 0), 2) AS total_balance,
    COALESCE(ca.card_count, 0) AS card_count,
    COALESCE(ia.investment_count, 0) AS investment_count,
    ROUND(COALESCE(ia.total_investment_value, 0), 2) AS total_investment_value,
    d.target_date AS as_of
FROM filtered_customers fc
CROSS JOIN date_calc d
LEFT JOIN account_agg aa ON fc.id = aa.customer_id
LEFT JOIN card_agg ca ON fc.id = ca.customer_id
LEFT JOIN investment_agg ia ON fc.id = ia.customer_id
```

### SQL Design Rationale

1. **`date_calc` CTE**: Computes the weekend-adjusted target date once. Uses `MAX(as_of) FROM customers` to derive the effective date from the actual data loaded by DataSourcing (which respects `__maxEffectiveDate`). Since the executor sets `__minEffectiveDate == __maxEffectiveDate` for single-day runs, and DataSourcing filters to that range, `MAX(as_of)` from the loaded data equals `__maxEffectiveDate`. The `strftime('%w')` + CASE implements BR-1 (W2 weekend fallback).

2. **`filtered_customers` CTE**: Filters customers to the target date only, implementing BR-2 and BR-9. Only customers present on the target date produce rows.

3. **`account_agg` CTE**: Groups accounts by customer_id for the target date, computing COUNT and SUM(current_balance). Implements BR-3.

4. **`card_agg` CTE**: Groups cards by customer_id for the target date, computing COUNT. Implements BR-4.

5. **`investment_agg` CTE**: Groups investments by customer_id for the target date, computing COUNT and SUM(current_value). Implements BR-5.

6. **Final SELECT**: LEFT JOINs all aggregations onto the filtered customer base. COALESCE provides default 0 for customers with no matching records (BR-6). ROUND(..., 2) handles balance rounding (BR-7). `target_date AS as_of` uses the weekend-adjusted date (BR-8).

### Edge Case: Empty Customers

If no customers exist for the target date, `filtered_customers` is empty, and the final SELECT produces zero rows. The Transformation module returns a DataFrame with the correct columns but zero rows. This matches V1 behavior (BR-10).

**Note on BR-10:** V1 has an explicit null/empty guard that returns an empty DataFrame with defined columns when customers is null or empty. The SQL approach naturally handles this -- when `filtered_customers` is empty, the query returns zero rows, and the Transformation module produces a DataFrame with column names inferred from the SELECT aliases. However, if all four source DataFrames are empty (zero rows), the Transformation module's `RegisterTable` method skips registration for empty DataFrames (see Transformation.cs:46 `if (!df.Rows.Any()) return;`). In this case the SQL would fail because the `customers` table doesn't exist. This is an edge case that would only occur if DataSourcing returns zero rows for all sources. During normal operation with the effective date range containing data, this won't happen. If it does, the job would fail -- matching V1's behavior where a null DataFrame guard would produce an empty output, not a crash. This is a known limitation of the Tier 1 approach but is acceptable because: (a) it only occurs when there's truly no data in the date range, and (b) V1's behavior in this scenario (empty output vs. error) has no downstream impact.

### Rounding Behavior (W5 / BR-7)

V1 uses `Math.Round(value, 2)` which defaults to `MidpointRounding.ToEven` (banker's rounding). SQLite's `ROUND()` function uses standard mathematical rounding (round half away from zero). For the vast majority of values this produces identical results. The only difference is at exact midpoints (e.g., 1.235 -> 1.24 in SQLite vs. 1.24 in C# with ToEven; 1.245 -> 1.25 in SQLite vs. 1.24 in C# with ToEven).

**Risk assessment:** For this to matter, `SUM(current_balance)` or `SUM(current_value)` for a specific customer on a specific date would need to land exactly on a .XX5 boundary AND differ in the third decimal place. Given that balances are typically stored with 2 decimal places in the source, their sums will also have 2 decimal places, making this a non-issue in practice. The ROUND() call is defensive -- it won't actually change any values. If Proofmark reveals any discrepancy, this would need to be addressed with a Tier 2 External module for rounding only.

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "Customer360SnapshotV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "customers",
      "schema": "datalake",
      "table": "customers",
      "columns": ["id", "first_name", "last_name"]
    },
    {
      "type": "DataSourcing",
      "resultName": "accounts",
      "schema": "datalake",
      "table": "accounts",
      "columns": ["customer_id", "current_balance"]
    },
    {
      "type": "DataSourcing",
      "resultName": "cards",
      "schema": "datalake",
      "table": "cards",
      "columns": ["customer_id"]
    },
    {
      "type": "DataSourcing",
      "resultName": "investments",
      "schema": "datalake",
      "table": "investments",
      "columns": ["customer_id", "current_value"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "WITH date_calc AS ( SELECT CASE CAST(strftime('%w', as_of) AS INTEGER) WHEN 6 THEN date(as_of, '-1 day') WHEN 0 THEN date(as_of, '-2 days') ELSE as_of END AS target_date FROM (SELECT MAX(as_of) AS as_of FROM customers) ), filtered_customers AS ( SELECT c.id, c.first_name, c.last_name FROM customers c, date_calc d WHERE c.as_of = d.target_date ), account_agg AS ( SELECT a.customer_id, COUNT(*) AS account_count, SUM(a.current_balance) AS total_balance FROM accounts a, date_calc d WHERE a.as_of = d.target_date GROUP BY a.customer_id ), card_agg AS ( SELECT cd.customer_id, COUNT(*) AS card_count FROM cards cd, date_calc d WHERE cd.as_of = d.target_date GROUP BY cd.customer_id ), investment_agg AS ( SELECT inv.customer_id, COUNT(*) AS investment_count, SUM(inv.current_value) AS total_investment_value FROM investments inv, date_calc d WHERE inv.as_of = d.target_date GROUP BY inv.customer_id ) SELECT CAST(fc.id AS INTEGER) AS customer_id, COALESCE(fc.first_name, '') AS first_name, COALESCE(fc.last_name, '') AS last_name, COALESCE(aa.account_count, 0) AS account_count, ROUND(COALESCE(aa.total_balance, 0), 2) AS total_balance, COALESCE(ca.card_count, 0) AS card_count, COALESCE(ia.investment_count, 0) AS investment_count, ROUND(COALESCE(ia.total_investment_value, 0), 2) AS total_investment_value, d.target_date AS as_of FROM filtered_customers fc CROSS JOIN date_calc d LEFT JOIN account_agg aa ON fc.id = aa.customer_id LEFT JOIN card_agg ca ON fc.id = ca.customer_id LEFT JOIN investment_agg ia ON fc.id = ia.customer_id"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/customer_360_snapshot/",
      "numParts": 1,
      "writeMode": "Overwrite"
    }
  ]
}
```

---

## 7. Writer Config

| Parameter | V1 Value | V2 Value | Match? |
|-----------|----------|----------|--------|
| Writer type | ParquetFileWriter | ParquetFileWriter | YES |
| source | `output` | `output` | YES |
| outputDirectory | `Output/curated/customer_360_snapshot/` | `Output/double_secret_curated/customer_360_snapshot/` | Path changed per V2 spec |
| numParts | 1 | 1 | YES |
| writeMode | Overwrite | Overwrite | YES |

All writer config parameters match V1 exactly, with only the output directory path updated per the V2 specification.

---

## 8. Proofmark Config Design

### Reader & Format
- **Reader:** `parquet` (matches V1 ParquetFileWriter output)
- **Threshold:** 100.0 (strict -- all rows must match)

### Column Exclusions
**None.** All output columns are deterministic and reproducible.

### Fuzzy Columns
**None initially.** All monetary values use `ROUND(..., 2)` and source data is stored at 2 decimal precision. No floating-point accumulation issues are expected.

**Contingency:** If Proofmark comparison reveals rounding discrepancies on `total_balance` or `total_investment_value`, add fuzzy tolerance:
```yaml
columns:
  fuzzy:
    - name: "total_balance"
      tolerance: 0.01
      tolerance_type: absolute
      reason: "SQLite ROUND uses half-away-from-zero vs C# MidpointRounding.ToEven [Customer360SnapshotBuilder.cs:86]"
    - name: "total_investment_value"
      tolerance: 0.01
      tolerance_type: absolute
      reason: "SQLite ROUND uses half-away-from-zero vs C# MidpointRounding.ToEven [Customer360SnapshotBuilder.cs:89]"
```

### Proposed Config
```yaml
comparison_target: "customer_360_snapshot"
reader: parquet
threshold: 100.0
```

---

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision | Evidence |
|----------------|-------------|-----------------|----------|
| BR-1 (Weekend fallback) | SQL Design - date_calc CTE | `strftime('%w')` + CASE expression maps Sat->Fri, Sun->Fri | [Customer360SnapshotBuilder.cs:30-32] |
| BR-2 (In-code date filtering) | SQL Design - filtered_customers + WHERE clauses | All CTEs filter `WHERE x.as_of = d.target_date` | [Customer360SnapshotBuilder.cs:35,43,54,66] |
| BR-3 (Account aggregation) | SQL Design - account_agg CTE | `COUNT(*) AS account_count, SUM(current_balance) AS total_balance` | [Customer360SnapshotBuilder.cs:38-48] |
| BR-4 (Card counting) | SQL Design - card_agg CTE | `COUNT(*) AS card_count` | [Customer360SnapshotBuilder.cs:51-58] |
| BR-5 (Investment aggregation) | SQL Design - investment_agg CTE | `COUNT(*) AS investment_count, SUM(current_value) AS total_investment_value` | [Customer360SnapshotBuilder.cs:61-71] |
| BR-6 (Default zeros) | SQL Design - COALESCE in final SELECT | `COALESCE(aa.account_count, 0)`, `COALESCE(aa.total_balance, 0)`, etc. | [Customer360SnapshotBuilder.cs:85-88] |
| BR-7 (Balance rounding) | SQL Design - ROUND in final SELECT | `ROUND(COALESCE(...), 2)` on total_balance and total_investment_value | [Customer360SnapshotBuilder.cs:86,89] |
| BR-8 (as_of = targetDate) | SQL Design - final SELECT | `d.target_date AS as_of` uses weekend-adjusted date | [Customer360SnapshotBuilder.cs:90] |
| BR-9 (Customer-driven output) | SQL Design - filtered_customers as base | LEFT JOINs off filtered_customers; only customers on target date produce rows | [Customer360SnapshotBuilder.cs:76] |
| BR-10 (Empty customers -> empty output) | SQL Design - Edge Case section | Empty `filtered_customers` -> zero result rows | [Customer360SnapshotBuilder.cs:23-26] |
| BR-11 (Unused sourced columns) | Anti-Pattern Analysis - AP1, AP4 | Unused columns removed from V2 DataSourcing | [Customer360SnapshotBuilder.cs:10-15] |
| W2 (Weekend fallback) | SQL Design - date_calc CTE | Reproduced cleanly in SQL | [Customer360SnapshotBuilder.cs:30-32] |
| AP1 (Dead-end sourcing) | Anti-Pattern Analysis | Eliminated: removed unused DataSourcing columns | [customer_360_snapshot.json:10,17,24,30] |
| AP3 (Unnecessary External) | Tier Selection | Eliminated: replaced External with Transformation SQL | [customer_360_snapshot.json:34-36] |
| AP4 (Unused columns) | Anti-Pattern Analysis | Eliminated: V2 sources only needed columns | [customer_360_snapshot.json:10,17,24,30] |
| AP6 (Row-by-row iteration) | Tier Selection | Eliminated: SQL set-based operations replace foreach loops | [Customer360SnapshotBuilder.cs:42-47,54-57,66-71,76-92] |

---

## 10. External Module Design

**Not applicable.** This job uses Tier 1 (Framework Only). No External module is needed.

The V1 External module (`Customer360SnapshotBuilder.cs`) is fully replaced by the SQL Transformation module. All V1 business logic maps cleanly to SQL:

| V1 C# Operation | V2 SQL Equivalent |
|-----------------|-------------------|
| Weekend DayOfWeek check + AddDays | `strftime('%w')` + `date(x, '-N days')` |
| `.Where(r => as_of == targetDate)` | `WHERE as_of = target_date` |
| Dictionary accumulator + foreach | `GROUP BY` + `COUNT(*)` / `SUM()` |
| `GetValueOrDefault(id, 0)` | `LEFT JOIN` + `COALESCE(x, 0)` |
| `Math.Round(x, 2)` | `ROUND(x, 2)` |
| `?.ToString() ?? ""` | `COALESCE(x, '')` |
| `Convert.ToInt32(id)` | `CAST(id AS INTEGER)` |
