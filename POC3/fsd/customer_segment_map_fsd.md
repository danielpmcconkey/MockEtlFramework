# CustomerSegmentMap -- Functional Specification Document

## 1. Overview and Tier Selection

**Job:** CustomerSegmentMapV2
**Tier:** 1 -- Framework Only (DataSourcing -> Transformation (SQL) -> CsvFileWriter)

This job produces a mapping of customers to their assigned segments by joining the `customers_segments` association table with the `segments` reference table. Output is a CSV file in Append mode, accumulating segment assignments across effective dates.

**Tier Justification:** All business logic consists of an INNER JOIN between two tables on `segment_id` and `as_of`, with a simple SELECT and ORDER BY. This is straightforward SQL that requires no procedural logic, no External module, and no special data access patterns. Tier 1 is the obvious and correct choice.

---

## 2. V2 Module Chain

```
DataSourcing (customers_segments)
  -> DataSourcing (segments)
    -> Transformation (SQL JOIN, result: "seg_map")
      -> CsvFileWriter (Append, LF, header)
```

**Modules (4 total):**

| Step | Module Type    | resultName / source | Purpose                                             |
|------|---------------|---------------------|-----------------------------------------------------|
| 1    | DataSourcing  | `customers_segments` | Load customer-segment associations from datalake    |
| 2    | DataSourcing  | `segments`           | Load segment reference data from datalake           |
| 3    | Transformation | `seg_map`           | JOIN customers_segments with segments; produce output columns |
| 4    | CsvFileWriter | source: `seg_map`    | Write CSV to Output/double_secret_curated/customer_segment_map.csv |

---

## 3. Anti-Pattern Analysis

### Anti-Patterns Identified in V1

| ID  | Name              | Applies? | V1 Evidence | V2 Action |
|-----|-------------------|----------|-------------|-----------|
| AP1 | Dead-end sourcing | YES      | [customer_segment_map.json:20-23] -- `branches` table is sourced with columns `branch_id, branch_name, city, state_province` but never referenced in the Transformation SQL (line 29). The SQL only references `customers_segments cs` and `segments s`. | **ELIMINATE.** Remove the `branches` DataSourcing entry entirely from V2 config. Do not source data that is never used. |
| AP4 | Unused columns    | NO       | All columns sourced for `customers_segments` (`customer_id`, `segment_id`) and `segments` (`segment_id`, `segment_name`, `segment_code`) are referenced in the SQL. Note: `as_of` is auto-appended by DataSourcing and used in the JOIN condition. | No action needed. |
| AP3 | Unnecessary External | NO    | V1 already uses Tier 1 (DataSourcing + Transformation + CsvFileWriter). No External module exists. | No action needed. V2 maintains Tier 1. |

### Output-Affecting Wrinkles

| ID  | Name | Applies? | Evidence | V2 Action |
|-----|------|----------|----------|-----------|
| W1-W12 | (all) | NO | No wrinkles apply to this job. The job is a clean SQL JOIN with straightforward CSV output. No Sunday logic, no weekend fallback, no boundary rows, no integer division, no rounding, no double epsilon, no trailer, no hardcoded dates, no wrong write mode, no absurd numParts, no header-every-append. | No action needed. |

### Summary

- **1 anti-pattern eliminated:** AP1 (dead-end sourcing of `branches` table)
- **0 wrinkles to reproduce:** None applicable
- **0 anti-patterns preserved:** N/A

---

## 4. Output Schema

| Column        | Type   | Source Table            | Source Column    | Transformation           |
|---------------|--------|------------------------|------------------|--------------------------|
| customer_id   | int    | customers_segments     | customer_id      | Pass-through             |
| segment_id    | int    | customers_segments     | segment_id       | Pass-through             |
| segment_name  | string | segments               | segment_name     | Joined on segment_id + as_of |
| segment_code  | string | segments               | segment_code     | Joined on segment_id + as_of |
| as_of         | date   | customers_segments     | as_of            | Pass-through (auto-appended by DataSourcing) |

**Column order:** customer_id, segment_id, segment_name, segment_code, as_of (matches V1 SQL SELECT order).

**Non-deterministic fields:** None. All output values are deterministic and reproducible.

---

## 5. SQL Design

### Transformation SQL

```sql
SELECT cs.customer_id,
       cs.segment_id,
       s.segment_name,
       s.segment_code,
       cs.as_of
FROM customers_segments cs
JOIN segments s
  ON cs.segment_id = s.segment_id
 AND cs.as_of = s.as_of
ORDER BY cs.customer_id, cs.segment_id
```

### SQL Design Notes

- **INNER JOIN:** Only customer-segment associations with a matching segment record (for the same `as_of` date) appear in the output. This matches V1 behavior per BRD BR-2.
- **Dual-key join:** The join condition uses both `segment_id` AND `as_of` to ensure the segment name/code is from the same effective date snapshot as the association. This matches V1 behavior per BRD BR-1.
- **ORDER BY:** Results are ordered by `customer_id` ascending, then `segment_id` ascending. This matches V1 behavior per BRD BR-3.
- **as_of in output:** The `as_of` column from `customers_segments` is included in the SELECT, per BRD BR-4.
- **Identical to V1 SQL:** The V2 SQL is identical to V1 because V1's SQL was already clean and correct. The only V1 problem was the dead-end `branches` sourcing (AP1), which is a config issue, not a SQL issue.

---

## 6. V2 Job Config JSON

**File:** `JobExecutor/Jobs/customer_segment_map_v2.json`

```json
{
  "jobName": "CustomerSegmentMapV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "customers_segments",
      "schema": "datalake",
      "table": "customers_segments",
      "columns": ["customer_id", "segment_id"]
    },
    {
      "type": "DataSourcing",
      "resultName": "segments",
      "schema": "datalake",
      "table": "segments",
      "columns": ["segment_id", "segment_name", "segment_code"]
    },
    {
      "type": "Transformation",
      "resultName": "seg_map",
      "sql": "SELECT cs.customer_id, cs.segment_id, s.segment_name, s.segment_code, cs.as_of FROM customers_segments cs JOIN segments s ON cs.segment_id = s.segment_id AND cs.as_of = s.as_of ORDER BY cs.customer_id, cs.segment_id"
    },
    {
      "type": "CsvFileWriter",
      "source": "seg_map",
      "outputFile": "Output/double_secret_curated/customer_segment_map.csv",
      "includeHeader": true,
      "writeMode": "Append",
      "lineEnding": "LF"
    }
  ]
}
```

### Config Changes from V1

| Element                | V1 Value                                        | V2 Value                                                   | Reason                    |
|------------------------|------------------------------------------------|-----------------------------------------------------------|---------------------------|
| jobName                | `CustomerSegmentMap`                            | `CustomerSegmentMapV2`                                     | V2 naming convention      |
| branches DataSourcing  | Present (4 columns)                             | **Removed**                                                | AP1: dead-end sourcing    |
| outputFile             | `Output/curated/customer_segment_map.csv`       | `Output/double_secret_curated/customer_segment_map.csv`    | V2 output path            |
| All other config       | (unchanged)                                     | (unchanged)                                                | V1 config was clean       |

---

## 7. Writer Config

| Property       | Value                                                  | Matches V1? |
|---------------|-------------------------------------------------------|-------------|
| type          | CsvFileWriter                                          | Yes         |
| source        | `seg_map`                                              | Yes         |
| outputFile    | `Output/double_secret_curated/customer_segment_map.csv` | Path changed to V2 output dir |
| includeHeader | `true`                                                 | Yes         |
| writeMode     | `Append`                                               | Yes         |
| lineEnding    | `LF`                                                   | Yes         |
| trailerFormat | (not configured)                                       | Yes         |

### Write Mode Implications

- **Append mode:** Each effective date run appends that day's segment mappings to the CSV. The header is written only on first creation (when the file does not yet exist). Subsequent appends add data rows only. This is handled correctly by the framework's CsvFileWriter (see `CsvFileWriter.cs` line 47: `if (_includeHeader && !append)`).
- Over a multi-day auto-advance run, the output accumulates all daily segment assignments. A customer-segment pair appearing on multiple dates will have multiple rows with different `as_of` values.

---

## 8. Proofmark Config Design

**File:** `POC3/proofmark_configs/customer_segment_map.yaml`

```yaml
comparison_target: "customer_segment_map"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

### Proofmark Config Rationale

| Setting        | Value | Justification                                                                                    |
|---------------|-------|--------------------------------------------------------------------------------------------------|
| reader        | csv   | V1 and V2 both use CsvFileWriter                                                                |
| threshold     | 100.0 | All output is deterministic; byte-identical match expected                                        |
| header_rows   | 1     | `includeHeader: true` -- one header row at the top of the file                                   |
| trailer_rows  | 0     | No trailer configured. Even though Append mode is used, there are no trailers to worry about.    |
| excluded cols | none  | No non-deterministic fields identified (BRD: "None identified")                                  |
| fuzzy cols    | none  | No floating-point or rounding concerns -- all values are integers or strings                     |

---

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision | Evidence |
|-----------------|-------------|-----------------|----------|
| BR-1: JOIN on segment_id + as_of | S5 SQL Design | INNER JOIN with dual-key condition: `ON cs.segment_id = s.segment_id AND cs.as_of = s.as_of` | [customer_segment_map.json:29] |
| BR-2: INNER JOIN (only matched rows) | S5 SQL Design | `JOIN` (not LEFT JOIN) -- unmatched associations excluded | [customer_segment_map.json:29] |
| BR-3: ORDER BY customer_id, segment_id | S5 SQL Design | `ORDER BY cs.customer_id, cs.segment_id` | [customer_segment_map.json:29] |
| BR-4: as_of in output | S4 Output Schema, S5 SQL | `cs.as_of` in SELECT list | [customer_segment_map.json:29] |
| BR-5: branches sourced but unused | S3 Anti-Pattern Analysis | Identified as AP1, eliminated in V2 by removing the DataSourcing entry | [customer_segment_map.json:20-23] |
| BR-6: Writer reads from "seg_map" | S6 Config, S7 Writer Config | `"source": "seg_map"` in CsvFileWriter config | [customer_segment_map.json:33] |
| BR-7: No External module | S1 Tier, S2 Module Chain | Tier 1 maintained: DataSourcing + Transformation + CsvFileWriter | [customer_segment_map.json] |
| BRD Write Mode: Append | S7 Writer Config | `"writeMode": "Append"` | [customer_segment_map.json:36] |
| BRD Line Ending: LF | S7 Writer Config | `"lineEnding": "LF"` | [customer_segment_map.json:37] |
| BRD Include Header: true | S7 Writer Config | `"includeHeader": true` | [customer_segment_map.json:35] |

### Anti-Pattern Traceability

| Anti-Pattern | BRD Reference | FSD Section | V2 Action |
|-------------|---------------|-------------|-----------|
| AP1: Dead-end sourcing (branches) | BR-5, OQ-1 | S3, S6 | Eliminated -- branches DataSourcing removed from V2 config |

---

## 10. External Module Design

**Not applicable.** This job is Tier 1 -- no External module is needed. All business logic is expressed in a single SQL Transformation.

---

## Appendix: Open Questions Disposition

| BRD Open Question | Disposition |
|-------------------|-------------|
| OQ-1: branches table sourced but unused | Resolved as AP1 (dead-end sourcing). Removed in V2. Whether V1's inclusion was intentional or accidental is irrelevant -- it does not affect output, so removing it is safe and correct. |
| OQ-2: JOIN requires matching as_of dates; gaps could silently drop rows | This is by-design behavior of the INNER JOIN. V2 replicates this exactly. If the segments table has gaps, the same rows will be dropped in both V1 and V2. No action needed for output equivalence. |
