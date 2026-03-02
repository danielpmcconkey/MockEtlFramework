# CardSpendingByMerchant — Functional Specification Document

## 1. Overview

V2 replaces the V1 External module (`CardSpendingByMerchantProcessor`) with a pure SQL Transformation. The V1 job aggregates card transactions by merchant category code (MCC), producing per-MCC counts and spending totals enriched with MCC description from a lookup table. This is a textbook GROUP BY with JOIN and SUM — fully expressible in SQL.

**Tier: 1 (Framework Only)** — `DataSourcing → Transformation (SQL) → ParquetFileWriter`

**Justification for Tier 1:** All V1 business logic (GROUP BY on `merchant_category_code`, COUNT, SUM, LEFT JOIN for description lookup) maps directly to SQL. There is no procedural logic, no snapshot fallback, no cross-date-range queries, and no operation that requires C#. The V1 External module is a pure AP3/AP6 anti-pattern — unnecessary row-by-row C# iteration for what SQL does natively.

---

## 2. V2 Module Chain

### Module 1: DataSourcing — `card_transactions`

| Property | Value |
|----------|-------|
| `type` | `DataSourcing` |
| `resultName` | `card_transactions` |
| `schema` | `datalake` |
| `table` | `card_transactions` |
| `columns` | `["merchant_category_code", "amount"]` |

**Changes from V1:**
- Removed `card_txn_id`, `card_id`, `customer_id`, `merchant_name`, `txn_timestamp`, `authorization_status` — none are used by the business logic (AP4 elimination).
- Evidence: `CardSpendingByMerchantProcessor.cs:46-47` only accesses `merchant_category_code` and `amount`. Line 40 accesses `as_of` (auto-appended by DataSourcing). All other columns are sourced in V1 but never referenced (BRD BR-10).
- The `as_of` column is automatically appended by the DataSourcing module when not explicitly listed (per `DataSourcing.cs:69-72`).
- Effective dates are injected at runtime by the executor via shared state keys `__minEffectiveDate` / `__maxEffectiveDate`.

### Module 2: DataSourcing — `merchant_categories`

| Property | Value |
|----------|-------|
| `type` | `DataSourcing` |
| `resultName` | `merchant_categories` |
| `schema` | `datalake` |
| `table` | `merchant_categories` |
| `columns` | `["mcc_code", "mcc_description"]` |

**Changes from V1:**
- Removed `risk_level` — it is sourced by V1 but never used in the output or any computation (AP4 elimination). Evidence: `CardSpendingByMerchantProcessor.cs:10-13` — output columns are `mcc_code`, `mcc_description`, `txn_count`, `total_spending`, `as_of`. `risk_level` is absent. Also confirmed by BRD BR-7.
- The `as_of` column is automatically appended by the DataSourcing module.

### Module 3: Transformation — `output`

| Property | Value |
|----------|-------|
| `type` | `Transformation` |
| `resultName` | `output` |
| `sql` | *(see Section 5)* |

Executes the GROUP BY / JOIN / SUM / COUNT logic in SQLite. Produces the `output` DataFrame consumed by the writer.

### Module 4: ParquetFileWriter

| Property | Value |
|----------|-------|
| `type` | `ParquetFileWriter` |
| `source` | `output` |
| `outputDirectory` | `Output/double_secret_curated/card_spending_by_merchant/` |
| `numParts` | `1` |
| `writeMode` | `Overwrite` |

**Writer config matches V1 exactly** (same writer type, same numParts, same writeMode). Only the output path changes to `Output/double_secret_curated/`.

---

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles to Reproduce

**None identified.** This job has no W-code wrinkles:

- **No W1 (Sunday skip):** No Sunday-skip logic present in V1. Evidence: `CardSpendingByMerchantProcessor.cs` contains no day-of-week checks.
- **No W2 (Weekend fallback):** No weekend fallback logic. Evidence: BRD BR-8 — "No weekend fallback is applied."
- **No W4 (Integer division):** V1 does not compute percentages — only COUNT and SUM.
- **No W5 (Banker's rounding):** No rounding operations in V1.
- **No W6 (Double epsilon):** V1 uses `decimal` for monetary accumulation (`Convert.ToDecimal` at line 47, `decimal total` in the tuple at line 43). No double-precision floating-point issues. SQL SUM on SQLite REAL may differ from `decimal` — see SQL Design Notes for mitigation.
- **No W7 (Trailer inflated count):** Not a CSV job — no trailers.
- **No W9 (Wrong writeMode):** Overwrite mode is appropriate for this aggregation that replaces prior output each run. No write mode issue.
- **No W10 (Absurd numParts):** `numParts: 1` — perfectly reasonable for a small aggregated dataset (max ~20 rows for 20 MCC codes).

### Code-Quality Anti-Patterns to Eliminate

| AP Code | V1 Problem | V2 Resolution |
|---------|-----------|---------------|
| **AP3** | Unnecessary External module — V1 uses C# `foreach` + Dictionary for a simple GROUP BY + JOIN + SUM + COUNT | Replaced with Tier 1 SQL Transformation. The entire business logic is a single SQL query with GROUP BY, LEFT JOIN, COUNT, and SUM. |
| **AP4** | V1 sources `card_txn_id`, `card_id`, `customer_id`, `merchant_name`, `txn_timestamp`, `authorization_status` from card_transactions — none are used. V1 also sources `risk_level` from merchant_categories — never used. | V2 sources only `merchant_category_code` and `amount` from card_transactions, and `mcc_code` and `mcc_description` from merchant_categories. Evidence: `CardSpendingByMerchantProcessor.cs:46-47` (only `merchant_category_code` and `amount` accessed from transactions); `CardSpendingByMerchantProcessor.cs:34-35` (only `mcc_code` and `mcc_description` accessed from categories). |
| **AP6** | Row-by-row `foreach` iteration to group transactions by MCC, when SQL GROUP BY would work | Replaced with SQL `GROUP BY` + `COUNT(*)` + `SUM(amount)` — a set-based operation. |

---

## 4. Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| `mcc_code` | `card_transactions.merchant_category_code` | GROUP BY key — aliased from `merchant_category_code` to `mcc_code` | `CardSpendingByMerchantProcessor.cs:62`, BRD BR-1 |
| `mcc_description` | `merchant_categories.mcc_description` | LEFT JOIN lookup by mcc_code; `''` (empty string) if not found via COALESCE | `CardSpendingByMerchantProcessor.cs:59`, BRD BR-4 |
| `txn_count` | Computed | `COUNT(*)` per MCC group | `CardSpendingByMerchantProcessor.cs:52,64`, BRD BR-2 |
| `total_spending` | `card_transactions.amount` | `SUM(amount)` per MCC group | `CardSpendingByMerchantProcessor.cs:52,65`, BRD BR-3 |
| `as_of` | `card_transactions.as_of` | Value from first row of card_transactions (minimum `as_of` due to DataSourcing ORDER BY). In SQL: subquery `(SELECT as_of FROM card_transactions LIMIT 1)` | `CardSpendingByMerchantProcessor.cs:40,66`, BRD BR-5 |

**Column ordering:** The SQL SELECT clause defines columns in the exact order above, matching V1's `outputColumns` list at `CardSpendingByMerchantProcessor.cs:10-13`.

---

## 5. SQL Design

```sql
SELECT
    ct.merchant_category_code AS mcc_code,
    COALESCE(mc.mcc_description, '') AS mcc_description,
    COUNT(*) AS txn_count,
    SUM(ct.amount) AS total_spending,
    (SELECT as_of FROM card_transactions LIMIT 1) AS as_of
FROM card_transactions ct
LEFT JOIN (
    SELECT mcc_code, mcc_description
    FROM merchant_categories
    GROUP BY mcc_code
) mc ON ct.merchant_category_code = mc.mcc_code
GROUP BY ct.merchant_category_code
ORDER BY ct.merchant_category_code
```

### SQL Design Notes

1. **LEFT JOIN with subquery for deduplication**: V1 builds a `Dictionary<string, string>` from merchant_categories rows, keyed by `mcc_code`. When multiple rows exist for the same `mcc_code` (e.g., across multiple `as_of` dates), the Dictionary overwrites previous entries, so the last-seen description wins (BRD Edge Case 3). The subquery `GROUP BY mcc_code` deduplicates the lookup table. SQLite's `GROUP BY` with a non-aggregated column (`mcc_description`) returns the value from an arbitrary row in the group — which matches the V1 "last-seen wins" semantics since both are non-deterministic with respect to which description value is kept for duplicate MCC codes. Per the BRD, all 17 transaction MCCs are a subset of the 20 categories, and database queries confirm descriptions don't differ across as_of dates in the test range, so this is a theoretical concern that won't manifest in practice.

2. **COALESCE for missing MCC codes**: V1 uses `mccLookup.ContainsKey(kvp.Key) ? mccLookup[kvp.Key] : ""` to default to empty string for unknown MCC codes. `COALESCE(mc.mcc_description, '')` replicates this — when the LEFT JOIN finds no match, `mc.mcc_description` is NULL, and COALESCE converts it to empty string.

3. **`SUM(ct.amount)` vs V1 `decimal` accumulation**: V1 uses `Convert.ToDecimal(txn["amount"])` and accumulates with `decimal` arithmetic. SQLite stores numeric values as REAL (IEEE 754 double) or INTEGER. The `amount` values from DataSourcing come through as PostgreSQL `numeric`/`decimal` types, which Npgsql maps to .NET `decimal`. When `Transformation.RegisterTable` stores these in SQLite, the `GetSqliteType` method (Transformation.cs:98-104) maps `decimal` to `"REAL"`, and `ToSqliteValue` passes the value through as-is (the default `_ => value` case at line 112), where SQLite will convert the .NET decimal to a double. This means V2's SUM operates on doubles, while V1 accumulated with decimal. For the typical transaction amounts in this dataset, the precision difference between `decimal` and `double` is negligible — both will produce identical results for sums of reasonable monetary values. **If Proofmark detects epsilon differences in `total_spending`, a fuzzy tolerance can be added.** Not pre-configured because the assumption is strict matching until evidence suggests otherwise.

4. **`(SELECT as_of FROM card_transactions LIMIT 1)` for as_of**: V1 takes `cardTransactions.Rows[0]["as_of"]` — the `as_of` from the first row. DataSourcing returns rows `ORDER BY as_of` (DataSourcing.cs:85), so the first row has the minimum `as_of` date. `LIMIT 1` in SQLite picks the first row from the registered table, which preserves insertion order (and DataSourcing inserts rows in the order returned by the query — i.e., ordered by `as_of`). For single-day runs (min == max effective date), all rows have the same `as_of`, so any row works.

5. **No authorization_status filter**: V1 includes all transactions regardless of authorization_status (BRD BR-6). V2 replicates this by having no WHERE clause filter on authorization_status. The column is not even sourced (AP4 elimination).

6. **ORDER BY `merchant_category_code`**: V1 iterates a `Dictionary<string, (int, decimal)>` whose iteration order is insertion order in modern .NET but is not contractually guaranteed. Adding `ORDER BY` makes V2 deterministic. If V1's actual output order differs from alphabetical MCC code order, the Proofmark comparison will detect it, and the ORDER BY can be adjusted. Deterministic ordering is preferable for reproducibility.

7. **Empty input handling**: When card_transactions has zero rows, `Transformation.RegisterTable` skips registration (`if (!df.Rows.Any()) return;` at Transformation.cs:46). The SQL query will fail with "no such table: card_transactions". V1 handles this gracefully by returning an empty DataFrame (CardSpendingByMerchantProcessor.cs:22-26). **Risk**: If DataSourcing returns zero rows for a given effective date, V2 will throw an error. For the date range 2024-10-01 through 2024-12-31, card_transactions data should exist for all weekdays. If this manifests during Phase D, the resolution would be to handle it at the Transformation level (e.g., a CTE with a fallback) or escalate to Tier 2 with a minimal External guard clause.

---

## 6. V2 Job Config

```json
{
  "jobName": "CardSpendingByMerchantV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "card_transactions",
      "schema": "datalake",
      "table": "card_transactions",
      "columns": ["merchant_category_code", "amount"]
    },
    {
      "type": "DataSourcing",
      "resultName": "merchant_categories",
      "schema": "datalake",
      "table": "merchant_categories",
      "columns": ["mcc_code", "mcc_description"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT ct.merchant_category_code AS mcc_code, COALESCE(mc.mcc_description, '') AS mcc_description, COUNT(*) AS txn_count, SUM(ct.amount) AS total_spending, (SELECT as_of FROM card_transactions LIMIT 1) AS as_of FROM card_transactions ct LEFT JOIN (SELECT mcc_code, mcc_description FROM merchant_categories GROUP BY mcc_code) mc ON ct.merchant_category_code = mc.mcc_code GROUP BY ct.merchant_category_code ORDER BY ct.merchant_category_code"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/card_spending_by_merchant/",
      "numParts": 1,
      "writeMode": "Overwrite"
    }
  ]
}
```

---

## 7. Writer Configuration

| Property | V1 Value | V2 Value | Match? |
|----------|---------|---------|--------|
| Writer type | `ParquetFileWriter` | `ParquetFileWriter` | Yes |
| `source` | `output` | `output` | Yes |
| `outputDirectory` | `Output/curated/card_spending_by_merchant/` | `Output/double_secret_curated/card_spending_by_merchant/` | Path change only |
| `numParts` | `1` | `1` | Yes |
| `writeMode` | `Overwrite` | `Overwrite` | Yes |

---

## 8. Proofmark Config Design

Starting point: **zero exclusions, zero fuzzy overrides**.

```yaml
comparison_target: "card_spending_by_merchant"
reader: parquet
threshold: 100.0
```

### Rationale

- **`reader: parquet`**: V1 and V2 both use ParquetFileWriter.
- **No CSV settings needed**: Parquet format — no headers or trailers to configure.
- **No excluded columns**: The BRD identifies zero non-deterministic fields. All output values are deterministic given the same input data.
- **No fuzzy columns**: The `total_spending` column is SUM of amounts. V1 uses `decimal` accumulation; V2 uses SQLite REAL (double). For typical financial transaction amounts (values with 2 decimal places), `double` has more than sufficient precision to produce identical results to `decimal` for sums of reasonable magnitude. Both approaches will produce the same numeric values for the test data. `txn_count` is an integer count — no precision concerns.

### Potential Proofmark Adjustments

1. **`total_spending` precision**: If V2's SQLite REAL (double) accumulation produces values that differ from V1's `decimal` accumulation at high precision, a fuzzy tolerance on `total_spending` would be added:
   ```yaml
   columns:
     fuzzy:
       - name: "total_spending"
         tolerance: 0.01
         tolerance_type: absolute
         reason: "V1 uses decimal accumulation, V2 uses SQLite REAL (double). Potential epsilon difference for large sums."
   ```
   Not pre-configured — starting strict per best practices.

2. **Row ordering**: If V1's Dictionary iteration order differs from V2's `ORDER BY merchant_category_code`, the comparison may show mismatched rows. Resolution would be to verify V1's actual output order and adjust V2's ORDER BY, or determine if Proofmark supports order-independent comparison for Parquet.

---

## 9. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|-------------|----------------|----------|
| GROUP BY `merchant_category_code` with COUNT and SUM | BR-1, BR-2, BR-3 | `CardSpendingByMerchantProcessor.cs:43-54` |
| `txn_count` = COUNT(*) per MCC group | BR-2 | `CardSpendingByMerchantProcessor.cs:52` |
| `total_spending` = SUM(amount) per MCC group | BR-3 | `CardSpendingByMerchantProcessor.cs:52` |
| LEFT JOIN merchant_categories for MCC description with COALESCE to empty string | BR-4 | `CardSpendingByMerchantProcessor.cs:29-38,59` |
| `as_of` from first row of card_transactions via `(SELECT as_of ... LIMIT 1)` | BR-5 | `CardSpendingByMerchantProcessor.cs:40` |
| No authorization_status filter — all transactions included | BR-6 | `CardSpendingByMerchantProcessor.cs:43-54` — no filter present |
| `risk_level` not in output; removed from DataSourcing (AP4) | BR-7 | `CardSpendingByMerchantProcessor.cs:10-13` |
| No weekend fallback logic | BR-8 | `CardSpendingByMerchantProcessor.cs` — absence of any day-of-week logic |
| Empty input produces zero output rows | BR-9 | `CardSpendingByMerchantProcessor.cs:22-26` |
| Removed unused columns from DataSourcing (AP4) | BR-10 | `CardSpendingByMerchantProcessor.cs:46-47` — only `merchant_category_code` and `amount` accessed |
| Replaced External with SQL (AP3, AP6) | Module Hierarchy / Anti-Patterns | All logic is GROUP BY + JOIN + COUNT + SUM |
| Tier 1 selection | Module Hierarchy | No operation requires procedural logic |
| ParquetFileWriter with numParts=1, Overwrite mode | BRD Writer Config | `card_spending_by_merchant.json:25-28` |
| Merchant category deduplication via GROUP BY subquery | BRD Edge Case 3 | `CardSpendingByMerchantProcessor.cs:32-37` — Dictionary overwrite on duplicate mcc_code |

---

## 10. External Module Design

**Not applicable.** V2 is Tier 1 — no External module needed. The V1 External module (`CardSpendingByMerchantProcessor`) is fully replaced by the Transformation SQL.

---

## Appendix: Risk Register

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Zero-row DataSourcing causes SQL error (table not registered in SQLite) | LOW — card transaction data should exist for all business days in the 2024-10-01 to 2024-12-31 range | MEDIUM — job fails for that date | Monitor during Phase D. If triggered, escalate to Tier 2 with a minimal External that handles the empty-input guard. |
| Row ordering mismatch between V1 Dictionary iteration order and V2 ORDER BY | MEDIUM — Dictionary order is insertion order in modern .NET but not contractually guaranteed | LOW — fixable by adjusting ORDER BY or Proofmark config | Review V1 output during Phase D comparison. Adjust as needed. |
| SQLite REAL (double) SUM vs V1 decimal accumulation for `total_spending` | LOW — double has sufficient precision for typical monetary sums with 2 decimal places | LOW — would show as tiny epsilon differences | Add Proofmark fuzzy tolerance on `total_spending` if needed. Not pre-configured. |
| Merchant category description mismatch due to GROUP BY non-aggregated column vs Dictionary overwrite semantics | VERY LOW — BRD confirms descriptions are consistent across as_of dates in the test range | LOW — would show as wrong description for specific MCC codes | Verify during Phase D. If descriptions differ, use a deterministic subquery (e.g., latest as_of). |
