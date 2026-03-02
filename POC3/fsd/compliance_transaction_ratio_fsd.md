# ComplianceTransactionRatio — Functional Specification Document

## 1. Overview

V2 replaces V1's monolithic External module (`ComplianceTransactionRatioWriter`) with a **Tier 2** pipeline: `DataSourcing -> Transformation (SQL) -> External (minimal: trailer-aware CSV writer)`. The V1 External module performs grouping, counting, integer division, NULL coalescing, and direct file I/O -- all in one class. V2 moves the core grouping logic into a SQL Transformation, leaving the cross-DataFrame computation (transaction count, ratio calculation) and file-writing concern to a minimal External module.

**Tier justification:** The grouping/counting/ordering logic (GROUP BY event_type, COUNT, NULL coalescing, ORDER BY) is fully expressible in SQLite SQL. Tier 1 would be ideal. However, two framework limitations prevent it:

1. **W7 trailer inflation:** The trailer line requires an inflated input row count (sum of compliance_events + transactions row counts). The framework's CsvFileWriter only supports `{row_count}` which substitutes the output DataFrame's row count (the number of grouped rows, e.g. 5), not the inflated sum (~4378). There is no token or mechanism in CsvFileWriter to inject a custom count.

2. **Empty transactions table:** When `transactions` has zero rows, DataSourcing returns an empty DataFrame. Transformation's `RegisterTable` skips empty DataFrames [Transformation.cs:46], so any SQL subquery referencing `transactions` would fail with "no such table." V1 handles this gracefully (`transactions?.Count ?? 0`), producing `events_per_1000_txns = 0`. Moving the transaction count to the External module avoids this SQLite limitation.

**Why not Tier 1:** CsvFileWriter cannot produce the inflated trailer, and SQL cannot safely reference potentially-empty DataFrames.

**Why not Tier 3:** DataSourcing handles data access perfectly, and SQL handles the grouping logic. Only the cross-DataFrame computation and file writing need custom behavior.

## 2. V2 Module Chain

| Step | Module | Config Summary |
|------|--------|---------------|
| 1 | DataSourcing | `compliance_events`: `event_id`, `customer_id`, `event_type`, `status` from `datalake.compliance_events` |
| 2 | DataSourcing | `transactions`: `transaction_id`, `account_id`, `amount` from `datalake.transactions` |
| 3 | Transformation | SQL groups compliance_events by event_type with COALESCE for NULLs, produces `grouped_events` with `event_type` and `event_count` columns |
| 4 | External | `ComplianceTransactionRatioV2Processor`: reads `grouped_events` + raw DataFrame counts from shared state, augments with `txn_count`/`events_per_1000_txns`/`as_of`, writes CSV with inflated trailer |

**Key differences from V1:**
- Grouping logic moved from C# foreach loop to SQL GROUP BY (AP3 partial elimination, AP6 elimination)
- NULL coalescing handled in SQL COALESCE (cleaner than C# null-conditional)
- Direct file I/O retained in minimal External module ONLY because framework CsvFileWriter cannot produce the inflated trailer count (W7)
- External module is minimal: reads pre-grouped data, adds computed columns, writes file

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (W-codes) -- Reproduce

| W-Code | V1 Behavior | V2 Approach | Evidence |
|--------|-------------|-------------|----------|
| W4 | Integer division: `(eventCount * 1000) / txnCount` where both operands are `int`, truncating the result | External module uses C# integer division: `(eventCount * RatePerThousand) / txnCount`. Both operands are `int`, producing identical truncation. Named constant `RatePerThousand = 1000` replaces magic number. Comment documents this is V1 replication. | [ComplianceTransactionRatioWriter.cs:54] |
| W7 | Trailer uses `inputCount = complianceEvents.Count + transactions.Count` (sum of ALL input rows, not output rows). For a typical day: ~115 compliance events + ~4263 transactions = ~4378, but only ~5 output rows. | External module computes `inputCount` from shared state DataFrame counts, writes trailer with inflated count. Comment documents this is V1 replication. | [ComplianceTransactionRatioWriter.cs:28,59] |

### Code-Quality Anti-Patterns (AP-codes) -- Eliminate

| AP Code | V1 Problem | V2 Resolution |
|---------|-----------|---------------|
| AP3 | Entire job logic handled by External module -- grouping, counting, division, NULL handling, file writing all in C# [ComplianceTransactionRatioWriter.cs:6-77] | **Partially eliminated.** Grouping, NULL coalescing, and ordering moved to SQL Transformation. External module retained ONLY for cross-DataFrame computation (txn_count, ratio) and file I/O due to W7 trailer constraint. |
| AP4 | V1 sources `event_id`, `customer_id`, `status` from compliance_events and `transaction_id`, `account_id`, `amount` from transactions, but only uses `event_type` from compliance_events and only COUNT from transactions | **Retained for safety.** Removing unused columns from DataSourcing would not change row counts (the inflated trailer depends only on row count, not column content). However, retaining V1's column list ensures shared state DataFrames are structurally identical, avoiding any risk of subtle divergence. The unused columns are a minor inefficiency. |
| AP5 | NULL event_type coalesced to "Unknown" [ComplianceTransactionRatioWriter.cs:36] | **Reproduced for output equivalence** using `COALESCE(event_type, 'Unknown')` in SQL. This is an output-affecting behavior, not a code-quality issue. Documented with SQL comment. |
| AP6 | Row-by-row `foreach` loop over compliance_events to build eventGroups dictionary [ComplianceTransactionRatioWriter.cs:34-38] | **Eliminated.** Replaced with SQL `GROUP BY COALESCE(event_type, 'Unknown')` with `COUNT(*)`. |

### Anti-Patterns Not Applicable

| AP Code | Why Not Applicable |
|---------|-------------------|
| AP1 | No dead-end sourcing -- both compliance_events (for grouping) and transactions (for count) are used |
| AP2 | No cross-job duplication identified |
| AP7 | The value `1000` in `events_per_1000_txns` is a "per 1,000" rate denominator, not a magic threshold. V2 uses named constant `RatePerThousand` for clarity. |
| AP8 | No complex SQL or unused CTEs -- the query is straightforward |
| AP9 | Job name matches output: compliance_transaction_ratio computes compliance-to-transaction ratios |
| AP10 | DataSourcing uses framework effective date injection (no over-sourcing dates) |

## 4. Output Schema

| Column | Type | Source | Transformation | Evidence |
|--------|------|--------|---------------|----------|
| event_type | text | compliance_events.event_type | GROUP BY key, NULL coalesced to "Unknown" via SQL COALESCE | [ComplianceTransactionRatioWriter.cs:36,50] |
| event_count | integer | Computed | COUNT(*) of compliance_events per event_type (from SQL) | [ComplianceTransactionRatioWriter.cs:37,51] |
| txn_count | integer | transactions | Total row count of ALL transactions DataFrame (from External module) | [ComplianceTransactionRatioWriter.cs:30,52] |
| events_per_1000_txns | integer | Computed | `(event_count * 1000) / txn_count` -- integer division, truncated. 0 if txn_count = 0. (from External module) | [ComplianceTransactionRatioWriter.cs:54] |
| as_of | text | __maxEffectiveDate | Formatted as yyyy-MM-dd string (from External module) | [ComplianceTransactionRatioWriter.cs:25,55] |

**Column order** must match V1 exactly: `event_type, event_count, txn_count, events_per_1000_txns, as_of`.

**Trailer row** (not part of the output DataFrame -- written directly by External module):
- Format: `TRAILER|{inputCount}|{dateStr}`
- `inputCount` = compliance_events.Count + transactions.Count (inflated, W7)
- `dateStr` = __maxEffectiveDate formatted as yyyy-MM-dd (BR-9)

## 5. SQL Design

The Transformation SQL handles compliance_events grouping only. It does NOT reference the `transactions` table, avoiding the empty-table registration problem (see Section 1 tier justification).

```sql
SELECT
    -- BR-8: NULL event_type coalesced to "Unknown"
    -- V1: row["event_type"]?.ToString() ?? "Unknown"
    COALESCE(event_type, 'Unknown') AS event_type,
    COUNT(*) AS event_count
FROM compliance_events
GROUP BY COALESCE(event_type, 'Unknown')
-- BR-5: Alphabetical ordering by event_type
ORDER BY event_type
```

The remaining three output columns (`txn_count`, `events_per_1000_txns`, `as_of`) are computed by the External module because:

1. **`txn_count`** requires the `transactions` DataFrame row count. When `transactions` is empty, its SQLite table is not registered [Transformation.cs:46], so a SQL subquery `(SELECT COUNT(*) FROM transactions)` would fail. The External module safely reads `transactions?.Count ?? 0` from shared state.

2. **`events_per_1000_txns`** depends on `txn_count` and must use integer division (W4). Computed in the External module alongside `txn_count`.

3. **`as_of`** comes from `__maxEffectiveDate` in shared state, not from a SQL table. The External module reads it directly and formats as `yyyy-MM-dd`.

### SQL Notes

- **Integer grouping:** `COUNT(*)` in the SQL naturally produces an integer, which the External module reads and uses directly in the integer division for `events_per_1000_txns`.

- **Empty compliance_events edge case:** If `compliance_events` is empty, DataSourcing returns a zero-row DataFrame. Transformation's `RegisterTable` skips empty DataFrames [Transformation.cs:46]. The SQL will fail with "no such table: compliance_events" and the Transformation will throw. The job fails for that date. V1's behavior when compliance_events is empty is to return an empty output DataFrame and NOT write the CSV file [ComplianceTransactionRatioWriter.cs:18-21]. In V2, the job failure means no file operation occurs. Since this is Overwrite mode, the previous file (if any) is preserved in both V1 and V2. The net effect is identical: no output file for that date.

## 6. V2 Job Config

```json
{
  "jobName": "ComplianceTransactionRatioV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "compliance_events",
      "schema": "datalake",
      "table": "compliance_events",
      "columns": ["event_id", "customer_id", "event_type", "status"]
    },
    {
      "type": "DataSourcing",
      "resultName": "transactions",
      "schema": "datalake",
      "table": "transactions",
      "columns": ["transaction_id", "account_id", "amount"]
    },
    {
      "type": "Transformation",
      "resultName": "grouped_events",
      "sql": "SELECT COALESCE(event_type, 'Unknown') AS event_type, COUNT(*) AS event_count FROM compliance_events GROUP BY COALESCE(event_type, 'Unknown') ORDER BY event_type"
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.ComplianceTransactionRatioV2Processor"
    }
  ]
}
```

**Notes on module chain:**
- Two DataSourcing modules pull the same tables/columns as V1 (needed for accurate input row counts for W7 trailer)
- Transformation groups compliance_events and produces `grouped_events` (not `output`, since the External creates the final `output`)
- External module reads `grouped_events`, `compliance_events`, `transactions`, and `__maxEffectiveDate` from shared state, computes the remaining columns, writes the CSV, and stores an empty `output` DataFrame

## 7. Writer Configuration

This job has NO framework writer module, matching V1. The External module writes the CSV file directly.

| Parameter | V1 Value | V2 Value | Match? |
|-----------|----------|----------|--------|
| Writer type | Direct file I/O (External module) | Direct file I/O (External module) | Yes |
| outputFile | `Output/curated/compliance_transaction_ratio.csv` | `Output/double_secret_curated/compliance_transaction_ratio.csv` | Path change per spec |
| includeHeader | true (manually written) | true (manually written) | Yes |
| trailerFormat | `TRAILER|{inputCount}|{date}` (inflated count, W7) | `TRAILER|{inputCount}|{date}` (inflated count, W7) | Yes |
| writeMode | Overwrite (`append: false`) | Overwrite (`append: false`) | Yes |
| lineEnding | LF (`\n`) | LF (`\n`) | Yes |

## 8. Proofmark Config Design

**Starting position:** Zero exclusions, zero fuzzy overrides.

**Analysis of each output column:**

| Column | Deterministic? | Fuzzy needed? | Verdict |
|--------|---------------|---------------|---------|
| event_type | Yes -- deterministic GROUP BY key with COALESCE | No | STRICT |
| event_count | Yes -- deterministic COUNT of source rows per group | No | STRICT |
| txn_count | Yes -- deterministic COUNT of all transactions | No | STRICT |
| events_per_1000_txns | Yes -- deterministic integer division | No | STRICT |
| as_of | Yes -- deterministic from __maxEffectiveDate | No | STRICT |

**Trailer row:** The trailer is a single line `TRAILER|{inputCount}|{dateStr}`. It is pipe-delimited, not comma-delimited. Since V1 and V2 both compute the inflated count from the same input data, the trailer will match exactly.

**Non-deterministic fields:** None identified (BRD confirms this).

**CSV structure:** Header row (1), data rows (variable, ~5 per event type), trailer row (1). Since writeMode is Overwrite, only the last effective date's output is retained, with exactly one trailer at the end of the file.

**Proofmark config:**

```yaml
comparison_target: "compliance_transaction_ratio"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

No exclusions or fuzzy overrides required. All columns are deterministically derived from source data and should match exactly between V1 and V2.

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision |
|----------------|-------------|-----------------|
| BR-1: Group by event_type | Sections 2, 5 | SQL `GROUP BY COALESCE(event_type, 'Unknown')` performs the grouping. |
| BR-2: Total txn_count from ALL transactions | Sections 2, 10 | External module reads `transactions` DataFrame from shared state and computes `transactions.Count ?? 0`. |
| BR-3: Integer division for events_per_1000_txns | Sections 3 (W4), 10 | External module computes `(eventCount * RatePerThousand) / txnCount` using C# integer division. |
| BR-4: Division-by-zero guard | Section 10 | External module uses ternary: `txnCount > 0 ? (eventCount * RatePerThousand) / txnCount : 0`. |
| BR-5: Alphabetical order by event_type | Section 5 | SQL `ORDER BY event_type`. |
| BR-6: Inflated trailer count (W7) | Sections 3, 10 | External module computes `inputCount = complianceEvents.Count + transactions.Count` and writes `TRAILER\|{inputCount}\|{dateStr}`. |
| BR-7: Direct file I/O bypassing CsvFileWriter | Sections 1, 2, 7 | External module writes CSV directly. Framework CsvFileWriter cannot produce the inflated trailer (W7). |
| BR-8: NULL event_type -> "Unknown" | Sections 3, 5 | SQL `COALESCE(event_type, 'Unknown')`. |
| BR-9: Date format yyyy-MM-dd from __maxEffectiveDate | Section 10 | External module reads `__maxEffectiveDate` from shared state and formats as `yyyy-MM-dd`. |
| BR-10: No framework writer module | Sections 2, 6, 7 | V2 config has no CsvFileWriter module, matching V1. |
| Edge Case 1: Empty compliance_events | Section 5 (SQL Notes) | Transformation fails (table not registered). Job fails for that date. No file written. Matches V1 behavior (no file written when compliance_events empty). |
| Edge Case 2: Zero transactions | Section 10 | txnCount = 0, events_per_1000_txns = 0 for all rows. External module handles gracefully. |
| Edge Case 3: Integer truncation | Section 3 (W4) | Reproduced with integer arithmetic in External module. |
| Edge Case 4: Inflated trailer count | Section 3 (W7) | Reproduced in External module with input DataFrame counts. |

## 10. External Module Design

**Module:** `ComplianceTransactionRatioV2Processor`
**File:** `ExternalModules/ComplianceTransactionRatioV2Processor.cs`
**Purpose:** Augment the SQL-grouped output with transaction count and ratio columns, then write the CSV file with an inflated trailer. This is the minimal logic that cannot be expressed through the framework's CsvFileWriter.

### Interface

Implements `IExternalStep.Execute(Dictionary<string, object> sharedState)`.

### Input from Shared State

| Key | Type | Description |
|-----|------|-------------|
| `grouped_events` | DataFrame | SQL output: `event_type`, `event_count` (grouped, ordered alphabetically) |
| `compliance_events` | DataFrame | Raw compliance events (needed for input row count for W7 trailer) |
| `transactions` | DataFrame | Raw transactions (needed for txn_count column and input row count for W7 trailer) |
| `__maxEffectiveDate` | DateOnly | Effective date for as_of column and trailer date |

### Named Constants

```csharp
// BR-3: Rate denominator for "per 1,000 transactions" metric
private const int RatePerThousand = 1000;
```

### Behavior

1. **Read `compliance_events` from shared state.** If null or empty (Count == 0), set `sharedState["output"]` to an empty DataFrame with the output columns (`event_type`, `event_count`, `txn_count`, `events_per_1000_txns`, `as_of`) and return immediately. This matches V1 behavior [ComplianceTransactionRatioWriter.cs:18-21] where no CSV is written for empty compliance_events.

2. **Read `grouped_events` from shared state.** This is the Transformation output with `event_type` and `event_count` columns, already ordered alphabetically.

3. **Compute counts:**
   - `txnCount = transactions?.Count ?? 0` (BR-2)
   - `inputCount = complianceEvents.Count + (transactions?.Count ?? 0)` (W7: inflated trailer count)

4. **Read `__maxEffectiveDate` from shared state and format as `yyyy-MM-dd`** (BR-9).

5. **Build output rows** by iterating `grouped_events` rows (already ordered by event_type from SQL):
   - `event_type`: from grouped_events row (string)
   - `event_count`: from grouped_events row (integer, cast from `long` since SQLite COUNT returns int64)
   - `txn_count`: computed above (same value for all rows)
   - `events_per_1000_txns`: `txnCount > 0 ? (eventCount * RatePerThousand) / txnCount : 0` -- W4: integer division, intentional V1 replication
   - `as_of`: formatted date string

6. **Write CSV file** to `Output/double_secret_curated/compliance_transaction_ratio.csv`:
   - Resolve path relative to solution root using the same `GetSolutionRoot()` pattern as V1
   - Use `StreamWriter` with `append: false` (Overwrite mode)
   - Write header: `event_type,event_count,txn_count,events_per_1000_txns,as_of\n`
   - Write data rows: `{event_type},{event_count},{txn_count},{events_per_1000_txns},{as_of}\n`
   - Write trailer: `TRAILER|{inputCount}|{dateStr}\n` -- W7: inflated count, intentional V1 replication
   - Line ending: LF (`\n`)

7. **Set `sharedState["output"]`** to an empty DataFrame with the five output columns (matching V1 behavior [ComplianceTransactionRatioWriter.cs:62]).

### Key Design Decisions

- **Integer division (W4):** The External module uses `(int eventCount * RatePerThousand) / (int txnCount)` -- natural C# integer division. This matches V1 exactly. Comment in code documents this is intentional V1 replication.
- **Inflated trailer (W7):** The External module computes `inputCount` from the raw source DataFrames, not from the grouped output. Comment in code documents this is intentional V1 replication.
- **Empty output DataFrame (BR-7):** The External module stores an empty DataFrame in `sharedState["output"]`, matching V1. Any downstream module expecting data would get nothing.
- **Output path:** Uses `Output/double_secret_curated/compliance_transaction_ratio.csv` (V2 output directory), resolved relative to solution root.
- **Row iteration over grouped_events:** This iteration is NOT the AP6 anti-pattern. The grouping is done in SQL. The iteration here is writing each pre-grouped row to the output file -- inherent to file I/O and not replaceable by SQL.
- **event_count type casting:** SQLite `COUNT(*)` returns `int64` (long). The External module must cast to `int` before performing the integer division to match V1's `int` arithmetic. This is safe because event counts per type will never exceed int32 range.
