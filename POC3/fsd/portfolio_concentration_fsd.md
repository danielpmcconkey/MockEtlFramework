# PortfolioConcentration — Functional Specification Document

## 1. Job Summary

This job computes sector concentration percentages for each customer's portfolio, producing a Parquet file with one row per (customer_id, investment_id, sector) tuple. It joins holdings to securities for sector lookup, computes per-customer total portfolio value and per-sector subtotals using double-precision arithmetic (W6), then calculates a sector percentage using integer division (W4) that almost always produces 0. The job contains two deliberately-replicated computational bugs and several code-quality anti-patterns that V2 eliminates.

---

## 2. V2 Module Chain

**Tier:** Tier 2 — Framework + Minimal External (SCALPEL)

```
DataSourcing (holdings)
  -> DataSourcing (securities)
    -> Transformation (SQL: join, group, aggregate with double SUM)
      -> External (PortfolioConcentrationV2Processor: type coercion + W4 integer division)
        -> ParquetFileWriter (Parquet output, numParts=1, Overwrite)
```

### Tier Selection Rationale

**Why not Tier 1 (pure SQL)?** Two reasons prevent a pure DataSourcing -> Transformation -> ParquetFileWriter chain:

1. **Parquet type fidelity.** The Transformation module reads SQLite results via `reader.GetValue()`, which returns `long` for INTEGER columns. V1's External module outputs `customer_id` and `investment_id` as C# `int` (32-bit), which the ParquetFileWriter maps to Parquet INT32. If these columns flow through SQLite, they become `long` (64-bit) and map to Parquet INT64 — a schema mismatch. Similarly, V1 outputs `sector_pct` as `decimal` (Parquet DECIMAL), but SQLite integer division returns `long` (Parquet INT64).

2. **W4 replication fidelity.** V1's integer division is `(int)doubleValue / (int)doubleValue`, which truncates the double to a 32-bit int via C# cast semantics (truncate towards zero). SQLite's `CAST(x AS INTEGER)` produces a 64-bit integer. While the arithmetic result is the same for these values (0), the output type stored in Parquet differs.

**Why Tier 2 and not Tier 3?** DataSourcing handles data retrieval with framework-injected effective dates. The SQL Transformation handles the join between holdings and securities, the grouping by (customer_id, investment_id, sector), and the double-precision SUM aggregation (W6 replication via SQLite REAL arithmetic). The External module handles ONLY: (a) type coercion from `long` to `int` for integer columns, (b) W4 integer division with correct C# truncation semantics, and (c) constructing correctly-typed output rows. The ParquetFileWriter handles file output. No business logic lives in the External.

### Module 1: DataSourcing — holdings
- **resultName:** `holdings`
- **schema:** `datalake`
- **table:** `holdings`
- **columns:** `["customer_id", "investment_id", "security_id", "current_value"]`
- **Effective dates:** Injected by framework (`__minEffectiveDate` / `__maxEffectiveDate`)
- **Note:** V1 sources 6 columns (`holding_id`, `investment_id`, `security_id`, `customer_id`, `quantity`, `current_value`). Only `customer_id` (grouping key + total value key), `investment_id` (grouping key), `security_id` (sector lookup join key), and `current_value` (aggregation) are used. `holding_id` and `quantity` are eliminated per AP4 (unused columns).

### Module 2: DataSourcing — securities
- **resultName:** `securities`
- **schema:** `datalake`
- **table:** `securities`
- **columns:** `["security_id", "sector"]`
- **Effective dates:** Injected by framework (`__minEffectiveDate` / `__maxEffectiveDate`)
- **Note:** V1 sources 5 columns (`security_id`, `ticker`, `security_name`, `security_type`, `sector`). Only `security_id` (join key) and `sector` (grouping key) are used. `ticker`, `security_name`, and `security_type` are eliminated per AP4.

### Module 3: Transformation — join, group, aggregate
- **resultName:** `sector_agg`
- **sql:** See Section 4 for full SQL
- **Purpose:** LEFT JOIN holdings to securities on security_id, GROUP BY (customer_id, investment_id, COALESCE(sector, 'Unknown')), compute SUM(current_value) as sector_value and customer-level total_value, add as_of from `__maxEffectiveDate`. The SUM operates on SQLite REAL (double) values, which replicates W6 epsilon behavior.

### Module 4: External — PortfolioConcentrationV2Processor
- **Assembly:** `ExternalModules/bin/Debug/net8.0/ExternalModules.dll`
- **Type:** `ExternalModules.PortfolioConcentrationV2Processor`
- **Purpose:** Minimal type coercion and W4 replication. Reads the `sector_agg` DataFrame, and for each row:
  1. Casts `customer_id` and `investment_id` from `long` to `int` (matching V1's `Convert.ToInt32`)
  2. Reads `sector_value` and `total_value` as `double` (already REAL from SQLite — W6 preserved)
  3. Applies W4: `int sectorInt = (int)sectorValue; int totalInt = (int)totalValue; decimal sectorPct = (decimal)(sectorInt / totalInt);`
  4. Reads `as_of` as string (from SQLite TEXT), parses to `DateOnly`
  5. Constructs output rows with correct CLR types for Parquet schema equivalence
- Stores result as `"output"` in shared state.
- **Why this can't be in SQL:** SQLite's CAST to INTEGER returns `long`, not `int`. The Parquet schema must match V1's INT32 for customer_id/investment_id and DECIMAL for sector_pct. Additionally, the W4 integer division must use C#'s `(int)` cast semantics (truncate double toward zero to 32-bit int), not SQLite's 64-bit integer truncation.

### Module 5: ParquetFileWriter
- **source:** `output`
- **outputDirectory:** `Output/double_secret_curated/portfolio_concentration/`
- **numParts:** 1
- **writeMode:** Overwrite

---

## 3. DataSourcing Config

### Table: datalake.holdings

| Column | Used For | Evidence |
|--------|----------|----------|
| customer_id | Grouping key (sector aggregation + customer total) | [PortfolioConcentrationCalculator.cs:41,54] |
| investment_id | Grouping key (sector aggregation) | [PortfolioConcentrationCalculator.cs:55] |
| security_id | JOIN key to securities for sector lookup | [PortfolioConcentrationCalculator.cs:56] |
| current_value | SUM aggregation for sector_value and total_value | [PortfolioConcentrationCalculator.cs:43,58] |

**Columns eliminated (AP4):** `holding_id` (never referenced in output or computation), `quantity` (never referenced in output or computation). Evidence: [PortfolioConcentrationCalculator.cs] — grep for "holding_id" and "quantity" yields zero hits in the External module.

### Table: datalake.securities

| Column | Used For | Evidence |
|--------|----------|----------|
| security_id | JOIN key from holdings | [PortfolioConcentrationCalculator.cs:32] |
| sector | Output column, grouping key | [PortfolioConcentrationCalculator.cs:33] |

**Columns eliminated (AP4):** `ticker`, `security_name`, `security_type` (never referenced). Evidence: [PortfolioConcentrationCalculator.cs:29-34] — only security_id and sector are accessed from securities rows.

### Table: datalake.investments — REMOVED (AP1)

V1 sources investments with columns `[investment_id, customer_id, account_type, current_value]` but the External module **never accesses** `sharedState["investments"]`. The variable `investments` is assigned on line 18 but never used in any computation. Evidence: [PortfolioConcentrationCalculator.cs:18] assigns the variable; no subsequent line references it. [portfolio_concentration.json:20-25] configures the DataSourcing. BRD BR-8 confirms: "Investments data is sourced by the job config but never referenced by the External module."

### Effective Date Handling

No explicit `minEffectiveDate`/`maxEffectiveDate` in the job config. The framework executor injects `__minEffectiveDate` and `__maxEffectiveDate` into shared state, and both DataSourcing modules use these automatically. Evidence: [portfolio_concentration.json] — no date fields in any DataSourcing module. BRD BR-9 confirms.

---

## 4. Transformation SQL

```sql
SELECT
    h.customer_id,
    h.investment_id,
    COALESCE(s.sector, 'Unknown') AS sector,
    SUM(h.current_value) AS sector_value,
    cust_total.total_value AS total_value,
    (SELECT MAX(as_of) FROM holdings) AS as_of
FROM holdings h
LEFT JOIN securities s
    ON h.security_id = s.security_id
    AND h.as_of = s.as_of
LEFT JOIN (
    SELECT customer_id, SUM(current_value) AS total_value
    FROM holdings
    GROUP BY customer_id
) cust_total
    ON h.customer_id = cust_total.customer_id
GROUP BY h.customer_id, h.investment_id, COALESCE(s.sector, 'Unknown'), cust_total.total_value
ORDER BY h.customer_id, h.investment_id, sector
```

### SQL Design Notes

1. **LEFT JOIN to securities (BR-3, BR-10):** Holdings whose `security_id` has no match in securities must default to `"Unknown"` sector. `COALESCE(s.sector, 'Unknown')` handles both cases: (a) no matching securities row (NULL from LEFT JOIN), (b) matching row with NULL sector value. Evidence: [PortfolioConcentrationCalculator.cs:33,57] — `secRow["sector"]?.ToString() ?? "Unknown"` and `sectorLookup.GetValueOrDefault(secId, "Unknown")`.

2. **Customer total as subquery (BR-2):** V1 computes `customerTotalValue` across ALL of a customer's holdings, regardless of investment or sector. The subquery `SELECT customer_id, SUM(current_value) AS total_value FROM holdings GROUP BY customer_id` replicates this. The result is joined back to the main query so each output row carries the customer's total. Evidence: [PortfolioConcentrationCalculator.cs:38-48].

3. **Double-precision SUM (W6):** DataSourcing reads `current_value` as `decimal` from PostgreSQL. The Transformation module's `GetSqliteType` maps `decimal` to SQLite REAL. `ToSqliteValue` passes `decimal` through, and SQLite stores it as double-precision float. `SUM()` on REAL columns accumulates in double, replicating V1's `double` accumulation. The same epsilon errors are produced because the conversion path `decimal -> double -> SUM(double)` mirrors V1's `Convert.ToDouble(row["current_value"])` then `double +=`. Evidence: [Transformation.cs:98-103] `decimal => "REAL"`, [Transformation.cs:106-113] decimal falls through to `_ => value`.

4. **JOIN condition includes as_of:** Both tables are full-load snapshots. For multi-day ranges, joining on `security_id AND as_of` ensures same-day sector mapping. V1's dictionary-based lookup (`sectorLookup[secId] = ...`) overwrites per security_id across all days (BRD Edge Case 6), but for single-day auto-advance runs (the actual execution pattern), this produces the same result. The `AND h.as_of = s.as_of` condition is the relationally correct approach.

5. **as_of from MAX(as_of) (BR-7):** V1 gets `maxDate` from `__maxEffectiveDate` in shared state. Since DataSourcing filters by the effective date range and auto-advance processes one day at a time (min = max), `MAX(as_of) FROM holdings` equals `__maxEffectiveDate`. SQLite returns this as TEXT in `yyyy-MM-dd` format (via `ToSqliteValue` DateOnly conversion). The External module parses this back to DateOnly.

6. **sector_pct is NOT computed here.** The W4 integer division (`(int)sectorValue / (int)totalValue`) requires C# cast semantics. SQLite integer division would produce the same numerical result but with type `long` instead of `decimal`. The External module handles this computation. See Section 2 (Module 4) for details.

7. **ORDER BY for deterministic output.** V1's output row order depends on Dictionary iteration order (insertion order in .NET). The SQL `ORDER BY h.customer_id, h.investment_id, sector` ensures deterministic, reproducible ordering. If this produces a different row order than V1, it will not affect Parquet comparison (Proofmark compares data values, not physical row order in Parquet).

---

## 5. Writer Config

| Parameter | V1 Value | V2 Value | Rationale |
|-----------|----------|----------|-----------|
| type | ParquetFileWriter | ParquetFileWriter | Same writer type per BLUEPRINT |
| source | `output` | `output` | Same shared state key |
| outputDirectory | `Output/curated/portfolio_concentration/` | `Output/double_secret_curated/portfolio_concentration/` | V2 output path per BLUEPRINT |
| numParts | 1 | 1 | Matches V1 [portfolio_concentration.json:35] |
| writeMode | Overwrite | Overwrite | Matches V1 [portfolio_concentration.json:36] |

### Write Mode Implications

Overwrite mode: each effective date's run replaces the entire Parquet output directory. For multi-day auto-advance runs, only the final effective date's output persists. The `as_of` column records `__maxEffectiveDate`, so only the last processed date is reflected. Evidence: BRD "Write Mode Implications" section.

---

## 6. Wrinkle Replication

### W4: Integer Division (sector_pct)

**V1 behavior:** `int sectorInt = (int)sectorValue; int totalInt = (int)totalValue; decimal sectorPct = (decimal)(sectorInt / totalInt);` — truncates double values to 32-bit int, performs integer division (result is 0 for all cases where sector_value < total_value), then casts to decimal. Evidence: [PortfolioConcentrationCalculator.cs:75-77].

**V2 replication:** The External module (PortfolioConcentrationV2Processor) performs the identical computation:
```csharp
// W4: V1 bug — integer division truncates to 0. Replicated for output equivalence.
// V1 casts double -> int (truncating decimal portion), then divides int/int which
// floors to 0 for any case where sector_value < total_value.
int sectorInt = (int)sectorValue;
int totalInt = (int)totalValue;
decimal sectorPct = (decimal)(sectorInt / totalInt);
```

The V2 prescription from KNOWN_ANTI_PATTERNS.md says: "cast to decimal, compute the correct value, then explicitly truncate: `Math.Truncate((decimal)numerator / denominator)`". However, this prescription assumes the goal is to replicate the truncated result of proper decimal division. In this job, the V1 bug is more severe: the truncation happens BEFORE division (casting double to int loses the fractional portion of the monetary value), and then integer division produces 0 because the int numerator < int denominator for any sector that isn't 100% of the portfolio. The `Math.Truncate` approach would produce a different result for edge cases where the sector IS 100% (V1 would produce 1, `Math.Truncate((decimal)sectorInt / totalInt)` would also produce 1). For exact replication, we use V1's exact integer division pattern with a clear comment.

**Output type:** `decimal` — stored as Parquet DECIMAL. The value is always 0m (or 1m in the edge case where a customer has only one sector).

### W6: Double Arithmetic (sector_value, total_value)

**V1 behavior:** `double value = Convert.ToDouble(row["current_value"]);` followed by `customerTotalValue[customerId] += value;` and `sectorValues[key] += value;`. All accumulation uses `double`, introducing floating-point epsilon errors. Evidence: [PortfolioConcentrationCalculator.cs:42-47, 58-63].

**V2 replication:** The SQL Transformation accumulates via `SUM(h.current_value)` on SQLite REAL columns. The framework's `Transformation.GetSqliteType` maps `decimal` (from PostgreSQL `numeric`) to SQLite REAL (double), and `SUM` on REAL accumulates in double-precision. The resulting `sector_value` and `total_value` values carry the same epsilon errors as V1's double accumulation. Evidence: [Transformation.cs:98-103] type mapping, [Transformation.cs:106-113] value conversion.

The External module reads these as `double` (from SQLite REAL via `reader.GetValue()` returning `double`) and passes them through to the output DataFrame unchanged. The ParquetFileWriter maps `double` to Parquet DOUBLE. Evidence: [ParquetFileWriter.cs:97] `double or float => typeof(double?)`.

**Comment in External module:**
```csharp
// W6: V1 uses double (not decimal) for monetary accumulation.
// Epsilon errors in output are intentional V1 replication.
// SQLite REAL (double) SUM in the upstream Transformation replicates this.
```

---

## 7. Anti-Pattern Elimination

### AP1: Dead-End Sourcing — ELIMINATED

**V1 problem:** The job config sources `datalake.investments` with columns `[investment_id, customer_id, account_type, current_value]`, but the External module never accesses `sharedState["investments"]`. Evidence: [portfolio_concentration.json:19-25] configures the DataSourcing; [PortfolioConcentrationCalculator.cs:18] assigns the variable but never uses it. BRD BR-8 confirms.

**V2 action:** The investments DataSourcing module is removed entirely from the V2 job config. Only holdings and securities are sourced.

### AP3: Unnecessary External Module — PARTIALLY ELIMINATED

**V1 problem:** V1 uses a full External module for ALL logic: building the sector lookup dictionary, iterating holdings rows, computing aggregates, and building output rows. The business logic (join + group + aggregate) is fully expressible in SQL.

**V2 action:** Business logic (join, grouping, aggregation) is moved to the SQL Transformation (Tier 1 modules). The External module is retained ONLY for type coercion (long -> int) and W4 integer division — operations that cannot be expressed in SQLite with the correct output types. This is a Tier 2 (SCALPEL) use of External.

### AP4: Unused Columns — ELIMINATED

**V1 problem:** Holdings sources `holding_id` and `quantity` which are never used. Securities sources `ticker`, `security_name`, and `security_type` which are never used. Evidence: [PortfolioConcentrationCalculator.cs] — these column names never appear in any computation or output.

**V2 action:**
- Holdings: 6 columns -> 4 columns (removed `holding_id`, `quantity`)
- Securities: 5 columns -> 2 columns (removed `ticker`, `security_name`, `security_type`)

### AP6: Row-by-Row Iteration — ELIMINATED

**V1 problem:** Three separate `foreach` loops: (1) build sector lookup dictionary from securities rows, (2) iterate holdings rows to accumulate customer total values, (3) iterate holdings rows again to accumulate sector values. All three are classic set-operation candidates. Evidence: [PortfolioConcentrationCalculator.cs:30-48, 51-64].

**V2 action:** All three operations are replaced with a single SQL statement: LEFT JOIN for sector lookup, subquery for customer totals, GROUP BY for sector aggregation. The External module does iterate the aggregated output to apply W4, but this is per-output-row I/O (unavoidable), not a replacement for set operations.

### AP7: Magic Values — ADDRESSED

**V1 problem:** `"Unknown"` is hardcoded as the default sector string with no named constant. Evidence: [PortfolioConcentrationCalculator.cs:33,57].

**V2 action:** In the SQL Transformation, `'Unknown'` appears in `COALESCE(s.sector, 'Unknown')` which is idiomatic SQL. In the External module, a named constant is defined:
```csharp
private const string DefaultSector = "Unknown"; // Default for securities with null/missing sector
```

### Anti-Patterns NOT Applicable

| Code | Name | Why Not |
|------|------|---------|
| AP2 | Duplicated logic | No cross-job duplication identified for this specific computation. |
| AP5 | Asymmetric NULLs | NULL handling is consistent: NULL or missing sector -> "Unknown". No asymmetry. |
| AP8 | Complex SQL / unused CTEs | V1 has no SQL (all C#). V2's SQL is straightforward with no unused CTEs. |
| AP9 | Misleading names | "portfolio_concentration" accurately describes the job's output (sector concentration percentages per portfolio). The fact that sector_pct is always 0 due to W4 is a bug, not a naming issue. |
| AP10 | Over-sourcing dates | V1 uses framework-injected effective dates; V2 does the same. No explicit date filtering needed. |

---

## 8. Proofmark Config

```yaml
comparison_target: "portfolio_concentration"
reader: parquet
threshold: 100.0
columns:
  fuzzy:
    - name: "sector_value"
      tolerance: 0.0000000001
      tolerance_type: absolute
      reason: "W6: Double-precision accumulation via SUM. V1 accumulates doubles in a C# foreach loop; V2 accumulates via SQLite SUM on REAL columns. Both use double arithmetic, but the order of additions may differ (V1 iterates DataFrame row order; SQLite may optimize aggregation order), potentially producing different epsilon-level results. [PortfolioConcentrationCalculator.cs:58-63] vs SQLite SUM."
    - name: "total_value"
      tolerance: 0.0000000001
      tolerance_type: absolute
      reason: "W6: Double-precision accumulation via SUM. Same rationale as sector_value — accumulation order may differ between V1 C# loop and SQLite SUM. [PortfolioConcentrationCalculator.cs:42-47] vs SQLite SUM."
```

### Proofmark Design Notes

- **reader: parquet** — Output is Parquet (ParquetFileWriter in both V1 and V2).
- **threshold: 100.0** — All rows must match. No non-deterministic fields.
- **No EXCLUDED columns** — All columns are deterministic. `as_of` comes from `__maxEffectiveDate` (deterministic per effective date). No timestamps or UUIDs.
- **FUZZY on sector_value and total_value** — Both columns use double-precision accumulation (W6). While both V1 and V2 use `double` arithmetic, the aggregation order may differ: V1 iterates DataFrame rows in insertion order; SQLite's SUM may aggregate in a different order. Since floating-point addition is not associative, different summation orders can produce epsilon-level differences. A tolerance of 1e-10 (absolute) is far tighter than any business-meaningful difference but accommodates double-precision accumulation order variance. If Proofmark passes strict (which is possible if the aggregation order happens to match), the fuzzy columns can be removed. Start with this safety net.
- **sector_pct is NOT fuzzy** — It is always 0 (or 1) due to W4 integer division. Integer values have no epsilon variance.
- **customer_id, investment_id are NOT fuzzy** — Integer values, exact match.
- **as_of is NOT fuzzy** — Date value from `__maxEffectiveDate`, exact match.
- **sector is NOT fuzzy** — String value, exact match.

---

## 9. Open Questions

1. **Row ordering in Parquet.** V1's output row order depends on `Dictionary<(int, int, string), double>` iteration order, which in .NET follows insertion order. V2 uses `ORDER BY customer_id, investment_id, sector` in SQL. If V1's insertion order doesn't match this sort order, the physical row positions in the Parquet file will differ. Proofmark likely compares data values (not physical position) for Parquet, so this should not be an issue. If it IS an issue, the External module can be extended to match V1's insertion order by iterating holdings in DataFrame order and building the same key sequence. **Confidence: MEDIUM that row order doesn't matter for Proofmark.**

2. **Double accumulation order (W6).** V1 iterates holdings rows in DataFrame order (which is `ORDER BY as_of` from DataSourcing). SQLite's SUM may or may not aggregate in the same order. If the accumulation order differs, the double-precision epsilon errors will differ. The fuzzy tolerance on sector_value and total_value provides a safety net. If Proofmark reports differences exceeding the tolerance, we may need to move the aggregation into the External module to exactly replicate V1's iteration order. **Confidence: HIGH that the tolerance handles this; LOW that the tolerance is even needed.**

3. **Division by zero (BRD Edge Case 3).** If a customer's total holdings value truncates to 0 after `(int)` cast (e.g., all holdings have current_value between 0.0 and 1.0), V1's `sectorInt / totalInt` throws `DivideByZeroException`. V2 replicates this behavior by using the same integer division pattern. If this occurs in the test data, both V1 and V2 will fail identically. If we need to handle this gracefully in V2, a guard clause can be added. **Confidence: MEDIUM that this doesn't occur in test data; HIGH that V1 and V2 will behave identically if it does.**

4. **Cross-date aggregation (BRD Edge Case 6).** When the effective date range spans multiple days, V1's securities lookup dictionary overwrites per security_id (keeping the last-seen as_of's sector mapping), while V2's SQL joins on `security_id AND as_of` (keeping each day's mapping separate). For single-day auto-advance (the execution pattern), this is identical. For multi-day ranges, the results could differ if a security changes sectors between days. **Confidence: HIGH that single-day auto-advance makes this moot.**

---

## 10. V2 Job Config JSON

```json
{
  "jobName": "PortfolioConcentrationV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "holdings",
      "schema": "datalake",
      "table": "holdings",
      "columns": ["customer_id", "investment_id", "security_id", "current_value"]
    },
    {
      "type": "DataSourcing",
      "resultName": "securities",
      "schema": "datalake",
      "table": "securities",
      "columns": ["security_id", "sector"]
    },
    {
      "type": "Transformation",
      "resultName": "sector_agg",
      "sql": "SELECT h.customer_id, h.investment_id, COALESCE(s.sector, 'Unknown') AS sector, SUM(h.current_value) AS sector_value, cust_total.total_value AS total_value, (SELECT MAX(as_of) FROM holdings) AS as_of FROM holdings h LEFT JOIN securities s ON h.security_id = s.security_id AND h.as_of = s.as_of LEFT JOIN (SELECT customer_id, SUM(current_value) AS total_value FROM holdings GROUP BY customer_id) cust_total ON h.customer_id = cust_total.customer_id GROUP BY h.customer_id, h.investment_id, COALESCE(s.sector, 'Unknown'), cust_total.total_value ORDER BY h.customer_id, h.investment_id, sector"
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.PortfolioConcentrationV2Processor"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/portfolio_concentration/",
      "numParts": 1,
      "writeMode": "Overwrite"
    }
  ]
}
```

### Config Design Notes

- **investments DataSourcing removed (AP1):** V1's investments source is dead-end. Not included.
- **Column reduction (AP4):** holdings 6 -> 4 columns; securities 5 -> 2 columns.
- **Transformation resultName is `sector_agg`**, not `output`. The External module reads `sector_agg`, applies W4, and stores the result as `output` for the ParquetFileWriter.
- **firstEffectiveDate:** Matches V1's `2024-10-01`.

---

## 11. External Module Design — PortfolioConcentrationV2Processor

### Responsibility

This External module has ONE job: read the SQL Transformation's aggregated output, apply W4 integer division to compute sector_pct, and coerce types to match V1's Parquet schema. ALL business logic (join, grouping, aggregation) is handled upstream in SQL.

### Interface

Implements `IExternalStep.Execute(Dictionary<string, object> sharedState)`.

### Named Constants

```csharp
private const string InputKey = "sector_agg";   // Transformation output
private const string OutputKey = "output";        // ParquetFileWriter input
private const string DefaultSector = "Unknown";   // Sector default for null/missing
```

### Algorithm

```
1. Read "sector_agg" DataFrame from shared state (aggregated rows from Transformation)
2. If sector_agg is null or empty:
     - Store empty DataFrame as "output" with correct column names
     - Return shared state — BR-6
3. For each row in sector_agg:
     a. customer_id = Convert.ToInt32(row["customer_id"])      // long -> int
     b. investment_id = Convert.ToInt32(row["investment_id"])   // long -> int
     c. sector = row["sector"].ToString()                       // already string
     d. sectorValue = Convert.ToDouble(row["sector_value"])    // REAL -> double (W6)
     e. totalValue = Convert.ToDouble(row["total_value"])      // REAL -> double (W6)
     f. as_of = DateOnly.Parse(row["as_of"].ToString())        // TEXT -> DateOnly
     g. // W4: Integer division — replicated for output equivalence
        int sectorInt = (int)sectorValue;
        int totalInt = (int)totalValue;
        decimal sectorPct = (decimal)(sectorInt / totalInt);
     h. Add Row: { customer_id (int), investment_id (int), sector (string),
                    sector_value (double), total_value (double),
                    sector_pct (decimal), as_of (DateOnly) }
4. Store new DataFrame as "output" in shared state
5. Return shared state
```

### Output Type Map (for Parquet schema equivalence)

| Column | CLR Type | Parquet Type | V1 Match |
|--------|----------|-------------|----------|
| customer_id | `int` | INT32 | Yes — V1 uses `Convert.ToInt32` |
| investment_id | `int` | INT32 | Yes — V1 uses `Convert.ToInt32` |
| sector | `string` | STRING | Yes |
| sector_value | `double` | DOUBLE | Yes — V1 stores as `double` |
| total_value | `double` | DOUBLE | Yes — V1 stores as `double` |
| sector_pct | `decimal` | DECIMAL | Yes — V1 casts `(decimal)(int/int)` |
| as_of | `DateOnly` | DATE | Yes — V1 stores `DateOnly` |

### Empty Input Handling (BR-6)

If `sector_agg` is null or empty (which happens when holdings or securities is empty — the SQL LEFT JOIN + GROUP BY produces 0 rows for empty holdings), the External module stores an empty DataFrame with the correct output columns and returns. This matches V1's guard clause at [PortfolioConcentrationCalculator.cs:20-24].

### Data Flow

```
SharedState at External entry:
  "holdings"    -> DataFrame (raw rows from DataSourcing — not used by External)
  "securities"  -> DataFrame (raw rows from DataSourcing — not used by External)
  "sector_agg"  -> DataFrame (aggregated rows from Transformation)
  "__maxEffectiveDate" -> DateOnly

External reads: "sector_agg"
External writes: "output"

SharedState at External exit:
  "holdings"    -> DataFrame (unchanged)
  "securities"  -> DataFrame (unchanged)
  "sector_agg"  -> DataFrame (unchanged)
  "output"      -> DataFrame (correctly-typed output rows)
  "__maxEffectiveDate" -> DateOnly (unchanged)
```

---

## 12. Traceability Matrix

| BRD Requirement | FSD Section | V2 Implementation |
|----------------|-------------|-------------------|
| BR-1: Sector concentration per (customer_id, investment_id, sector) | Sections 2 (Module 3), 4 (SQL) | SQL `GROUP BY h.customer_id, h.investment_id, COALESCE(s.sector, 'Unknown')` |
| BR-2: Total portfolio value per customer_id | Sections 4 (SQL note 2) | SQL subquery: `SELECT customer_id, SUM(current_value) AS total_value FROM holdings GROUP BY customer_id` |
| BR-3: Sector lookup maps security_id to sector, unknown defaults to "Unknown" | Sections 4 (SQL note 1), 7 (AP7) | SQL `LEFT JOIN securities s ON h.security_id = s.security_id` + `COALESCE(s.sector, 'Unknown')` |
| BR-4 / W6: Double arithmetic for accumulation | Sections 4 (SQL note 3), 6 (W6) | SQLite REAL SUM replicates double accumulation |
| BR-5 / W4: Integer division for sector_pct | Sections 2 (Module 4), 6 (W4), 11 (Algorithm step 3g) | External module: `(int)sectorValue / (int)totalValue` cast to `decimal` |
| BR-6: Empty input -> empty DataFrame | Sections 11 (Empty Input Handling) | External checks sector_agg.Count, returns empty DataFrame |
| BR-7: as_of from __maxEffectiveDate | Sections 4 (SQL note 5) | SQL `(SELECT MAX(as_of) FROM holdings)` equals __maxEffectiveDate for single-day runs |
| BR-8 / AP1: Investments sourced but unused | Sections 3 (investments REMOVED), 7 (AP1) | investments DataSourcing removed from V2 config |
| BR-9: Effective dates from executor injection | Section 3 (Effective Date Handling) | DataSourcing modules use framework-injected dates |
| BR-10: Null sector -> "Unknown" | Sections 4 (SQL note 1), 7 (AP7) | SQL `COALESCE(s.sector, 'Unknown')` |
| W4: Integer division | Section 6 (W4) | External module replicates exact C# integer division pattern |
| W6: Double arithmetic | Section 6 (W6) | SQLite REAL SUM + External passthrough preserves double values |
| AP1: Dead-end sourcing | Section 7 (AP1) | investments removed |
| AP3: Unnecessary External | Section 7 (AP3) | Business logic moved to SQL; External retained only for type coercion + W4 |
| AP4: Unused columns | Section 7 (AP4) | holdings 6->4, securities 5->2 |
| AP6: Row-by-row iteration | Section 7 (AP6) | Three foreach loops replaced with single SQL statement |
| AP7: Magic values | Section 7 (AP7) | Named constants in External; COALESCE in SQL |
