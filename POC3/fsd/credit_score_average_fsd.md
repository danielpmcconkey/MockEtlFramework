# CreditScoreAverage -- Functional Specification Document

## 1. Overview

**Job Name:** CreditScoreAverageV2
**Config File:** `credit_score_average_v2.json`
**Module Tier:** Tier 2 (Framework + Minimal External)

This job produces a per-customer credit score summary by averaging scores across all three bureaus (Equifax, TransUnion, Experian) and includes individual bureau scores, customer name, and effective date. Output is a CSV file with a CRLF line ending and a CONTROL trailer line.

### Tier Justification

Tier 1 (pure SQL) is insufficient because of a **decimal precision requirement**. The V1 External module computes the average score using C# `decimal` arithmetic via LINQ `Average()`, producing up to 28-29 significant digits of precision. SQLite's `AVG()` function returns a `REAL` (IEEE 754 double), which provides only ~15-17 significant digits. The `ToString()` representations of `decimal` vs `double` differ for non-integer averages, and 1459 out of 2230 customers per day have non-integer average scores. This would produce byte-level output differences.

Additionally, the `as_of` column flows through V1 as a `DateOnly` object (from DataSourcing), which `CsvFileWriter.FormatField` renders via `DateOnly.ToString()` (producing locale-dependent format like `"10/01/2024"`). In a pure SQL approach, `as_of` would pass through SQLite as a TEXT string `"2024-10-01"` (due to `Transformation.ToSqliteValue`), producing a different format in the CSV output.

The External module handles ONLY these two concerns:
1. Decimal average computation from SQL-provided SUM and COUNT
2. Proper `DateOnly` passthrough for the `as_of` column

All joining, grouping, filtering, and conditional aggregation is handled by SQL in the Transformation step.

---

## 2. V2 Module Chain

```
DataSourcing (credit_scores) -> DataSourcing (customers) -> Transformation (SQL) -> External (decimal avg + as_of) -> CsvFileWriter
```

### Module 1: DataSourcing -- credit_scores
- **resultName:** `credit_scores`
- **schema:** `datalake`
- **table:** `credit_scores`
- **columns:** `["customer_id", "bureau", "score"]`
- Effective dates injected by executor via shared state.
- Note: `credit_score_id` removed vs V1 (AP4 -- unused column).

### Module 2: DataSourcing -- customers
- **resultName:** `customers`
- **schema:** `datalake`
- **table:** `customers`
- **columns:** `["id", "first_name", "last_name"]`
- Effective dates injected by executor via shared state.
- Note: V1 also sourced these same columns. No change needed.

### Module 3: Transformation (SQL)
- **resultName:** `grouped_scores`
- **sql:** See Section 5 for full SQL design.
- Performs: INNER JOIN between credit_scores and customers, GROUP BY customer_id, conditional aggregation for per-bureau scores, SUM/COUNT for downstream decimal average computation.

### Module 4: External (CreditScoreAverageV2Processor)
- **assemblyPath:** `/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll`
- **typeName:** `ExternalModules.CreditScoreAverageV2Processor`
- Reads `grouped_scores` DataFrame from shared state.
- Computes `avg_score` as `(decimal)score_sum / (decimal)score_count` for each row.
- Reconstructs `as_of` as a `DateOnly` value from the string representation.
- Produces `output` DataFrame with final column order matching V1.

### Module 5: CsvFileWriter
- **source:** `output`
- **outputFile:** `Output/double_secret_curated/credit_score_average.csv`
- **includeHeader:** `true`
- **trailerFormat:** `CONTROL|{date}|{row_count}|{timestamp}`
- **writeMode:** `Overwrite`
- **lineEnding:** `CRLF`

---

## 3. Anti-Pattern Analysis

### Identified Anti-Patterns

| ID | Name | Applies? | V1 Evidence | V2 Action |
|----|------|----------|-------------|-----------|
| AP1 | Dead-end sourcing | YES | V1 sources `segments` table (credit_score_average.json:19-23) but the External module never reads it (CreditScoreAverager.cs:16-17 only retrieves `credit_scores` and `customers`). | **ELIMINATED.** V2 config does not source the segments table. |
| AP3 | Unnecessary External module | PARTIAL | V1 uses a full External module for logic that is mostly expressible in SQL (join, group, conditional aggregation). The decimal average computation is the only part that cannot be done in SQL. | **PARTIALLY ELIMINATED.** V2 moves join/group/aggregation to SQL Transformation. External module reduced to minimal decimal arithmetic and DateOnly reconstruction only. |
| AP4 | Unused columns | YES | V1 sources `credit_score_id` (credit_score_average.json:10) but the External module never references it (CreditScoreAverager.cs:29-31 only uses `customer_id`, `bureau`, `score`). | **ELIMINATED.** V2 DataSourcing for credit_scores omits `credit_score_id`. |
| AP6 | Row-by-row iteration | PARTIAL | V1 uses nested `foreach` loops: one to build score dictionary (CreditScoreAverager.cs:27-37), one to build customer name dictionary (CreditScoreAverager.cs:41-47), one to iterate grouped scores and build output rows with inner loop for bureau matching (CreditScoreAverager.cs:51-95). | **PARTIALLY ELIMINATED.** Join, grouping, and conditional aggregation moved to SQL. External module retains a single loop for decimal division -- minimal procedural logic that cannot be expressed in SQL. |

### Output-Affecting Wrinkles

| ID | Name | Applies? | Evidence | V2 Action |
|----|------|----------|----------|-----------|
| W9 | Wrong writeMode | POSSIBLE | V1 uses `Overwrite` (credit_score_average.json:37). For multi-day auto-advance runs, only the last day's output survives. | **REPRODUCED.** V2 uses the same `Overwrite` mode. Comment in FSD: V1 uses Overwrite -- prior days' data is lost on each run. |

No other W-codes apply to this job. Specifically:
- W1 (Sunday skip): No Sunday check in V1 code.
- W4 (Integer division): Average uses decimal division, not integer division.
- W5 (Banker's rounding): No explicit rounding in V1 code.
- W6 (Double epsilon): V1 uses decimal, not double, for score accumulation.
- W7 (Trailer inflated count): V1 uses the framework's CsvFileWriter for trailer generation, not manual file writing. The framework counts output DataFrame rows, not input rows.
- W8 (Trailer stale date): V1 uses the framework's `{date}` token which reads from `__maxEffectiveDate`.

---

## 4. Output Schema

| Column | Source | Transformation | V1 Evidence |
|--------|--------|---------------|-------------|
| `customer_id` | credit_scores.customer_id | Cast to int. GROUP BY key. | [CreditScoreAverager.cs:29,85] |
| `first_name` | customers.first_name | Pass-through from customer record. NULL coalesced to empty string. | [CreditScoreAverager.cs:44,87] |
| `last_name` | customers.last_name | Pass-through from customer record. NULL coalesced to empty string. | [CreditScoreAverager.cs:45,88] |
| `avg_score` | credit_scores.score | C# `decimal` average of all scores for this customer across all bureaus. | [CreditScoreAverager.cs:61,89] |
| `equifax_score` | credit_scores.score | Score where bureau = 'equifax' (case-insensitive). NULL if no Equifax entry. | [CreditScoreAverager.cs:72-73,90] |
| `transunion_score` | credit_scores.score | Score where bureau = 'transunion' (case-insensitive). NULL if no TransUnion entry. | [CreditScoreAverager.cs:75-76,91] |
| `experian_score` | credit_scores.score | Score where bureau = 'experian' (case-insensitive). NULL if no Experian entry. | [CreditScoreAverager.cs:78-79,92] |
| `as_of` | customers.as_of | Pass-through as `DateOnly` from customer record. Rendered via `DateOnly.ToString()`. | [CreditScoreAverager.cs:46,93] |

### Column Order
Columns appear in the output in exactly this order, matching V1: `customer_id, first_name, last_name, avg_score, equifax_score, transunion_score, experian_score, as_of`.

### NULL Handling
- `first_name`, `last_name`: COALESCE to empty string (V1 uses `?.ToString() ?? ""`).
- `equifax_score`, `transunion_score`, `experian_score`: NULL when bureau not present for customer. CsvFileWriter renders NULL/null as empty field (bare comma).
- `avg_score`: Always non-null for customers in output (they must have at least one score to appear).
- `as_of`: Always non-null (comes from DataSourcing effective date filter).

---

## 5. SQL Design

### Transformation SQL

```sql
SELECT
    cs.customer_id,
    COALESCE(c.first_name, '') AS first_name,
    COALESCE(c.last_name, '') AS last_name,
    SUM(cs.score) AS score_sum,
    COUNT(cs.score) AS score_count,
    MAX(CASE WHEN LOWER(cs.bureau) = 'equifax' THEN cs.score END) AS equifax_score,
    MAX(CASE WHEN LOWER(cs.bureau) = 'transunion' THEN cs.score END) AS transunion_score,
    MAX(CASE WHEN LOWER(cs.bureau) = 'experian' THEN cs.score END) AS experian_score,
    c.as_of
FROM credit_scores cs
INNER JOIN customers c ON cs.customer_id = c.id AND cs.as_of = c.as_of
GROUP BY cs.customer_id, c.first_name, c.last_name, c.as_of
ORDER BY cs.customer_id
```

### SQL Design Rationale

1. **INNER JOIN** between credit_scores and customers: Matches V1 behavior where customers without scores are excluded (BR-8: score-driven iteration) and scores without matching customers are excluded (BR-3: `customerNames.ContainsKey` check). The join condition includes `cs.as_of = c.as_of` to match records within the same effective date.

2. **COALESCE on name fields**: Matches V1's `?.ToString() ?? ""` null coalescing (BR-2 evidence at CreditScoreAverager.cs:44-45).

3. **SUM and COUNT instead of AVG**: AVG would return SQLite REAL (double), losing decimal precision. SUM and COUNT are passed to the External module for decimal division. This is the core reason for Tier 2.

4. **Conditional aggregation (MAX + CASE WHEN)**: Extracts per-bureau scores. `MAX` is used because there is at most one score per bureau per customer per day (verified by data inspection). When no row matches the CASE condition, MAX returns NULL, which matches V1's `DBNull.Value` behavior (BR-2).

5. **LOWER for case-insensitive bureau matching**: Matches V1's `bureau.ToLower()` (CreditScoreAverager.cs:70). Database constraint limits values to `'Equifax'`, `'TransUnion'`, `'Experian'`, but V1 uses case-insensitive matching, so V2 does the same.

6. **ORDER BY customer_id**: V1 iterates `scoresByCustomer` dictionary in insertion order, which follows the credit_scores DataFrame row order. DataSourcing orders by `as_of`; within a single day, PostgreSQL returns rows ordered by `credit_score_id` (observed as ascending customer_id). Explicit ORDER BY ensures deterministic, matching row order.

7. **GROUP BY includes c.first_name, c.last_name, c.as_of**: Required by SQL for non-aggregated columns. Does not change behavior since there is one customer record per id per as_of day.

### Edge Cases in SQL

- **Empty credit_scores or customers**: If either table is empty after DataSourcing, the Transformation module's `RegisterTable` skips empty DataFrames (Transformation.cs:46). The SQL query against the missing table would fail. The External module must handle this case by checking if `grouped_scores` exists and is non-empty, producing an empty output DataFrame if not. This matches V1's null/empty guard (CreditScoreAverager.cs:19-23).

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "CreditScoreAverageV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "credit_scores",
      "schema": "datalake",
      "table": "credit_scores",
      "columns": ["customer_id", "bureau", "score"]
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
      "resultName": "grouped_scores",
      "sql": "SELECT cs.customer_id, COALESCE(c.first_name, '') AS first_name, COALESCE(c.last_name, '') AS last_name, SUM(cs.score) AS score_sum, COUNT(cs.score) AS score_count, MAX(CASE WHEN LOWER(cs.bureau) = 'equifax' THEN cs.score END) AS equifax_score, MAX(CASE WHEN LOWER(cs.bureau) = 'transunion' THEN cs.score END) AS transunion_score, MAX(CASE WHEN LOWER(cs.bureau) = 'experian' THEN cs.score END) AS experian_score, c.as_of FROM credit_scores cs INNER JOIN customers c ON cs.customer_id = c.id AND cs.as_of = c.as_of GROUP BY cs.customer_id, c.first_name, c.last_name, c.as_of ORDER BY cs.customer_id"
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.CreditScoreAverageV2Processor"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/credit_score_average.csv",
      "includeHeader": true,
      "trailerFormat": "CONTROL|{date}|{row_count}|{timestamp}",
      "writeMode": "Overwrite",
      "lineEnding": "CRLF"
    }
  ]
}
```

### Config Changes vs V1

| Field | V1 Value | V2 Value | Reason |
|-------|----------|----------|--------|
| `jobName` | `CreditScoreAverage` | `CreditScoreAverageV2` | V2 naming convention |
| DataSourcing: segments | Present | **Removed** | AP1: dead-end sourcing eliminated |
| DataSourcing: credit_scores columns | `["credit_score_id", "customer_id", "bureau", "score"]` | `["customer_id", "bureau", "score"]` | AP4: unused column eliminated |
| Transformation module | Not present | Added | AP3/AP6: SQL replaces most External logic |
| External typeName | `CreditScoreAverager` | `CreditScoreAverageV2Processor` | V2 naming; minimal scope |
| CsvFileWriter outputFile | `Output/curated/...` | `Output/double_secret_curated/...` | V2 output path |

### Config Preserved from V1

| Field | Value | Reason |
|-------|-------|--------|
| `firstEffectiveDate` | `"2024-10-01"` | Same date range as V1 |
| `includeHeader` | `true` | Output equivalence |
| `trailerFormat` | `"CONTROL\|{date}\|{row_count}\|{timestamp}"` | Output equivalence |
| `writeMode` | `"Overwrite"` | Output equivalence (W9 noted) |
| `lineEnding` | `"CRLF"` | Output equivalence |

---

## 7. Writer Config

- **Writer Type:** CsvFileWriter (matches V1)
- **Output Path:** `Output/double_secret_curated/credit_score_average.csv`
- **includeHeader:** true
- **trailerFormat:** `CONTROL|{date}|{row_count}|{timestamp}`
- **writeMode:** Overwrite
  - // V1 uses Overwrite -- prior days' data is lost on each run. For multi-day auto-advance, only the last effective date's output survives on disk.
- **lineEnding:** CRLF

All writer config parameters match V1 exactly. Only the output path changes.

---

## 8. Proofmark Config Design

### Config File: `POC3/proofmark_configs/credit_score_average.yaml`

```yaml
comparison_target: "credit_score_average"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

### Proofmark Design Rationale

- **reader: csv** -- V1 and V2 both use CsvFileWriter.
- **header_rows: 1** -- V1 config has `includeHeader: true`.
- **trailer_rows: 1** -- V1 config has `trailerFormat` present and `writeMode: Overwrite`. Overwrite mode produces a single trailer at end of file.
- **threshold: 100.0** -- Strict. All data columns must match exactly.
- **No EXCLUDED columns** -- All data columns are deterministic. The `as_of` column is derived from the effective date, not from runtime. The trailer contains `{timestamp}` which is non-deterministic, but Proofmark's `trailer_rows: 1` strips the trailer before comparison, so no exclusion is needed.
- **No FUZZY columns** -- The V2 External module computes `avg_score` using the same C# `decimal` arithmetic as V1. Per-bureau scores are integers passed through directly. No floating-point precision differences expected.

---

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Implementation Element |
|-----------------|-------------|----------------------|
| BR-1: Average across all bureaus per customer | Sec 5 (SQL: SUM/COUNT), Sec 10 (External: decimal division) | SQL computes SUM and COUNT; External divides as decimal |
| BR-2: Per-bureau scores with case-insensitive match, NULL for missing | Sec 5 (SQL: MAX + CASE WHEN + LOWER) | SQL conditional aggregation handles bureau matching and NULL default |
| BR-3: Only customers with scores AND in customers table | Sec 5 (SQL: INNER JOIN) | INNER JOIN excludes unmatched records from both sides |
| BR-4: Empty input produces empty DataFrame with correct schema | Sec 10 (External: empty guard) | External checks for null/empty `grouped_scores`, returns empty DataFrame |
| BR-5: as_of from customers table, not credit_scores | Sec 5 (SQL: c.as_of) | SQL selects `c.as_of` from customers table |
| BR-6: Segments table unused | Sec 3 (AP1 elimination) | Segments DataSourcing removed from V2 config |
| BR-7: Last customer entry per id wins (dictionary overwrite) | Sec 5 (SQL: GROUP BY) | SQL GROUP BY on id with single record per id per as_of makes this moot |
| BR-8: Score-driven iteration (customers without scores excluded) | Sec 5 (SQL: INNER JOIN, credit_scores drives) | INNER JOIN ensures only customers with scores appear |
| OQ-1: Segments table unused | Sec 3 (AP1) | Confirmed unused. Removed from V2. |
| OQ-2: Multiple scores per bureau (last wins) | Sec 5 (SQL: MAX) | MAX returns the single value when only one exists. Verified: no duplicate bureau scores per customer per day in data. |
| Trailer format | Sec 7 (Writer Config) | `CONTROL\|{date}\|{row_count}\|{timestamp}` preserved |
| CRLF line endings | Sec 7 (Writer Config) | `lineEnding: "CRLF"` preserved |
| Overwrite write mode | Sec 7 (Writer Config) | `writeMode: "Overwrite"` preserved |

---

## 10. External Module Design

### File: `ExternalModules/CreditScoreAverageV2Processor.cs`

### Purpose

Minimal Tier 2 External module that performs exactly two operations that cannot be expressed in SQLite SQL:

1. **Decimal average computation**: Divides `score_sum` by `score_count` using C# `decimal` arithmetic to match V1's LINQ `Average()` precision.
2. **DateOnly reconstruction**: Converts the `as_of` string (from SQLite TEXT passthrough) back to a `DateOnly` object so that `CsvFileWriter.FormatField` renders it with the same format as V1.

### Input

Reads `grouped_scores` DataFrame from shared state, produced by the Transformation step. Expected columns:
- `customer_id` (int)
- `first_name` (string, already COALESCE'd)
- `last_name` (string, already COALESCE'd)
- `score_sum` (int/long -- SUM of integer scores)
- `score_count` (int/long -- COUNT of scores)
- `equifax_score` (int or null)
- `transunion_score` (int or null)
- `experian_score` (int or null)
- `as_of` (string -- "yyyy-MM-dd" from SQLite)

### Output

Produces `output` DataFrame in shared state with columns in V1 order:
- `customer_id` (int)
- `first_name` (string)
- `last_name` (string)
- `avg_score` (decimal -- computed from score_sum / score_count)
- `equifax_score` (object -- int or DBNull.Value)
- `transunion_score` (object -- int or DBNull.Value)
- `experian_score` (object -- int or DBNull.Value)
- `as_of` (DateOnly)

### Logic

```csharp
// Pseudocode -- actual implementation by Developer subagent
public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
{
    var outputColumns = new List<string>
    {
        "customer_id", "first_name", "last_name", "avg_score",
        "equifax_score", "transunion_score", "experian_score", "as_of"
    };

    var grouped = sharedState.ContainsKey("grouped_scores")
        ? sharedState["grouped_scores"] as DataFrame : null;

    // BR-4: Empty input guard
    if (grouped == null || grouped.Count == 0)
    {
        sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
        return sharedState;
    }

    var outputRows = new List<Row>();
    foreach (var row in grouped.Rows)
    {
        var scoreSum = Convert.ToDecimal(row["score_sum"]);
        var scoreCount = Convert.ToDecimal(row["score_count"]);

        // Decimal division matches V1's LINQ Average() precision
        var avgScore = scoreSum / scoreCount;

        // Reconstruct DateOnly from SQLite text representation
        var asOfStr = row["as_of"]?.ToString() ?? "";
        var asOf = DateOnly.Parse(asOfStr);

        // Bureau scores: pass through as-is, convert null to DBNull.Value
        // to match V1 behavior (CsvFileWriter renders DBNull.Value as empty)
        object? equifax = row["equifax_score"] is null or DBNull
            ? DBNull.Value : row["equifax_score"];
        object? transunion = row["transunion_score"] is null or DBNull
            ? DBNull.Value : row["transunion_score"];
        object? experian = row["experian_score"] is null or DBNull
            ? DBNull.Value : row["experian_score"];

        outputRows.Add(new Row(new Dictionary<string, object?>
        {
            ["customer_id"] = Convert.ToInt32(row["customer_id"]),
            ["first_name"] = row["first_name"]?.ToString() ?? "",
            ["last_name"] = row["last_name"]?.ToString() ?? "",
            ["avg_score"] = avgScore,
            ["equifax_score"] = equifax,
            ["transunion_score"] = transunion,
            ["experian_score"] = experian,
            ["as_of"] = asOf
        }));
    }

    sharedState["output"] = new DataFrame(outputRows, outputColumns);
    return sharedState;
}
```

### Design Notes

- The External module does NO data sourcing, joining, grouping, or filtering. All of that is handled upstream by DataSourcing and Transformation.
- The module's sole computational responsibility is `decimal` division and `DateOnly` reconstruction.
- NULL handling for bureau scores: SQLite returns `null` for `MAX(CASE WHEN ... END)` when no row matches. The Transformation module's `ReaderToDataFrame` converts `DBNull` to `null` (Transformation.cs:90). The External converts these back to `DBNull.Value` to match V1's behavior, since `CsvFileWriter.FormatField` checks for `null` (rendering as empty) but V1 uses `DBNull.Value`. In practice, `CsvFileWriter` handles both `null` and `DBNull.Value` the same way -- `FormatField` checks `val is null` which is `false` for `DBNull.Value`, but `DBNull.Value.ToString()` returns `""`. Either way the CSV field is empty. However, to be maximally safe, the External should match V1's use of `DBNull.Value`.

---

## Appendix A: Data Observations

The following observations were made by querying the `datalake` schema. They inform design decisions but are not requirements -- V2 must handle the general case.

- **2230 customers** per effective date, all present in both credit_scores and customers tables.
- **Exactly 3 scores per customer per day** (one per bureau: Equifax, TransUnion, Experian).
- **No duplicate scores per bureau per customer per day** (verified via GROUP BY HAVING COUNT > 1).
- **Bureau values** are constrained by CHECK constraint to `'Equifax'`, `'TransUnion'`, `'Experian'` (mixed case).
- **Score values** are integers in range 300-850 (CHECK constraint).
- **1459 of 2230 customers** have non-integer average scores (sum not divisible by 3), confirming the decimal precision concern.
- **Natural ordering** of credit_scores is by ascending customer_id within each as_of date.
