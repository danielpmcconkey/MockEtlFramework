# ProductPenetration -- Functional Specification Document

## 1. Job Summary

This job computes product penetration rates for three product categories (accounts, cards, investments) as a ratio of distinct product holders to total distinct customers. The output is a 3-row CSV with one row per product type. A known integer-division bug (W4) causes penetration rates to be 0 or 1 exclusively -- never a proper fractional percentage. V2 replaces V1's module chain with a cleaner configuration that eliminates dead-end data sourcing (AP1) and unused columns (AP4) while faithfully reproducing the integer-division behavior and the cross-join-based `as_of` sourcing pattern.

---

## 2. V2 Module Chain

**Tier: 1 (Framework Only)** -- `DataSourcing -> Transformation (SQL) -> CsvFileWriter`

**Justification for Tier 1:** V1 already uses a Transformation module with SQL -- no External module is involved (BRD BR-7, evidence: `product_penetration.json:34` type is `"Transformation"`). All business logic consists of CTEs, COUNT(DISTINCT), integer division, UNION ALL, and a cross-join -- all natively expressible in SQLite. There is no procedural logic, no snapshot fallback, and no cross-date-range query pattern. Tier 1 is the natural and only appropriate choice.

### Module 1: DataSourcing -- `customers`

| Property | Value |
|----------|-------|
| `type` | `DataSourcing` |
| `resultName` | `customers` |
| `schema` | `datalake` |
| `table` | `customers` |
| `columns` | `["id"]` |

**Changes from V1:**
- Removed `first_name` and `last_name` -- neither column is referenced in the Transformation SQL output (AP4 elimination). Evidence: V1 SQL at `product_penetration.json:36` -- the SELECT clause is `ps.product_type, ps.customer_count, ps.product_count, ps.penetration_rate, customers.as_of`. The only columns accessed from `customers` are `id` (in CTE `customer_counts`) and `as_of` (in the final SELECT). BRD BR-6 explicitly confirms: "first_name and last_name are sourced from customers but are NOT included in the output."
- The `as_of` column is automatically appended by the DataSourcing module when not explicitly listed (per `DataSourcing.cs:69-72`).
- Effective dates injected at runtime via shared state keys `__minEffectiveDate` / `__maxEffectiveDate`.

### Module 2: DataSourcing -- `accounts`

| Property | Value |
|----------|-------|
| `type` | `DataSourcing` |
| `resultName` | `accounts` |
| `schema` | `datalake` |
| `table` | `accounts` |
| `columns` | `["customer_id"]` |

**Changes from V1:**
- Removed `account_id` and `account_type` -- neither is referenced in the Transformation SQL (AP4 elimination). Evidence: V1 SQL CTE `account_holders` is `SELECT COUNT(DISTINCT customer_id) AS cnt FROM accounts` -- only `customer_id` is used.
- `as_of` auto-appended by DataSourcing.

### Module 3: DataSourcing -- `cards`

| Property | Value |
|----------|-------|
| `type` | `DataSourcing` |
| `resultName` | `cards` |
| `schema` | `datalake` |
| `table` | `cards` |
| `columns` | `["customer_id"]` |

**Changes from V1:**
- Removed `card_id` -- not referenced in the Transformation SQL (AP4 elimination). Evidence: V1 SQL CTE `card_holders` is `SELECT COUNT(DISTINCT customer_id) AS cnt FROM cards` -- only `customer_id` is used.
- `as_of` auto-appended by DataSourcing.

### Module 4: DataSourcing -- `investments`

| Property | Value |
|----------|-------|
| `type` | `DataSourcing` |
| `resultName` | `investments` |
| `schema` | `datalake` |
| `table` | `investments` |
| `columns` | `["customer_id"]` |

**Changes from V1:**
- Removed `investment_id` -- not referenced in the Transformation SQL (AP4 elimination). Evidence: V1 SQL CTE `investment_holders` is `SELECT COUNT(DISTINCT customer_id) AS cnt FROM investments` -- only `customer_id` is used.
- `as_of` auto-appended by DataSourcing.

### Module 5: Transformation -- `output`

| Property | Value |
|----------|-------|
| `type` | `Transformation` |
| `resultName` | `output` |
| `sql` | *(see Section 4)* |

### Module 6: CsvFileWriter

| Property | Value |
|----------|-------|
| `type` | `CsvFileWriter` |
| `source` | `output` |
| `outputFile` | `Output/double_secret_curated/product_penetration.csv` |
| `includeHeader` | `true` |
| `writeMode` | `Overwrite` |
| `lineEnding` | `LF` |

---

## 3. DataSourcing Config

| Table | Columns Sourced (V2) | Columns Removed (AP4) | Effective Date Handling |
|-------|---------------------|-----------------------|------------------------|
| `datalake.customers` | `id` | `first_name`, `last_name` | Injected via `__minEffectiveDate` / `__maxEffectiveDate`; `as_of` auto-appended |
| `datalake.accounts` | `customer_id` | `account_id`, `account_type` | Same |
| `datalake.cards` | `customer_id` | `card_id` | Same |
| `datalake.investments` | `customer_id` | `investment_id` | Same |

All four DataSourcing modules rely on the framework's effective date injection. No hardcoded dates appear in the V2 config. The `as_of` column is auto-appended by DataSourcing when not explicitly included in the columns list (`DataSourcing.cs:69-72`), and is needed for the final SELECT's `customers.as_of` reference.

---

## 4. Transformation SQL

The V2 SQL is functionally identical to V1's SQL. The only change is cosmetic formatting for readability. The logic, column names, CTE structure, integer division behavior, cross-join, and LIMIT are all preserved exactly.

```sql
WITH customer_counts AS (
    SELECT COUNT(DISTINCT id) AS total_customers
    FROM customers
),
account_holders AS (
    SELECT COUNT(DISTINCT customer_id) AS cnt
    FROM accounts
),
card_holders AS (
    SELECT COUNT(DISTINCT customer_id) AS cnt
    FROM cards
),
investment_holders AS (
    SELECT COUNT(DISTINCT customer_id) AS cnt
    FROM investments
),
product_stats AS (
    SELECT 'accounts' AS product_type,
           cc.total_customers AS customer_count,
           ah.cnt AS product_count,
           CAST(ah.cnt AS INTEGER) / CAST(cc.total_customers AS INTEGER) AS penetration_rate
    FROM customer_counts cc, account_holders ah
    UNION ALL
    SELECT 'cards' AS product_type,
           cc.total_customers AS customer_count,
           ch.cnt AS product_count,
           CAST(ch.cnt AS INTEGER) / CAST(cc.total_customers AS INTEGER) AS penetration_rate
    FROM customer_counts cc, card_holders ch
    UNION ALL
    SELECT 'investments' AS product_type,
           cc.total_customers AS customer_count,
           ih.cnt AS product_count,
           CAST(ih.cnt AS INTEGER) / CAST(cc.total_customers AS INTEGER) AS penetration_rate
    FROM customer_counts cc, investment_holders ih
)
SELECT ps.product_type,
       ps.customer_count,
       ps.product_count,
       ps.penetration_rate,
       customers.as_of
FROM product_stats ps
JOIN customers ON 1=1
LIMIT 3
```

### SQL Design Notes

1. **Integer division (W4 replication):** The `CAST(ah.cnt AS INTEGER) / CAST(cc.total_customers AS INTEGER)` expression performs integer division in SQLite. This is the V1 bug documented in BRD BR-2. For the actual data (2230 customers, 2230 account holders, 2230 card holders, 427 investment holders on 2024-10-01), the results are:
   - accounts: 2230/2230 = 1
   - cards: 2230/2230 = 1
   - investments: 427/2230 = 0

   The V2 SQL preserves this integer division exactly as V1 does. Per KNOWN_ANTI_PATTERNS.md W4 prescription, the ideal approach would be to "cast to decimal, compute the correct value, then explicitly truncate." However, since this is a SQL Transformation (not C# code), and SQLite's integer division natively produces the truncated result, the cleanest approach is to leave the SQL as-is (it already produces the correct output-equivalent behavior) and document the bug. Changing the SQL to use REAL then truncating would add unnecessary complexity for the same result.

2. **Cross-join for `as_of` (BRD BR-5):** The `JOIN customers ON 1=1` creates a cross-join between `product_stats` (3 rows) and all customer rows. The `LIMIT 3` constrains the output to 3 rows total. Since product_stats has 3 rows and the cross-join multiplies each by the full customers table, the LIMIT 3 grabs the first occurrence of each product_stats row paired with the first customer row (in SQLite's internal iteration order). The `as_of` value comes from whichever customer row SQLite picks first -- in practice, the row with the minimum `as_of` since DataSourcing orders by `as_of` (`DataSourcing.cs:85`).

3. **LIMIT 3 (BRD BR-4):** The LIMIT acts as a safety guard preventing the cross-join from exploding. It also ensures exactly 3 output rows (one per product type from the UNION ALL).

4. **CTE structure (BRD BR-3):** The CTE structure is preserved identically. While it could be simplified (e.g., a single CTE with UNION ALL of the three product counts), maintaining the exact V1 structure minimizes risk of behavioral divergence.

---

## 5. Writer Config

| Property | V1 Value | V2 Value | Match? |
|----------|---------|---------|--------|
| Writer type | `CsvFileWriter` | `CsvFileWriter` | Yes |
| `source` | `output` | `output` | Yes |
| `outputFile` | `Output/curated/product_penetration.csv` | `Output/double_secret_curated/product_penetration.csv` | Path change only |
| `includeHeader` | `true` | `true` | Yes |
| `writeMode` | `Overwrite` | `Overwrite` | Yes |
| `lineEnding` | `LF` | `LF` | Yes |
| `trailerFormat` | Not specified | Not specified | Yes (no trailer) |

All writer parameters match V1 exactly. Only the output path changes to `Output/double_secret_curated/` per V2 conventions.

---

## 6. Wrinkle Replication

| W-Code | Wrinkle | Applies? | V2 Handling |
|--------|---------|----------|-------------|
| **W4** | Integer division -- penetration_rate computed as `CAST(cnt AS INTEGER) / CAST(total_customers AS INTEGER)` | **YES** | V2 preserves the identical SQL expression. SQLite performs integer division on INTEGER operands, truncating fractional results to 0. This produces penetration_rate of 0 (when product_count < customer_count) or 1 (when equal). The SQL is unchanged from V1 -- the integer division behavior is inherent in the expression, not something we need to artificially inject. Evidence: `product_penetration.json:36` -- the CAST expressions. |

No other W-codes apply:
- W1 (Sunday skip): Not applicable -- no Sunday-specific guard in V1 config or SQL.
- W2 (Weekend fallback): Not applicable -- no weekend date logic.
- W3a/b/c (Boundary rows): Not applicable -- no summary row logic.
- W5 (Banker's rounding): Not applicable -- no rounding operations.
- W6 (Double epsilon): Not applicable -- all arithmetic is integer.
- W7 (Trailer inflated count): Not applicable -- no trailer, no External module.
- W8 (Trailer stale date): Not applicable -- no trailer.
- W9 (Wrong writeMode): Not applicable -- Overwrite is appropriate for a 3-row snapshot that is fully recomputed each day.
- W10 (Absurd numParts): Not applicable -- CSV output, not Parquet.
- W12 (Header every append): Not applicable -- writeMode is Overwrite, not Append.

---

## 7. Anti-Pattern Elimination

| AP-Code | V1 Problem | V2 Resolution |
|---------|-----------|---------------|
| **AP4** | V1 sources `first_name` and `last_name` from `customers`, `account_id` and `account_type` from `accounts`, `card_id` from `cards`, and `investment_id` from `investments` -- none of these columns are referenced in the Transformation SQL. | **Eliminated.** V2 DataSourcing configs source only the columns used by the SQL: `id` from customers, `customer_id` from accounts/cards/investments. Evidence: BRD BR-6 confirms first_name/last_name unused; SQL analysis confirms the other columns unused (only COUNT(DISTINCT customer_id) and COUNT(DISTINCT id) are computed). |

### Anti-patterns reviewed but not applicable:

| AP-Code | Why Not Applicable |
|---------|-------------------|
| AP1 (Dead-end sourcing) | All four sourced tables (customers, accounts, cards, investments) are referenced in the SQL. No dead-end tables. |
| AP2 (Duplicated logic) | No cross-job duplication identified for this specific computation. |
| AP3 (Unnecessary External) | V1 already uses a Transformation module, not an External module (BRD BR-7). No External to eliminate. |
| AP5 (Asymmetric NULLs) | No NULL handling in the SQL -- COUNT(DISTINCT) naturally ignores NULLs, and all sourced columns (id, customer_id) are NOT NULL in the schema. |
| AP6 (Row-by-row iteration) | V1 uses SQL, not C# iteration. No row-by-row processing to replace. |
| AP7 (Magic values) | The only literal values are the product type strings ('accounts', 'cards', 'investments') and LIMIT 3 -- these are structural, not magic thresholds. |
| AP8 (Complex SQL / unused CTEs) | All CTEs are used in the final query. The CTE structure is necessary for the UNION ALL pattern. No unused CTEs. |
| AP9 (Misleading names) | "product_penetration" accurately describes what the job computes (product penetration rates). |
| AP10 (Over-sourcing dates) | V1 uses the framework's effective date injection (no hardcoded dates in the config). DataSourcing filters by date at the source. No over-sourcing. |

---

## 8. Proofmark Config

Starting point: **zero exclusions, zero fuzzy overrides**.

```yaml
comparison_target: "product_penetration"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

### Rationale

- **`reader: csv`**: Both V1 and V2 use CsvFileWriter.
- **`header_rows: 1`**: Both V1 and V2 have `includeHeader: true`.
- **`trailer_rows: 0`**: No `trailerFormat` specified in V1 or V2 config -- no trailer rows to strip.
- **No excluded columns**: The BRD identifies `as_of` as potentially non-deterministic due to the `JOIN customers ON 1=1 LIMIT 3` cross-join (BRD Non-Deterministic Fields, confidence MEDIUM). However, since V1 and V2 both use the identical SQL expression and the same DataSourcing module (which orders rows by `as_of` per `DataSourcing.cs:85`), the SQLite iteration order should be the same given the same input data. We start with strict comparison. If the `as_of` column causes failures due to non-deterministic cross-join behavior, the resolution would be to add it as an EXCLUDED column with documented evidence from the comparison report.
- **No fuzzy columns**: All computed values are integers (COUNT results and integer-division results). No floating-point precision concerns.

### Potential Proofmark Adjustments

1. **`as_of` non-determinism**: If Proofmark reports mismatches on the `as_of` column, add it as EXCLUDED with reason: "Cross-join `JOIN customers ON 1=1 LIMIT 3` makes as_of value dependent on SQLite internal row ordering -- non-deterministic across runs [product_penetration.json:36]."
2. **Row ordering**: If the 3 rows appear in a different order between V1 and V2, investigate whether Proofmark supports order-independent comparison, or adjust the SQL to match V1's observed order.

---

## 9. Open Questions

1. **Cross-join `as_of` determinism (MEDIUM confidence):** The `JOIN customers ON 1=1 LIMIT 3` pattern means the `as_of` value is selected from whichever customer row appears first in SQLite's iteration. Since DataSourcing orders by `as_of`, this should be the minimum `as_of` in the effective date range (which, for single-day execution, is the only `as_of` value). But this is an implementation detail of SQLite's query planner, not a guarantee. If V1 and V2 produce different `as_of` values, this column may need to be excluded from Proofmark comparison.

2. **Zero-row edge case on weekends (LOW confidence):** If the effective date falls on a weekend or holiday where no `as_of` data exists in the datalake, all four DataSourcing modules return zero-row DataFrames. Per `Transformation.cs:46`, zero-row DataFrames are not registered as SQLite tables, causing the SQL to fail with "no such table." V1 behaves identically -- its Transformation module uses the same framework code. So both V1 and V2 would fail the same way on weekends. The date range 2024-10-01 through 2024-12-31 includes only weekdays with data, so this is not expected to manifest. If it does, both V1 and V2 would produce the same error (no output file), and Proofmark comparison would be moot for those dates.

3. **LIMIT 3 row-selection ordering (LOW confidence):** The UNION ALL in `product_stats` produces rows in the order accounts, cards, investments. The cross-join with customers then multiplies these 3 rows by N customer rows. The LIMIT 3 should grab the first 3 rows from this cross product, which (in SQLite's typical left-to-right nested loop join) would be the first product_stats row ('accounts') paired with the first customer row, then the second product_stats row ('cards') with the first customer row, then the third ('investments') with the first customer row. This is consistent between V1 and V2 since both use the identical SQL on the identical data. But it depends on SQLite's join implementation, not a formal guarantee.

---

## V2 Job Config

```json
{
  "jobName": "ProductPenetrationV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "customers",
      "schema": "datalake",
      "table": "customers",
      "columns": ["id"]
    },
    {
      "type": "DataSourcing",
      "resultName": "accounts",
      "schema": "datalake",
      "table": "accounts",
      "columns": ["customer_id"]
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
      "columns": ["customer_id"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "WITH customer_counts AS (SELECT COUNT(DISTINCT id) AS total_customers FROM customers), account_holders AS (SELECT COUNT(DISTINCT customer_id) AS cnt FROM accounts), card_holders AS (SELECT COUNT(DISTINCT customer_id) AS cnt FROM cards), investment_holders AS (SELECT COUNT(DISTINCT customer_id) AS cnt FROM investments), product_stats AS (SELECT 'accounts' AS product_type, cc.total_customers AS customer_count, ah.cnt AS product_count, CAST(ah.cnt AS INTEGER) / CAST(cc.total_customers AS INTEGER) AS penetration_rate FROM customer_counts cc, account_holders ah UNION ALL SELECT 'cards' AS product_type, cc.total_customers AS customer_count, ch.cnt AS product_count, CAST(ch.cnt AS INTEGER) / CAST(cc.total_customers AS INTEGER) AS penetration_rate FROM customer_counts cc, card_holders ch UNION ALL SELECT 'investments' AS product_type, cc.total_customers AS customer_count, ih.cnt AS product_count, CAST(ih.cnt AS INTEGER) / CAST(cc.total_customers AS INTEGER) AS penetration_rate FROM customer_counts cc, investment_holders ih) SELECT ps.product_type, ps.customer_count, ps.product_count, ps.penetration_rate, customers.as_of FROM product_stats ps JOIN customers ON 1=1 LIMIT 3"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/product_penetration.csv",
      "includeHeader": true,
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

---

## Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|-------------|----------------|----------|
| Tier 1 Framework Only | BR-7 (uses Transformation, not External) | `product_penetration.json:34` type="Transformation" |
| Remove `first_name`, `last_name` from customers (AP4) | BR-6 (sourced but not in output) | `product_penetration.json:36` SQL SELECT clause |
| Remove `account_id`, `account_type` from accounts (AP4) | BRD Source Tables / SQL analysis | SQL only uses `COUNT(DISTINCT customer_id)` from accounts |
| Remove `card_id` from cards (AP4) | BRD Source Tables / SQL analysis | SQL only uses `COUNT(DISTINCT customer_id)` from cards |
| Remove `investment_id` from investments (AP4) | BRD Source Tables / SQL analysis | SQL only uses `COUNT(DISTINCT customer_id)` from investments |
| Preserve integer division in SQL (W4) | BR-2 (integer division bug) | `product_penetration.json:36` CAST expressions |
| Preserve cross-join `JOIN customers ON 1=1` | BR-5 (as_of from cross-join) | `product_penetration.json:36` |
| Preserve `LIMIT 3` | BR-4 (output limited to 3 rows) | `product_penetration.json:36` |
| Preserve CTE structure | BR-3 (CTE-based computation) | `product_penetration.json:36` |
| CsvFileWriter with Overwrite, LF, no trailer | BRD Writer Configuration | `product_penetration.json:39-45` |
| Output path to `double_secret_curated/` | BLUEPRINT V2 conventions | `POC3/BLUEPRINT.md` Phase B naming conventions |
| Proofmark strict comparison, no exclusions | BRD Non-Deterministic Fields (MEDIUM confidence on as_of) | Starting strict per CONFIG_GUIDE.md best practices |
