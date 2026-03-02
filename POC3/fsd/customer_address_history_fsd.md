# CustomerAddressHistoryV2 -- Functional Specification Document

## 1. Overview

**Job:** CustomerAddressHistoryV2
**Tier:** Tier 1 -- Framework Only (DataSourcing -> Transformation (SQL) -> ParquetFileWriter)

This job produces a historical record of customer addresses across the effective date range. It sources the `addresses` table, filters out rows with NULL `customer_id`, orders by `customer_id`, and writes the result to Parquet in Append mode. Each effective date run appends that day's address snapshot, building a cumulative temporal history via the `as_of` column.

**Tier Justification:** All business logic (NULL filtering, column selection, ordering) is expressible as a single SQL statement. No procedural logic, no complex date manipulation, no cross-table joins in the output. Tier 1 is the natural fit.

---

## 2. V2 Module Chain

```
DataSourcing (addresses)
  -> Transformation (SQL: filter + order)
    -> ParquetFileWriter (Append, 2 parts)
```

**Module count:** 3 (down from 4 in V1, which had a dead-end `branches` DataSourcing)

| Step | Module | Config Key | Purpose |
|------|--------|-----------|---------|
| 1 | DataSourcing | `addresses` | Load address data from `datalake.addresses` for the effective date range |
| 2 | Transformation | `addr_history` | Filter NULL customer_id, select output columns, order by customer_id |
| 3 | ParquetFileWriter | -- | Write `addr_history` to Parquet directory, 2 parts, Append mode |

---

## 3. Anti-Pattern Analysis

### Identified Anti-Patterns

| ID | Anti-Pattern | Applies? | V1 Evidence | V2 Prescription |
|----|-------------|----------|-------------|-----------------|
| AP1 | Dead-end sourcing | **YES** | V1 sources `datalake.branches` [customer_address_history.json:14-17] but the SQL transformation references only `addresses a` [customer_address_history.json:22]. The branches table is loaded into shared state but never used. | **ELIMINATED.** V2 removes the `branches` DataSourcing entry entirely. Only `addresses` is sourced. |
| AP4 | Unused columns | **YES** | V1 sources `address_id` [customer_address_history.json:10] but the SQL SELECT does not include it [customer_address_history.json:22]. The column is fetched from PostgreSQL but never appears in the output. | **ELIMINATED.** V2 DataSourcing requests only the 6 columns actually used in the SQL: `customer_id`, `address_line1`, `city`, `state_province`, `postal_code`, `country`. The `as_of` column is automatically appended by the DataSourcing module when not listed. |
| AP3 | Unnecessary External module | No | V1 does not use an External module for this job. | N/A |
| AP8 | Complex SQL / unused CTEs | **MINOR** | V1 SQL uses a subquery (`SELECT ... FROM (SELECT ... FROM addresses a WHERE ...) sub ORDER BY sub.customer_id`) that could be simplified to a single-level query [customer_address_history.json:22]. The subquery adds no value -- the same filter and ordering can be expressed in a flat SELECT. | **ELIMINATED.** V2 SQL flattens to a single-level query: `SELECT ... FROM addresses a WHERE ... ORDER BY ...`. |

### Output-Affecting Wrinkles

| ID | Wrinkle | Applies? | Assessment |
|----|---------|----------|------------|
| W9 | Wrong writeMode | No | Append mode is correct for an accumulating history table with `as_of` for temporal tracking. |
| W10 | Absurd numParts | No | 2 parts for an address history table is reasonable. |
| W1-W8, W12 | Other wrinkles | No | No Sunday skip, no weekend fallback, no boundary summaries, no integer division, no rounding, no double epsilon, no trailer issues, no stale dates, no repeated headers. |

---

## 4. Output Schema

| Column | Type | Source | Transformation | Evidence |
|--------|------|--------|---------------|----------|
| customer_id | int | addresses.customer_id | Pass-through, filtered NOT NULL | [customer_address_history.json:22] |
| address_line1 | string | addresses.address_line1 | Pass-through | [customer_address_history.json:22] |
| city | string | addresses.city | Pass-through | [customer_address_history.json:22] |
| state_province | string | addresses.state_province | Pass-through | [customer_address_history.json:22] |
| postal_code | string | addresses.postal_code | Pass-through | [customer_address_history.json:22] |
| country | string | addresses.country | Pass-through | [customer_address_history.json:22] |
| as_of | date/string | addresses.as_of | Pass-through (injected by DataSourcing, available in SQLite as TEXT) | [customer_address_history.json:22] |

**Column count:** 7 (same as V1)

**Non-deterministic fields:** None. All columns are sourced directly from the datalake snapshot data.

---

## 5. SQL Design

### V1 SQL (for reference)

```sql
SELECT sub.customer_id, sub.address_line1, sub.city, sub.state_province,
       sub.postal_code, sub.country, sub.as_of
FROM (
    SELECT a.customer_id, a.address_line1, a.city, a.state_province,
           a.postal_code, a.country, a.as_of
    FROM addresses a
    WHERE a.customer_id IS NOT NULL
) sub
ORDER BY sub.customer_id
```

### V2 SQL (simplified -- eliminates AP8 unnecessary subquery)

```sql
SELECT a.customer_id, a.address_line1, a.city, a.state_province,
       a.postal_code, a.country, a.as_of
FROM addresses a
WHERE a.customer_id IS NOT NULL
ORDER BY a.customer_id
```

**Changes from V1:**
- Removed the unnecessary subquery wrapper. The WHERE and ORDER BY operate identically on a flat query.
- Column order and selection are preserved exactly.
- The `customer_id IS NOT NULL` filter is preserved per BR-1.
- The `ORDER BY customer_id` is preserved per BR-2.

**Output equivalence argument:** SQLite (the Transformation engine) evaluates `SELECT ... FROM t WHERE ... ORDER BY ...` identically whether the filter is in a subquery or the main query. The same rows in the same order are produced. The subquery in V1 adds no semantic value -- it is purely syntactic nesting.

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "CustomerAddressHistoryV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "addresses",
      "schema": "datalake",
      "table": "addresses",
      "columns": ["customer_id", "address_line1", "city", "state_province", "postal_code", "country"]
    },
    {
      "type": "Transformation",
      "resultName": "addr_history",
      "sql": "SELECT a.customer_id, a.address_line1, a.city, a.state_province, a.postal_code, a.country, a.as_of FROM addresses a WHERE a.customer_id IS NOT NULL ORDER BY a.customer_id"
    },
    {
      "type": "ParquetFileWriter",
      "source": "addr_history",
      "outputDirectory": "Output/double_secret_curated/customer_address_history/",
      "numParts": 2,
      "writeMode": "Append"
    }
  ]
}
```

**Differences from V1 config:**
1. `jobName`: `CustomerAddressHistoryV2` (V2 naming convention)
2. Removed `branches` DataSourcing module (AP1: dead-end sourcing eliminated)
3. Removed `address_id` from `addresses` column list (AP4: unused column eliminated)
4. Simplified SQL: removed unnecessary subquery (AP8 eliminated)
5. `outputDirectory` changed to `Output/double_secret_curated/customer_address_history/`

**Preserved from V1:**
- `firstEffectiveDate`: `"2024-10-01"` (same bootstrap date)
- `resultName` for DataSourcing: `"addresses"` (SQL references this table name)
- `resultName` for Transformation: `"addr_history"` (writer reads from this)
- `source` for writer: `"addr_history"`
- `numParts`: 2
- `writeMode`: `"Append"`
- Writer type: `ParquetFileWriter` (matches V1)

---

## 7. Writer Configuration

| Property | V1 Value | V2 Value | Match? |
|----------|----------|----------|--------|
| Writer type | ParquetFileWriter | ParquetFileWriter | Yes |
| source | `addr_history` | `addr_history` | Yes |
| outputDirectory | `Output/curated/customer_address_history/` | `Output/double_secret_curated/customer_address_history/` | Path change only (per project convention) |
| numParts | 2 | 2 | Yes |
| writeMode | Append | Append | Yes |

**Write mode implications:** Append mode means each effective date's execution appends new Parquet part files to the directory. Over the full date range (2024-10-01 through 2024-12-31), each day's address snapshot is appended. The same address may appear multiple times with different `as_of` values. The ParquetFileWriter in Append mode does NOT delete existing files before writing -- it creates new part files alongside existing ones.

**Correction:** Re-reading `ParquetFileWriter.cs`, Append mode does NOT create additional files -- it writes to the same `part-00000.parquet` and `part-00001.parquet` filenames on each execution. Since it only deletes existing `.parquet` files in Overwrite mode (lines 36-40), in Append mode the existing files are overwritten by `File.Create(filePath)` on line 80. Wait -- `File.Create` truncates existing files. Let me re-examine this.

Actually, looking more carefully at `ParquetFileWriter.cs`: the code uses `File.Create(filePath)` (line 80) which always creates a new file (or truncates if existing). In Append mode, it does NOT delete existing parquet files first, but since it writes to the same part file names (`part-00000.parquet`, `part-00001.parquet`), each execution overwrites the prior day's files. This means **Append mode in ParquetFileWriter behaves identically to Overwrite for same-named files** -- only the DataFrame content at the time of writing matters. The "Append" semantic here means the framework does not clear the directory first, but since filenames are deterministic, the last execution's data wins.

However, for output equivalence this doesn't matter -- V1 and V2 both use the same ParquetFileWriter with the same Append mode and same numParts. The final output after running all dates will be identical in both cases: only the last effective date's data persists in the part files.

---

## 8. Proofmark Config Design

**Reader:** `parquet` (matches writer type)

**Exclusions:** None. All output columns are deterministic, sourced directly from datalake data.

**Fuzzy columns:** None. All columns are pass-through from source (no arithmetic, no rounding, no floating-point operations).

**Threshold:** 100.0 (strict -- no tolerance needed for pass-through data).

```yaml
comparison_target: "customer_address_history"
reader: parquet
threshold: 100.0
```

**Rationale for zero overrides:** This job performs no computation -- it is a filtered, ordered projection of source data. Every column is a direct pass-through from `datalake.addresses`. There are no numeric calculations, no rounding, no timestamps, and no non-deterministic fields. Strict comparison at 100% threshold is appropriate.

---

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision | Evidence |
|----------------|-------------|-----------------|----------|
| BR-1: Filter NULL customer_id | SQL Design | `WHERE a.customer_id IS NOT NULL` in V2 SQL | [customer_address_history.json:22] |
| BR-2: ORDER BY customer_id | SQL Design | `ORDER BY a.customer_id` in V2 SQL | [customer_address_history.json:22] |
| BR-3: Include as_of column | SQL Design, Output Schema | `a.as_of` in V2 SQL SELECT list. DataSourcing auto-appends `as_of` when not in column list. | [customer_address_history.json:22] |
| BR-4: Branches sourced but unused | Anti-Pattern Analysis (AP1) | **ELIMINATED.** Branches DataSourcing removed from V2 config. | [customer_address_history.json:14-17,22] |
| BR-5: Result stored as addr_history | V2 Job Config | Transformation `resultName: "addr_history"` | [customer_address_history.json:20] |
| BR-6: Writer reads from addr_history | V2 Job Config | ParquetFileWriter `source: "addr_history"` | [customer_address_history.json:25] |
| BR-7: address_id NOT in output | Output Schema, Anti-Pattern Analysis (AP4) | address_id removed from DataSourcing columns. Not in SQL SELECT. | [customer_address_history.json:10,22] |
| OQ-1: Branches unused | Anti-Pattern Analysis (AP1) | Dead-end sourcing confirmed and eliminated. | [customer_address_history.json:14-17] |
| OQ-2: address_id excluded | Anti-Pattern Analysis (AP4) | Intentional per V1 SQL SELECT. V2 also excludes from DataSourcing. | [customer_address_history.json:10,22] |

### Anti-Pattern Traceability

| Anti-Pattern | Identified | Action | Outcome |
|-------------|-----------|--------|---------|
| AP1 (Dead-end sourcing) | Yes -- branches table | Removed branches DataSourcing module | Eliminated |
| AP4 (Unused columns) | Yes -- address_id column | Removed address_id from DataSourcing columns | Eliminated |
| AP8 (Complex SQL / unused CTEs) | Yes -- unnecessary subquery | Flattened SQL to single-level query | Eliminated |

---

## 10. External Module Design

**Not applicable.** This is a Tier 1 job. No External module is needed. All logic is handled by DataSourcing, Transformation (SQL), and ParquetFileWriter.
