# CustomerComplianceRisk — Functional Specification Document

## 1. Overview & Tier Selection

**Job:** CustomerComplianceRiskV2
**Tier:** Tier 1 — Framework Only (`DataSourcing -> Transformation (SQL) -> CsvFileWriter`)

**Justification:** V1 uses an External module (`CustomerComplianceRiskCalculator.cs`) to perform LEFT JOIN aggregations with COUNT, a weighted SUM formula, NULL coalescing, and an empty-input guard. Every one of these operations maps directly to SQL constructs available in SQLite:
- LEFT JOIN + subquery for count aggregation (replaces AP6 row-by-row Dictionary accumulation)
- COALESCE for NULL handling (replaces `?.ToString() ?? ""`)
- Arithmetic expression for risk score (replaces C# `double` math)
- ROUND for output equivalence (see W5 note below on rounding semantics)
- Empty DataSourcing -> empty SQLite table -> zero-row output (replaces C# `null` / `Count == 0` guard)

There is zero procedural logic in V1 that cannot be expressed in a single SQL query. The External module is textbook AP3 (unnecessary External) and AP6 (row-by-row iteration). Tier 1 eliminates both completely.

**Key behavioral note:** The V1 code has an account_id/customer_id mismatch bug (BR-4/BR-5) where `high_txn_count` is keyed by `account_id` but looked up by `customer_id`. Since customer IDs (1001+) never equal account IDs (3001+) in the data, `high_txn_count` is always 0. Additionally, no transaction in the data exceeds the 5000 threshold (BR-12, max is 4200). The V2 SQL simply omits the transactions table entirely and hardcodes `high_txn_count` as 0 — this produces identical output to V1 while being explicit about the behavior rather than accidentally arriving at it through a bug.

**Traces to:** BRD BR-1 through BR-12

---

## 2. V2 Module Chain

| Step | Module | Purpose |
|------|--------|---------|
| 1 | DataSourcing | Pull `compliance_events` from `datalake` with effective date filtering |
| 2 | DataSourcing | Pull `wire_transfers` from `datalake` with effective date filtering |
| 3 | DataSourcing | Pull `customers` from `datalake` with effective date filtering |
| 4 | Transformation | SQL: LEFT JOIN aggregations, risk score computation, NULL coalescing |
| 5 | CsvFileWriter | Write output CSV with header, no trailer |

**Modules removed from V1:**
- DataSourcing for `transactions` table — AP1: dead-end source. Due to BR-4/BR-5 (account_id/customer_id mismatch) and BR-12 (max amount 4200 < 5000 threshold), the transactions data never contributes to the output. `high_txn_count` is always 0. Removing this source and hardcoding 0 in SQL produces identical output.
- External module `CustomerComplianceRiskCalculator` — AP3: replaced by SQL Transformation, AP6: row-by-row iteration eliminated.

**Columns removed from V1 DataSourcing configs (AP4):**
- `compliance_events`: removed `event_id`, `event_type`, `status` — only `customer_id` is needed for COUNT aggregation
- `wire_transfers`: removed `wire_id`, `amount`, `direction` — only `customer_id` is needed for COUNT aggregation
- `customers`: columns `id`, `first_name`, `last_name` all retained — all used in output

---

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (Preserved)

| ID | V1 Behavior | V2 Implementation | Trace |
|----|------------|-------------------|-------|
| W5 | Banker's rounding via `Math.Round(riskScore, 2, MidpointRounding.ToEven)` | **Technical note:** SQLite ROUND() uses round-half-away-from-zero. V1 uses C# Math.Round() which defaults to MidpointRounding.ToEven (banker's rounding). These will diverge at exact midpoints (e.g., 2.5 rounds to 2 in C# but 3 in SQLite). However, since all input values are integer counts multiplied by integer weights (30, 20, 10), the risk scores are always exact integers (BR-8) and rounding has no practical effect — no midpoint values are possible. The SQL uses `ROUND(risk_score, 2)` for behavioral equivalence; the rounding function choice is irrelevant for this data because the pre-rounded values never have significant digits beyond the integer. | BRD BR-7 |
| W6 | Double-precision arithmetic for risk score: `double riskScore = (complianceCount * 30.0) + (wireCount * 20.0) + (highTxnCount * 10.0)` | SQLite uses 64-bit IEEE 754 floating point for REAL arithmetic, which is equivalent to C# `double`. The SQL expression `(compliance_events * 30.0) + (wire_count * 20.0) + (high_txn_count * 10.0)` produces identical results. Since all inputs are integers and weights are exact powers of 10, there are no epsilon differences in practice (BR-8). | BRD BR-6, BR-8 |
| W9 | Overwrite mode — each run replaces entire file, multi-day runs retain only last day's output | V2 uses `writeMode: Overwrite` matching V1. Comment: V1 uses Overwrite — prior days' data is lost on each run. | BRD Write Mode Implications |

### Code-Quality Anti-Patterns (Eliminated)

| ID | V1 Problem | V2 Fix | Trace |
|----|-----------|--------|-------|
| AP1 | `transactions` table sourced but effectively never contributes to output (account_id/customer_id mismatch + threshold never met) | Removed from V2 DataSourcing entirely. `high_txn_count` hardcoded as 0 in SQL with explanatory comment. | BRD BR-4, BR-5, BR-12 |
| AP3 | External module (`CustomerComplianceRiskCalculator.cs`) used for logic fully expressible in SQL | Replaced with single Transformation module containing LEFT JOINs + COUNT + arithmetic | BRD full scope |
| AP4 | `event_id`, `event_type`, `status` sourced from compliance_events but unused in output (only customer_id matters for COUNT); `wire_id`, `amount`, `direction` sourced from wire_transfers but unused (only customer_id matters for COUNT) | V2 sources only `customer_id` from compliance_events and wire_transfers | BRD Output Schema |
| AP6 | Row-by-row `foreach` with Dictionary accumulation for counting compliance events, wires, and high-value transactions | Replaced with SQL `LEFT JOIN (SELECT ... COUNT(*) ... GROUP BY ...)` subqueries | BRD BR-2, BR-3 |
| AP7 | Magic values: `5000` threshold, `30.0`/`20.0`/`10.0` weights hardcoded without documentation | Values remain the same in SQL (output must match), but documented with SQL comments explaining business meaning. The 5000 threshold is moot (transactions table removed) but noted in FSD for traceability. | BRD BR-4, BR-6 |

---

## 4. Output Schema

| Column | Type | Source | Transformation | Trace |
|--------|------|--------|---------------|-------|
| customer_id | INTEGER | customers.id | `CAST(c.id AS INTEGER)` | BRD BR-1, Output Schema |
| first_name | TEXT | customers.first_name | `COALESCE(c.first_name, '')` — NULL coalesced to empty string | BRD BR-10 |
| last_name | TEXT | customers.last_name | `COALESCE(c.last_name, '')` — NULL coalesced to empty string | BRD BR-10 |
| compliance_events | INTEGER | Computed | COUNT of all compliance_events per customer_id (no filter on event_type or status) | BRD BR-2 |
| wire_count | INTEGER | Computed | COUNT of all wire_transfers per customer_id (no filter on direction/amount/status) | BRD BR-3 |
| high_txn_count | INTEGER | Literal 0 | Hardcoded to 0. V1 bug: account_id/customer_id mismatch means this is always 0. See BR-4, BR-5, BR-12. | BRD BR-4, BR-5, BR-12 |
| risk_score | REAL | Computed | `ROUND((compliance_events * 30.0) + (wire_count * 20.0) + (0 * 10.0), 2)` — double arithmetic with ROUND to 2 places (see W5 note: rounding mode difference is moot since values are always exact integers) | BRD BR-6, BR-7, BR-8 |
| as_of | TEXT | customers.as_of | Direct passthrough from customer row's as_of field | BRD BR-11 |

**Row count per run:** One row per customer in the customers DataFrame for the effective date. Customers with zero compliance events or zero wires get counts of 0 and risk_score of 0.0. If customers DataFrame is empty for the effective date, zero rows are produced.

---

## 5. SQL Design

### Transformation SQL

```sql
-- V2 CustomerComplianceRisk: Tier 1 SQL replacement for V1 External module
-- Produces one row per customer with compliance event count, wire count,
-- and a weighted risk score.
--
-- V1 bug replicated (BR-4/BR-5): high_txn_count is always 0 because V1
-- keys transaction counts by account_id but looks them up by customer_id.
-- Customer IDs (1001+) never match account IDs (3001+).
-- Additionally, max transaction amount is 4200 < 5000 threshold (BR-12).
-- We hardcode 0 rather than sourcing transactions at all.
--
-- Risk score weights: compliance_events * 30.0, wire_count * 20.0,
-- high_txn_count * 10.0 (always 0 contribution)

SELECT
    CAST(c.id AS INTEGER) AS customer_id,
    COALESCE(c.first_name, '') AS first_name,
    COALESCE(c.last_name, '') AS last_name,
    COALESCE(ce.event_count, 0) AS compliance_events,
    COALESCE(wt.wire_count, 0) AS wire_count,
    0 AS high_txn_count,
    ROUND(
        (COALESCE(ce.event_count, 0) * 30.0)
        + (COALESCE(wt.wire_count, 0) * 20.0)
        + (0 * 10.0),
    2) AS risk_score,
    c.as_of
FROM customers c
LEFT JOIN (
    SELECT customer_id, COUNT(*) AS event_count
    FROM compliance_events
    GROUP BY customer_id
) ce ON c.id = ce.customer_id
LEFT JOIN (
    SELECT customer_id, COUNT(*) AS wire_count
    FROM wire_transfers
    GROUP BY customer_id
) wt ON c.id = wt.customer_id
```

**Design notes:**

1. **One row per customer (BR-1):** The `customers` table drives the output via the FROM clause. LEFT JOINs ensure every customer appears even if they have zero compliance events or wires. `COALESCE(..., 0)` handles NULL counts from unmatched LEFT JOINs, replicating V1's `GetValueOrDefault(customerId, 0)`.

2. **All compliance events counted (BR-2):** The subquery `SELECT customer_id, COUNT(*) FROM compliance_events GROUP BY customer_id` counts ALL events per customer — no filter on `event_type` or `status`, matching V1 behavior.

3. **All wires counted (BR-3):** The subquery `SELECT customer_id, COUNT(*) FROM wire_transfers GROUP BY customer_id` counts ALL wires per customer — no filter on `direction`, `amount`, or `status`, matching V1 behavior.

4. **High transaction count always 0 (BR-4, BR-5, BR-12):** Rather than sourcing `transactions`, performing the > 5000 filter, keying by `account_id`, and then getting 0 on every `customer_id` lookup (as V1 does), V2 hardcodes `0 AS high_txn_count`. This is explicitly documented as replicating the V1 bug's net effect. The `(0 * 10.0)` term in the risk score formula is retained for formula clarity even though it contributes nothing.

5. **Risk score formula (BR-6, W6):** `ROUND((COALESCE(ce.event_count, 0) * 30.0) + (COALESCE(wt.wire_count, 0) * 20.0) + (0 * 10.0), 2)` matches V1's `double riskScore = (complianceCount * 30.0) + (wireCount * 20.0) + (highTxnCount * 10.0)`. SQLite uses IEEE 754 double-precision floats for REAL arithmetic, which is identical to C# `double`. The `30.0`/`20.0`/`10.0` literals force REAL arithmetic in SQLite, matching V1's double behavior.

6. **Rounding (BR-7, W5):** SQLite ROUND() uses round-half-away-from-zero. V1 uses C# Math.Round() which defaults to MidpointRounding.ToEven (banker's rounding). These will diverge at exact midpoints (e.g., 2.5 rounds to 2 in C# but 3 in SQLite). However, since all results are exact integers (BR-8 — integer counts multiplied by integer weights 30/20/10), rounding has no practical effect and no midpoint values can occur. ROUND is included for behavioral equivalence; the difference in rounding semantics is irrelevant for this data.

7. **NULL coalescing (BR-10):** `COALESCE(c.first_name, '')` and `COALESCE(c.last_name, '')` replicate V1's `?.ToString() ?? ""` behavior.

8. **as_of from customer row (BR-11):** `c.as_of` comes directly from the customers table, matching V1's `custRow["as_of"]`. This is NOT `__maxEffectiveDate` — it's the customer row's own `as_of` field. For single-day runs these are the same value, but the FSD traces to the correct source.

9. **Empty input handling (BR-9):** If the `customers` DataFrame is empty for a given effective date, DataSourcing returns an empty DataFrame. The Transformation module's `RegisterTable` method skips registration for empty DataFrames (`Transformation.cs:46-47`). The SQL references `customers` as the driving table — if it doesn't exist as a SQLite table, the query will error. However, this only happens if there are zero customer records for the effective date. The data lake has daily snapshots for the full Oct-Dec 2024 range with customer data present on every date. If this becomes an issue during Phase D, the mitigation is to escalate to Tier 2 with a minimal guard External module.

10. **Row ordering:** V1 iterates `customers.Rows` in DataFrame order (which is the order DataSourcing returns them — `ORDER BY as_of` from PostgreSQL). V2's SQL has no explicit ORDER BY, which means results follow SQLite's internal ordering from the `customers` table scan. Since DataSourcing loads customers with `ORDER BY as_of`, and within a single-day run all customers share the same `as_of`, the internal row order depends on insertion order into the SQLite in-memory table, which matches DataSourcing return order. If row order causes Proofmark comparison failure, we can add `ORDER BY c.id` to the SQL to match V1's effective ordering (customer iteration order, which follows DB retrieval order keyed on `id`).

11. **Cross-as_of aggregation:** The subqueries aggregate across ALL rows in `compliance_events` and `wire_transfers` without filtering by `as_of`. This is correct — V1's row-by-row dictionaries also aggregate across all rows in the DataFrame (which includes all `as_of` dates in the effective date range). For single-day runs (min = max effective date), there's only one `as_of` value, so this is equivalent. For multi-day runs, V1 would aggregate across all dates and then join against each customer row including its `as_of` — but since the job uses Overwrite mode, only the last day's output survives, making multi-day aggregation behavior moot.

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "CustomerComplianceRiskV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "compliance_events",
      "schema": "datalake",
      "table": "compliance_events",
      "columns": ["customer_id"]
    },
    {
      "type": "DataSourcing",
      "resultName": "wire_transfers",
      "schema": "datalake",
      "table": "wire_transfers",
      "columns": ["customer_id"]
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
      "sql": "SELECT CAST(c.id AS INTEGER) AS customer_id, COALESCE(c.first_name, '') AS first_name, COALESCE(c.last_name, '') AS last_name, COALESCE(ce.event_count, 0) AS compliance_events, COALESCE(wt.wire_count, 0) AS wire_count, 0 AS high_txn_count, ROUND((COALESCE(ce.event_count, 0) * 30.0) + (COALESCE(wt.wire_count, 0) * 20.0) + (0 * 10.0), 2) AS risk_score, c.as_of FROM customers c LEFT JOIN (SELECT customer_id, COUNT(*) AS event_count FROM compliance_events GROUP BY customer_id) ce ON c.id = ce.customer_id LEFT JOIN (SELECT customer_id, COUNT(*) AS wire_count FROM wire_transfers GROUP BY customer_id) wt ON c.id = wt.customer_id"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/customer_compliance_risk.csv",
      "includeHeader": true,
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

**Config changes from V1:**
- Removed `transactions` DataSourcing entirely (AP1: dead-end source due to account_id/customer_id mismatch bug)
- Removed unused columns from `compliance_events`: `event_id`, `event_type`, `status` (AP4: only `customer_id` needed for COUNT)
- Removed unused columns from `wire_transfers`: `wire_id`, `amount`, `direction` (AP4: only `customer_id` needed for COUNT)
- Replaced External module with Transformation (AP3: unnecessary External, AP6: row-by-row iteration)
- `as_of` is NOT listed in columns for any DataSourcing — the framework auto-appends it (see `DataSourcing.cs:69`)
- Output path changed to `Output/double_secret_curated/customer_compliance_risk.csv`
- All writer params preserved exactly: `includeHeader: true`, `writeMode: "Overwrite"`, `lineEnding: "LF"`, no `trailerFormat` (V1 has none)

---

## 7. Writer Configuration

| Parameter | V1 Value | V2 Value | Match? |
|-----------|----------|----------|--------|
| Writer type | CsvFileWriter | CsvFileWriter | Yes |
| source | "output" | "output" | Yes |
| outputFile | `Output/curated/customer_compliance_risk.csv` | `Output/double_secret_curated/customer_compliance_risk.csv` | Path change only (required) |
| includeHeader | true | true | Yes |
| trailerFormat | (none) | (none) | Yes |
| writeMode | Overwrite | Overwrite | Yes |
| lineEnding | LF | LF | Yes |

**Trailer behavior:** No trailer. V1 has no `trailerFormat` in its config, V2 matches.

**Write mode (Overwrite, W9):** Each run replaces the entire file. Multi-day auto-advance runs will only retain the last effective date's output. This matches V1 behavior. V1 uses Overwrite — prior days' data is lost on each run.

---

## 8. Proofmark Config Design

```yaml
comparison_target: "customer_compliance_risk"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

**Design rationale:**
- **reader: csv** — V1 and V2 both use CsvFileWriter
- **header_rows: 1** — `includeHeader: true` in both V1 and V2
- **trailer_rows: 0** — No `trailerFormat` in V1 or V2 config, so no trailer rows
- **threshold: 100.0** — All output columns are deterministic. Risk score uses double arithmetic but with integer inputs and integer weights, results are always exact integers (BR-8). No floating-point epsilon concerns. 100% match expected.
- **No excluded columns** — All eight output columns (customer_id, first_name, last_name, compliance_events, wire_count, high_txn_count, risk_score, as_of) are deterministic. The BRD identifies zero non-deterministic fields.
- **No fuzzy columns** — risk_score is the only computed floating-point column. Since all inputs are integers multiplied by integer weights (30, 20, 10), the results are exact integers cast to double/REAL. No epsilon differences between C# `double` and SQLite REAL (both IEEE 754 64-bit). W5 (rounding mode divergence: SQLite uses round-half-away-from-zero vs C#'s banker's rounding) and W6 (double epsilon) are technically applicable but have no practical effect for this data — all pre-rounded values are exact integers, so no midpoints are ever encountered (BR-8).

**Row ordering concern:** V1 iterates customers in DataFrame order (DataSourcing retrieval order). V2's SQL follows SQLite's internal order from the `customers` table scan. Starting with strict config; if row order causes comparison failure during Phase D, we will add `ORDER BY customer_id` to the SQL.

---

## 9. Traceability Matrix

| BRD Requirement | FSD Section | V2 Implementation |
|----------------|-------------|-------------------|
| BR-1: One row per customer | SQL Design note #1 | `FROM customers c LEFT JOIN ...` ensures one output row per customer |
| BR-2: All compliance events counted (no filter) | SQL Design note #2, Anti-Pattern Analysis | Subquery `COUNT(*)` with no WHERE filter on event_type/status |
| BR-3: All wires counted (no filter) | SQL Design note #3, Anti-Pattern Analysis | Subquery `COUNT(*)` with no WHERE filter on direction/amount/status |
| BR-4: High txn keyed by account_id (bug) | SQL Design note #4, Anti-Pattern Analysis (AP1) | Transactions table removed; `high_txn_count` hardcoded as 0 |
| BR-5: account_id/customer_id mismatch → always 0 | SQL Design note #4 | Same as BR-4: net effect replicated by hardcoding 0 |
| BR-6: Risk score formula (double arithmetic) | SQL Design note #5, Anti-Pattern Analysis (W6) | `ROUND((... * 30.0) + (... * 20.0) + (0 * 10.0), 2)` — SQLite REAL = IEEE 754 double |
| BR-7: Banker's rounding | SQL Design note #6, Anti-Pattern Analysis (W5) | SQLite `ROUND()` uses round-half-away-from-zero (differs from V1's banker's rounding), but all values are exact integers so the difference is moot |
| BR-8: Effective zero high_txn_count → integer results | SQL Design note #4, #6 | Hardcoded 0 means risk_score is always integer-valued; rounding is no-op |
| BR-9: Empty input guard | SQL Design note #9 | Empty customers DataFrame → empty SQLite table → zero output rows (or table-not-found; see risk register) |
| BR-10: NULL coalescing for first_name/last_name | SQL Design note #7 | `COALESCE(c.first_name, '')`, `COALESCE(c.last_name, '')` |
| BR-11: as_of from customer row (not __maxEffectiveDate) | SQL Design note #8 | `c.as_of` selected directly from customers table |
| BR-12: No transactions exceed 5000 threshold | SQL Design note #4 | Transactions table removed entirely; threshold is moot |

---

## 10. External Module Design

**Not applicable.** This job is Tier 1 — no External module needed. The V1 External module (`CustomerComplianceRiskCalculator.cs`) is fully replaced by the SQL Transformation.

The V1 External module performed:
1. Row-by-row counting of compliance events by customer_id (AP6 → SQL GROUP BY)
2. Row-by-row counting of wires by customer_id (AP6 → SQL GROUP BY)
3. Row-by-row counting of high-value transactions by account_id with > 5000 filter (AP1 → removed, result always 0)
4. Iteration over customers with dictionary lookups and risk score computation (AP6 → SQL LEFT JOIN + arithmetic)
5. NULL coalescing for first_name/last_name (→ SQL COALESCE)
6. Double arithmetic with rounding to 2 dp (W5/W6 → SQLite REAL + ROUND; rounding mode difference is moot since values are exact integers)

All six operations are expressible in a single SQL query. No procedural logic, no conditional branching, no external state access. Tier 1 is the correct and complete solution.

---

## Appendix: Risk Register

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Empty customers DataFrame causes SQLite table-not-found error | LOW (data lake has daily snapshots for full Oct-Dec 2024 range with customer data on every date) | HIGH (job would fail, not produce output) | Monitor during Phase D. If triggered, escalate to Tier 2 with a minimal guard External module that produces an empty output DataFrame when customers is empty. |
| Row order mismatch between V1 (DataFrame iteration order) and V2 (SQLite internal order) | MEDIUM (V1 order depends on DataSourcing retrieval order; V2 depends on SQLite table scan order, which should match insertion order) | LOW (Proofmark likely does set-based comparison) | Start with strict Proofmark config. If row order causes failure, add `ORDER BY customer_id` to SQL. |
| SQLite type coercion for risk_score differs from C# double | LOW (integer inputs × integer weights produce exact double values; no epsilon concerns) | LOW (would only affect decimal places, and values are always integers) | Verified: BR-8 confirms all risk scores are integer-valued. No precision concern. |
| CAST(c.id AS INTEGER) behaves differently in SQLite vs V1's Convert.ToInt32 | LOW (customer IDs are already integers in the data; CAST is defensive) | LOW (would cause value mismatch if IDs were non-integer, but they aren't) | If comparison fails on customer_id type, remove CAST and let SQLite infer type from source column. |
| Compliance events or wire transfers empty for a date while customers exist | LOW-MEDIUM (possible for some effective dates) | NONE (LEFT JOIN handles this correctly — COALESCE gives 0) | SQL design already handles this via LEFT JOIN + COALESCE. |
