# CreditScoreSnapshot -- Functional Specification Document

## 1. Overview & Tier Selection

**Job**: CreditScoreSnapshotV2
**Config**: `credit_score_snapshot_v2.json`
**Tier**: 1 (Framework Only) -- `DataSourcing -> Transformation (SQL) -> CsvFileWriter`

This job produces a pass-through snapshot of all credit score records for the effective date range. Every row from `datalake.credit_scores` is copied directly to the output CSV with no filtering, aggregation, or transformation.

**Tier Justification**: The V1 External module (`CreditScoreProcessor.cs`) performs a trivial row-by-row copy from the `credit_scores` DataFrame to the `output` DataFrame. This is a textbook `SELECT *` operation, perfectly expressible as a single SQL statement in a Transformation module. There is zero logic that requires procedural C# code. Tier 1 is the only appropriate choice.

---

## 2. V2 Module Chain

```
DataSourcing (credit_scores)
    -> Transformation (SQL: pass-through SELECT)
        -> CsvFileWriter (Output/double_secret_curated/credit_score_snapshot.csv)
```

### Module 1: DataSourcing -- credit_scores

| Property | Value |
|----------|-------|
| resultName | `credit_scores` |
| schema | `datalake` |
| table | `credit_scores` |
| columns | `credit_score_id`, `customer_id`, `bureau`, `score` |

**Note**: The `as_of` column is NOT listed in the columns array. Per the framework's DataSourcing behavior [DataSourcing.cs:69-72], when `as_of` is not in the caller's column list, it is automatically appended to the SELECT clause and included in the output DataFrame. This matches V1 behavior -- the V1 config also omits `as_of` from the columns list, but the External module accesses `row["as_of"]` [CreditScoreProcessor.cs:33] because DataSourcing injects it.

### Module 2: Transformation -- output

| Property | Value |
|----------|-------|
| resultName | `output` |
| sql | `SELECT credit_score_id, customer_id, bureau, score, SUBSTR(as_of, 6, 2) || '/' || SUBSTR(as_of, 9, 2) || '/' || SUBSTR(as_of, 1, 4) AS as_of FROM credit_scores` |

This SQL produces byte-identical output to the V1 External module's row-by-row copy. The column order matches the V1 output schema exactly [CreditScoreProcessor.cs:10-13, 27-34].

### Module 3: CsvFileWriter

| Property | Value | Evidence |
|----------|-------|----------|
| source | `output` | [credit_score_snapshot.json:26] |
| outputFile | `Output/double_secret_curated/credit_score_snapshot.csv` | V2 path convention |
| includeHeader | `true` | [credit_score_snapshot.json:28] |
| writeMode | `Overwrite` | [credit_score_snapshot.json:29] |
| lineEnding | `CRLF` | [credit_score_snapshot.json:30] |
| trailerFormat | (not configured) | [credit_score_snapshot.json:25-31] -- no trailer in V1 |

---

## 3. Anti-Pattern Analysis

### Anti-Patterns Identified and Eliminated

| ID | Anti-Pattern | V1 Evidence | V2 Resolution |
|----|-------------|-------------|---------------|
| AP1 | Dead-end sourcing | V1 config sources `datalake.branches` [credit_score_snapshot.json:13-17] but the External module never accesses it [CreditScoreProcessor.cs:15 -- only `credit_scores` retrieved from shared state] | **Eliminated.** V2 does not source the `branches` table at all. Only `credit_scores` is sourced. |
| AP3 | Unnecessary External module | V1 uses `CreditScoreProcessor` [credit_score_snapshot.json:19-22] which performs a trivial foreach pass-through [CreditScoreProcessor.cs:24-35] | **Eliminated.** V2 replaces the External module with a Transformation module containing a simple SELECT statement. |
| AP4 | Unused columns | All columns of `branches` (branch_id, branch_name, city, state_province) are sourced but never referenced [CreditScoreProcessor.cs:15] | **Eliminated.** V2 removes the entire `branches` DataSourcing entry. |
| AP6 | Row-by-row iteration | V1 External module uses `foreach` loop to copy rows one at a time [CreditScoreProcessor.cs:24-35] | **Eliminated.** V2 uses a set-based SQL SELECT in the Transformation module. |

### Output-Affecting Wrinkles

**None identified.** This job is a pure pass-through with no calculations, rounding, date logic, weekend handling, trailers, or write mode anomalies. No W-codes apply.

---

## 4. Output Schema

| Column | Type | Source | Transformation | Evidence |
|--------|------|--------|---------------|----------|
| credit_score_id | integer | credit_scores.credit_score_id | Pass-through | [CreditScoreProcessor.cs:29], [DB: credit_scores.credit_score_id is integer] |
| customer_id | integer | credit_scores.customer_id | Pass-through | [CreditScoreProcessor.cs:30], [DB: credit_scores.customer_id is integer] |
| bureau | varchar | credit_scores.bureau | Pass-through | [CreditScoreProcessor.cs:31], [DB: credit_scores.bureau is character varying] |
| score | integer | credit_scores.score | Pass-through | [CreditScoreProcessor.cs:32], [DB: credit_scores.score is integer] |
| as_of | date | credit_scores.as_of | Pass-through (auto-injected by DataSourcing) | [CreditScoreProcessor.cs:33], [DataSourcing.cs:69-72, 105-108] |

**Column order**: credit_score_id, customer_id, bureau, score, as_of -- matching the V1 External module's output column definition [CreditScoreProcessor.cs:10-13].

**Non-deterministic fields**: None. All columns are deterministic pass-through values from the source table.

---

## 5. SQL Design

### Transformation SQL

```sql
SELECT credit_score_id, customer_id, bureau, score, as_of
FROM credit_scores
```

**Design rationale**: This is a direct replacement for the V1 External module's foreach loop [CreditScoreProcessor.cs:24-35]. The V1 module copies every column from every row in `credit_scores` without filtering, aggregation, or computation. A bare SELECT achieves identical results.

**Column ordering**: The SELECT lists columns in the exact order defined by the V1 External module's `outputColumns` list [CreditScoreProcessor.cs:10-13]. This ensures the CSV header and data columns appear in the same order as V1.

**Empty input behavior**: When the `credit_scores` DataSourcing returns zero rows (no data in the effective date range), the Transformation module's SQL SELECT against an empty SQLite table produces an empty DataFrame. However, note that per the Transformation module behavior [Transformation.cs:46], if the input DataFrame has zero rows, the SQLite table is not registered (`if (!df.Rows.Any()) return;`). A SELECT against a non-existent table would error. This edge case requires attention during testing -- if the Transformation module throws on empty input, the behavior may differ from V1's empty DataFrame guard [CreditScoreProcessor.cs:17-21].

**Mitigation for empty input**: In practice, the date range should always contain data (the executor only runs for dates with known data). If this edge case surfaces during Phase D testing, it can be addressed by adjusting the SQL or adding error handling in a resolution cycle.

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "CreditScoreSnapshotV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "credit_scores",
      "schema": "datalake",
      "table": "credit_scores",
      "columns": ["credit_score_id", "customer_id", "bureau", "score"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT credit_score_id, customer_id, bureau, score, SUBSTR(as_of, 6, 2) || '/' || SUBSTR(as_of, 9, 2) || '/' || SUBSTR(as_of, 1, 4) AS as_of FROM credit_scores"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/credit_score_snapshot.csv",
      "includeHeader": true,
      "writeMode": "Overwrite",
      "lineEnding": "CRLF"
    }
  ]
}
```

### Key differences from V1 config:

| Aspect | V1 | V2 | Reason |
|--------|----|----|--------|
| Job name | `CreditScoreSnapshot` | `CreditScoreSnapshotV2` | V2 naming convention |
| Branches DataSourcing | Present [credit_score_snapshot.json:13-17] | **Removed** | AP1: Dead-end sourcing eliminated |
| External module | `CreditScoreProcessor` [credit_score_snapshot.json:19-22] | **Removed** | AP3: Replaced with Transformation |
| Transformation module | Not present | **Added** | Tier 1 SQL-based pass-through replaces External |
| Output path | `Output/curated/credit_score_snapshot.csv` | `Output/double_secret_curated/credit_score_snapshot.csv` | V2 output directory |
| Writer config | Same | Same (includeHeader, writeMode, lineEnding) | Output equivalence requirement |

---

## 7. Writer Configuration

| Property | Value | Matches V1? | Evidence |
|----------|-------|-------------|----------|
| Writer type | CsvFileWriter | Yes | [credit_score_snapshot.json:25] |
| source | `output` | Yes | [credit_score_snapshot.json:26] |
| outputFile | `Output/double_secret_curated/credit_score_snapshot.csv` | Path changed per V2 convention | V1: `Output/curated/credit_score_snapshot.csv` [credit_score_snapshot.json:27] |
| includeHeader | `true` | Yes | [credit_score_snapshot.json:28] |
| writeMode | `Overwrite` | Yes | [credit_score_snapshot.json:29] |
| lineEnding | `CRLF` | Yes | [credit_score_snapshot.json:30] |
| trailerFormat | (not configured) | Yes | V1 has no trailer [credit_score_snapshot.json:25-31] |

**Write mode note**: Overwrite mode means each effective date execution replaces the entire file. For multi-day auto-advance runs, only the last effective date's output survives on disk. This matches V1 behavior exactly. [BRD: Write Mode Implications]

---

## 8. Proofmark Config Design

```yaml
comparison_target: "credit_score_snapshot"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

### Rationale

| Setting | Value | Justification |
|---------|-------|---------------|
| reader | `csv` | V1 and V2 both use CsvFileWriter |
| threshold | `100.0` | All columns are deterministic pass-through; no tolerance needed |
| header_rows | `1` | `includeHeader: true` in both V1 and V2 writer configs |
| trailer_rows | `0` | No trailer configured in V1 or V2 |
| Excluded columns | None | No non-deterministic fields identified in BRD |
| Fuzzy columns | None | No calculations, rounding, or floating-point operations; all values are pass-through integers/strings/dates |

**Starting position**: Zero exclusions, zero fuzzy overrides. This is the strictest possible comparison. If Phase D testing reveals discrepancies, they will be investigated and resolved before any overrides are added.

---

## 9. Traceability Matrix

| BRD Requirement | FSD Design Element | Implementation |
|-----------------|-------------------|----------------|
| BR-1: All credit score rows passed through directly | Transformation SQL: `SELECT credit_score_id, customer_id, bureau, score, SUBSTR(as_of, 6, 2) || '/' || SUBSTR(as_of, 9, 2) || '/' || SUBSTR(as_of, 1, 4) AS as_of FROM credit_scores` | SQL SELECT with no WHERE clause produces all rows |
| BR-2: Empty credit_scores -> empty DataFrame with correct schema | Transformation module on empty input (see Section 5 edge case note) | Framework handles empty DataFrames; edge case documented for testing |
| BR-3: Branches table sourced but NOT used | V2 config removes branches DataSourcing entirely (AP1 elimination) | branches not present in V2 config |
| BRD Output Schema: 5 columns in specific order | SQL SELECT column order matches V1 outputColumns definition | credit_score_id, customer_id, bureau, score, as_of |
| BRD Writer: includeHeader=true | CsvFileWriter includeHeader=true | Matches V1 |
| BRD Writer: writeMode=Overwrite | CsvFileWriter writeMode=Overwrite | Matches V1 |
| BRD Writer: lineEnding=CRLF | CsvFileWriter lineEnding=CRLF | Matches V1 |
| BRD Writer: no trailer | CsvFileWriter has no trailerFormat | Matches V1 |
| OQ-1: Branches unused (dead code or missing feature?) | Treated as dead code (AP1). Eliminated in V2. | branches not sourced |

---

## 10. External Module Design

**Not applicable.** This is a Tier 1 implementation. No External module is needed.

The V1 External module (`CreditScoreProcessor.cs`) is entirely replaced by the Transformation module's SQL query. The V1 module's complete logic is:
1. Retrieve `credit_scores` from shared state [CreditScoreProcessor.cs:15]
2. Guard against null/empty input [CreditScoreProcessor.cs:17-21]
3. Copy every row field-by-field to output [CreditScoreProcessor.cs:24-35]

Steps 1 and 3 are inherent in the `SELECT ... FROM credit_scores` SQL. Step 2 (empty input guard) is handled by the Transformation module producing an empty DataFrame from an empty table.

---

## Appendix: Design Decisions Log

### Decision 1: Remove branches DataSourcing
- **Choice**: Eliminate branches table from V2 config
- **Alternative considered**: Keep branches sourcing to match V1 config structure
- **Rationale**: AP1 (dead-end sourcing) explicitly requires removal of unused DataSourcing entries. The branches table is loaded into shared state but never accessed by any module in the pipeline [CreditScoreProcessor.cs:15 -- only `credit_scores` retrieved]. Sourcing unused data wastes database I/O and memory. The output is unaffected because branches data never flows into the output DataFrame.

### Decision 2: Tier 1 over Tier 2/3
- **Choice**: Tier 1 (DataSourcing -> Transformation -> CsvFileWriter)
- **Alternative considered**: Tier 2 with minimal External for empty input guard
- **Rationale**: The V1 External module's entire business logic is `SELECT *` expressed as a foreach loop. AP3 (unnecessary External module) and AP6 (row-by-row iteration) both mandate replacing this with framework modules. There is no operation in the V1 code that cannot be expressed in SQL. Even the empty input guard is implicitly handled by SQL operating on an empty table.

### Decision 3: Column order preservation
- **Choice**: SQL SELECT lists columns in exact V1 order (credit_score_id, customer_id, bureau, score, as_of)
- **Rationale**: The CsvFileWriter writes columns in DataFrame column order. The V1 External module defines output columns in this specific order [CreditScoreProcessor.cs:10-13]. The V2 SQL SELECT must produce columns in the same order to achieve byte-identical CSV output.
