# InvestmentAccountOverview — Functional Specification Document

## 1. Overview & Tier Selection

**Tier: 2 (Framework + Minimal External)**

This job produces a denormalized CSV of investment accounts enriched with customer first and last names. Each investment row maps 1:1 to an output row. The job includes a Sunday skip behavior (W1) that returns an empty DataFrame on Sundays.

**Why Tier 2 (not Tier 1):** The Sunday skip (W1) depends on `__maxEffectiveDate` from shared state. The Transformation module's SQL runs inside SQLite, which has no access to shared state values — it can only query DataFrames registered as tables. The `__maxEffectiveDate` is a `DateOnly` scalar in shared state, not a DataFrame. The SQL cannot evaluate `DayOfWeek == Sunday` against the effective date.

Additionally, when DataSourcing returns zero rows (which happens on Sundays since the datalake has no weekend data), the Transformation module's `RegisterTable` skips empty DataFrames entirely (Transformation.cs:46 — `if (!df.Rows.Any()) return;`). This means the SQL would fail with a "table not found" error on days with no source data.

A minimal External module handles *only* the Sunday guard and the empty-input guard, then performs the LEFT JOIN logic and outputs the result DataFrame. DataSourcing still handles all data retrieval. The framework's CsvFileWriter handles all output.

**Why not Tier 1:** SQL alone cannot implement the Sunday skip without access to `__maxEffectiveDate`. Even if the Sunday skip were removed, the empty-table registration issue in `Transformation` would cause SQL errors on zero-data days.

**Why not Tier 3:** DataSourcing is perfectly capable of sourcing both tables with effective date injection. There is no reason to bypass it.

## 2. V2 Module Chain

```
DataSourcing (investments) → DataSourcing (customers) → External (InvestmentAccountOverviewV2Processor) → CsvFileWriter
```

| Step | Module Type | Details |
|------|-------------|---------|
| 1 | DataSourcing | Source `datalake.investments` with columns: `investment_id`, `customer_id`, `account_type`, `current_value`, `risk_profile`. Store as `investments`. |
| 2 | DataSourcing | Source `datalake.customers` with columns: `id`, `first_name`, `last_name`. Store as `customers`. |
| 3 | External | `InvestmentAccountOverviewV2Processor` — Sunday guard, empty-input guard, LEFT JOIN investments to customers, output 8-column DataFrame as `output`. |
| 4 | CsvFileWriter | Write `output` to `Output/double_secret_curated/investment_account_overview.csv` with header, trailer, LF line endings, Overwrite mode. |

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (must reproduce)

| W-Code | Applies? | V1 Behavior | V2 Handling |
|--------|----------|-------------|-------------|
| W1 | **YES** | Sunday skip — returns empty DataFrame on Sundays | External module checks `__maxEffectiveDate.DayOfWeek == Sunday` and returns empty DataFrame. Comment: `// V1 behavior: no output on Sundays (W1)` |
| W2-W12 | No | Not present in this job | N/A |

### Code-Quality Anti-Patterns (must eliminate)

| AP-Code | Applies? | V1 Problem | V2 Fix |
|---------|----------|------------|--------|
| AP1 | **YES** | V1 sources `advisor_id` from investments — never used in output | **Eliminated.** V2 DataSourcing does not include `advisor_id`. |
| AP3 | **PARTIAL** | V1 uses External for logic that is largely a LEFT JOIN expressible in SQL. However, the Sunday skip and empty-table issues prevent a pure Tier 1 approach. | **Partially eliminated.** Moved from Tier 3 (External does everything) to Tier 2 (DataSourcing handles data retrieval, External handles only business logic). The External is minimal and focused. |
| AP4 | **YES** | V1 sources `prefix`, `suffix` from customers and `advisor_id` from investments — none appear in output | **Eliminated.** V2 sources only the columns needed: `id`, `first_name`, `last_name` from customers; `investment_id`, `customer_id`, `account_type`, `current_value`, `risk_profile` from investments. |
| AP6 | **YES** | V1 uses row-by-row `foreach` with a Dictionary lookup to join investments to customers | **Partially eliminated.** The External module uses LINQ's `ToDictionary` for the customer lookup and iterates investments — this is still per-row but is the idiomatic C# pattern for a hash-join. Pure SQL (Tier 1) would eliminate this entirely, but Tier 2 is required due to W1. The iteration pattern is clean and well-structured. |
| AP2, AP5, AP7, AP8, AP9, AP10 | No | Not present in this job | N/A |

## 4. Output Schema

| Column | Source | Type | Transformation | Evidence |
|--------|--------|------|----------------|----------|
| investment_id | investments.investment_id | int | `Convert.ToInt32` | [BRD BR-4, InvestmentAccountOverviewBuilder.cs:57] |
| customer_id | investments.customer_id | int | `Convert.ToInt32` | [BRD BR-4, InvestmentAccountOverviewBuilder.cs:50,58] |
| first_name | customers.first_name | string | Null coalesced to `""` via lookup; `""` if no matching customer | [BRD BR-3, InvestmentAccountOverviewBuilder.cs:42,59] |
| last_name | customers.last_name | string | Null coalesced to `""` via lookup; `""` if no matching customer | [BRD BR-3, InvestmentAccountOverviewBuilder.cs:43,60] |
| account_type | investments.account_type | string | Null coalesced to `""` | [BRD BR-4, InvestmentAccountOverviewBuilder.cs:61] |
| current_value | investments.current_value | decimal | `Convert.ToDecimal`, no rounding | [BRD BR-6, InvestmentAccountOverviewBuilder.cs:62] |
| risk_profile | investments.risk_profile | string | Null coalesced to `""` | [BRD BR-4, InvestmentAccountOverviewBuilder.cs:63] |
| as_of | investments.as_of | date | Row-level pass-through (NOT `__maxEffectiveDate`) | [BRD BR-5, InvestmentAccountOverviewBuilder.cs:64] |

### Column Order

Columns must appear in this exact order: `investment_id, customer_id, first_name, last_name, account_type, current_value, risk_profile, as_of`

Evidence: [InvestmentAccountOverviewBuilder.cs:10-14]

### Trailer Row

Format: `TRAILER|{row_count}|{date}` — handled by the framework's CsvFileWriter. `{row_count}` = number of data rows, `{date}` = `__maxEffectiveDate`.

Evidence: [BRD BR-9, investment_account_overview.json:29]

## 5. SQL Design

**N/A** — This job uses Tier 2 with an External module instead of a Transformation (SQL) module. The join logic is implemented in C# within the External module.

**Rationale:** The Sunday skip (W1) requires access to `__maxEffectiveDate` from shared state, which is not available inside SQLite. Additionally, on days with empty source data, `Transformation.RegisterTable` skips empty DataFrames (Transformation.cs:46), which would cause SQL execution to fail with missing table errors.

If the Sunday skip were not required and empty-data handling were not an issue, the equivalent SQL would be:

```sql
-- Reference-only: what a Tier 1 SQL would look like
SELECT
    CAST(i.investment_id AS INTEGER) AS investment_id,
    CAST(i.customer_id AS INTEGER) AS customer_id,
    COALESCE(c.first_name, '') AS first_name,
    COALESCE(c.last_name, '') AS last_name,
    COALESCE(i.account_type, '') AS account_type,
    CAST(i.current_value AS REAL) AS current_value,
    COALESCE(i.risk_profile, '') AS risk_profile,
    i.as_of
FROM investments i
LEFT JOIN customers c ON i.customer_id = c.id AND i.as_of = c.as_of
ORDER BY i.ROWID
```

Note: The as_of join condition would be needed because the datalake uses full-load snapshots — without it, a multi-day date range would produce a cross-join across dates.

## 6. V2 Job Config JSON

```json
{
  "jobName": "InvestmentAccountOverviewV2",
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
      "type": "DataSourcing",
      "resultName": "customers",
      "schema": "datalake",
      "table": "customers",
      "columns": ["id", "first_name", "last_name"]
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.InvestmentAccountOverviewV2Processor"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/investment_account_overview.csv",
      "includeHeader": true,
      "trailerFormat": "TRAILER|{row_count}|{date}",
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

**Config changes from V1:**
- `jobName`: `InvestmentAccountOverview` → `InvestmentAccountOverviewV2`
- `outputFile`: `Output/curated/...` → `Output/double_secret_curated/...`
- DataSourcing `investments`: removed `advisor_id` (AP4 — unused column)
- DataSourcing `customers`: removed `prefix` and `suffix` (AP4 — unused columns)
- External `typeName`: `...InvestmentAccountOverviewBuilder` → `...InvestmentAccountOverviewV2Processor`
- All writer config parameters (`includeHeader`, `trailerFormat`, `writeMode`, `lineEnding`) preserved exactly from V1.

## 7. Writer Config

| Property | Value | Matches V1? |
|----------|-------|-------------|
| type | CsvFileWriter | Yes |
| source | `output` | Yes |
| outputFile | `Output/double_secret_curated/investment_account_overview.csv` | Path changed per V2 convention |
| includeHeader | `true` | Yes |
| trailerFormat | `TRAILER|{row_count}|{date}` | Yes |
| writeMode | `Overwrite` | Yes |
| lineEnding | `LF` | Yes |

## 8. Proofmark Config Design

```yaml
comparison_target: "investment_account_overview"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

**Justification:**
- **reader: csv** — V1 and V2 both use CsvFileWriter.
- **header_rows: 1** — `includeHeader: true` in the writer config.
- **trailer_rows: 1** — `trailerFormat` is present and `writeMode` is `Overwrite`. In Overwrite mode, the file ends with exactly one trailer row.
- **threshold: 100.0** — All fields are deterministic. No non-deterministic fields identified (BRD: "None identified").
- **No excluded columns** — No non-deterministic fields.
- **No fuzzy columns** — `current_value` is passed through as `Convert.ToDecimal` with no rounding; both V1 and V2 will use the same conversion. No floating-point accumulation (W6 does not apply).

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision |
|-----------------|-------------|-----------------|
| BR-1: Sunday skip | Tier Selection, Anti-Pattern W1, External Module Design | External module implements Sunday guard against `__maxEffectiveDate` |
| BR-2: Empty input guard | External Module Design | External module returns empty DataFrame when investments or customers is null/empty |
| BR-3: Customer name lookup | External Module Design, Output Schema | LEFT JOIN via Dictionary lookup; missing customers default to empty strings |
| BR-4: 1:1 investment-to-output mapping | External Module Design, Output Schema | One output row per investment row |
| BR-5: Row-level as_of | Output Schema, External Module Design | `as_of` comes from each investment row, not `__maxEffectiveDate` |
| BR-6: No rounding on current_value | Output Schema, External Module Design | `Convert.ToDecimal` with no rounding |
| BR-7: prefix/suffix sourced but unused | Anti-Pattern AP4 | **Eliminated** — V2 does not source prefix or suffix |
| BR-8: advisor_id sourced but unused | Anti-Pattern AP4, AP1 | **Eliminated** — V2 does not source advisor_id |
| BR-9: Trailer format | Writer Config, Proofmark Config | Framework CsvFileWriter handles `TRAILER|{row_count}|{date}` |
| BR-10: Effective date injection | V2 Module Chain | DataSourcing uses runtime-injected `__minEffectiveDate`/`__maxEffectiveDate` |

| Anti-Pattern | Disposition |
|--------------|-------------|
| W1 (Sunday skip) | **Reproduced** with clean guard clause and comment |
| AP1 (Dead-end sourcing) | **Eliminated** — removed `advisor_id` from investments DataSourcing |
| AP3 (Unnecessary External) | **Partially eliminated** — External is now minimal (Tier 2), not full pipeline (Tier 3). DataSourcing handles data retrieval; CsvFileWriter handles output. |
| AP4 (Unused columns) | **Eliminated** — removed `prefix`, `suffix` from customers; removed `advisor_id` from investments |
| AP6 (Row-by-row iteration) | **Partially addressed** — External uses Dictionary-based hash-join, which is idiomatic C#. Full SQL elimination blocked by W1 requirement. |

## 10. External Module Design

### Class: `InvestmentAccountOverviewV2Processor`

**File:** `ExternalModules/InvestmentAccountOverviewV2Processor.cs`

**Responsibility:** Minimal Tier 2 External — implements Sunday skip guard, empty-input guard, and LEFT JOIN of investments to customers. Does NOT handle data retrieval (DataSourcing does that) or file output (CsvFileWriter does that).

**Interface:** Implements `IExternalStep`

### Pseudocode

```
Execute(sharedState):
    1. Define output columns: [investment_id, customer_id, first_name, last_name,
       account_type, current_value, risk_profile, as_of]

    2. Read __maxEffectiveDate from shared state (fallback: DateOnly.FromDateTime(DateTime.Today))

    3. // V1 behavior: no output on Sundays (W1)
       If maxDate.DayOfWeek == Sunday:
           Set sharedState["output"] = empty DataFrame with output columns
           Return sharedState

    4. Read "investments" and "customers" DataFrames from shared state

    5. If investments is null/empty OR customers is null/empty:
           Set sharedState["output"] = empty DataFrame with output columns
           Return sharedState

    6. Build customer lookup: Dictionary<int, (string firstName, string lastName)>
       For each customer row:
           customerLookup[id] = (first_name ?? "", last_name ?? "")

    7. Build output rows:
       For each investment row:
           customerId = Convert.ToInt32(row["customer_id"])
           Look up (firstName, lastName) from customerLookup — default ("", "") if not found
           Create output row:
               investment_id = Convert.ToInt32(row["investment_id"])
               customer_id = customerId
               first_name = looked-up firstName
               last_name = looked-up lastName
               account_type = row["account_type"]?.ToString() ?? ""
               current_value = Convert.ToDecimal(row["current_value"])
               risk_profile = row["risk_profile"]?.ToString() ?? ""
               as_of = row["as_of"]   // Row-level, NOT __maxEffectiveDate

    8. Set sharedState["output"] = new DataFrame(outputRows, outputColumns)
    9. Return sharedState
```

### Key Design Notes

1. **Sunday guard (W1):** The guard checks `DayOfWeek.Sunday` on `__maxEffectiveDate`. This is placed before any data processing. The comment `// V1 behavior: no output on Sundays (W1)` makes the intent explicit.

2. **Empty input guard:** V1 returns empty output if either `investments` or `customers` is null or has zero rows. V2 reproduces this exactly. Note: this means if there are investments but zero customers, the output is empty — this is V1's behavior, not a LEFT JOIN with empty names.

3. **Customer lookup via Dictionary:** Uses `ToDictionary` or equivalent to build a hash map keyed on customer `id`. This replaces V1's row-by-row dictionary build with the same pattern but cleaner code.

4. **No LINQ accumulation:** No monetary accumulation occurs, so W6 (double epsilon) does not apply. `Convert.ToDecimal` is used for `current_value` exactly as V1 does.

5. **as_of preservation:** Each output row's `as_of` comes from the investment row's own `as_of` value, not from `__maxEffectiveDate`. This is critical for multi-day date ranges (BR-5).

6. **Column order:** The output DataFrame constructor receives columns in the exact order defined in V1's `outputColumns` list (line 10-14 of the V1 External module).
