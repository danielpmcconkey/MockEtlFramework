# CardCustomerSpending — Functional Specification Document

## 1. Overview

**Job:** CardCustomerSpendingV2
**Tier:** Tier 1 (Framework Only: DataSourcing -> Transformation (SQL) -> ParquetFileWriter)

This job produces a per-customer spending summary for card transactions on a single target date, enriched with customer name. It applies weekend fallback logic (W2): when the effective date falls on Saturday or Sunday, the target date shifts to the preceding Friday.

The V1 implementation uses an External module (`CardCustomerSpendingProcessor.cs`) for weekend date logic, date filtering, customer lookup, and grouping. All of these operations are expressible in SQLite SQL, making the External module unnecessary (AP3). The V2 replaces the entire External module with a single SQL Transformation.

## 2. V2 Module Chain

```
DataSourcing("card_transactions") -> DataSourcing("customers") -> Transformation(SQL) -> ParquetFileWriter
```

| Step | Module | Config Key | Purpose |
|------|--------|------------|---------|
| 1 | DataSourcing | `card_transactions` | Load card_transactions with columns: `customer_id`, `amount` (only columns used in output) |
| 2 | DataSourcing | `customers` | Load customers with columns: `id`, `first_name`, `last_name` (only columns used in output) |
| 3 | Transformation | `output` | Weekend fallback, date filtering, grouping, customer join -- all in one SQL statement |
| 4 | ParquetFileWriter | — | Write to `Output/double_secret_curated/card_customer_spending/`, 1 part, Overwrite |

**Tier justification:** Every operation in the V1 External module maps directly to SQL constructs:
- Weekend fallback: SQLite `strftime('%w', date)` + `date()` function with day offsets
- Date filtering: `WHERE as_of = targetDate`
- Grouping + aggregation: `GROUP BY customer_id` with `COUNT(*)` and `SUM(amount)`
- Customer name lookup: `LEFT JOIN` with deduplication subquery

No procedural logic is required. Tier 1 is sufficient.

## 3. Anti-Pattern Analysis

### Wrinkles Reproduced (Output-Affecting)

| W-Code | V1 Behavior | V2 Approach |
|--------|-------------|-------------|
| W2 | Weekend fallback: Saturday -> Friday (maxDate - 1 day), Sunday -> Friday (maxDate - 2 days) | Reproduced in SQL via CTE using `strftime('%w', max_date)` to detect weekend and `date(max_date, '-N days')` for adjustment. Comment in SQL documents the V1 behavior replication. |

### Anti-Patterns Eliminated

| AP-Code | V1 Problem | V2 Fix |
|---------|------------|--------|
| AP1 | `accounts` table sourced but never used in processing | Removed from V2 DataSourcing config entirely. Only `card_transactions` and `customers` are sourced. |
| AP3 | External module used where SQL can express all logic | Replaced with Transformation module (SQL). The weekend fallback, date filtering, grouping, and customer join are all done in a single SQL statement. |
| AP4 | Unused columns sourced: `card_txn_id`, `card_id` from card_transactions; `prefix`, `suffix` from customers | Removed. V2 sources only: `customer_id`, `amount` from card_transactions; `id`, `first_name`, `last_name` from customers. |
| AP6 | Row-by-row `foreach` loops for grouping and customer lookup | Replaced with SQL `GROUP BY` and `LEFT JOIN` -- set-based operations. |

### Anti-Patterns Not Applicable

| AP-Code | Why N/A |
|---------|--------|
| AP2 | No cross-job duplication identified for this job's logic. |
| AP5 | No asymmetric NULL handling -- customer not found defaults to '' consistently for both first_name and last_name. |
| AP7 | No magic values or hardcoded thresholds in this job. |
| AP8 | No complex unused CTEs. V2 SQL uses a single CTE for the target date, which is fully utilized. |
| AP9 | Job name "CardCustomerSpending" accurately describes output. |
| AP10 | DataSourcing already uses framework effective date injection (no manual date filtering). |

## 4. Output Schema

| Column | Type | Source | Transformation | Evidence |
|--------|------|--------|---------------|----------|
| customer_id | int | card_transactions.customer_id | GROUP BY key | [CardCustomerSpendingProcessor.cs:80] |
| first_name | string | customers.first_name | LEFT JOIN on customer_id = id; COALESCE to '' if not found | [CardCustomerSpendingProcessor.cs:82], [CardCustomerSpendingProcessor.cs:77] |
| last_name | string | customers.last_name | LEFT JOIN on customer_id = id; COALESCE to '' if not found | [CardCustomerSpendingProcessor.cs:83], [CardCustomerSpendingProcessor.cs:77] |
| txn_count | int | card_transactions | COUNT(*) per customer for target date | [CardCustomerSpendingProcessor.cs:84], [CardCustomerSpendingProcessor.cs:70] |
| total_spending | decimal | card_transactions.amount | SUM(amount) per customer for target date | [CardCustomerSpendingProcessor.cs:85], [CardCustomerSpendingProcessor.cs:70] |
| as_of | date | Derived | Set to targetDate (weekend-adjusted), NOT the original __maxEffectiveDate | [CardCustomerSpendingProcessor.cs:86] |

**Empty output behavior:** If card_transactions is empty or no transactions match the target date, the output is an empty DataFrame with the above schema. The Transformation module returns zero rows if no data matches the WHERE clause, which naturally produces this behavior.

**Customer not found:** If a customer_id in card_transactions has no match in customers, `first_name` and `last_name` default to empty string (`''`). Achieved via LEFT JOIN + COALESCE. [BR-8, BR-9, Edge Case 2]

## 5. SQL Design

The Transformation SQL uses a CTE to compute the weekend-adjusted target date, then performs the filtering, grouping, and customer join in a single query.

```sql
WITH target AS (
    -- W2: Weekend fallback — use Friday's data on Saturday/Sunday
    -- V1 behavior: CardCustomerSpendingProcessor.cs:18-20
    SELECT
        CASE strftime('%w', MAX(as_of))
            WHEN '6' THEN date(MAX(as_of), '-1 day')   -- Saturday -> Friday
            WHEN '0' THEN date(MAX(as_of), '-2 days')   -- Sunday -> Friday
            ELSE MAX(as_of)                              -- Weekday -> as-is
        END AS target_date
    FROM card_transactions
),
-- BR-12: Customer lookup uses last-seen name (last as_of wins due to dictionary overwrite)
-- V1 iterates customers ordered by as_of (ascending), dictionary overwrites, so MAX(as_of) wins
customer_latest AS (
    SELECT
        c.id,
        c.first_name,
        c.last_name
    FROM customers c
    INNER JOIN (
        SELECT id, MAX(as_of) AS max_as_of
        FROM customers
        GROUP BY id
    ) latest ON c.id = latest.id AND c.as_of = latest.max_as_of
)
SELECT
    ct.customer_id,
    COALESCE(cl.first_name, '') AS first_name,
    COALESCE(cl.last_name, '') AS last_name,
    COUNT(*) AS txn_count,
    SUM(ct.amount) AS total_spending,
    -- BR-11: as_of uses the weekend-adjusted targetDate, not __maxEffectiveDate
    t.target_date AS as_of
FROM card_transactions ct
CROSS JOIN target t
LEFT JOIN customer_latest cl ON ct.customer_id = cl.id
WHERE ct.as_of = t.target_date
GROUP BY ct.customer_id, cl.first_name, cl.last_name, t.target_date
```

### SQL Design Notes

1. **Target date computation (CTE `target`):** Uses `MAX(as_of)` from card_transactions as a proxy for `__maxEffectiveDate`. Since DataSourcing filters card_transactions to the effective date range and orders by as_of, the MAX(as_of) in the loaded data equals the `__maxEffectiveDate` value. The `strftime('%w')` function returns day of week (0=Sunday, 6=Saturday), and `date()` with day offsets handles the shift to Friday.

2. **Customer deduplication (CTE `customer_latest`):** V1's dictionary-overwrite pattern means the last-seen customer row wins. DataSourcing returns rows ordered by `as_of` ascending, so the row with MAX(as_of) per customer ID is the winner. This CTE explicitly picks the row with MAX(as_of) for each customer, matching V1 behavior. [BR-12]

3. **LEFT JOIN + COALESCE:** Handles the case where a customer_id in card_transactions has no match in customers -- first_name and last_name default to empty string, matching V1's `GetValueOrDefault(kvp.Key, ("", ""))`. [BR-6, Edge Case 2]

4. **Empty data handling:** If card_transactions is empty, the CTE `target` returns NULL for target_date (MAX of empty set), and the WHERE clause `ct.as_of = t.target_date` matches nothing, producing zero output rows. Same if no transactions match the target date. This matches V1 behavior. [BR-8, BR-9]

5. **Decimal precision:** V1 uses `Convert.ToDecimal(txn["amount"])` for monetary accumulation. SQLite's `SUM()` on REAL values produces floating-point results. However, the Transformation module's `GetSqliteType` maps `decimal` to `REAL`, so values go through SQLite as REAL (double). The ParquetFileWriter then uses `Convert.ToDecimal()` to write them back as decimal?. This mirrors the V1 pipeline where amounts come from PostgreSQL as numeric, are converted to decimal in the External module, and written to Parquet. The precision path is equivalent.

6. **as_of column type:** The target_date computed by SQLite's `date()` function returns a string (e.g., `"2024-10-04"`). The `Transformation.ToSqliteValue` converts DateOnly to string format `yyyy-MM-dd`, and `ReaderToDataFrame` reads it back as a string from SQLite. The ParquetFileWriter's `GetParquetType` would see a string, not a DateOnly. This needs attention -- see Section 10 (Potential Issues).

## 6. V2 Job Config JSON

```json
{
  "jobName": "CardCustomerSpendingV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "card_transactions",
      "schema": "datalake",
      "table": "card_transactions",
      "columns": ["customer_id", "amount"]
    },
    {
      "type": "DataSourcing",
      "resultName": "customers",
      "schema": "datalake",
      "table": "customers",
      "columns": ["id", "first_name", "last_name"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "WITH target AS (SELECT CASE strftime('%w', MAX(as_of)) WHEN '6' THEN date(MAX(as_of), '-1 day') WHEN '0' THEN date(MAX(as_of), '-2 days') ELSE MAX(as_of) END AS target_date FROM card_transactions), customer_latest AS (SELECT c.id, c.first_name, c.last_name FROM customers c INNER JOIN (SELECT id, MAX(as_of) AS max_as_of FROM customers GROUP BY id) latest ON c.id = latest.id AND c.as_of = latest.max_as_of) SELECT ct.customer_id, COALESCE(cl.first_name, '') AS first_name, COALESCE(cl.last_name, '') AS last_name, CAST(COUNT(*) AS INTEGER) AS txn_count, SUM(ct.amount) AS total_spending, t.target_date AS as_of FROM card_transactions ct CROSS JOIN target t LEFT JOIN customer_latest cl ON ct.customer_id = cl.id WHERE ct.as_of = t.target_date GROUP BY ct.customer_id, cl.first_name, cl.last_name, t.target_date"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/card_customer_spending/",
      "numParts": 1,
      "writeMode": "Overwrite"
    }
  ]
}
```

## 7. Writer Config

| Parameter | V1 Value | V2 Value | Match? |
|-----------|----------|----------|--------|
| Writer type | ParquetFileWriter | ParquetFileWriter | Yes |
| source | `"output"` | `"output"` | Yes |
| outputDirectory | `Output/curated/card_customer_spending/` | `Output/double_secret_curated/card_customer_spending/` | Path changed per spec |
| numParts | 1 | 1 | Yes |
| writeMode | Overwrite | Overwrite | Yes |

**Write mode implications (W9 check):** Overwrite mode means each effective date run replaces the output directory. For multi-day auto-advance runs, only the last effective date's output survives. This matches V1 behavior. The BRD documents this at "Write Mode Implications". This is V1's intended behavior -- no W9 issue here.

## 8. Proofmark Config Design

```yaml
comparison_target: "card_customer_spending"
reader: parquet
threshold: 100.0
```

**Rationale for zero overrides:**
- **No EXCLUDED columns:** All output columns are deterministic. No timestamps, UUIDs, or other non-reproducible fields. [BRD: "Non-Deterministic Fields: None identified"]
- **No FUZZY columns:** V1 uses `decimal` for monetary accumulation (`Convert.ToDecimal`), not `double`. No floating-point epsilon errors expected. The `txn_count` is an integer count. The `as_of` is a date. `customer_id` is an integer. `first_name` and `last_name` are strings. All should match exactly.

**Potential risk:** The `as_of` column type may differ between V1 (DateOnly written to Parquet) and V2 (string from SQLite written to Parquet). If Proofmark fails on `as_of` type mismatch, this would need investigation -- but we start strict per the "start with zero overrides" principle.

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision |
|----------------|-------------|-----------------|
| BR-1: Weekend fallback | SQL CTE `target` | `strftime('%w')` + `date()` offsets reproduce Saturday->Friday and Sunday->Friday logic |
| BR-2: Filter to target date | SQL WHERE clause | `WHERE ct.as_of = t.target_date` |
| BR-3: Group by customer | SQL GROUP BY | `GROUP BY ct.customer_id, ...` |
| BR-4: txn_count = COUNT | SQL SELECT | `COUNT(*) AS txn_count` |
| BR-5: total_spending = SUM(amount) | SQL SELECT | `SUM(ct.amount) AS total_spending` |
| BR-6: Customer name lookup | SQL LEFT JOIN | `LEFT JOIN customer_latest cl ON ct.customer_id = cl.id` with COALESCE for missing |
| BR-7: Dead accounts sourcing | Anti-Pattern Analysis (AP1) | `accounts` table removed from V2 DataSourcing entirely |
| BR-8: Empty input handling | SQL Design Note 4 | Empty card_transactions -> MAX returns NULL -> WHERE matches nothing -> zero rows |
| BR-9: No matching date | SQL Design Note 4 | No rows with target date -> WHERE matches nothing -> zero rows |
| BR-10: Unused prefix/suffix | Anti-Pattern Analysis (AP4) | Columns removed from V2 DataSourcing config |
| BR-11: as_of = targetDate | SQL SELECT | `t.target_date AS as_of` -- uses weekend-adjusted date |
| BR-12: Unfiltered customer lookup (last-seen wins) | SQL CTE `customer_latest` | Picks MAX(as_of) per customer to match dictionary-overwrite behavior |

| Anti-Pattern | Disposition |
|-------------|-------------|
| W2 (Weekend fallback) | Reproduced in SQL with clear comments |
| AP1 (Dead-end sourcing) | Eliminated -- accounts table removed |
| AP3 (Unnecessary External) | Eliminated -- replaced with SQL Transformation |
| AP4 (Unused columns) | Eliminated -- only used columns sourced |
| AP6 (Row-by-row iteration) | Eliminated -- replaced with SQL GROUP BY and JOIN |

## 10. Potential Issues and Mitigations

### as_of Column Type

**Issue:** V1's External module sets `as_of = targetDate` where `targetDate` is a `DateOnly`. The ParquetFileWriter sees a `DateOnly` value and writes it as a `DateOnly?` Parquet column. In V2, SQLite's `date()` function returns a string (e.g., `"2024-10-04"`). The Transformation module's `ReaderToDataFrame` reads this as a `string`. The ParquetFileWriter sees a string and writes it as a `string` Parquet column.

**Impact:** Proofmark comparison may fail because V1 writes `as_of` as a Date type in Parquet while V2 writes it as a String type.

**Mitigation:** If this causes a Proofmark failure, a Tier 2 approach would be needed: a minimal External module that converts the `as_of` column from string to DateOnly. Alternatively, the Resolution phase can determine whether Proofmark is type-sensitive for this column.

However, there is a second possibility: the DataSourcing module reads `as_of` from PostgreSQL as a DateOnly (via `DateOnly.FromDateTime`). When Transformation registers card_transactions in SQLite, `ToSqliteValue` converts DateOnly to string format `yyyy-MM-dd`. So within SQLite, all `as_of` values are strings. The WHERE clause `ct.as_of = t.target_date` compares strings, which works correctly. The output `as_of` will be a string.

**Recommendation:** Proceed with Tier 1. If Proofmark comparison fails on `as_of` type, escalate to Tier 2 with a minimal External module that casts the string `as_of` back to DateOnly. This keeps the design clean and validates the simplest approach first.

### total_spending Precision

**Issue:** V1 accumulates amounts using `decimal` arithmetic in C#. V2 uses SQLite's `SUM()` which operates on REAL (IEEE 754 double). For most practical amounts, the precision difference is negligible, but edge cases with many decimal places could diverge.

**Mitigation:** V1 receives amounts from PostgreSQL via DataSourcing as .NET objects. The `Convert.ToDecimal` in the External module converts them. In V2, DataSourcing stores them in the DataFrame, then Transformation puts them into SQLite as REAL (per `GetSqliteType`: decimal maps to REAL). So both paths go through floating-point at the SQLite layer. However, V1 never touches SQLite -- it goes PostgreSQL -> DataFrame -> C# decimal directly.

**Recommendation:** Start strict. If Proofmark fails on `total_spending` precision, add a FUZZY tolerance. This matches the "start strict, add evidence-based overrides" principle.

## 11. External Module Design

**Not applicable.** This is a Tier 1 implementation. No External module is needed.

If the as_of type issue (Section 10) requires escalation to Tier 2, the External module would be minimal:

```csharp
// Only if needed -- converts string as_of back to DateOnly for Parquet type match
public class CardCustomerSpendingV2Processor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var df = (DataFrame)sharedState["output"];
        // Convert string as_of to DateOnly for Parquet type equivalence
        var rows = df.Rows.Select(r => new Row(new Dictionary<string, object?>
        {
            ["customer_id"] = r["customer_id"],
            ["first_name"] = r["first_name"],
            ["last_name"] = r["last_name"],
            ["txn_count"] = r["txn_count"],
            ["total_spending"] = r["total_spending"],
            ["as_of"] = DateOnly.Parse(r["as_of"]?.ToString() ?? "")
        })).ToList();
        sharedState["output"] = new DataFrame(rows, df.Columns.ToList());
        return sharedState;
    }
}
```

This is held in reserve. The initial implementation is Tier 1 only.
