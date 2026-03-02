# CustomerBranchActivity — Functional Specification Document

## 1. Overview

**Job:** CustomerBranchActivityV2
**Tier:** Tier 2 — DataSourcing + Minimal External + CsvFileWriter

Produces a per-customer count of branch visits enriched with customer name information, using the effective date range injected by the executor. The V1 implementation uses a full External module (`CustomerBranchActivityBuilder`) for logic that is fundamentally a GROUP BY count with a LEFT JOIN. The V2 replaces the row-by-row C# iteration with clean, set-based LINQ in a minimal External module.

**Why not Tier 1 (pure SQL)?** The framework's `Transformation` module registers DataFrames as SQLite tables via `RegisterTable`, which silently skips empty DataFrames (`if (!df.Rows.Any()) return;` — [Lib/Modules/Transformation.cs:46]). When either `customers` or `branch_visits` returns 0 rows for a given effective date (weekend guard scenario per BR-3 and BR-4), the SQL query would reference a non-existent table and throw. The V1 behavior is to produce an empty DataFrame with the correct schema — not to fail. A minimal External module is required to handle this empty-guard behavior that SQLite's Transformation cannot express.

**Why not Tier 1 with SQL workaround?** The `Transformation` module executes a single SQL statement via `ExecuteReader` ([Lib/Modules/Transformation.cs:37-38]). There is no mechanism to conditionally skip execution, run DDL, or handle missing tables within a single SQL statement.

## 2. V2 Module Chain

```
DataSourcing("branch_visits")  →  DataSourcing("customers")  →  External(CustomerBranchActivityV2Processor)  →  CsvFileWriter
```

| Step | Module | Purpose |
|------|--------|---------|
| 1 | DataSourcing | Pull `branch_visits` (columns: `customer_id`) from `datalake` for the effective date range |
| 2 | DataSourcing | Pull `customers` (columns: `id`, `first_name`, `last_name`) from `datalake` for the effective date range |
| 3 | External | Empty-guard check + set-based aggregation via LINQ: GROUP BY customer_id, COUNT visits, LEFT JOIN customer names, assign single as_of from first branch_visits row |
| 4 | CsvFileWriter | Write `output` DataFrame to CSV with header, CRLF line endings, Append mode |

**Removed from V1 chain:**
- DataSourcing for `branches` table (AP1: dead-end sourcing — sourced but never consumed by External module; see Section 3)
- Unused columns from `branch_visits`: `visit_id`, `branch_id`, `visit_purpose` (AP4: unused columns; see Section 3)

## 3. Anti-Pattern Analysis

### Anti-Patterns Eliminated

| ID | Anti-Pattern | V1 Evidence | V2 Resolution |
|----|-------------|-------------|---------------|
| AP1 | Dead-end sourcing | V1 sources `datalake.branches` ([customer_branch_activity.json:19-24]) but the External module never reads it ([CustomerBranchActivityBuilder.cs:15-16] — only reads `branch_visits` and `customers`). BRD BR-7 confirms. | **Eliminated.** V2 config does not source `branches` at all. |
| AP3 | Unnecessary External module | V1 uses `CustomerBranchActivityBuilder` for logic that is a GROUP BY count + LEFT JOIN — standard SQL operations. The only justification for External in V2 is the empty-table guard (framework limitation), not the business logic itself. | **Partially eliminated.** External module is retained but redesigned: it uses LINQ set-based operations instead of row-by-row foreach loops. The External is justified solely by the framework's inability to handle empty tables in Transformation SQL. |
| AP4 | Unused columns | V1 sources `visit_id`, `branch_id`, `visit_purpose` from `branch_visits` ([customer_branch_activity.json:10]) but the External module only uses `customer_id` ([CustomerBranchActivityBuilder.cs:42-49]). BRD BR-8 confirms. | **Eliminated.** V2 DataSourcing for `branch_visits` only requests `customer_id`. The `as_of` column is automatically appended by the DataSourcing module. |
| AP6 | Row-by-row iteration | V1 iterates `customers.Rows` in a `foreach` to build a lookup dictionary ([CustomerBranchActivityBuilder.cs:33-39]), then iterates `branchVisits.Rows` in another `foreach` to count visits ([CustomerBranchActivityBuilder.cs:42-49]), then iterates `visitCounts` dictionary in a third `foreach` to build output rows ([CustomerBranchActivityBuilder.cs:56-78]). | **Eliminated.** V2 uses LINQ `GroupBy`, `ToDictionary`, and `Select` for set-based operations. |

### Output-Affecting Wrinkles Preserved

| ID | Wrinkle | V1 Behavior | V2 Replication Strategy |
|----|---------|-------------|------------------------|
| BR-5 (not a cataloged W-code, but output-affecting) | Single as_of from first row | V1 takes `as_of` from `branchVisits.Rows[0]["as_of"]` ([CustomerBranchActivityBuilder.cs:52]) and applies it to ALL output rows, regardless of which dates individual visits occurred on. | V2 replicates this exactly: takes `as_of` from the first row of the `branch_visits` DataFrame. Since DataSourcing orders by `as_of` ([DataSourcing.cs:85] `ORDER BY as_of`), the first row's `as_of` is the earliest date in the effective range. Comment in code: `// V1 behavior: single as_of from first branch_visits row applied to all output rows [CustomerBranchActivityBuilder.cs:52]` |
| BR-9 (not a cataloged W-code, but output-affecting) | Dictionary insertion order | V1 output row order follows `visitCounts` dictionary enumeration order, which is insertion order — the order each customer_id is first encountered in `branch_visits` ([CustomerBranchActivityBuilder.cs:56-78]). Since DataSourcing orders by `as_of`, this means customers are ordered by their earliest visit date, then by row order within that date. | V2 replicates by using LINQ that preserves insertion order: `GroupBy` on the original sequence preserves group order by first appearance. |
| BR-6 (not a cataloged W-code, but output-affecting) | Null names for missing customers | When a `customer_id` in `branch_visits` has no match in `customers`, `first_name` and `last_name` are null ([CustomerBranchActivityBuilder.cs:61-68]). | V2 uses a LEFT-style lookup: if customer_id is not in the customer dictionary, names default to null. |
| BR-10 (not a cataloged W-code, but output-affecting) | Cross-date aggregation | Visit counts aggregate across ALL as_of dates in the effective range — not per-date ([CustomerBranchActivityBuilder.cs:42-49]). | V2 replicates: the LINQ GroupBy operates on the full `branch_visits` DataFrame (which contains all dates in the effective range) without date filtering. |

### Anti-Patterns Not Applicable

| ID | Why Not Applicable |
|----|-------------------|
| AP2 | No evidence of cross-job logic duplication for this specific job |
| AP5 | No asymmetric NULL handling — null names are consistently applied (BR-6) |
| AP7 | No magic values or hardcoded thresholds in this job |
| AP8 | No SQL in V1 (External module only); V2 does not use Transformation SQL |
| AP9 | Job name accurately describes output (customer branch activity) |
| AP10 | DataSourcing uses executor-injected effective dates (no over-sourcing) |
| W1-W12 | No cataloged wrinkles apply. No Sunday skip, weekend fallback, boundary rows, integer division, banker's rounding, double epsilon, trailer issues, or header-every-append behavior. |

## 4. Output Schema

| Column | Type | Source | Transformation | BRD Requirement |
|--------|------|--------|---------------|-----------------|
| `customer_id` | integer | `branch_visits.customer_id` | GROUP BY key — each unique customer_id produces one output row | BR-1 |
| `first_name` | string (nullable) | `customers.first_name` | Lookup by customer_id; null if no match in customers | BR-2, BR-6 |
| `last_name` | string (nullable) | `customers.last_name` | Lookup by customer_id; null if no match in customers | BR-2, BR-6 |
| `as_of` | date/string | `branch_visits.Rows[0]["as_of"]` | Single value from first row of branch_visits, applied to all output rows | BR-5 |
| `visit_count` | integer | `branch_visits` | COUNT of all rows per customer_id across all as_of dates | BR-1, BR-10 |

**Column order:** `customer_id`, `first_name`, `last_name`, `as_of`, `visit_count` (matches V1 output column order per [CustomerBranchActivityBuilder.cs:10-13]).

## 5. SQL Design

**Not applicable — V2 uses LINQ in a minimal External module instead of Transformation SQL.**

The equivalent SQL logic (for documentation/traceability purposes) would be:

```sql
-- This SQL is NOT used in V2; it documents the logical intent of the LINQ operations.
-- It cannot be used in Transformation because empty DataFrames are not registered as SQLite tables.

SELECT
    bv.customer_id,
    c.first_name,
    c.last_name,
    (SELECT as_of FROM branch_visits ORDER BY ROWID LIMIT 1) AS as_of,
    COUNT(*) AS visit_count
FROM branch_visits bv
LEFT JOIN (
    -- Last-write-wins: latest as_of per customer takes precedence
    SELECT id, first_name, last_name
    FROM customers
    GROUP BY id
    HAVING as_of = MAX(as_of)
) c ON bv.customer_id = c.id
GROUP BY bv.customer_id
ORDER BY MIN(ROWID)  -- preserves first-encounter order
```

**Note:** The actual V1 customer lookup uses dictionary last-write-wins semantics ([CustomerBranchActivityBuilder.cs:35] `customerNames[custId] = ...` overwrites on duplicate keys). Since DataSourcing orders by `as_of`, the last write for each customer_id corresponds to the latest `as_of` date. The LINQ implementation replicates this.

## 6. V2 Job Config JSON

```json
{
  "jobName": "CustomerBranchActivityV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "branch_visits",
      "schema": "datalake",
      "table": "branch_visits",
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
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.CustomerBranchActivityV2Processor"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/customer_branch_activity.csv",
      "includeHeader": true,
      "writeMode": "Append",
      "lineEnding": "CRLF"
    }
  ]
}
```

**Changes from V1 config:**
- **Removed:** `branches` DataSourcing entry (AP1: dead-end sourcing)
- **Removed:** `visit_id`, `branch_id`, `visit_purpose` from `branch_visits` columns (AP4: unused columns)
- **Changed:** External module typeName from `CustomerBranchActivityBuilder` to `CustomerBranchActivityV2Processor`
- **Changed:** jobName from `CustomerBranchActivity` to `CustomerBranchActivityV2`
- **Changed:** outputFile path from `Output/curated/` to `Output/double_secret_curated/`
- **Preserved:** `firstEffectiveDate`, `includeHeader`, `writeMode` (Append), `lineEnding` (CRLF), no `trailerFormat`

## 7. Writer Config

| Property | Value | Matches V1? | Evidence |
|----------|-------|-------------|---------|
| type | CsvFileWriter | Yes | [customer_branch_activity.json:32] |
| source | `output` | Yes | [customer_branch_activity.json:33] |
| outputFile | `Output/double_secret_curated/customer_branch_activity.csv` | Path changed to V2 output directory; filename matches | [customer_branch_activity.json:34] |
| includeHeader | `true` | Yes | [customer_branch_activity.json:35] |
| writeMode | `Append` | Yes | [customer_branch_activity.json:36] |
| lineEnding | `CRLF` | Yes | [customer_branch_activity.json:37] |
| trailerFormat | (absent — no trailer) | Yes | V1 config has no trailerFormat field |

**CsvFileWriter Append behavior note:** Per [CsvFileWriter.cs:42-47], in Append mode when the file already exists, the header is NOT re-written. The header is only written on the first execution (when the file doesn't exist yet). This matches V1 behavior since V1's External module does not directly write the file — CsvFileWriter handles it.

## 8. Proofmark Config Design

```yaml
comparison_target: "customer_branch_activity"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

**Rationale:**
- **reader: csv** — V1 and V2 both use CsvFileWriter
- **header_rows: 1** — `includeHeader: true` in writer config; header written on first execution only (Append mode)
- **trailer_rows: 0** — No `trailerFormat` in writer config; no trailer rows in output
- **threshold: 100.0** — All output values are integers or strings; no floating-point arithmetic. Byte-identical output expected.
- **No excluded columns** — BRD identifies no non-deterministic fields. All values are derived deterministically from source data.
- **No fuzzy columns** — No floating-point calculations; `visit_count` is an integer count, names are string lookups, `as_of` is a date.

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision |
|-----------------|-------------|-----------------|
| BR-1: Aggregate visit count per customer | Section 4 (visit_count column), Section 5 (LINQ GroupBy) | LINQ `GroupBy(customer_id).Count()` replaces V1's foreach loop |
| BR-2: Customer name lookup by id | Section 4 (first_name, last_name columns), Section 5 (LINQ ToDictionary) | LINQ ToDictionary for customer lookup; last-write-wins preserved via DataSourcing ORDER BY as_of |
| BR-3: Weekend guard on empty customers | Section 1 (Tier 2 justification), External Module Design (empty check) | External module checks `customers.Count == 0` and returns empty output DataFrame |
| BR-4: Empty visits guard | Section 1 (Tier 2 justification), External Module Design (empty check) | External module checks `branchVisits.Count == 0` and returns empty output DataFrame |
| BR-5: Single as_of from first row | Section 3 (Output-Affecting Wrinkles), Section 4 (as_of column) | External module reads `branchVisits.Rows[0]["as_of"]` — documented V1 behavior replication |
| BR-6: Null names for missing customers | Section 3 (Output-Affecting Wrinkles), Section 4 (nullable first_name, last_name) | Lookup returns null when customer_id not found in dictionary |
| BR-7: branches table unused | Section 3 (AP1 eliminated), Section 6 (branches removed from config) | `branches` DataSourcing entry removed entirely |
| BR-8: Unused sourced columns | Section 3 (AP4 eliminated), Section 6 (columns trimmed) | Only `customer_id` sourced from `branch_visits`; `visit_id`, `branch_id`, `visit_purpose` removed |
| BR-9: Dictionary insertion order | Section 3 (Output-Affecting Wrinkles), External Module Design | LINQ GroupBy preserves first-encounter order; output rows ordered by first appearance of customer_id |
| BR-10: Cross-date aggregation | Section 3 (Output-Affecting Wrinkles), Section 4 (visit_count column) | GroupBy operates on full DataFrame across all as_of dates — no per-date filtering |

| Anti-Pattern | FSD Section | Resolution |
|-------------|-------------|------------|
| AP1: Dead-end sourcing (branches) | Section 3, Section 6 | Eliminated — branches not sourced in V2 |
| AP3: Unnecessary External module | Section 1, Section 3 | Partially eliminated — External retained but justified by framework limitation (empty table handling); logic is set-based LINQ, not row-by-row |
| AP4: Unused columns | Section 3, Section 6 | Eliminated — only `customer_id` sourced from branch_visits |
| AP6: Row-by-row iteration | Section 3, External Module Design | Eliminated — three foreach loops replaced with LINQ set-based operations |

## 10. External Module Design

**File:** `ExternalModules/CustomerBranchActivityV2Processor.cs`

**Justification:** Framework's Transformation module cannot handle empty DataFrames (tables are not registered in SQLite when empty — [Lib/Modules/Transformation.cs:46]). Business rules BR-3 and BR-4 require returning an empty DataFrame with correct schema when source data is empty. This cannot be expressed in a single SQL statement.

**Design:**

```csharp
// CustomerBranchActivityV2Processor.cs
// Tier 2 External: handles empty-guard + set-based aggregation via LINQ.
// Justified by framework limitation: Transformation.RegisterTable skips
// empty DataFrames [Lib/Modules/Transformation.cs:46], causing SQL to fail
// on missing tables when source data is empty (BR-3, BR-4).

public class CustomerBranchActivityV2Processor : IExternalStep
{
    // Output schema columns — matches V1 output exactly
    private static readonly List<string> OutputColumns = new()
    {
        "customer_id", "first_name", "last_name", "as_of", "visit_count"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var branchVisits = sharedState.GetValueOrDefault("branch_visits") as DataFrame;
        var customers = sharedState.GetValueOrDefault("customers") as DataFrame;

        // BR-3: Weekend guard — if customers is null or empty, produce empty output
        if (customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // BR-4: If branch_visits is null or empty, produce empty output
        if (branchVisits == null || branchVisits.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), OutputColumns);
            return sharedState;
        }

        // BR-2: Build customer name lookup (last-write-wins for duplicate customer_ids
        // across as_of dates). DataSourcing orders by as_of [DataSourcing.cs:85],
        // so the last entry per id is the latest as_of date.
        var customerNames = customers.Rows
            .GroupBy(r => Convert.ToInt32(r["id"]))
            .ToDictionary(
                g => g.Key,
                g => {
                    var last = g.Last(); // last-write-wins (latest as_of)
                    return (
                        firstName: last["first_name"]?.ToString() ?? "",
                        lastName: last["last_name"]?.ToString() ?? ""
                    );
                }
            );

        // BR-5: Single as_of from first branch_visits row, applied to all output rows.
        // V1 behavior: branchVisits.Rows[0]["as_of"] [CustomerBranchActivityBuilder.cs:52]
        var asOf = branchVisits.Rows[0]["as_of"];

        // BR-1, BR-10: Aggregate visit count per customer across ALL as_of dates.
        // LINQ GroupBy preserves group order by first appearance (BR-9).
        var visitGroups = branchVisits.Rows
            .GroupBy(r => Convert.ToInt32(r["customer_id"]));

        var outputRows = visitGroups.Select(g =>
        {
            var customerId = g.Key;
            var visitCount = g.Count();

            // BR-6: Null names when customer_id not found in customers lookup
            string? firstName = null;
            string? lastName = null;
            if (customerNames.TryGetValue(customerId, out var names))
            {
                firstName = names.firstName;
                lastName = names.lastName;
            }

            return new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = customerId,
                ["first_name"] = firstName,
                ["last_name"] = lastName,
                ["as_of"] = asOf,
                ["visit_count"] = visitCount
            });
        }).ToList();

        sharedState["output"] = new DataFrame(outputRows, OutputColumns);
        return sharedState;
    }
}
```

**Key differences from V1 External module:**
1. **AP1 eliminated:** Does not reference `branches` — only reads `branch_visits` and `customers` from shared state (same as V1 runtime behavior, but V2 config doesn't source `branches` at all)
2. **AP4 eliminated:** Only expects `customer_id` and `as_of` from `branch_visits` (V2 DataSourcing only pulls `customer_id`; `as_of` is auto-appended)
3. **AP6 eliminated:** Three `foreach` loops replaced with LINQ `GroupBy`, `ToDictionary`, and `Select`
4. **All BR-codes preserved:** Empty guards (BR-3, BR-4), single as_of (BR-5), null names (BR-6), insertion order (BR-9), cross-date aggregation (BR-10)
5. **Every V1 behavior replication is documented with a comment citing the BRD requirement and V1 source evidence**

## Open Questions (from BRD)

| ID | Question | FSD Resolution |
|----|----------|----------------|
| OQ-1 | Is the single as_of from the first branch_visit row intentional? | Replicated as-is for output equivalence (BR-5). Documented with comment. |
| OQ-2 | Why is the branches table sourced but never used? | Eliminated as AP1 in V2. Vestigial or planned enrichment that was never implemented. |
