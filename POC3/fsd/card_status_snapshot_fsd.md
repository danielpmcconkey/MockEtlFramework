# CardStatusSnapshot — Functional Specification Document

## 1. Overview

**Job:** CardStatusSnapshotV2
**Tier:** 1 (Framework Only) — `DataSourcing -> Transformation (SQL) -> ParquetFileWriter`

CardStatusSnapshot produces a daily count of cards grouped by `card_status`, providing a snapshot view of the card portfolio's status distribution over time. The entire business logic is a simple `GROUP BY` aggregation expressible in a single SQL statement. No External module is needed or justified.

### Tier Justification

The V1 implementation already uses a pure Tier 1 chain (`DataSourcing -> Transformation -> ParquetFileWriter`). The SQL is a single `SELECT ... GROUP BY` with no procedural logic, no cross-date-range queries, no DISTINCT ON, and no operations that exceed SQLite's capabilities. Tier 1 is the obvious and correct choice.

---

## 2. V2 Module Chain

```
DataSourcing (cards) -> Transformation (SQL aggregation) -> ParquetFileWriter
```

| Step | Module | Config Key | Purpose |
|------|--------|-----------|---------|
| 1 | DataSourcing | `cards` | Source `card_status` column from `datalake.cards` for the effective date range |
| 2 | Transformation | `output` | Aggregate cards by `card_status` and `as_of`, producing `card_count` per group |
| 3 | ParquetFileWriter | — | Write output to 50 Parquet part files in Overwrite mode |

---

## 3. Anti-Pattern Analysis

### Identified Anti-Patterns

| Code | Name | Applies? | V1 Evidence | V2 Prescription |
|------|------|----------|-------------|-----------------|
| AP1 | Dead-end sourcing | No | V1 sources one table (`cards`) and uses it in the SQL. No unused DataSourcing entries. | N/A |
| AP4 | Unused columns | **Yes** | V1 sources 6 columns (`card_id`, `customer_id`, `card_type`, `card_number_masked`, `expiration_date`, `card_status`) [card_status_snapshot.json:10] but the SQL only references `card_status` and `as_of` [card_status_snapshot.json:15]. Five of six sourced columns are never used. | **Eliminate.** V2 sources only `card_status`. The `as_of` column is automatically appended by DataSourcing when not explicitly listed in columns. |
| W10 | Absurd numParts | **Yes** | V1 splits output into 50 parts [card_status_snapshot.json:20] for a dataset that produces ~3 rows per day (one per card status). | **Reproduce.** V2 uses `numParts: 50` to match V1 output. Most part files will be empty or contain a single row. |
| W9 | Wrong writeMode | **Possible** | V1 uses Overwrite [card_status_snapshot.json:21]. For a daily snapshot job in auto-advance mode, Overwrite means only the last effective date's output survives. This may or may not be intentional — the BRD notes this behavior. | **Reproduce.** V2 uses `writeMode: Overwrite` to match V1. |
| AP3 | Unnecessary External | No | V1 does not use an External module. | N/A |
| AP10 | Over-sourcing dates | No | V1 relies on framework-injected effective dates via shared state keys. No hardcoded date filtering in SQL. | N/A |

### Anti-Patterns NOT Present

| Code | Name | Reason Not Applicable |
|------|------|-----------------------|
| W1-W3c | Day-of-week behaviors | No External module; pure SQL with no day-of-week logic. |
| W4 | Integer division | No percentage calculations. `COUNT(*)` produces an integer naturally. |
| W5 | Banker's rounding | No rounding operations. |
| W6 | Double epsilon | No monetary accumulation or floating-point arithmetic. |
| W7-W8 | Trailer issues | Parquet output, no trailers. |
| W12 | Header every append | Parquet output, not CSV. |
| AP2 | Duplicated logic | No cross-job duplication identified. |
| AP5 | Asymmetric NULLs | No NULL handling required; `card_status` has exactly three non-null values. |
| AP6 | Row-by-row iteration | No External module, no procedural code. |
| AP7 | Magic values | No thresholds or hardcoded boundaries. |
| AP8 | Complex SQL / unused CTEs | SQL is a single simple `GROUP BY`. No CTEs or window functions. |
| AP9 | Misleading names | Job name accurately describes what it produces: a snapshot of card status counts. |

---

## 4. Output Schema

| Column | Type | Source | Transformation | Evidence |
|--------|------|--------|---------------|----------|
| `card_status` | TEXT | `datalake.cards.card_status` | Grouped by | [card_status_snapshot.json:15] `GROUP BY c.card_status` |
| `card_count` | INTEGER | `datalake.cards` | `COUNT(*)` per group | [card_status_snapshot.json:15] `COUNT(*) AS card_count` |
| `as_of` | TEXT | `datalake.cards.as_of` (injected by DataSourcing) | Grouped by | [card_status_snapshot.json:15] `GROUP BY ... c.as_of` |

**Column ordering:** `card_status`, `card_count`, `as_of` — matches V1 SQL SELECT clause order.

**Known status values:** `Active`, `Blocked`, `Expired` [BRD BR-3, DB query evidence].

**Expected row count per single-day run:** 3 rows (one per status), assuming all three statuses have at least one card on that date.

---

## 5. SQL Design

### V1 SQL (Reference)

```sql
SELECT c.card_status, COUNT(*) AS card_count, c.as_of
FROM cards c
GROUP BY c.card_status, c.as_of
```

### V2 SQL

```sql
SELECT c.card_status, COUNT(*) AS card_count, c.as_of
FROM cards c
GROUP BY c.card_status, c.as_of
```

**Rationale:** The V1 SQL is already clean and correct. It contains no anti-patterns (no unused CTEs, no complex window functions, no unnecessary subqueries). The only change in V2 is in the DataSourcing config (removing unused columns per AP4), not in the SQL itself.

**SQLite compatibility:** `COUNT(*)`, `GROUP BY`, and basic column references are fully supported by SQLite. No Postgres-specific features are used. No compatibility concerns.

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "CardStatusSnapshotV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "cards",
      "schema": "datalake",
      "table": "cards",
      "columns": ["card_status"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT c.card_status, COUNT(*) AS card_count, c.as_of FROM cards c GROUP BY c.card_status, c.as_of"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/card_status_snapshot/",
      "numParts": 50,
      "writeMode": "Overwrite"
    }
  ]
}
```

### Config Changes from V1

| Field | V1 Value | V2 Value | Reason |
|-------|----------|----------|--------|
| `jobName` | `CardStatusSnapshot` | `CardStatusSnapshotV2` | V2 naming convention |
| `DataSourcing.columns` | `["card_id", "customer_id", "card_type", "card_number_masked", "expiration_date", "card_status"]` | `["card_status"]` | AP4: Remove 5 unused columns. Only `card_status` is referenced in the SQL. `as_of` is auto-appended by DataSourcing. |
| `outputDirectory` | `Output/curated/card_status_snapshot/` | `Output/double_secret_curated/card_status_snapshot/` | V2 output path convention |
| `numParts` | `50` | `50` | W10: Preserved for output equivalence despite being excessive |
| `writeMode` | `Overwrite` | `Overwrite` | Preserved to match V1 behavior |
| `sql` | (unchanged) | (unchanged) | SQL is already clean; no changes needed |

---

## 7. Writer Configuration

| Property | Value | V1 Match | Notes |
|----------|-------|----------|-------|
| Writer Type | `ParquetFileWriter` | Yes | Same as V1 |
| `source` | `output` | Yes | Same DataFrame name |
| `outputDirectory` | `Output/double_secret_curated/card_status_snapshot/` | Path changed | V2 output path; structure matches V1 |
| `numParts` | `50` | Yes | W10: Absurd for ~3 rows, but required for output equivalence |
| `writeMode` | `Overwrite` | Yes | Each run replaces prior output |

**Partitioning behavior (from ParquetFileWriter.cs):** Rows are distributed using integer division with remainder. For 3 rows across 50 parts: `partSize = 3 / 50 = 0`, `remainder = 3 % 50 = 3`. Parts 0-2 each get 1 row. Parts 3-49 get 0 rows (empty Parquet files with schema only). This matches V1 exactly because the same framework code is used.

---

## 8. Proofmark Config Design

### Recommended Configuration

```yaml
comparison_target: "card_status_snapshot"
reader: parquet
threshold: 100.0
```

### Justification

- **Reader:** `parquet` — matches the V1/V2 writer type (ParquetFileWriter).
- **Threshold:** `100.0` — all rows must match exactly. The output is fully deterministic.
- **Excluded columns:** None. All three output columns (`card_status`, `card_count`, `as_of`) are deterministic and must match exactly.
- **Fuzzy columns:** None. `card_count` is an integer `COUNT(*)` with no floating-point arithmetic. `card_status` and `as_of` are text values with no precision concerns.

This is the strictest possible configuration. No overrides are needed because:
1. There are no non-deterministic fields (BRD confirms: "None identified").
2. There are no floating-point calculations that could produce epsilon differences.
3. The same SQL and same framework writer produce both V1 and V2 output.

---

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision | Evidence |
|-----------------|-------------|-----------------|----------|
| BR-1: Group by card_status, as_of | Sec 5 (SQL Design) | SQL `GROUP BY c.card_status, c.as_of` preserved exactly from V1 | [card_status_snapshot.json:15] |
| BR-2: card_count = COUNT(*) | Sec 5 (SQL Design) | SQL `COUNT(*) AS card_count` preserved exactly from V1 | [card_status_snapshot.json:15] |
| BR-3: Three status values (Active, Blocked, Expired) | Sec 4 (Output Schema) | No filtering applied; all statuses pass through naturally | [DB: SELECT DISTINCT card_status FROM datalake.cards] |
| BR-4: Unused sourced columns | Sec 3 (Anti-Pattern: AP4) | **Eliminated.** V2 sources only `card_status`. | [card_status_snapshot.json:10 vs :15] |
| BR-5: 50 part files | Sec 7 (Writer Config) | `numParts: 50` preserved (W10) | [card_status_snapshot.json:20] |
| Edge Case 1: Weekday-only data | Sec 5 (SQL Design) | No special handling needed. DataSourcing returns no rows on weekends; SQL produces 0 output rows; Parquet writer creates 50 empty part files. | [BRD Edge Case 1] |
| Edge Case 2: 50 parts with few rows | Sec 7 (Writer Config) | Accepted as V1 behavior (W10). Partitioning is handled by framework code identically in V1 and V2. | [BRD Edge Case 2] |
| Edge Case 3: No status filtering | Sec 5 (SQL Design) | SQL has no WHERE clause; all statuses included. | [BRD Edge Case 3] |
| Writer: Parquet, Overwrite | Sec 7 (Writer Config) | ParquetFileWriter with Overwrite mode, matching V1 | [card_status_snapshot.json:18-22] |
| Anti-pattern: AP4 | Sec 3 (Anti-Pattern Analysis) | Eliminated: removed 5 unused columns from DataSourcing | V1 config vs SQL analysis |
| Anti-pattern: W10 | Sec 3 (Anti-Pattern Analysis) | Reproduced: numParts=50 preserved for output equivalence | [card_status_snapshot.json:20] |

---

## 10. External Module Design

**Not applicable.** This job is Tier 1 (Framework Only). No External module is needed.

The entire business logic is a single SQL `GROUP BY` aggregation. DataSourcing handles date-range filtering. The Transformation module handles the SQL. The ParquetFileWriter handles output. There is no procedural logic, no cross-date-range queries, and no operations outside SQLite's capabilities.
