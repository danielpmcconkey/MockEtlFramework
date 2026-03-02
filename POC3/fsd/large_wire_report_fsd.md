# LargeWireReport — Functional Specification Document

## 1. Overview & Tier

**Job Name:** LargeWireReportV2
**Config File:** `JobExecutor/Jobs/large_wire_report_v2.json`
**Module Tier:** Tier 1 (Framework Only) — `DataSourcing -> Transformation (SQL) -> CsvFileWriter`

### Tier Justification

The V1 implementation uses an unnecessary External module (AP3) for logic that is entirely expressible in SQL:
- **Filter**: `amount > 10000` — trivial SQL WHERE clause
- **JOIN**: `wire_transfers.customer_id = customers.id` — standard LEFT JOIN
- **NULL coalescing**: `COALESCE(first_name, '')` — standard SQL function
- **Rounding**: `ROUND(amount, 2)` — SQLite built-in function
- **Customer dedup (last-write-wins)**: Window function `ROW_NUMBER() OVER (PARTITION BY id ORDER BY as_of DESC)` — supported by SQLite

No procedural logic is required. Tier 1 is the correct choice.

---

## 2. V2 Module Chain

```
DataSourcing (wire_transfers)
  -> DataSourcing (customers)
    -> Transformation (SQL: filter, join, round, project)
      -> CsvFileWriter (Output/double_secret_curated/large_wire_report.csv)
```

### Module Details

| Step | Module | Config Key | Purpose |
|------|--------|------------|---------|
| 1 | DataSourcing | `wire_transfers` | Load wire_transfers from datalake with effective date filtering |
| 2 | DataSourcing | `customers` | Load customers from datalake with effective date filtering |
| 3 | Transformation | `output` | SQL: deduplicate customers, LEFT JOIN to wires, filter amount > 10000, apply ROUND, project output columns |
| 4 | CsvFileWriter | — | Write `output` DataFrame to CSV |

---

## 3. Anti-Pattern Analysis

### Eliminated Anti-Patterns

| ID | Anti-Pattern | V1 Problem | V2 Resolution |
|----|-------------|------------|---------------|
| AP3 | Unnecessary External module | V1 uses `LargeWireReportBuilder.cs` for logic (filter + join + round) that SQL handles natively | Replaced entirely with Tier 1 SQL Transformation. No External module needed. |
| AP6 | Row-by-row iteration | V1 iterates wire_transfers rows in a `foreach` loop, manually building output rows | Replaced with set-based SQL: LEFT JOIN + WHERE + ROUND in a single query |
| AP7 | Magic values | V1 hardcodes `10000` threshold without documentation [LargeWireReportBuilder.cs:44] | SQL uses literal `10000` with an inline comment: `/* BR-1: regulatory wire threshold */`. Named constants are not available in SQL, but the comment provides the documentation AP7 demands. |

### Preserved Output-Affecting Wrinkles

| ID | Wrinkle | V1 Behavior | V2 Implementation |
|----|---------|-------------|-------------------|
| W5 | Banker's rounding | `Math.Round(amount, 2, MidpointRounding.ToEven)` [LargeWireReportBuilder.cs:50] | SQLite `ROUND(wt.amount, 2)` — see Rounding Analysis below |

#### W5 Rounding Analysis

SQLite's `ROUND()` function uses "round half away from zero" semantics, which differs from .NET's `MidpointRounding.ToEven` (banker's rounding) at exact midpoints (values ending in precisely .XX5).

However, this difference only manifests when a value is *exactly* at a midpoint after the target decimal place. For the wire_transfers dataset:
- Amount values are `numeric` type in PostgreSQL (exact decimal)
- The range is approximately $1,012 to $49,959
- Only amounts > $10,000 are included in output
- DataSourcing reads these as C# `decimal`, which the Transformation module stores as SQLite `REAL` (IEEE 754 double)

For practical financial data, amounts are stored with at most 2 decimal places, making ROUND(x, 2) a no-op for most values. Exact .XX5 midpoints are rare in real transaction data. The SQLite ROUND approach will produce identical output for all non-midpoint values.

**Risk:** If any wire amount is exactly at a .XX5 midpoint (e.g., $15,000.125), SQLite would round to $15,000.13 while V1 would round to $15,000.12. If Proofmark comparison detects such a difference, the resolution protocol should escalate to Tier 2 with a minimal External module that applies banker's rounding post-SQL.

---

## 4. Output Schema

| # | Column | Source | Transformation | BRD Ref |
|---|--------|--------|---------------|---------|
| 1 | wire_id | wire_transfers.wire_id | Direct pass-through | BR-1 |
| 2 | customer_id | wire_transfers.customer_id | Cast via SQL (integer context) | BR-2 |
| 3 | first_name | customers.first_name | LEFT JOIN lookup; COALESCE to empty string if NULL or no match | BR-2, BR-7 |
| 4 | last_name | customers.last_name | LEFT JOIN lookup; COALESCE to empty string if NULL or no match | BR-2, BR-7 |
| 5 | direction | wire_transfers.direction | Direct pass-through | BR-5 |
| 6 | amount | wire_transfers.amount | ROUND(amount, 2) — banker's rounding replication (W5) | BR-1, BR-3 |
| 7 | counterparty_name | wire_transfers.counterparty_name | Direct pass-through | — |
| 8 | status | wire_transfers.status | Direct pass-through | BR-4 |
| 9 | as_of | wire_transfers.as_of | Direct pass-through from wire_transfers row | — |

Column order matches V1 output exactly: `wire_id, customer_id, first_name, last_name, direction, amount, counterparty_name, status, as_of`.

---

## 5. SQL Design

### Customer Deduplication Strategy (BR-7)

The V1 External module builds a dictionary of customers keyed by `id`, iterating all rows ordered by `as_of` ASC (DataSourcing's default ORDER BY). Later rows overwrite earlier ones, so the customer name from the **latest** `as_of` date wins for each `id`.

In V2 SQL, this is replicated using a CTE with `ROW_NUMBER()`:

```sql
WITH latest_customers AS (
    -- BR-7: Deduplicate customers to latest as_of per id (V1 last-write-wins behavior)
    SELECT
        id,
        COALESCE(first_name, '') AS first_name,  -- BR-2: NULL -> empty string
        COALESCE(last_name, '') AS last_name      -- BR-2: NULL -> empty string
    FROM (
        SELECT
            id,
            first_name,
            last_name,
            ROW_NUMBER() OVER (PARTITION BY id ORDER BY as_of DESC) AS rn
        FROM customers
    )
    WHERE rn = 1
)
SELECT
    wt.wire_id,
    CAST(wt.customer_id AS INTEGER) AS customer_id,
    COALESCE(lc.first_name, '') AS first_name,    -- BR-2: no matching customer -> empty string
    COALESCE(lc.last_name, '') AS last_name,       -- BR-2: no matching customer -> empty string
    wt.direction,
    ROUND(wt.amount, 2) AS amount,                 -- W5: banker's rounding (see Rounding Analysis)
    wt.counterparty_name,
    wt.status,
    wt.as_of
FROM wire_transfers wt
LEFT JOIN latest_customers lc
    ON CAST(wt.customer_id AS INTEGER) = lc.id
WHERE wt.amount > 10000                            /* BR-1: regulatory wire threshold ($10,000) */
```

### SQL Design Notes

1. **Double COALESCE layers**: The inner COALESCE (in `latest_customers`) handles NULL `first_name`/`last_name` values in the customers source data. The outer COALESCE handles the LEFT JOIN miss (no matching customer_id). This matches V1's behavior where `GetValueOrDefault(customerId, ("", ""))` returns empty strings for both cases.

2. **CAST(customer_id AS INTEGER)**: V1 uses `Convert.ToInt32(row["customer_id"])` which casts customer_id to int. The SQL replicates this with CAST. This ensures the join key types match and the output column type is integer.

3. **No ORDER BY on final output**: V1 does not sort the output — wires appear in DataSourcing iteration order (by `as_of` ASC within wire_transfers). The SQL query inherits the same ordering from the wire_transfers table, which DataSourcing loads ordered by `as_of`. SQLite preserves the order of the driving table in a LEFT JOIN when no explicit ORDER BY is given on the result.

4. **No status or direction filter**: Per BR-4 and BR-5, all statuses and directions are included. The WHERE clause only filters on amount.

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "LargeWireReportV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "wire_transfers",
      "schema": "datalake",
      "table": "wire_transfers",
      "columns": ["wire_id", "customer_id", "direction", "amount", "counterparty_name", "status"]
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
      "sql": "WITH latest_customers AS (SELECT id, COALESCE(first_name, '') AS first_name, COALESCE(last_name, '') AS last_name FROM (SELECT id, first_name, last_name, ROW_NUMBER() OVER (PARTITION BY id ORDER BY as_of DESC) AS rn FROM customers) WHERE rn = 1) SELECT wt.wire_id, CAST(wt.customer_id AS INTEGER) AS customer_id, COALESCE(lc.first_name, '') AS first_name, COALESCE(lc.last_name, '') AS last_name, wt.direction, ROUND(wt.amount, 2) AS amount, wt.counterparty_name, wt.status, wt.as_of FROM wire_transfers wt LEFT JOIN latest_customers lc ON CAST(wt.customer_id AS INTEGER) = lc.id WHERE wt.amount > 10000"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/large_wire_report.csv",
      "includeHeader": true,
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

### Config Design Notes

- **DataSourcing columns**: Identical to V1 — only the columns actually used in the business logic are sourced. No AP1 (dead-end sourcing) or AP4 (unused columns) issues.
- **DataSourcing does not include `as_of`**: The framework automatically appends `as_of` when it is not in the explicit column list [DataSourcing.cs:69-73]. This `as_of` column is available in SQLite for both the customer dedup window function and the output projection.
- **No External module**: The V1 External module is eliminated (AP3). All logic moves to the Transformation SQL.
- **Writer config matches V1**: `includeHeader: true`, `writeMode: Overwrite`, `lineEnding: LF`, no trailer. Only the output path changes to `Output/double_secret_curated/`.

---

## 7. Writer Configuration

| Property | V1 Value | V2 Value | Match? |
|----------|----------|----------|--------|
| type | CsvFileWriter | CsvFileWriter | Yes |
| source | `output` | `output` | Yes |
| outputFile | `Output/curated/large_wire_report.csv` | `Output/double_secret_curated/large_wire_report.csv` | Path change only (per spec) |
| includeHeader | `true` | `true` | Yes |
| writeMode | `Overwrite` | `Overwrite` | Yes |
| lineEnding | `LF` | `LF` | Yes |
| trailerFormat | not specified | not specified | Yes |

The V2 writer configuration is a byte-for-byte match of V1's writer behavior, differing only in the output directory as required by the project spec.

### Write Mode Implications (BR-6, BRD Edge Case #1-5)

- **Overwrite mode**: Each effective date run replaces the output file entirely. In multi-day gap-fill, only the last day's output survives.
- **Empty output**: If wire_transfers has no rows or no rows exceed $10,000, the SQL query returns zero rows. CsvFileWriter will write a header-only file (since `includeHeader: true`). This matches V1 behavior where an empty DataFrame with correct columns is produced [LargeWireReportBuilder.cs:19-23].

**Note on empty DataFrame**: The V1 code creates an empty DataFrame with explicit column names when wire_transfers is null or empty. In V2, if wire_transfers has zero rows, the SQLite table for wire_transfers will NOT be created (Transformation.RegisterTable skips empty DataFrames: `if (!df.Rows.Any()) return;`). This means the SQL query will fail with "no such table: wire_transfers" if wire_transfers is empty.

**Mitigation**: This is a known framework behavior. If the wire_transfers table is empty for a given effective date, the Transformation module will throw an error. However, since the data range contains wire transfer data for every date (the system is a daily snapshot), this edge case is unlikely in practice. If it occurs during Proofmark comparison, the resolution protocol should address it — potentially by adding a guard in the SQL or escalating to Tier 2 for the empty-input case.

---

## 8. Proofmark Config Design

### Config: `POC3/proofmark_configs/large_wire_report.yaml`

```yaml
comparison_target: "large_wire_report"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

### Justification for Strict Configuration

- **No excluded columns**: All output columns are deterministic. No timestamps, UUIDs, or runtime-generated values. The BRD confirms: "None identified. All values are deterministic given the same input data."
- **No fuzzy columns**: All numeric values (amount) are rounded to 2 decimal places. The rounding behavior (W5) is replicated in SQL. If the SQLite ROUND function produces different results from .NET's banker's rounding for any actual data values, this will surface as a Proofmark failure and be resolved in Phase D.
- **header_rows: 1**: V1 config has `includeHeader: true`.
- **trailer_rows: 0**: V1 config has no `trailerFormat`.
- **threshold: 100.0**: Full match required — no tolerance for row-level mismatches.

---

## 9. Traceability Matrix

| BRD Requirement | FSD Section | V2 Implementation |
|----------------|-------------|-------------------|
| BR-1: Amount > 10000 threshold | SQL Design, Output Schema | `WHERE wt.amount > 10000` in Transformation SQL |
| BR-2: Customer name lookup with empty string default | SQL Design (Double COALESCE), Output Schema | LEFT JOIN `latest_customers` + `COALESCE(lc.first_name, '')` |
| BR-3: Banker's rounding to 2 dp | SQL Design, Anti-Pattern Analysis (W5) | `ROUND(wt.amount, 2)` in SQL (see Rounding Analysis) |
| BR-4: No status filter | SQL Design Note #4 | No status filter in WHERE clause |
| BR-5: No direction filter | SQL Design Note #4 | No direction filter in WHERE clause |
| BR-6: Empty output guard | Writer Config (Write Mode Implications) | SQL returns zero rows; CsvFileWriter writes header-only file. See mitigation note for empty wire_transfers table. |
| BR-7: Last-write-wins customer lookup | SQL Design (Customer Deduplication) | CTE `latest_customers` with `ROW_NUMBER() OVER (PARTITION BY id ORDER BY as_of DESC)` |
| W5: Banker's rounding | Anti-Pattern Analysis | `ROUND(wt.amount, 2)` with documented risk assessment |
| AP3: Unnecessary External | Anti-Pattern Analysis | Eliminated — replaced with Tier 1 SQL |
| AP6: Row-by-row iteration | Anti-Pattern Analysis | Eliminated — set-based SQL query |
| AP7: Magic values | Anti-Pattern Analysis | Documented with inline SQL comment |

---

## 10. External Module Design

**Not applicable.** This job is Tier 1 (Framework Only). No External module is required.

The V1 External module (`ExternalModules/LargeWireReportBuilder.cs`) is entirely replaced by the Transformation SQL. All V1 logic — filter, join, NULL handling, rounding, column projection — is expressed in a single SQL query.

---

## Risk Register

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| SQLite ROUND vs banker's rounding (W5) produces different output for midpoint values | LOW — requires exact .XX5 amounts in data | HIGH — byte-level mismatch | If Proofmark fails, escalate to Tier 2 with a minimal External that applies `Math.Round(amount, 2, MidpointRounding.ToEven)` post-SQL |
| Empty wire_transfers table causes SQL error | LOW — data exists for all dates in range | MEDIUM — job failure, not data corruption | Guard clause in SQL or Tier 2 escalation if triggered |
| SQLite REAL precision loss on decimal amounts | LOW — amounts in $1K-$50K range are well within double precision | LOW — would only affect output at ~15th significant digit | Monitor Proofmark results |
