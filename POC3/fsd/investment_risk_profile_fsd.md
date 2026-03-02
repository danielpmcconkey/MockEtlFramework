# InvestmentRiskProfile — Functional Specification Document

## 1. Overview & Tier Selection

**Job:** InvestmentRiskProfileV2
**Config:** `investment_risk_profile_v2.json`
**Tier:** Tier 1 — Framework Only (`DataSourcing -> Transformation (SQL) -> CsvFileWriter`)

This job produces a per-investment risk classification by enriching each investment record with a computed `risk_tier` column based on the investment's `current_value`. The V1 implementation uses an External module (`InvestmentRiskClassifier.cs`) for row-by-row iteration and CASE-style logic that is straightforwardly expressible in SQL. No procedural or non-SQL-expressible logic exists, making this a textbook Tier 1 candidate.

**Tier 1 justification:** Every operation performed by the V1 External module (NULL coalescing, threshold-based CASE, type casting, column pass-through) maps directly to standard SQL constructs. There is no loop state, no cross-row accumulation, and no external I/O. An External module is completely unnecessary.

## 2. V2 Module Chain

```
DataSourcing (investments) -> Transformation (SQL) -> CsvFileWriter
```

| Step | Module Type | Purpose |
|------|-------------|---------|
| 1 | DataSourcing | Source `datalake.investments` with columns: `investment_id`, `customer_id`, `account_type`, `current_value`, `risk_profile`. Effective dates injected by executor. |
| 2 | Transformation | SQL computes `risk_tier` from `current_value` thresholds, handles NULL coalescing for `current_value`, `risk_profile`, and `account_type`. |
| 3 | CsvFileWriter | Write `output` DataFrame to CSV with header, LF line endings, no trailer, Overwrite mode. |

**Removed from V1 chain:**
- DataSourcing for `customers` table (AP1: dead-end sourcing -- never used by V1 External module)
- External module (AP3: unnecessary External -- all logic expressible in SQL)

## 3. Anti-Pattern Analysis

### Anti-Patterns Eliminated in V2

| ID | Name | V1 Problem | V2 Resolution |
|----|------|------------|---------------|
| AP1 | Dead-end sourcing | V1 sources `datalake.customers` (id, first_name, last_name) but the External module never references `sharedState["customers"]`. Evidence: [InvestmentRiskClassifier.cs] has zero references to "customers". | **Eliminated.** V2 config removes the customers DataSourcing module entirely. |
| AP3 | Unnecessary External module | V1 uses `InvestmentRiskClassifier.cs` for logic that is pure SQL: NULL coalescing, CASE expressions, column selection. Evidence: [InvestmentRiskClassifier.cs:24-60] is a foreach loop doing per-row CASE/COALESCE. | **Eliminated.** V2 uses a Transformation module with SQL CASE and COALESCE. |
| AP4 | Unused columns | V1 sources `customers.id`, `customers.first_name`, `customers.last_name` -- none used. | **Eliminated.** V2 does not source the customers table at all (subsumed by AP1 fix). |
| AP6 | Row-by-row iteration | V1 uses `foreach (var row in investments.Rows)` to iterate and build output rows one at a time. Evidence: [InvestmentRiskClassifier.cs:25]. | **Eliminated.** V2 uses set-based SQL in a single Transformation step. |
| AP7 | Magic values | V1 uses hardcoded thresholds `200000` and `50000` with no documentation. Evidence: [InvestmentRiskClassifier.cs:40-41]. | **Eliminated.** V2 SQL uses clearly commented thresholds with business context explaining what they represent. |

### Output-Affecting Wrinkles Preserved in V2

| ID | Name | V1 Behavior | V2 Handling |
|----|------|-------------|-------------|
| AP5 | Asymmetric NULLs | NULL `current_value` defaults to 0 (numeric), but NULL `risk_profile` defaults to "Unknown" (string), and NULL `account_type` defaults to "" (empty string). Three different NULL treatments across three fields. Evidence: [InvestmentRiskClassifier.cs:32-36, 29]. | **Reproduced.** SQL uses `COALESCE(current_value, 0)`, `COALESCE(risk_profile, 'Unknown')`, and `COALESCE(account_type, '')`. Comment documents the asymmetry. |

**Note on W-codes:** No W-codes (W1-W12) apply to this job. There is no weekend fallback, no Sunday skip, no trailer, no integer division, no double-precision accumulation, no append-mode header duplication. The job is a straightforward Overwrite-mode CSV with deterministic output.

### BRD Correction: High Value Threshold

**CRITICAL:** The BRD (BR-2) states the High Value threshold is `current_value > 250000`. However, the actual V1 source code at [InvestmentRiskClassifier.cs:40] reads:

```csharp
if (currentValue > 200000)
    riskTier = "High Value";
```

The threshold in the V1 code is **200000**, not 250000. Since V2 must produce byte-identical output to V1, the V2 implementation uses `200000` as the High Value threshold. The BRD contains an error on this point.

- **V1 ground truth:** [InvestmentRiskClassifier.cs:40] `if (currentValue > 200000)`
- **BRD claim:** `current_value > 250000`
- **V2 follows:** V1 ground truth (200000)

## 4. Output Schema

| Column | Source | SQL Expression | V1 Evidence |
|--------|--------|---------------|-------------|
| `investment_id` | investments.investment_id | `CAST(investment_id AS INTEGER)` | [InvestmentRiskClassifier.cs:27] `Convert.ToInt32(row["investment_id"])` |
| `customer_id` | investments.customer_id | `CAST(customer_id AS INTEGER)` | [InvestmentRiskClassifier.cs:28] `Convert.ToInt32(row["customer_id"])` |
| `account_type` | investments.account_type | `COALESCE(account_type, '')` | [InvestmentRiskClassifier.cs:29] `row["account_type"]?.ToString() ?? ""` |
| `current_value` | investments.current_value | `COALESCE(current_value, 0)` | [InvestmentRiskClassifier.cs:32-34] null -> 0m |
| `risk_profile` | investments.risk_profile | `COALESCE(risk_profile, 'Unknown')` | [InvestmentRiskClassifier.cs:36] null -> "Unknown" |
| `risk_tier` | Computed | CASE expression on current_value thresholds | [InvestmentRiskClassifier.cs:39-45] |
| `as_of` | investments.as_of | Pass-through from source row | [InvestmentRiskClassifier.cs:55] `row["as_of"]` |

**Column order** matches V1 External module output: `investment_id, customer_id, account_type, current_value, risk_profile, risk_tier, as_of`.

## 5. SQL Design

```sql
SELECT
    CAST(investment_id AS INTEGER) AS investment_id,
    CAST(customer_id AS INTEGER) AS customer_id,
    -- AP5: V1 null-coalesces account_type to empty string
    COALESCE(account_type, '') AS account_type,
    -- AP5: V1 null-coalesces current_value to 0 (asymmetric with risk_profile -> 'Unknown')
    COALESCE(current_value, 0) AS current_value,
    -- AP5: V1 null-coalesces risk_profile to 'Unknown' (asymmetric with current_value -> 0)
    COALESCE(risk_profile, 'Unknown') AS risk_profile,
    -- Risk tier based on current_value thresholds (NOT risk_profile field)
    -- V1 ground truth: threshold is 200000, NOT 250000 as BRD states
    -- [InvestmentRiskClassifier.cs:40-44]
    CASE
        WHEN COALESCE(current_value, 0) > 200000 THEN 'High Value'
        WHEN COALESCE(current_value, 0) > 50000 THEN 'Medium Value'
        ELSE 'Low Value'
    END AS risk_tier,
    as_of
FROM investments
```

**Design notes:**

1. The CASE expression evaluates against `COALESCE(current_value, 0)` to match V1 behavior where NULL current_value is first set to 0m, then compared against thresholds. NULL -> 0 -> "Low Value".
2. No ORDER BY clause. V1's External module iterates rows in the order received from DataSourcing, which orders by `as_of`. The Transformation module receives the DataFrame in that order, and SQLite preserves insertion order when no ORDER BY is specified. However, since DataSourcing already sorts by `as_of` and the SQL has no aggregation or join that would reorder rows, the output order is naturally preserved.
3. No GROUP BY or JOIN -- this is a 1:1 mapping from investment rows to output rows (BR-1).
4. The `risk_tier` is independent of the `risk_profile` column (BR-9). The `risk_profile` is passed through unmodified (after NULL coalescing).

## 6. V2 Job Config JSON

```json
{
  "jobName": "InvestmentRiskProfileV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "investments",
      "schema": "datalake",
      "table": "investments",
      "columns": ["investment_id", "customer_id", "account_type", "current_value", "risk_profile"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT CAST(investment_id AS INTEGER) AS investment_id, CAST(customer_id AS INTEGER) AS customer_id, COALESCE(account_type, '') AS account_type, COALESCE(current_value, 0) AS current_value, COALESCE(risk_profile, 'Unknown') AS risk_profile, CASE WHEN COALESCE(current_value, 0) > 200000 THEN 'High Value' WHEN COALESCE(current_value, 0) > 50000 THEN 'Medium Value' ELSE 'Low Value' END AS risk_tier, as_of FROM investments"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/investment_risk_profile.csv",
      "includeHeader": true,
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

## 7. Writer Configuration

| Parameter | V1 Value | V2 Value | Match? |
|-----------|----------|----------|--------|
| Writer type | CsvFileWriter | CsvFileWriter | Yes |
| source | `output` | `output` | Yes |
| outputFile | `Output/curated/investment_risk_profile.csv` | `Output/double_secret_curated/investment_risk_profile.csv` | Path changed per V2 convention |
| includeHeader | true | true | Yes |
| writeMode | Overwrite | Overwrite | Yes |
| lineEnding | LF | LF | Yes |
| trailerFormat | not specified | not specified | Yes |

**Write mode implications (BR-4, Edge Case 5-6):** Overwrite mode means each effective date run replaces the entire file. For multi-day auto-advance, only the final date's output persists. Since `as_of` is sourced from the investment row's own value (not `__maxEffectiveDate`), the output correctly reflects per-row dates.

## 8. Proofmark Config Design

```yaml
comparison_target: "investment_risk_profile"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

**Justification for strict config (zero exclusions, zero fuzzy):**

- **No non-deterministic fields:** All output columns are derived deterministically from source data. No timestamps, no UUIDs, no random values. (BRD: "None identified.")
- **No floating-point accumulation:** `current_value` is passed through via COALESCE (no arithmetic), so no epsilon concerns.
- **No trailer:** No trailer format specified in V1 config, so `trailer_rows: 0`.
- **Header:** V1 uses `includeHeader: true`, so `header_rows: 1`.

Start strict. Only add overrides if comparison fails with evidence.

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision | Implementation |
|-----------------|-------------|-----------------|----------------|
| BR-1: 1:1 investment mapping | SQL Design | No GROUP BY or JOIN -- each investment row produces one output row | SQL SELECT with no aggregation |
| BR-2: Risk tier thresholds | SQL Design, BRD Correction | **Corrected:** V1 uses 200000 (not 250000). CASE expression with COALESCE(current_value, 0) | CASE WHEN > 200000 / > 50000 / ELSE |
| BR-3: Asymmetric NULL handling | SQL Design, Anti-Pattern Analysis | Three different NULL defaults: 0, "Unknown", "" | COALESCE with appropriate defaults per column |
| BR-4: Empty output on null/empty | SQL Design | SQL naturally returns 0 rows if investments table is empty for the date range | No special handling needed -- framework behavior |
| BR-5: Row-level as_of | SQL Design | `as_of` selected directly from investments row | `as_of` in SELECT, no transformation |
| BR-6: Customers sourced but unused | Module Chain, AP1 | **Eliminated.** Customers DataSourcing removed entirely | V2 config has no customers module |
| BR-7: account_type null -> "" | SQL Design | `COALESCE(account_type, '')` | SQL COALESCE |
| BR-8: Effective dates from executor | Module Chain | No explicit dates in DataSourcing config -- injected by executor | Standard framework behavior |
| BR-9: risk_tier independent of risk_profile | SQL Design | CASE evaluates current_value only; risk_profile passed through | Separate COALESCE and CASE |

## 10. External Module Design

**Not applicable.** This is a Tier 1 implementation. No External module is needed.

All V1 External module logic has been replaced by a single SQL Transformation:
- Row-by-row foreach loop (AP6) -> set-based SQL
- C# null coalescing (AP5) -> SQL COALESCE
- C# if/else chain (AP7) -> SQL CASE WHEN
- Convert.ToInt32 casts -> CAST(... AS INTEGER)
- Dead-end customers source (AP1) -> removed

## Appendix: Edge Case Handling

| Edge Case | V1 Behavior | V2 Handling |
|-----------|-------------|-------------|
| NULL current_value | Defaults to 0m, classifies as "Low Value" | `COALESCE(current_value, 0)` -> CASE ELSE "Low Value" |
| NULL risk_profile | Defaults to "Unknown" | `COALESCE(risk_profile, 'Unknown')` |
| NULL account_type | Defaults to "" | `COALESCE(account_type, '')` |
| Boundary: current_value = 200000 | "Medium Value" (strictly greater than) | `> 200000` means 200000 is NOT "High Value" |
| Boundary: current_value = 50000 | "Low Value" (strictly greater than) | `> 50000` means 50000 is NOT "Medium Value" |
| Negative current_value | "Low Value" (< 50000) | Same -- CASE ELSE catches all <= 50000 |
| Empty investments table | Empty DataFrame returned, empty CSV with header only | SQL returns 0 rows, CsvFileWriter writes header only |
| Weekend dates | No data in datalake for weekends -> 0 rows | Same behavior -- DataSourcing returns empty, SQL returns empty |
