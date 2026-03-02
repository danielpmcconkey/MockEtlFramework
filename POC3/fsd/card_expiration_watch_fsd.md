# CardExpirationWatch — Functional Specification Document

## 1. Overview

**Job Name (V2):** CardExpirationWatchV2
**Config File:** `JobExecutor/Jobs/card_expiration_watch_v2.json`
**Module Tier:** Tier 2 — Framework + Minimal External (SCALPEL)

This job identifies cards expiring within the next 90 days relative to the effective date (with weekend fallback), enriched with customer names and a days-until-expiry calculation. The output is written as Parquet.

### Tier Justification

The core business logic (join cards to customers, compute days-until-expiry, filter to 0-90 day window, apply weekend fallback) is fully expressible in SQL. However, the Transformation module stores all values through SQLite, which converts `DateOnly` to TEXT strings and returns integers as `long`. The V1 Parquet output has `DateOnly`-typed columns (`expiration_date`, `as_of`) and `int`-typed columns (`customer_id`, `days_until_expiry`). Byte-identical Parquet output requires matching these CLR types exactly.

A minimal External module is needed ONLY for type coercion — converting the Transformation module's string dates back to `DateOnly` and `long` integers back to `int`. No business logic lives in the External module.

---

## 2. V2 Module Chain

```
DataSourcing (cards)
  → DataSourcing (customers)
    → Transformation (SQL: join, weekend fallback, days-until-expiry, 0-90 filter)
      → External (type coercion only)
        → ParquetFileWriter
```

| Step | Module | Config Key | Purpose |
|------|--------|------------|---------|
| 1 | DataSourcing | `cards` | Load `datalake.cards` — columns: `card_id`, `customer_id`, `card_type`, `expiration_date` |
| 2 | DataSourcing | `customers` | Load `datalake.customers` — columns: `id`, `first_name`, `last_name` |
| 3 | Transformation | `output` | SQL: join cards to customers, compute targetDate (weekend fallback), calculate days_until_expiry, filter to 0-90 window, set as_of = targetDate |
| 4 | External | — | Type coercion: cast `expiration_date` and `as_of` from string to DateOnly; cast `customer_id` and `days_until_expiry` from long to int |
| 5 | ParquetFileWriter | — | Write `output` DataFrame to Parquet |

---

## 3. Anti-Pattern Analysis

### Anti-Patterns Identified in V1

| Code | Name | V1 Behavior | V2 Action |
|------|------|-------------|-----------|
| W2 | Weekend fallback | Uses previous Friday's date on Saturday/Sunday for targetDate | **Reproduce.** Implement in SQL with `CASE WHEN strftime('%w', ...) = '6'` and `= '0'` logic. Comment documents this is intentional V1 behavior replication. |
| AP3 | Unnecessary External module | V1 uses a full External module (`CardExpirationWatchProcessor`) for logic that is expressible in SQL | **Eliminated.** Business logic moved to SQL Transformation. External module retained ONLY for type coercion (Tier 2 scalpel), which is a framework limitation, not business logic. |
| AP6 | Row-by-row iteration | V1 iterates every card row with `foreach`, builds customer lookup dictionary, evaluates expiry one-by-one | **Eliminated.** Replaced with set-based SQL: `LEFT JOIN` for customer lookup, `WHERE` clause for 0-90 day filtering. |

### Anti-Patterns NOT Present in V1

| Code | Status |
|------|--------|
| AP1 (Dead-end sourcing) | Not present — both `cards` and `customers` are used. |
| AP4 (Unused columns) | Not present — all sourced columns are used in output or joins. |
| AP7 (Magic values) | The 90-day threshold is a business constant. V1 uses literal `90`. V2 SQL uses literal `90` with a comment documenting its business meaning. |
| AP10 (Over-sourcing dates) | Not present — DataSourcing uses framework effective date injection. |
| W1, W3a-c, W4, W5, W6, W7, W8, W9, W10, W12 | Not applicable to this job. |

---

## 4. Output Schema

| Column | Type (CLR) | Parquet Type | Source | Transformation | BRD Ref |
|--------|-----------|-------------|--------|----------------|---------|
| `card_id` | string | string | `cards.card_id` | Pass-through | BR-8 |
| `customer_id` | int | INT32 | `cards.customer_id` | Cast to int | BR-6 |
| `first_name` | string | string | `customers.first_name` | LEFT JOIN on customer_id = id; `''` if not found | BR-6 |
| `last_name` | string | string | `customers.last_name` | LEFT JOIN on customer_id = id; `''` if not found | BR-6 |
| `card_type` | string | string | `cards.card_type` | Pass-through | BR-8 |
| `expiration_date` | DateOnly | DATE | `cards.expiration_date` | Pass-through (type restored by External) | BR-7 |
| `days_until_expiry` | int | INT32 | Derived | `julianday(expiration_date) - julianday(targetDate)`, cast to int | BR-3 |
| `as_of` | DateOnly | DATE | Derived | Set to targetDate (weekend-adjusted maxEffectiveDate) | BR-9 |

---

## 5. SQL Design

The Transformation module SQL performs all business logic in a single query:

```sql
WITH target AS (
    -- W2: Weekend fallback — use Friday's date on Saturday/Sunday
    -- Saturday (strftime %w = 6) → subtract 1 day
    -- Sunday (strftime %w = 0) → subtract 2 days
    -- Weekdays → use as-is
    SELECT
        CASE
            WHEN CAST(strftime('%w', MAX(as_of)) AS INTEGER) = 6
                THEN date(MAX(as_of), '-1 day')
            WHEN CAST(strftime('%w', MAX(as_of)) AS INTEGER) = 0
                THEN date(MAX(as_of), '-2 days')
            ELSE MAX(as_of)
        END AS target_date
    FROM cards
),
deduped_customers AS (
    -- Deduplicate customers across as_of snapshots.
    -- V1 uses dictionary last-writer-wins keyed by id.
    -- DataSourcing orders by as_of, so last row per id = MAX(as_of).
    SELECT c1.id, c1.first_name, c1.last_name
    FROM customers c1
    INNER JOIN (
        SELECT id, MAX(as_of) AS max_as_of
        FROM customers
        GROUP BY id
    ) c2 ON c1.id = c2.id AND c1.as_of = c2.max_as_of
)
SELECT
    c.card_id,
    CAST(c.customer_id AS INTEGER) AS customer_id,
    COALESCE(cu.first_name, '') AS first_name,
    COALESCE(cu.last_name, '') AS last_name,
    c.card_type,
    c.expiration_date,
    CAST(julianday(c.expiration_date) - julianday(t.target_date) AS INTEGER) AS days_until_expiry,
    t.target_date AS as_of
FROM cards c
CROSS JOIN target t
LEFT JOIN deduped_customers cu ON CAST(c.customer_id AS INTEGER) = CAST(cu.id AS INTEGER)
WHERE CAST(julianday(c.expiration_date) - julianday(t.target_date) AS INTEGER) >= 0
  AND CAST(julianday(c.expiration_date) - julianday(t.target_date) AS INTEGER) <= 90
```

### SQL Design Notes

1. **Weekend fallback (BR-1, W2):** Computed from `MAX(as_of)` of the cards table. This is equivalent to `__maxEffectiveDate` because DataSourcing loads cards for exactly the effective date range. On weekdays, `MAX(as_of)` equals the effective date. On weekends, the cards table has no data for that as_of, so the cards DataFrame is empty and the query returns zero rows (matching BR-10). The weekend fallback logic is included for completeness but is effectively a no-op for empty DataFrames since the `target` CTE's `MAX(as_of)` returns NULL when cards is empty, and all subsequent joins produce zero rows.

2. **Customer deduplication (BR-6):** V1 builds a flat dictionary from all customer rows, with last-writer-wins semantics. Since DataSourcing orders by `as_of`, the last row per customer id is from `MAX(as_of)`. The `deduped_customers` CTE replicates this by taking the row with the maximum `as_of` per customer `id`.

3. **No as_of/status filter (BR-8):** All card rows from all as_of snapshots are evaluated. The SQL does not filter on `cards.as_of` or any status column. This matches V1's `foreach (var card in cards.Rows)` which iterates all loaded rows.

4. **Days until expiry (BR-3):** `julianday(expiration_date) - julianday(target_date)` computes the calendar-day difference, matching V1's `DayNumber` subtraction. The `CAST AS INTEGER` truncates toward zero, matching C#'s integer subtraction behavior.

5. **0-90 day window (BR-2, BR-4, BR-5):** The `WHERE` clause filters to `days_until_expiry >= 0 AND days_until_expiry <= 90`, including both boundaries. Cards already expired (< 0) and cards expiring more than 90 days out (> 90) are excluded.

6. **Customer not found (Edge Case #3):** `LEFT JOIN` with `COALESCE(..., '')` produces empty strings when no matching customer exists, matching V1's `GetValueOrDefault(custId, ("", ""))`.

7. **Empty cards handling (BR-10):** If the cards DataFrame is empty, `MAX(as_of)` returns NULL, the `CROSS JOIN target` produces zero rows, and the output is an empty result set. The Transformation module will produce an empty DataFrame, which the ParquetFileWriter handles correctly.

8. **Duplicate cards across snapshots (Edge Case #1):** Each card row (across all as_of snapshots) is independently evaluated. If the same card_id appears in multiple snapshots and passes the 90-day filter each time, it appears multiple times in the output. This matches V1 behavior.

9. **Magic value 90 (AP7):** The 90-day threshold is a business-defined card renewal outreach window. It appears as a literal in the SQL `WHERE` clause. Since this is a SQL Transformation (not C# code), named constants are not applicable, but a SQL comment documents the meaning.

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "CardExpirationWatchV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "cards",
      "schema": "datalake",
      "table": "cards",
      "columns": ["card_id", "customer_id", "card_type", "expiration_date"]
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
      "resultName": "output_raw",
      "sql": "WITH target AS (SELECT CASE WHEN CAST(strftime('%w', MAX(as_of)) AS INTEGER) = 6 THEN date(MAX(as_of), '-1 day') WHEN CAST(strftime('%w', MAX(as_of)) AS INTEGER) = 0 THEN date(MAX(as_of), '-2 days') ELSE MAX(as_of) END AS target_date FROM cards), deduped_customers AS (SELECT c1.id, c1.first_name, c1.last_name FROM customers c1 INNER JOIN (SELECT id, MAX(as_of) AS max_as_of FROM customers GROUP BY id) c2 ON c1.id = c2.id AND c1.as_of = c2.max_as_of) SELECT c.card_id, CAST(c.customer_id AS INTEGER) AS customer_id, COALESCE(cu.first_name, '') AS first_name, COALESCE(cu.last_name, '') AS last_name, c.card_type, c.expiration_date, CAST(julianday(c.expiration_date) - julianday(t.target_date) AS INTEGER) AS days_until_expiry, t.target_date AS as_of FROM cards c CROSS JOIN target t LEFT JOIN deduped_customers cu ON CAST(c.customer_id AS INTEGER) = CAST(cu.id AS INTEGER) WHERE CAST(julianday(c.expiration_date) - julianday(t.target_date) AS INTEGER) >= 0 AND CAST(julianday(c.expiration_date) - julianday(t.target_date) AS INTEGER) <= 90"
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.CardExpirationWatchV2Processor"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/card_expiration_watch/",
      "numParts": 1,
      "writeMode": "Overwrite"
    }
  ]
}
```

---

## 7. Writer Configuration

| Parameter | V1 Value | V2 Value | Match? |
|-----------|----------|----------|--------|
| Writer type | ParquetFileWriter | ParquetFileWriter | Yes |
| `source` | `"output"` | `"output"` | Yes |
| `outputDirectory` | `Output/curated/card_expiration_watch/` | `Output/double_secret_curated/card_expiration_watch/` | Path change only (per spec) |
| `numParts` | 1 | 1 | Yes |
| `writeMode` | `"Overwrite"` | `"Overwrite"` | Yes |

---

## 8. Proofmark Config Design

**Reader:** `parquet` (matches V1 ParquetFileWriter output)
**Threshold:** `100.0` (strict — all rows must match)

### Column Overrides

**Excluded columns:** None.
**Fuzzy columns:** None.

All output columns are deterministic given the same input data and effective date (per BRD: "None identified"). No floating-point accumulation or runtime-generated values exist. Strict comparison is appropriate for all columns.

### Proofmark Config YAML

```yaml
comparison_target: "card_expiration_watch"
reader: parquet
threshold: 100.0
```

---

## 9. External Module Design (Type Coercion)

**File:** `ExternalModules/CardExpirationWatchV2Processor.cs`
**Purpose:** Convert Transformation output types to match V1 Parquet schema.

The External module performs ZERO business logic. It reads the `output_raw` DataFrame produced by the Transformation, iterates each row, casts date strings to `DateOnly` and integer values to `int`, and stores the result as the `output` DataFrame for the ParquetFileWriter.

### Type Coercion Map

| Column | Input Type (from SQLite) | Output Type (for Parquet) | Conversion |
|--------|-------------------------|--------------------------|------------|
| `card_id` | string | string | No conversion needed |
| `customer_id` | long | int | `Convert.ToInt32()` |
| `first_name` | string | string | No conversion needed |
| `last_name` | string | string | No conversion needed |
| `card_type` | string | string | No conversion needed |
| `expiration_date` | string (`"yyyy-MM-dd"`) | DateOnly | `DateOnly.Parse()` |
| `days_until_expiry` | long | int | `Convert.ToInt32()` |
| `as_of` | string (`"yyyy-MM-dd"`) | DateOnly | `DateOnly.Parse()` |

### Implementation Sketch

```csharp
public class CardExpirationWatchV2Processor : IExternalStep
{
    // Column names for the output DataFrame
    private static readonly List<string> OutputColumns = new()
    {
        "card_id", "customer_id", "first_name", "last_name", "card_type",
        "expiration_date", "days_until_expiry", "as_of"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var rawDf = (DataFrame)sharedState["output_raw"];

        // Type coercion only — no business logic.
        // The Transformation module's SQLite backend stores DateOnly as TEXT
        // and returns integers as long. The V1 Parquet schema uses DateOnly
        // and int types. This module restores the correct CLR types for
        // byte-identical Parquet output.
        var typedRows = new List<Row>();
        foreach (var row in rawDf.Rows)
        {
            typedRows.Add(new Row(new Dictionary<string, object?>
            {
                ["card_id"] = row["card_id"],
                ["customer_id"] = Convert.ToInt32(row["customer_id"]),
                ["first_name"] = row["first_name"]?.ToString() ?? "",
                ["last_name"] = row["last_name"]?.ToString() ?? "",
                ["card_type"] = row["card_type"],
                ["expiration_date"] = DateOnly.Parse(row["expiration_date"]!.ToString()!),
                ["days_until_expiry"] = Convert.ToInt32(row["days_until_expiry"]),
                ["as_of"] = DateOnly.Parse(row["as_of"]!.ToString()!)
            }));
        }

        sharedState["output"] = new DataFrame(typedRows, OutputColumns);
        return sharedState;
    }
}
```

### Empty DataFrame Handling

If the Transformation produces zero rows (weekend dates, no qualifying cards), `rawDf.Rows` is empty. The `foreach` produces zero `typedRows`, and the output DataFrame is empty with the correct schema. This matches V1's BR-10 behavior.

---

## 10. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision | Evidence |
|-----------------|-------------|-----------------|----------|
| BR-1: Weekend fallback | SQL Design (note 1) | `target` CTE applies weekend fallback via `strftime('%w')` with `-1 day` (Saturday) and `-2 days` (Sunday) | [CardExpirationWatchProcessor.cs:19-21] |
| BR-2: 0-90 day window | SQL Design (note 5) | `WHERE` clause: `days_until_expiry >= 0 AND days_until_expiry <= 90` | [CardExpirationWatchProcessor.cs:58] |
| BR-3: days_until_expiry calc | SQL Design (note 4) | `julianday(expiration_date) - julianday(target_date)` cast to INTEGER | [CardExpirationWatchProcessor.cs:56] |
| BR-4: Expired cards excluded | SQL Design (note 5) | `WHERE days_until_expiry >= 0` excludes negative values | [CardExpirationWatchProcessor.cs:58] |
| BR-5: Far-future cards excluded | SQL Design (note 5) | `WHERE days_until_expiry <= 90` excludes values > 90 | [CardExpirationWatchProcessor.cs:58] |
| BR-6: Customer name lookup | SQL Design (note 2, 6) | `LEFT JOIN deduped_customers` with `COALESCE(..., '')` for not-found | [CardExpirationWatchProcessor.cs:37-48, 61] |
| BR-7: DateTime handling | SQL Design (note 4) | SQLite `julianday()` handles both `yyyy-MM-dd` and `yyyy-MM-ddTHH:mm:ss` formats. Type coercion External module uses `DateOnly.Parse()` which handles date strings. | [CardExpirationWatchProcessor.cs:55] |
| BR-8: No as_of/status filter | SQL Design (note 3) | No `WHERE` clause on `cards.as_of` or any status column. All card rows from all snapshots are evaluated. | [CardExpirationWatchProcessor.cs:52] |
| BR-9: as_of = targetDate | SQL Design | `t.target_date AS as_of` in the SELECT clause | [CardExpirationWatchProcessor.cs:73] |
| BR-10: Empty input handling | SQL Design (note 7), External Module Design | Empty cards → `MAX(as_of)` returns NULL → zero rows from CROSS JOIN. External module handles empty DataFrame gracefully. | [CardExpirationWatchProcessor.cs:30-34] |
| W2: Weekend fallback | Anti-Pattern Analysis | Reproduced in SQL with clean `CASE/WHEN` and documented comment | [KNOWN_ANTI_PATTERNS.md: W2] |
| AP3: Unnecessary External | Anti-Pattern Analysis | **Eliminated.** Business logic moved to SQL. External retained only for type coercion (framework limitation). | [KNOWN_ANTI_PATTERNS.md: AP3] |
| AP6: Row-by-row iteration | Anti-Pattern Analysis | **Eliminated.** Replaced with set-based SQL JOIN and WHERE. | [KNOWN_ANTI_PATTERNS.md: AP6] |
| Edge Case #1: Duplicates | SQL Design (note 8) | All card rows across snapshots are independently evaluated — no deduplication of cards | BRD Edge Case #1 |
| Edge Case #2: Weekend data | SQL Design (note 1) | Weekend effective dates → empty cards DataFrame → empty output | BRD Edge Case #2 |
| Edge Case #3: Customer not found | SQL Design (note 6) | `LEFT JOIN` + `COALESCE(..., '')` | BRD Edge Case #3 |
| Edge Case #4: Expiration on targetDate | SQL Design (note 5) | `days_until_expiry >= 0` includes zero (expiry day itself) | BRD Edge Case #4 |
| Edge Case #5: No card_status filter | SQL Design (note 3) | No status column in WHERE — all statuses included | BRD Edge Case #5 |

---

## 11. Open Design Decisions

1. **Customer deduplication edge case:** If a customer's `first_name` or `last_name` changes across as_of snapshots, V1's last-writer-wins (latest as_of) and V2's `MAX(as_of)` subquery should produce the same result because DataSourcing orders by `as_of` ascending. However, if two rows for the same customer have the same `MAX(as_of)` (which shouldn't happen in practice since as_of is a date-level snapshot key), the behavior could differ. Risk: LOW — as_of is unique per customer per snapshot.

2. **julianday precision:** SQLite's `julianday()` returns a `REAL` (double). The difference between two date strings produces an exact integer for whole-day differences (no fractional days when both inputs are date-only strings). The `CAST AS INTEGER` truncates any floating-point epsilon, which matches C#'s integer subtraction. Risk: LOW.
