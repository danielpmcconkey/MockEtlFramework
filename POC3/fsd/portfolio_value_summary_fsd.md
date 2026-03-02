# PortfolioValueSummary — Functional Specification Document

## 1. Job Summary

This job produces a per-customer portfolio value summary by aggregating `holdings` data — computing `total_portfolio_value` (SUM of `current_value`) and `holding_count` (COUNT of holdings rows) for each customer, enriched with customer name from the `customers` table. A weekend fallback mechanism (W2) substitutes Friday's data when the effective date falls on Saturday or Sunday. The output is Parquet with Overwrite mode. V1 sources the `investments` table but never uses it (AP1), and the External module performs row-by-row iteration (AP6) where a SQL GROUP BY + JOIN would suffice.

## 2. V2 Module Chain

**Tier: 2 (Framework + Minimal External)**

```
DataSourcing (holdings) → DataSourcing (customers) → External (PortfolioValueSummaryV2Processor) → ParquetFileWriter
```

| Step | Module Type | Details |
|------|-------------|---------|
| 1 | DataSourcing | Source `datalake.holdings` with columns: `customer_id`, `current_value`. Store as `holdings`. |
| 2 | DataSourcing | Source `datalake.customers` with columns: `id`, `first_name`, `last_name`. Store as `customers`. |
| 3 | External | `PortfolioValueSummaryV2Processor` — Weekend fallback (W2), empty-input guard, date filter, per-customer aggregation with customer name lookup. Produces `output` DataFrame. |
| 4 | ParquetFileWriter | Write `output` to `Output/double_secret_curated/portfolio_value_summary/` with numParts=1, Overwrite mode. |

**Why Tier 2 (not Tier 1):** The weekend fallback (W2) depends on `__maxEffectiveDate` from shared state. The Transformation module's SQL runs inside SQLite, which has no access to shared state scalars — it can only query DataFrames registered as tables. The `__maxEffectiveDate` is a `DateOnly` scalar, not a DataFrame. SQL cannot evaluate `DayOfWeek` to determine Saturday/Sunday and compute the adjusted target date.

Additionally, when DataSourcing returns zero rows (possible if the adjusted target date has no data), `Transformation.RegisterTable` skips empty DataFrames entirely (Transformation.cs:46 — `if (!df.Rows.Any()) return;`). This would cause SQL execution to fail with a "table not found" error.

**Why not Tier 1:** SQL cannot implement the weekend fallback without access to `__maxEffectiveDate`. Even without that, the empty-table registration issue blocks pure SQL on zero-data days.

**Why not Tier 3:** DataSourcing is perfectly capable of sourcing both tables with effective date injection. There is no reason to bypass it.

## 3. DataSourcing Config

### Table 1: holdings

| Property | Value |
|----------|-------|
| resultName | `holdings` |
| schema | `datalake` |
| table | `holdings` |
| columns | `customer_id`, `current_value` |

**Effective date handling:** No explicit `minEffectiveDate`/`maxEffectiveDate` in config. The framework injects `__minEffectiveDate` and `__maxEffectiveDate` into shared state at runtime, and DataSourcing automatically filters `as_of` to that range. The External module further filters to `targetDate` (the weekend-adjusted date) within the returned rows.

**Column rationale:** V1 sources `holding_id`, `investment_id`, `security_id`, `customer_id`, `quantity`, `current_value` but only uses `customer_id` (group key), `current_value` (aggregation), and `as_of` (date filter). The other four columns are AP4 violations and are eliminated. Note: `as_of` is automatically appended by DataSourcing and does not need to be listed in the columns array.

Evidence: [PortfolioValueCalculator.cs:31-33,50-51] — only `as_of`, `customer_id`, and `current_value` are accessed.

### Table 2: customers

| Property | Value |
|----------|-------|
| resultName | `customers` |
| schema | `datalake` |
| table | `customers` |
| columns | `id`, `first_name`, `last_name` |

**Column rationale:** All three columns are used — `id` for the join key, `first_name` and `last_name` for name enrichment. No unused columns.

Evidence: [PortfolioValueCalculator.cs:39-41]

### Table REMOVED: investments

V1 sources `datalake.investments` with columns `investment_id`, `customer_id`, `account_type`, `current_value`, `risk_profile`. The External module never accesses `sharedState["investments"]`. This entire DataSourcing step is eliminated as AP1 (dead-end sourcing).

Evidence: [portfolio_value_summary.json:6-11] sources investments; [PortfolioValueCalculator.cs] has no reference to `sharedState["investments"]`. BRD BR-8 confirms this.

## 4. Transformation SQL

**N/A** — This job uses Tier 2 with an External module instead of a Transformation (SQL) module. The aggregation and join logic is implemented in C# within the External module.

**Rationale:** The weekend fallback (W2) requires access to `__maxEffectiveDate` from shared state, which is not available inside SQLite. Additionally, on days with empty source data after date filtering, `Transformation.RegisterTable` skips empty DataFrames (Transformation.cs:46), which would cause SQL execution to fail.

If weekend fallback were not required and empty-data handling were not an issue, the equivalent SQL would be:

```sql
-- Reference-only: what a Tier 1 SQL would look like
SELECT
    CAST(h.customer_id AS INTEGER) AS customer_id,
    COALESCE(c.first_name, '') AS first_name,
    COALESCE(c.last_name, '') AS last_name,
    ROUND(SUM(CAST(h.current_value AS REAL)), 2) AS total_portfolio_value,
    COUNT(*) AS holding_count,
    h.as_of
FROM holdings h
LEFT JOIN customers c ON h.customer_id = c.id AND h.as_of = c.as_of
WHERE h.as_of = :targetDate
GROUP BY h.customer_id, c.first_name, c.last_name, h.as_of
```

Note: This reference SQL would NOT produce identical output to V1 because (a) it cannot compute `targetDate` from `__maxEffectiveDate` with weekend fallback logic, (b) SQLite `ROUND` uses banker's rounding which may differ from `Math.Round(value, 2)` in edge cases, and (c) row ordering would differ (SQL uses GROUP BY order vs. V1's dictionary insertion order).

### External Module Design

**Class:** `PortfolioValueSummaryV2Processor`
**File:** `ExternalModules/PortfolioValueSummaryV2Processor.cs`
**Interface:** Implements `IExternalStep`

**Pseudocode:**

```
Execute(sharedState):
    1. Define output columns: [customer_id, first_name, last_name,
       total_portfolio_value, holding_count, as_of]

    2. Read __maxEffectiveDate from shared state

    3. // W2: Weekend fallback — use Friday's data on Sat/Sun
       targetDate = maxDate
       If maxDate.DayOfWeek == Saturday: targetDate = maxDate.AddDays(-1)
       Else if maxDate.DayOfWeek == Sunday: targetDate = maxDate.AddDays(-2)

    4. Read "holdings" and "customers" DataFrames from shared state

    5. If holdings is null/empty OR customers is null/empty:
           Set sharedState["output"] = empty DataFrame with output columns
           Return sharedState

    6. Filter holdings to rows where as_of == targetDate
       If no rows remain after filter, return empty DataFrame

    7. Build customer lookup: Dictionary<int, (string firstName, string lastName)>
       For each customer row:
           customerLookup[Convert.ToInt32(id)] = (first_name ?? "", last_name ?? "")

    8. Aggregate holdings per customer_id:
       For each filtered holding row:
           customerId = Convert.ToInt32(row["customer_id"])
           value = Convert.ToDecimal(row["current_value"])
           Accumulate (totalValue + value, holdingCount + 1)

    9. Build output rows:
       For each (customerId, totalValue, holdingCount) in aggregation:
           Look up (firstName, lastName) from customerLookup — default ("", "") if not found
           Create output row:
               customer_id = customerId
               first_name = looked-up firstName
               last_name = looked-up lastName
               total_portfolio_value = Math.Round(totalValue, 2)
               holding_count = holdingCount
               as_of = targetDate

   10. Set sharedState["output"] = new DataFrame(outputRows, outputColumns)
   11. Return sharedState
```

**Key Design Notes:**

1. **Weekend fallback (W2):** The guard checks `DayOfWeek.Saturday` and `DayOfWeek.Sunday` on `__maxEffectiveDate` and adjusts to Friday. Comment: `// W2: Weekend fallback — use Friday's data on Sat/Sun`.

2. **Empty input guard:** V1 returns empty output if either `holdings` or `customers` is null or has zero rows (BR-5). V2 reproduces this exactly. Note: this means if there are holdings but zero customers, the output is empty — this is V1's behavior, not a LEFT JOIN producing rows with empty names.

3. **Decimal arithmetic:** V1 accumulates using `decimal` (Convert.ToDecimal), so W6 (double epsilon) does NOT apply. V2 uses `decimal` throughout.

4. **Rounding:** `Math.Round(totalValue, 2)` uses default `MidpointRounding.ToEven` (banker's rounding). V1 also uses `Math.Round(totalValue, 2)` with default rounding. This is consistent. If W5 were flagged, we'd make it explicit, but since V1 and V2 both use the C# default, the output is identical.

5. **as_of from targetDate (BR-7):** The output `as_of` is set to the weekend-adjusted `targetDate`, NOT `__maxEffectiveDate` directly.

6. **Row ordering (BR-10):** V1 iterates a `Dictionary<int, ...>` which produces rows in insertion order. V2 uses the same pattern. Output row order matches dictionary insertion order (order of first encounter of each `customer_id` in the filtered holdings). This is deterministic given the same input ordering from DataSourcing.

## 5. Writer Config

| Property | Value | Matches V1? |
|----------|-------|-------------|
| type | ParquetFileWriter | Yes |
| source | `output` | Yes |
| outputDirectory | `Output/double_secret_curated/portfolio_value_summary/` | Path changed per V2 convention |
| numParts | `1` | Yes |
| writeMode | `Overwrite` | Yes |

Evidence: [portfolio_value_summary.json:32-38]

## 6. Output Schema

| Column | Source | Type | Transformation | Evidence |
|--------|--------|------|----------------|----------|
| customer_id | holdings.customer_id | int | `Convert.ToInt32` — group key | [PortfolioValueCalculator.cs:50] |
| first_name | customers.first_name | string | Null coalesced to `""` via lookup; `""` if no matching customer | [PortfolioValueCalculator.cs:39,67-68,71] |
| last_name | customers.last_name | string | Null coalesced to `""` via lookup; `""` if no matching customer | [PortfolioValueCalculator.cs:40,67-68,72] |
| total_portfolio_value | SUM(holdings.current_value) per customer_id | decimal | `Math.Round(totalValue, 2)` — banker's rounding (default) | [PortfolioValueCalculator.cs:51,73,75] |
| holding_count | COUNT of holdings rows per customer_id | int | Integer count | [PortfolioValueCalculator.cs:57,74] |
| as_of | targetDate (weekend-adjusted `__maxEffectiveDate`) | DateOnly | Weekend fallback applied | [PortfolioValueCalculator.cs:27-29,75] |

### Column Order

Columns must appear in this exact order: `customer_id, first_name, last_name, total_portfolio_value, holding_count, as_of`

Evidence: [PortfolioValueCalculator.cs:10-14]

## 7. Wrinkle Replication

| W-Code | Applies? | V1 Behavior | V2 Handling |
|--------|----------|-------------|-------------|
| W2 | **YES** | Weekend fallback — uses Friday's data on Saturday (maxDate - 1) and Sunday (maxDate - 2) | External module checks `maxDate.DayOfWeek` and adjusts `targetDate` to Friday. Comment: `// W2: Weekend fallback — use Friday's data on Sat/Sun`. Holdings filtered to `targetDate`, and output `as_of` is set to `targetDate`. |
| W1 | No | Sunday skip — not present in this job (this job uses W2 weekend fallback instead) | N/A |
| W3a-W3c | No | Boundary summary rows — not present | N/A |
| W4 | No | Integer division — not present (uses decimal) | N/A |
| W5 | **MONITOR** | Banker's rounding — V1 uses `Math.Round(totalValue, 2)` which defaults to `MidpointRounding.ToEven`. V2 uses the same call. | V2 uses `Math.Round(totalValue, 2)` identically. If any value hits the midpoint (e.g., x.xx5), both V1 and V2 will produce the same banker's-rounded result. No explicit handling needed — same code, same output. |
| W6 | No | Double epsilon — V1 uses `decimal` for accumulation, not `double` | N/A |
| W7-W12 | No | Not present in this job | N/A |

## 8. Anti-Pattern Elimination

| AP-Code | Applies? | V1 Problem | V2 Fix |
|---------|----------|------------|--------|
| AP1 | **YES** | V1 sources `datalake.investments` (5 columns) — never used by the External module. The `investments` DataFrame sits in shared state untouched. | **Eliminated.** V2 does not include a DataSourcing step for `investments`. Only `holdings` and `customers` are sourced. Evidence: [PortfolioValueCalculator.cs] never accesses `sharedState["investments"]`; BRD BR-8 confirms. |
| AP3 | **PARTIAL** | V1 uses an External module for logic that is largely a GROUP BY + LEFT JOIN expressible in SQL. However, the weekend fallback (W2) requires `__maxEffectiveDate` from shared state, and the empty-table registration issue in Transformation prevents pure Tier 1. | **Partially eliminated.** Moved from Tier 3 (External does everything including data access) to Tier 2 (DataSourcing handles data retrieval, External handles only business logic, ParquetFileWriter handles output). The External module is minimal and focused on the three things SQL cannot do: weekend date adjustment, post-filter aggregation with empty-table safety, and customer name enrichment. |
| AP4 | **YES** | V1 sources `holding_id`, `investment_id`, `security_id`, `quantity` from holdings — none are referenced in the External module. Only `customer_id`, `current_value`, and `as_of` (auto-appended) are used. | **Eliminated.** V2 DataSourcing for `holdings` includes only `customer_id` and `current_value`. The `as_of` column is automatically appended by the DataSourcing module. Evidence: [PortfolioValueCalculator.cs:31-33,50-51] — only these columns are accessed. |
| AP6 | **YES** | V1 uses row-by-row `foreach` loops: one to build a customer Dictionary lookup, one to accumulate per-customer totals, one to build output rows. | **Partially addressed.** The External module uses a Dictionary-based pattern which is idiomatic C# for a hash-join + aggregation. Full SQL elimination is blocked by the W2 requirement. The iteration is clean and well-structured (no nested loops, no repeated lookups). |
| AP2 | No | Not present in this job | N/A |
| AP5 | No | NULL handling is consistent: null names default to `""`, no asymmetry | N/A |
| AP7 | No | No magic values — weekend day checks are standard DayOfWeek enum values | N/A |
| AP8 | No | No SQL with unused CTEs (no SQL module in V1 or V2) | N/A |
| AP9 | No | Job name accurately describes what it produces | N/A |
| AP10 | No | V1 does not use SQL date filtering — the External module handles date filtering programmatically. V2 uses framework-injected effective dates for DataSourcing (correct pattern). | N/A |

## 9. Proofmark Config

```yaml
comparison_target: "portfolio_value_summary"
reader: parquet
threshold: 100.0
```

**Justification:**

- **reader: parquet** — Both V1 and V2 use ParquetFileWriter.
- **threshold: 100.0** — All fields are deterministic. BRD confirms "None identified" for non-deterministic fields.
- **No excluded columns** — No non-deterministic fields exist in the output.
- **No fuzzy columns** — `total_portfolio_value` uses `decimal` arithmetic with `Math.Round(value, 2)`. Both V1 and V2 use the exact same rounding call. No `double`-precision accumulation (W6 does not apply). Parquet preserves decimal precision, so no epsilon differences are expected.

## 10. V2 Job Config JSON

```json
{
  "jobName": "PortfolioValueSummaryV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "holdings",
      "schema": "datalake",
      "table": "holdings",
      "columns": ["customer_id", "current_value"]
    },
    {
      "type": "DataSourcing",
      "resultName": "customers",
      "schema": "datalake",
      "table": "customers",
      "columns": ["id", "first_name", "last_name"]
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.PortfolioValueSummaryV2Processor"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/portfolio_value_summary/",
      "numParts": 1,
      "writeMode": "Overwrite"
    }
  ]
}
```

**Config changes from V1:**

- `jobName`: `PortfolioValueSummary` -> `PortfolioValueSummaryV2`
- `outputDirectory`: `Output/curated/...` -> `Output/double_secret_curated/...`
- **Removed** DataSourcing for `investments` entirely (AP1 — dead-end sourcing, never used)
- DataSourcing `holdings`: removed `holding_id`, `investment_id`, `security_id`, `quantity` (AP4 — unused columns)
- External `typeName`: `...PortfolioValueCalculator` -> `...PortfolioValueSummaryV2Processor`
- All writer config parameters (`numParts`, `writeMode`) preserved exactly from V1.

## 11. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision |
|-----------------|-------------|-----------------|
| BR-1: Weekend fallback (Sat->Fri, Sun->Fri) | Tier Selection, Wrinkle W2, External Module Design | External module adjusts `__maxEffectiveDate` to Friday for weekend dates |
| BR-2: Holdings filtered to targetDate | External Module Design (step 6) | Filter `as_of == targetDate` on holdings rows after weekend adjustment |
| BR-3: Per-customer aggregation (SUM + COUNT) | External Module Design (step 8) | Dictionary-based accumulation of totalValue and holdingCount per customer_id |
| BR-4: total_portfolio_value rounded to 2dp | External Module Design (step 9), Output Schema | `Math.Round(totalValue, 2)` — same as V1 |
| BR-5: Empty output on null/empty input | External Module Design (step 5) | Returns empty DataFrame when holdings or customers is null/empty |
| BR-6: Customer name lookup with empty-string default | External Module Design (steps 7, 9), Output Schema | Dictionary lookup keyed on customers.id; missing customers get ("", "") |
| BR-7: as_of from targetDate, not __maxEffectiveDate | External Module Design (step 9), Output Schema | Output as_of = weekend-adjusted targetDate |
| BR-8: Investments sourced but unused | AP1 Elimination, DataSourcing Config | **Eliminated** — investments DataSourcing removed entirely |
| BR-9: Framework effective date injection | DataSourcing Config | No explicit dates in config; framework injects at runtime |
| BR-10: Row order from dictionary insertion | External Module Design (note 6) | V2 uses same Dictionary iteration pattern for output ordering |

| Anti-Pattern | Disposition |
|--------------|-------------|
| W2 (Weekend fallback) | **Reproduced** with clean guard clause and explicit comment |
| AP1 (Dead-end sourcing — investments) | **Eliminated** — investments DataSourcing removed |
| AP3 (Unnecessary External) | **Partially eliminated** — moved from implicit Tier 3 to explicit Tier 2; DataSourcing handles data, Writer handles output |
| AP4 (Unused columns — holdings) | **Eliminated** — removed holding_id, investment_id, security_id, quantity from holdings DataSourcing |
| AP6 (Row-by-row iteration) | **Partially addressed** — uses idiomatic C# Dictionary pattern; full SQL elimination blocked by W2 |

## 12. Open Questions

1. **Row ordering determinism (BR-10, MEDIUM confidence):** V1 iterates `customerTotals` which is a `Dictionary<int, ...>`. In .NET, `Dictionary` does not guarantee enumeration order, though in practice it follows insertion order when no deletions occur. V2 reproduces the same pattern. If Proofmark comparison fails due to row ordering differences, we may need to add an explicit `ORDER BY customer_id` equivalent (sort the output rows before constructing the DataFrame). Parquet readers may also reorder rows. This should be tested during Proofmark validation.

2. **Empty-input guard asymmetry (BR-5):** V1 returns empty output when customers is empty, even if holdings has data. This means a customer-less holding gets zero output rather than output with empty names. This is technically an edge case worth noting — the behavior is correct per V1, but a pure LEFT JOIN approach would emit those rows. V2 reproduces V1's behavior exactly.

3. **Holiday handling (BRD Open Question #3):** The Friday fallback does not account for market holidays. If Friday is a holiday with no holdings data, the output will be empty (zero rows after the date filter). No V1 code handles this, so V2 does not either. If this becomes a concern, a multi-day lookback could be added, but that would change output behavior and is out of scope.
