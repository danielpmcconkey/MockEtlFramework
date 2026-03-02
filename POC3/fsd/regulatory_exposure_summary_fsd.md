# RegulatoryExposureSummary — Functional Specification Document

## 1. Job Summary

RegulatoryExposureSummaryV2 computes a per-customer regulatory exposure score by aggregating compliance event counts, wire transfer counts, account counts, and total account balances, then applying a weighted formula `(compliance_events * 30) + (wire_count * 20) + (total_balance / 10000)` using decimal arithmetic with banker's rounding. The job includes weekend fallback logic that shifts Saturday/Sunday effective dates back to Friday for the customer filter and output `as_of` column, with a secondary fallback to all customer rows if no customers match the target date. Output is one Parquet file per run in Overwrite mode.

**Traces to:** BRD BR-1 through BR-11

---

## 2. V2 Module Chain

**Tier:** Tier 2 — Framework + Minimal External (`DataSourcing -> Transformation (SQL) -> External (minimal) -> ParquetFileWriter`)

**Justification:** V1 uses an External module (`RegulatoryExposureCalculator.cs`) to perform LEFT JOIN-style aggregations with COUNT and SUM, a weighted formula, NULL coalescing, and a weekend-fallback customer filter. Most of this logic maps directly to SQL. However, two operations cannot be reliably replicated in SQLite:

1. **Decimal arithmetic (BR-5):** V1 explicitly uses `decimal` literals (`30.0m`, `20.0m`, `10000.0m`) for the exposure score formula and `decimal` accumulation for `total_balance`. SQLite's REAL type is IEEE 754 double, not decimal. The division `totalBalance / 10000.0m` can produce values where double and decimal representations diverge after rounding (e.g., a total_balance of 123456.78m / 10000.0m = 12.345678m, which rounds differently under double vs decimal for certain values). Byte-identical output requires decimal precision.

2. **Banker's rounding (BR-5, BR-6):** V1 uses `Math.Round(decimal, 2)` which defaults to `MidpointRounding.ToEven`. SQLite's `ROUND()` operates on double and its rounding mode behavior is implementation-dependent. Since the exposure score involves a division by 10000 (producing arbitrary decimal values, unlike the integer-only risk_score in CustomerComplianceRisk), rounding mode differences can produce different output.

The V2 design uses SQL for all aggregation and joining (eliminating AP6), then a minimal External module that receives the pre-aggregated DataFrame and applies only the decimal arithmetic, banker's rounding, and weekend-fallback date assignment. This keeps the External's scope to the absolute minimum that SQLite cannot handle.

| Step | Module | Purpose |
|------|--------|---------|
| 1 | DataSourcing | Pull `compliance_events` (customer_id only) from `datalake` |
| 2 | DataSourcing | Pull `wire_transfers` (customer_id only) from `datalake` |
| 3 | DataSourcing | Pull `accounts` (customer_id, current_balance) from `datalake` |
| 4 | DataSourcing | Pull `customers` (id, first_name, last_name) from `datalake` |
| 5 | Transformation | SQL: weekend fallback, customer date filter with all-rows fallback, LEFT JOIN aggregations for counts and raw balance sum, NULL coalescing |
| 6 | External (minimal) | Decimal arithmetic: re-round `total_balance` to 2dp with banker's rounding, compute `exposure_score` with decimal precision and banker's rounding, set `as_of` to target date |
| 7 | ParquetFileWriter | Write output to `Output/double_secret_curated/regulatory_exposure_summary/` |

**Modules removed from V1:**
- External module `RegulatoryExposureCalculator` — AP3 partially eliminated. The bulk of the logic (aggregation, joining, filtering, NULL coalescing) moves to SQL. Only the decimal arithmetic and rounding remain in the External module.
- V1's row-by-row Dictionary accumulation (AP6) is fully replaced by SQL GROUP BY.

**Columns removed from V1 DataSourcing configs (AP4):**
- `compliance_events`: removed `event_id`, `event_type`, `status` — only `customer_id` is needed for COUNT aggregation
- `wire_transfers`: removed `wire_id`, `amount`, `direction` — only `customer_id` is needed for COUNT aggregation
- `accounts`: removed `account_id` — only `customer_id` and `current_balance` are needed

---

## 3. DataSourcing Config

| # | resultName | schema | table | columns | Effective Dates | Trace |
|---|-----------|--------|-------|---------|----------------|-------|
| 1 | compliance_events | datalake | compliance_events | customer_id | Injected by executor (`__minEffectiveDate`, `__maxEffectiveDate`) | BRD Source Tables |
| 2 | wire_transfers | datalake | wire_transfers | customer_id | Injected by executor | BRD Source Tables |
| 3 | accounts | datalake | accounts | customer_id, current_balance | Injected by executor | BRD Source Tables |
| 4 | customers | datalake | customers | id, first_name, last_name | Injected by executor | BRD Source Tables |

**Notes:**
- `as_of` is NOT listed in any columns array — the framework auto-appends it (see `DataSourcing.cs:69`).
- Effective dates are not hardcoded in the config — they are injected by the executor at runtime via shared state keys `__minEffectiveDate` / `__maxEffectiveDate`.
- V1 sourced `event_id`, `event_type`, `status` from compliance_events; `wire_id`, `amount`, `direction` from wire_transfers; and `account_id` from accounts. None of these are used in the output computation (AP4). V2 removes them.

---

## 4. Transformation SQL

```sql
-- V2 RegulatoryExposureSummary: Tier 2 SQL component
-- Performs aggregation, joining, customer date filtering (with weekend fallback),
-- and NULL coalescing. Decimal arithmetic deferred to External module.
--
-- Weekend fallback (W2/BR-1): Saturday -> Friday (-1 day), Sunday -> Friday (-2 days)
-- Customer filter (BR-2): Filter to target date, fall back to ALL rows if none match
-- Aggregations (BR-3): compliance events, wires, accounts counted/summed across
--   ALL as_of dates in the DataFrame (no date filter within aggregation)
-- Balance sum is raw (not yet rounded) — rounding deferred to External module

WITH effective AS (
    -- Derive effective date from customer data (all rows share same as_of in single-day run)
    SELECT MAX(as_of) AS eff_date FROM customers
),
target AS (
    -- Weekend fallback: Saturday(-1) and Sunday(-2) shift to Friday
    SELECT CASE CAST(strftime('%w', eff_date) AS INTEGER)
        WHEN 6 THEN date(eff_date, '-1 day')
        WHEN 0 THEN date(eff_date, '-2 days')
        ELSE eff_date
    END AS target_date
    FROM effective
),
date_filtered_customers AS (
    -- BR-2: Filter customers to target date; fall back to ALL if none match
    SELECT id, first_name, last_name, as_of
    FROM customers
    WHERE as_of = (SELECT target_date FROM target)
),
fallback_customers AS (
    -- If date_filtered_customers is empty, use all customer rows
    SELECT id, first_name, last_name, as_of
    FROM date_filtered_customers
    WHERE (SELECT COUNT(*) FROM date_filtered_customers) > 0
    UNION ALL
    SELECT id, first_name, last_name, as_of
    FROM customers
    WHERE (SELECT COUNT(*) FROM date_filtered_customers) = 0
),
comp_agg AS (
    -- BR-3: Count ALL compliance events per customer (no date filter)
    SELECT customer_id, COUNT(*) AS compliance_event_count
    FROM compliance_events
    GROUP BY customer_id
),
wire_agg AS (
    -- BR-3: Count ALL wire transfers per customer (no date filter)
    SELECT customer_id, COUNT(*) AS wire_transfer_count
    FROM wire_transfers
    GROUP BY customer_id
),
acct_agg AS (
    -- BR-3, BR-7: Count ALL accounts and SUM balance per customer (no date filter)
    -- Balance sum is raw here; decimal rounding applied in External module
    SELECT customer_id,
           COUNT(*) AS account_count,
           SUM(current_balance) AS raw_total_balance
    FROM accounts
    GROUP BY customer_id
)
SELECT
    CAST(fc.id AS INTEGER) AS customer_id,
    COALESCE(fc.first_name, '') AS first_name,
    COALESCE(fc.last_name, '') AS last_name,
    COALESCE(ca.account_count, 0) AS account_count,
    COALESCE(ca.raw_total_balance, 0.0) AS raw_total_balance,
    COALESCE(co.compliance_event_count, 0) AS compliance_events,
    COALESCE(wa.wire_transfer_count, 0) AS wire_count,
    (SELECT target_date FROM target) AS target_date
FROM fallback_customers fc
LEFT JOIN comp_agg co ON fc.id = co.customer_id
LEFT JOIN wire_agg wa ON fc.id = wa.customer_id
LEFT JOIN acct_agg ca ON fc.id = ca.customer_id
```

**Design notes:**

1. **Weekend fallback in SQL (BR-1, W2):** The `effective` CTE derives the effective date from the `customers` table (all rows share the same `as_of` in a single-day run). The `target` CTE applies the weekend shift using `strftime('%w', eff_date)`: Saturday (6) shifts -1 day, Sunday (0) shifts -2 days. This replicates V1's `DayOfWeek.Saturday => AddDays(-1)`, `DayOfWeek.Sunday => AddDays(-2)`.

2. **Customer filter with fallback (BR-2):** The `date_filtered_customers` CTE filters customers to `as_of = target_date`. Since DataSourcing only pulls the effective date's data (not Friday's data on a Saturday run), this filter will find no matches on weekends. The `fallback_customers` CTE implements the fallback: if `date_filtered_customers` is empty, all customer rows are used. This replicates V1's `targetCustomers.Count == 0` fallback at lines 39-43.

3. **Unfiltered aggregations (BR-3):** The `comp_agg`, `wire_agg`, and `acct_agg` CTEs aggregate across ALL rows in their respective DataFrames with no date filter, matching V1 behavior (lines 46-89).

4. **Raw balance sum:** The `raw_total_balance` column is the SUM of `current_balance` from SQLite REAL arithmetic. This is a pre-rounded value passed to the External module, which re-computes it as decimal if needed. Note: since DataSourcing provides `current_balance` as C# `decimal`, Transformation stores it as SQLite REAL (double). The SUM in REAL may differ at the epsilon level from a decimal SUM. The External module receives this value and uses it as-is but applies `Math.Round(totalBalance, 2)` with banker's rounding. If this produces different output than V1, the External module may need to re-query the raw account data from shared state and re-sum in decimal. This is flagged in the Risk Register.

5. **target_date passthrough:** The `target_date` value is included in the SQL output so the External module can use it for the output `as_of` column (BR-10) without needing to re-derive the weekend fallback logic.

6. **NULL coalescing (BR-11):** `COALESCE(fc.first_name, '')` and `COALESCE(fc.last_name, '')` replicate V1's `?.ToString() ?? ""`.

7. **Empty input (BR-9):** If the customers DataFrame is empty, the Transformation module's `RegisterTable` method skips registration for empty DataFrames. The SQL references `customers` as its driving CTE source — if the table doesn't exist, the query will error. The External module includes an empty-input guard (matching V1 lines 22-26) that checks for this condition before the SQL runs. See Risk Register.

---

## 5. External Module Design (Minimal — Tier 2)

**Module name:** `ExternalModules.RegulatoryExposureSummaryV2Processor`

**Scope:** This External module does ONLY three things:
1. Apply `Math.Round(total_balance, 2)` with banker's rounding (BR-6, W5)
2. Compute `exposure_score` using decimal arithmetic with banker's rounding (BR-4, BR-5, W5)
3. Set `as_of` to the `target_date` value from the SQL output (BR-10)

**It does NOT:**
- Perform any aggregation (done in SQL)
- Perform any joining (done in SQL)
- Filter customers (done in SQL)
- Implement weekend fallback date logic (done in SQL)
- Coalesce NULL values (done in SQL)

**Pseudocode:**
```csharp
public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
{
    var intermediate = sharedState["output"] as DataFrame;

    // BR-9: Empty input guard
    if (intermediate == null || intermediate.Count == 0)
    {
        sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
        return sharedState;
    }

    var outputRows = new List<Row>();
    foreach (var row in intermediate.Rows)
    {
        var accountCount = Convert.ToInt32(row["account_count"]);
        var rawTotalBalance = Convert.ToDecimal(row["raw_total_balance"]);
        var complianceEvents = Convert.ToInt32(row["compliance_events"]);
        var wireCount = Convert.ToInt32(row["wire_count"]);
        var targetDate = row["target_date"]?.ToString();

        // BR-6, W5: Banker's rounding on total_balance
        var totalBalance = Math.Round(rawTotalBalance, 2);

        // BR-4, BR-5, W5: Exposure score with decimal arithmetic + banker's rounding
        // Weights: compliance_events * 30, wire_count * 20, total_balance / 10000
        const decimal ComplianceWeight = 30.0m;
        const decimal WireWeight = 20.0m;
        const decimal BalanceDivisor = 10000.0m;

        var exposureScore = Math.Round(
            (complianceEvents * ComplianceWeight)
            + (wireCount * WireWeight)
            + (totalBalance / BalanceDivisor),
            2);
        // W5: Math.Round(decimal, 2) defaults to MidpointRounding.ToEven (banker's rounding)

        outputRows.Add(new Row(new Dictionary<string, object?>
        {
            ["customer_id"] = Convert.ToInt32(row["customer_id"]),
            ["first_name"] = row["first_name"]?.ToString() ?? "",
            ["last_name"] = row["last_name"]?.ToString() ?? "",
            ["account_count"] = accountCount,
            ["total_balance"] = totalBalance,
            ["compliance_events"] = complianceEvents,
            ["wire_count"] = wireCount,
            ["exposure_score"] = exposureScore,
            // BR-10: as_of = target date (after weekend fallback)
            ["as_of"] = DateOnly.Parse(targetDate!)
        }));
    }

    sharedState["output"] = new DataFrame(outputRows, OutputColumns);
    return sharedState;
}
```

**Named constants (AP7 elimination):**
- `ComplianceWeight = 30.0m` — weight for compliance event count in exposure formula
- `WireWeight = 20.0m` — weight for wire transfer count in exposure formula
- `BalanceDivisor = 10000.0m` — divisor for total balance in exposure formula

---

## 6. Writer Config

| Parameter | V1 Value | V2 Value | Match? |
|-----------|----------|----------|--------|
| Writer type | ParquetFileWriter | ParquetFileWriter | Yes |
| source | "output" | "output" | Yes |
| outputDirectory | `Output/curated/regulatory_exposure_summary/` | `Output/double_secret_curated/regulatory_exposure_summary/` | Path change only (required) |
| numParts | 1 | 1 | Yes |
| writeMode | Overwrite | Overwrite | Yes |

**Write mode (Overwrite, W9 — informational only):** Each run replaces all part files in the output directory. Multi-day auto-advance runs retain only the last effective date's output. This matches V1 behavior. This is Overwrite where Overwrite is the correct mode for a single-day snapshot, so W9 does not technically apply (W9 describes wrong writeMode usage). Documented for completeness.

---

## 7. Wrinkle Replication

| W-Code | V1 Behavior | V2 Replication | Trace |
|--------|------------|----------------|-------|
| W2 | Weekend fallback: Saturday `AddDays(-1)`, Sunday `AddDays(-2)` to reach Friday. Applied to customer date filter and output `as_of`. | Replicated in SQL via `strftime('%w', eff_date)` with CASE expression shifting Saturday (6) by -1 day and Sunday (0) by -2 days. The computed `target_date` is passed to the External module for the output `as_of` column. Comment: `// W2: Weekend fallback — use Friday's data on Sat/Sun`. | BRD BR-1, [RegulatoryExposureCalculator.cs:29-32] |
| W5 | Banker's rounding via `Math.Round(decimal, 2)` which defaults to `MidpointRounding.ToEven`. Applied to both `total_balance` (line 114) and `exposure_score` (line 106). | Replicated in External module using `Math.Round(value, 2)` on `decimal` values. Banker's rounding is the C# default for `Math.Round(decimal, int)`. Comment: `// W5: Math.Round(decimal, 2) defaults to MidpointRounding.ToEven (banker's rounding)`. | BRD BR-5, BR-6, [RegulatoryExposureCalculator.cs:105-106, 114] |

**Wrinkles considered but not applicable:**
- **W1 (Sunday skip):** V1 does not skip Sundays — it applies weekend fallback (W2) instead.
- **W4 (Integer division):** V1 uses decimal division (`totalBalance / 10000.0m`), not integer division.
- **W6 (Double epsilon):** V1 explicitly uses `decimal` arithmetic, not `double`. No epsilon issues.
- **W7 (Trailer inflated count):** No trailer in Parquet output.
- **W8 (Trailer stale date):** No trailer.
- **W9 (Wrong writeMode):** Overwrite is arguably correct for a single-snapshot job. Not a bug here.
- **W10 (Absurd numParts):** numParts is 1, which is reasonable for this dataset size.
- **W12 (Header every append):** Not applicable — Parquet output, Overwrite mode.

---

## 8. Anti-Pattern Elimination

| AP-Code | V1 Problem | V2 Fix | Trace |
|---------|-----------|--------|-------|
| AP2 | Duplicated logic — exposure score formula is similar to CustomerComplianceRisk's risk_score formula (noted at [RegulatoryExposureCalculator.cs:34]: `// AP2: duplicated logic — re-derives compliance risk similar to job #26`) | **Cannot fix within single job scope.** The two jobs have different formulas (this job includes `total_balance / 10000` and uses decimal arithmetic; CustomerComplianceRisk uses only integer weights and double arithmetic). They are related but not identical computations. Documented per AP2 prescription. | BRD Open Question #1 |
| AP3 | Unnecessary External module — V1 uses a full External module for logic that is mostly SQL-expressible | **Partially eliminated.** Reduced from a full External (Tier 3) to a minimal External (Tier 2). The aggregation, joining, customer filtering, weekend fallback, and NULL coalescing are all moved to SQL Transformation. Only the decimal arithmetic and banker's rounding remain in the External module because SQLite's REAL type (double) cannot guarantee decimal-precision output equivalence. | BRD full scope |
| AP4 | Unused columns sourced: `event_id`, `event_type`, `status` from compliance_events; `wire_id`, `amount`, `direction` from wire_transfers; `account_id` from accounts | **Eliminated.** V2 sources only the columns used in computation: `customer_id` from compliance_events and wire_transfers; `customer_id`, `current_balance` from accounts; `id`, `first_name`, `last_name` from customers. | BRD Source Tables, Output Schema |
| AP6 | Row-by-row iteration — V1 uses `foreach` loops with Dictionary accumulation for counting compliance events (lines 46-56), counting wires (lines 58-69), and computing account count + balance sum (lines 71-89) | **Eliminated.** All aggregation replaced by SQL `GROUP BY` + `COUNT(*)` / `SUM()` subqueries in the Transformation module. The External module iterates the pre-aggregated output rows (one per customer) only for the decimal arithmetic pass — this is O(n_customers), not O(n_events + n_wires + n_accounts) as in V1. | BRD BR-3, BR-7, [RegulatoryExposureCalculator.cs:46-89] |
| AP7 | Magic values — weights `30.0m`, `20.0m`, `10000.0m` used without explanation in the formula | **Eliminated.** V2 External module uses named constants: `ComplianceWeight = 30.0m`, `WireWeight = 20.0m`, `BalanceDivisor = 10000.0m` with descriptive names documenting their business meaning. Output values are unchanged. | BRD BR-4, [RegulatoryExposureCalculator.cs:105-106] |

**Anti-patterns considered but not applicable:**
- **AP1 (Dead-end sourcing):** All four source tables are used in V1 and V2. No dead-end sources.
- **AP5 (Asymmetric NULLs):** Not present — NULL handling is consistent (first_name and last_name both coalesced to "").
- **AP8 (Complex SQL / unused CTEs):** V1 has no SQL; V2's SQL CTEs are all used.
- **AP9 (Misleading names):** "regulatory_exposure_summary" accurately describes the output.
- **AP10 (Over-sourcing dates):** DataSourcing uses executor-injected effective dates, not full-table pulls with WHERE filtering.

---

## 9. Proofmark Config

```yaml
comparison_target: "regulatory_exposure_summary"
reader: parquet
threshold: 100.0
```

**Design rationale:**
- **reader: parquet** — V1 and V2 both use ParquetFileWriter.
- **threshold: 100.0** — All output columns are deterministic. 100% match required.
- **No excluded columns** — The BRD identifies zero non-deterministic fields. All nine output columns (customer_id, first_name, last_name, account_count, total_balance, compliance_events, wire_count, exposure_score, as_of) are fully deterministic.
- **No fuzzy columns** — V1 uses `decimal` arithmetic (not `double`), so there are no floating-point epsilon concerns. The V2 External module replicates the same decimal arithmetic. Both `total_balance` and `exposure_score` should be byte-identical between V1 and V2.

**Row ordering concern:** V1 iterates `targetCustomers` (a list of customer rows filtered from the DataFrame) in DataFrame order. V2's SQL output follows SQLite's internal order from the CTE scan. Since DataSourcing loads customers with `ORDER BY as_of` and all rows share the same `as_of` in a single-day run, the internal order depends on insertion order, which matches DataSourcing retrieval order. If row order causes Proofmark comparison failure during Phase D, we will add `ORDER BY customer_id` to the SQL.

---

## 10. Output Schema

| Column | Type | Source | Transformation | Trace |
|--------|------|--------|---------------|-------|
| customer_id | INTEGER | customers.id | `CAST(c.id AS INTEGER)` in SQL | BRD Output Schema, BR-8 |
| first_name | TEXT | customers.first_name | `COALESCE(c.first_name, '')` in SQL (BR-11) | BRD Output Schema, BR-11 |
| last_name | TEXT | customers.last_name | `COALESCE(c.last_name, '')` in SQL (BR-11) | BRD Output Schema, BR-11 |
| account_count | INTEGER | Computed | `COUNT(*)` of account rows per customer_id (all dates), via SQL | BRD Output Schema, BR-7 |
| total_balance | DECIMAL | Computed | `SUM(current_balance)` per customer_id (all dates) via SQL, then `Math.Round(value, 2)` with banker's rounding in External module | BRD Output Schema, BR-6 |
| compliance_events | INTEGER | Computed | `COUNT(*)` of compliance_events per customer_id (all dates), via SQL | BRD Output Schema, BR-3 |
| wire_count | INTEGER | Computed | `COUNT(*)` of wire_transfers per customer_id (all dates), via SQL | BRD Output Schema, BR-3 |
| exposure_score | DECIMAL | Computed | `Math.Round((compliance_events * 30.0m) + (wire_count * 20.0m) + (total_balance / 10000.0m), 2)` in External module with decimal arithmetic and banker's rounding | BRD Output Schema, BR-4, BR-5 |
| as_of | DATE | Computed | `target_date` from SQL (effective date after weekend fallback), converted to `DateOnly` in External module | BRD Output Schema, BR-10 |

**Row count per run:** One row per customer in the target-date-filtered customer list (or all customers if fallback triggers). Customers with zero compliance events, wires, or accounts get counts of 0 and exposure_score of 0.00. If customers DataFrame is empty for the effective date, zero rows are produced (BR-9).

---

## 11. V2 Job Config JSON

```json
{
  "jobName": "RegulatoryExposureSummaryV2",
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
      "resultName": "accounts",
      "schema": "datalake",
      "table": "accounts",
      "columns": ["customer_id", "current_balance"]
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
      "sql": "WITH effective AS (SELECT MAX(as_of) AS eff_date FROM customers), target AS (SELECT CASE CAST(strftime('%w', eff_date) AS INTEGER) WHEN 6 THEN date(eff_date, '-1 day') WHEN 0 THEN date(eff_date, '-2 days') ELSE eff_date END AS target_date FROM effective), date_filtered_customers AS (SELECT id, first_name, last_name, as_of FROM customers WHERE as_of = (SELECT target_date FROM target)), fallback_customers AS (SELECT id, first_name, last_name, as_of FROM date_filtered_customers WHERE (SELECT COUNT(*) FROM date_filtered_customers) > 0 UNION ALL SELECT id, first_name, last_name, as_of FROM customers WHERE (SELECT COUNT(*) FROM date_filtered_customers) = 0), comp_agg AS (SELECT customer_id, COUNT(*) AS compliance_event_count FROM compliance_events GROUP BY customer_id), wire_agg AS (SELECT customer_id, COUNT(*) AS wire_transfer_count FROM wire_transfers GROUP BY customer_id), acct_agg AS (SELECT customer_id, COUNT(*) AS account_count, SUM(current_balance) AS raw_total_balance FROM accounts GROUP BY customer_id) SELECT CAST(fc.id AS INTEGER) AS customer_id, COALESCE(fc.first_name, '') AS first_name, COALESCE(fc.last_name, '') AS last_name, COALESCE(ca.account_count, 0) AS account_count, COALESCE(ca.raw_total_balance, 0.0) AS raw_total_balance, COALESCE(co.compliance_event_count, 0) AS compliance_events, COALESCE(wa.wire_transfer_count, 0) AS wire_count, (SELECT target_date FROM target) AS target_date FROM fallback_customers fc LEFT JOIN comp_agg co ON fc.id = co.customer_id LEFT JOIN wire_agg wa ON fc.id = wa.customer_id LEFT JOIN acct_agg ca ON fc.id = ca.customer_id"
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.RegulatoryExposureSummaryV2Processor"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/regulatory_exposure_summary/",
      "numParts": 1,
      "writeMode": "Overwrite"
    }
  ]
}
```

**Config changes from V1:**
- Removed unused columns from `compliance_events`: `event_id`, `event_type`, `status` (AP4)
- Removed unused columns from `wire_transfers`: `wire_id`, `amount`, `direction` (AP4)
- Removed unused columns from `accounts`: `account_id` (AP4)
- Added Transformation module for aggregation/joining/filtering (AP3, AP6 partial elimination)
- Replaced full External module with minimal External for decimal arithmetic only (AP3 reduction)
- Output path changed to `Output/double_secret_curated/regulatory_exposure_summary/`
- All writer params preserved exactly: `numParts: 1`, `writeMode: "Overwrite"` (V1 match)
- `as_of` not listed in any DataSourcing columns — framework auto-appends it

---

## 12. Open Questions

1. **SQLite REAL precision for balance SUM:** The `raw_total_balance` computed in SQL uses SQLite REAL (double) arithmetic for SUM. V1 uses C# `decimal` accumulation. For most practical balance values, the double SUM and decimal SUM should agree to 2 decimal places after rounding. However, if a customer has many accounts with balances that trigger floating-point accumulation errors, the rounded results could diverge. **Mitigation:** If Proofmark detects mismatches on `total_balance` or `exposure_score`, the External module can be extended to re-sum `current_balance` from the raw `accounts` DataFrame (still in shared state) using decimal accumulation. This would bump the External module's scope slightly but maintain output equivalence.

2. **SQLite `strftime('%w')` day-of-week mapping:** SQLite's `strftime('%w')` returns 0 for Sunday and 6 for Saturday, matching the CASE expression. This needs to be verified with the actual Microsoft.Data.Sqlite implementation. If the mapping is different, the weekend fallback CTE would need adjustment.

3. **Empty customers DataFrame and SQLite table registration:** The Transformation module skips registration for empty DataFrames (`Transformation.cs:46-47`). If `customers` is empty, the SQL will fail because the `customers` table won't exist in SQLite. The External module has an empty-input guard, but it runs AFTER the Transformation. **Mitigation:** The data lake has daily snapshots for the full Oct-Dec 2024 range with customer data present on every date. If this becomes an issue during Phase D testing, the solution is to move the empty-input guard into the External module and place it BEFORE the Transformation, or restructure as Tier 3 for the empty-input edge case only.

4. **Row ordering between V1 and V2:** V1 iterates customers in DataFrame order (DataSourcing retrieval order from PostgreSQL). V2's SQL output follows SQLite's scan order. These should match (both follow insertion order), but if Proofmark reports row order mismatches, adding `ORDER BY customer_id` to the SQL would resolve it.

---

## Appendix: Risk Register

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Double vs decimal precision on balance SUM | LOW-MEDIUM (depends on actual data distribution) | MEDIUM (could cause `total_balance` and `exposure_score` mismatches) | If Proofmark fails, extend External module to re-sum balances from raw accounts DataFrame using decimal accumulation |
| Empty customers DataFrame causes Transformation error | LOW (data lake has daily snapshots for full date range) | HIGH (job would fail) | Monitor during Phase D. If triggered, restructure to add pre-Transformation empty guard |
| Row order mismatch between V1 and V2 | MEDIUM | LOW (Proofmark likely does set-based comparison for Parquet) | Add `ORDER BY customer_id` to SQL if comparison fails on row order |
| `strftime('%w')` mapping differs from expected | LOW (standard SQLite behavior) | HIGH (weekend fallback would produce wrong dates) | Verify during Phase D; adjust CASE expression if needed |
| SQLite type coercion changes integer counts to REAL | LOW (COALESCE with 0 should preserve INTEGER type) | LOW (would cause type mismatch in Parquet) | If Parquet type mismatch, adjust COALESCE defaults or add CAST in SQL |
