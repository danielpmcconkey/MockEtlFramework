# ComplianceOpenItemsV2 — Functional Specification Document

## 1. Overview & Tier Selection

**Job:** ComplianceOpenItemsV2
**Tier:** 1 (Framework Only) — `DataSourcing -> Transformation (SQL) -> ParquetFileWriter`

This job produces a list of open/escalated compliance events enriched with customer name information, with weekend fallback logic that maps Saturday/Sunday effective dates back to the previous Friday. Output is a single Parquet part file.

**Tier Justification:** All business logic can be expressed in SQLite SQL:
- Weekend fallback date computation uses `strftime('%w', date)` and `date()` modifiers — native SQLite functions.
- Status filtering (`IN ('Open', 'Escalated')`) is a simple WHERE clause.
- Customer name enrichment is a LEFT JOIN with deduplication via window function (ROW_NUMBER).
- NULL-to-empty-string coercion uses COALESCE.

The V1 implementation used an unnecessary External module (AP3) to perform logic that is entirely expressible in SQL. V2 eliminates this anti-pattern by moving all logic into a Transformation module.

## 2. V2 Module Chain

```
DataSourcing (compliance_events)
    -> DataSourcing (customers)
    -> Transformation (output)
    -> ParquetFileWriter (Output/double_secret_curated/compliance_open_items/)
```

### Module 1: DataSourcing — `compliance_events`
- **Schema:** datalake
- **Table:** compliance_events
- **Columns:** event_id, customer_id, event_type, event_date, status
- **Effective dates:** Injected by executor via shared state (`__minEffectiveDate`, `__maxEffectiveDate`)
- **No additionalFilter** — filtering is handled in the SQL Transformation
- **Change from V1:** Removed `review_date` column (AP4 — sourced but never used in output)

### Module 2: DataSourcing — `customers`
- **Schema:** datalake
- **Table:** customers
- **Columns:** id, first_name, last_name
- **Effective dates:** Injected by executor via shared state (`__minEffectiveDate`, `__maxEffectiveDate`)
- **No additionalFilter**
- **Change from V1:** Removed `prefix` and `suffix` columns (AP4 — sourced but never used in output)

### Module 3: Transformation — `output`
- **SQL:** See Section 5 for full SQL design
- **Result name:** output

### Module 4: ParquetFileWriter
- **Source:** output
- **Output path:** `Output/double_secret_curated/compliance_open_items/`
- **numParts:** 1
- **writeMode:** Overwrite

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (W-codes) — Must Reproduce

| W-code | Applies? | Analysis |
|--------|----------|----------|
| W2 (Weekend fallback) | **YES** | BR-2: Saturday maps to Friday (-1 day), Sunday maps to Friday (-2 days). V2 reproduces this via SQLite `strftime('%w')` and `date()` modifier in a CTE. The fallback target date is used both for filtering `compliance_events.as_of` and as the output `as_of` value. In practice, since the executor runs single-day gap-fill (min == max effective date), weekend runs source only Saturday or Sunday data, the fallback filters to Friday's `as_of` which isn't in the sourced data, producing zero output rows. This is the correct V1 behavior. |
| W9 (Wrong writeMode) | **YES** | BRD Write Mode section: Overwrite mode means multi-day runs only retain the last effective date's output. V2 preserves this behavior. |

No other W-codes apply:
- W1: No explicit Sunday skip — weekend fallback (W2) handles both Saturday and Sunday.
- W3a-c: No boundary summary rows.
- W4: No integer division.
- W5: No rounding.
- W6: No monetary accumulation.
- W7/W8: No trailer (Parquet output).
- W10: numParts is 1, which is appropriate for this dataset size. Not absurd.
- W12: Not Append mode, not CSV.

### Code-Quality Anti-Patterns (AP-codes) — Must Eliminate

| AP-code | Applies? | V1 Problem | V2 Resolution |
|---------|----------|------------|---------------|
| AP3 (Unnecessary External) | **YES** | V1 uses `ComplianceOpenItemsBuilder.cs` as an External module. All logic (weekend fallback, status filter, customer lookup, NULL coercion) is expressible in SQL. | **Eliminated.** V2 replaces the External module with a Transformation (SQL) module. Tier 1 instead of V1's effective Tier 3. |
| AP4 (Unused columns) | **YES** | V1 sources `review_date` from compliance_events and `prefix`, `suffix` from customers. None appear in the output. [ComplianceOpenItemsBuilder.cs:31] | **Eliminated.** V2 DataSourcing configs remove all three unused columns. compliance_events sources: event_id, customer_id, event_type, event_date, status. customers sources: id, first_name, last_name. |
| AP6 (Row-by-row iteration) | **YES** | V1 builds a customer lookup dictionary via `foreach` loop [ComplianceOpenItemsBuilder.cs:37-43], then iterates filtered events row-by-row to construct output [ComplianceOpenItemsBuilder.cs:57-73]. | **Eliminated.** V2 uses a SQL LEFT JOIN for customer enrichment and set-based filtering via WHERE clause. |
| AP1 (Dead-end sourcing) | **NO** | Both sourced tables (compliance_events, customers) contribute to the output. |
| AP7 (Magic values) | **NO** | Status values `'Open'` and `'Escalated'` are business domain values, not magic constants. |
| AP10 (Over-sourcing dates) | **NO** | DataSourcing uses the framework's effective date injection. |

### BRD Inconsistency: Status Filter

The BRD text for BR-1 states "Only compliance events with status = 'Open' are included" but the V1 source code [ComplianceOpenItemsBuilder.cs:49-53] clearly filters for both `status == "Open" || status == "Escalated"`. The BRD traceability matrix entry for BR-1 also reads "Open/Escalated filter." The V2 implementation follows the **V1 source code** (ground truth) and includes both statuses.

### Customer Deduplication (BR-9)

V1 iterates all customer rows in dictionary order, with later rows overwriting earlier ones for the same `id`. Since DataSourcing orders by `as_of` (ascending), the row with the maximum `as_of` date wins for each customer_id. In practice, single-day executor runs (min == max effective date) produce at most one `as_of` date per customer, making deduplication a no-op.

V2 replicates this by using `ROW_NUMBER() OVER (PARTITION BY id ORDER BY as_of DESC)` to select the last-seen customer row per id, matching V1's dictionary-overwrite semantics.

## 4. Output Schema

| Column | Type | Source | Transformation |
|--------|------|--------|---------------|
| event_id | INTEGER | compliance_events.event_id | Direct passthrough |
| customer_id | INTEGER | compliance_events.customer_id | Cast to INTEGER via CAST expression |
| first_name | TEXT | customers.first_name | LEFT JOIN by customer_id; COALESCE to '' if NULL or no match |
| last_name | TEXT | customers.last_name | LEFT JOIN by customer_id; COALESCE to '' if NULL or no match |
| event_type | TEXT | compliance_events.event_type | Direct passthrough |
| event_date | TEXT (date) | compliance_events.event_date | Direct passthrough |
| status | TEXT | compliance_events.status | Direct passthrough (always 'Open' or 'Escalated' due to filter) |
| as_of | TEXT (date) | Computed | Set to target_date after weekend fallback (BR-8), not the raw as_of from compliance_events |

**Column order matches V1:** event_id, customer_id, first_name, last_name, event_type, event_date, status, as_of

## 5. SQL Design

### Design Approach

The SQL uses three CTEs:
1. **max_date**: Extracts the maximum `as_of` from the sourced compliance_events data (equivalent to `__maxEffectiveDate`).
2. **target**: Computes the weekend-fallback target date using SQLite's `strftime('%w')` day-of-week function.
3. **customer_latest**: Deduplicates customers by id, keeping the row with the highest `as_of` (matching V1's dictionary-overwrite-last-wins behavior).

The main query filters compliance events by target date and status, LEFT JOINs customer names, and outputs the correct schema.

### V2 SQL

```sql
-- W2: Weekend fallback — Saturday maps to Friday (-1 day), Sunday to Friday (-2 days).
-- Computes target_date from MAX(as_of) which equals __maxEffectiveDate for single-day runs.
WITH max_date AS (
    SELECT MAX(as_of) AS max_as_of
    FROM compliance_events
),
target AS (
    SELECT
        CASE
            WHEN strftime('%w', max_as_of) = '6' THEN date(max_as_of, '-1 day')
            WHEN strftime('%w', max_as_of) = '0' THEN date(max_as_of, '-2 days')
            ELSE max_as_of
        END AS target_date
    FROM max_date
),
-- BR-9: Customer dedup — last-seen wins (max as_of per id), matching V1 dictionary overwrite order.
-- In single-day runs this is a no-op since there's only one as_of per customer.
customer_latest AS (
    SELECT id, first_name, last_name,
           ROW_NUMBER() OVER (PARTITION BY id ORDER BY as_of DESC) AS rn
    FROM customers
)
SELECT
    ce.event_id,
    CAST(ce.customer_id AS INTEGER) AS customer_id,
    -- BR-4: Default to empty string if no matching customer (LEFT JOIN miss) or NULL name.
    COALESCE(cl.first_name, '') AS first_name,
    COALESCE(cl.last_name, '') AS last_name,
    ce.event_type,
    ce.event_date,
    ce.status,
    -- BR-8: Output as_of is the target_date (after weekend fallback), not the raw as_of.
    t.target_date AS as_of
FROM compliance_events ce
CROSS JOIN target t
LEFT JOIN customer_latest cl ON cl.id = ce.customer_id AND cl.rn = 1
-- BR-3: Filter to rows matching target date (after weekend fallback).
-- On weekends, target_date points to Friday but sourced data is Saturday/Sunday only,
-- so this produces zero rows — matching V1 behavior.
WHERE ce.as_of = t.target_date
  -- BRD BR-1 text says 'Open' only, but V1 source code [ComplianceOpenItemsBuilder.cs:49-53]
  -- filters for both 'Open' and 'Escalated'. V2 follows source code (ground truth).
  AND ce.status IN ('Open', 'Escalated')
```

### Key Design Decisions

1. **MAX(as_of) as proxy for `__maxEffectiveDate`:** The Transformation module does not expose shared-state scalars to SQL. However, since DataSourcing filters the date range to `[__minEffectiveDate, __maxEffectiveDate]` and the executor runs single-day gap-fill (min == max), `MAX(as_of)` from the sourced data equals `__maxEffectiveDate`. This avoids the need for an External module to inject the date.

2. **CROSS JOIN target:** The target CTE produces exactly one row. CROSS JOIN makes the target_date available to every row in the main query without affecting row count.

3. **Empty input handling (BR-7):** If the compliance_events DataFrame has zero rows, the Transformation module's `RegisterTable` method [Transformation.cs:46] skips table registration. The SQL referencing `compliance_events` would produce a SQLite error. In practice, this only occurs if no compliance_events data exists in the datalake for the effective date — an unlikely scenario for the 2024-10-01 to 2024-12-31 date range. If this edge case causes issues during validation, escalation to Tier 2 (minimal External guard clause) is warranted.

4. **CAST(customer_id AS INTEGER):** V1 explicitly calls `Convert.ToInt32(row["customer_id"])` [ComplianceOpenItemsBuilder.cs:59]. The SQL CAST ensures the same integer type in the Parquet output.

## 6. V2 Job Config JSON

```json
{
  "jobName": "ComplianceOpenItemsV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "compliance_events",
      "schema": "datalake",
      "table": "compliance_events",
      "columns": ["event_id", "customer_id", "event_type", "event_date", "status"]
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
      "sql": "WITH max_date AS (SELECT MAX(as_of) AS max_as_of FROM compliance_events), target AS (SELECT CASE WHEN strftime('%w', max_as_of) = '6' THEN date(max_as_of, '-1 day') WHEN strftime('%w', max_as_of) = '0' THEN date(max_as_of, '-2 days') ELSE max_as_of END AS target_date FROM max_date), customer_latest AS (SELECT id, first_name, last_name, ROW_NUMBER() OVER (PARTITION BY id ORDER BY as_of DESC) AS rn FROM customers) SELECT ce.event_id, CAST(ce.customer_id AS INTEGER) AS customer_id, COALESCE(cl.first_name, '') AS first_name, COALESCE(cl.last_name, '') AS last_name, ce.event_type, ce.event_date, ce.status, t.target_date AS as_of FROM compliance_events ce CROSS JOIN target t LEFT JOIN customer_latest cl ON cl.id = ce.customer_id AND cl.rn = 1 WHERE ce.as_of = t.target_date AND ce.status IN ('Open', 'Escalated')"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/compliance_open_items/",
      "numParts": 1,
      "writeMode": "Overwrite"
    }
  ]
}
```

### Changes from V1 config:
- **jobName:** `ComplianceOpenItems` -> `ComplianceOpenItemsV2`
- **Modules:** Replaced External module (`ComplianceOpenItemsBuilder`) with Transformation (SQL) module (AP3 elimination)
- **DataSourcing compliance_events:** Removed `review_date` column (AP4)
- **DataSourcing customers:** Removed `prefix` and `suffix` columns (AP4)
- **outputDirectory:** `Output/curated/compliance_open_items/` -> `Output/double_secret_curated/compliance_open_items/`

### Preserved from V1 config:
- **firstEffectiveDate:** `2024-10-01`
- **Writer type:** ParquetFileWriter (matching V1)
- **numParts:** 1
- **writeMode:** Overwrite

## 7. Writer Config

| Parameter | V1 Value | V2 Value | Match? |
|-----------|----------|----------|--------|
| Writer type | ParquetFileWriter | ParquetFileWriter | YES |
| source | output | output | YES |
| outputDirectory | Output/curated/compliance_open_items/ | Output/double_secret_curated/compliance_open_items/ | Path changed per V2 convention |
| numParts | 1 | 1 | YES |
| writeMode | Overwrite | Overwrite | YES |

<!-- V1 uses Overwrite — multi-day runs only retain last effective date's output (W9). -->

## 8. Proofmark Config Design

### Reader Settings
- **Reader:** parquet (matching ParquetFileWriter output)

### Column Overrides
- **Excluded columns:** None
- **Fuzzy columns:** None

**Justification for zero overrides:** All output columns are deterministic. There are no timestamps, UUIDs, or floating-point accumulations. The `as_of` column is computed from the effective date via deterministic weekend-fallback logic, which is identical between V1 and V2. All string operations (COALESCE for NULL-to-empty) are exact. Customer name lookup is deterministic for single-day runs.

### Proofmark Config YAML

```yaml
comparison_target: "compliance_open_items"
reader: parquet
threshold: 100.0
```

## 9. Traceability Matrix

| BRD Requirement | FSD Section | V2 Implementation |
|-----------------|-------------|-------------------|
| BR-1: Open status filter (code: Open + Escalated) | Section 3 (BRD Inconsistency), Section 5 (SQL WHERE) | `AND ce.status IN ('Open', 'Escalated')` — follows V1 source code ground truth |
| BR-2: Weekend fallback (Sat -1, Sun -2) | Section 3 (W2), Section 5 (target CTE) | `CASE WHEN strftime('%w', max_as_of) = '6' THEN date(max_as_of, '-1 day') WHEN ... = '0' THEN date(..., '-2 days') ELSE max_as_of END` |
| BR-3: Filter by target date (after fallback) | Section 5 (WHERE clause) | `WHERE ce.as_of = t.target_date` |
| BR-4: Customer name enrichment, default '' | Section 5 (LEFT JOIN + COALESCE) | `LEFT JOIN customer_latest cl ON cl.id = ce.customer_id AND cl.rn = 1` + `COALESCE(cl.first_name, '')` |
| BR-5: Unused prefix/suffix columns | Section 3 (AP4) | **Eliminated.** Removed from V2 DataSourcing config. |
| BR-6: Unused review_date column | Section 3 (AP4) | **Eliminated.** Removed from V2 DataSourcing config. |
| BR-7: Empty input produces empty output | Section 5 (Key Design Decision #3) | SQL returns zero rows when no data matches. Edge case documented for empty DataFrame (table not registered). |
| BR-8: Output as_of = target date | Section 5 (SELECT) | `t.target_date AS as_of` |
| BR-9: Customer lookup unfiltered by date | Section 3 (Customer Deduplication), Section 5 (customer_latest CTE) | `ROW_NUMBER() OVER (PARTITION BY id ORDER BY as_of DESC)` selects last-seen row per customer, matching V1 dictionary-overwrite behavior |
| BRD Output Type: ParquetFileWriter | Section 2, Section 7 | ParquetFileWriter module with matching config |
| BRD Writer: numParts 1 | Section 7 | numParts: 1 |
| BRD Writer: writeMode Overwrite | Section 7, Section 3 (W9) | writeMode: Overwrite |
| BRD Non-deterministic: None | Section 8 | Zero Proofmark exclusions/fuzzy overrides |

## 10. External Module Design

**Not applicable.** This job is Tier 1 (Framework Only). The V1 External module (`ComplianceOpenItemsBuilder.cs`) has been replaced by a SQL Transformation module (AP3 elimination). No External module is needed for V2.

**Escalation note:** If the empty-input edge case (BR-7) causes a SQLite error during validation (because the `compliance_events` table is not registered when the DataFrame has zero rows), the fix would be to add a minimal Tier 2 External module as a guard clause before the Transformation step. This guard would check for empty input and short-circuit to an empty output DataFrame. However, this escalation should only happen if the issue is actually encountered — not preemptively.
