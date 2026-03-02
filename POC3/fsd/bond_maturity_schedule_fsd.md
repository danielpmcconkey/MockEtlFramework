# BondMaturitySchedule V2 -- Functional Specification Document

## 1. Overview

BondMaturityScheduleV2 replaces the V1 `BondMaturitySchedule` job, which produces a summary of bond-type securities with aggregated holding values and holder counts. The V1 implementation uses an External module (`BondMaturityScheduleBuilder.cs`) to filter securities to bonds, join with holdings, and aggregate per-security totals. All of this logic is expressible in standard SQL.

**Tier: 1 (Framework Only)** -- `DataSourcing -> Transformation (SQL) -> ParquetFileWriter`

**Tier Justification:** The V1 External module performs only filtering (`security_type = 'Bond'`), joining (securities to holdings on `security_id`), aggregation (`SUM`, `COUNT`), null coalescing (`COALESCE`), and rounding (`ROUND`). Every one of these operations maps directly to SQL. There is no procedural logic, no cross-date queries, no snapshot fallback, and no calculation that requires C# constructs. Tier 1 is the correct choice.

---

## 2. V2 Module Chain

| Step | Module Type | Config Key | Details |
|------|------------|------------|---------|
| 1 | DataSourcing | `securities` | `datalake.securities`: columns `security_id`, `ticker`, `security_name`, `security_type`, `sector` |
| 2 | DataSourcing | `holdings` | `datalake.holdings`: columns `security_id`, `current_value` |
| 3 | Transformation | `output` | SQL joins, filters, aggregates (see Section 5) |
| 4 | ParquetFileWriter | -- | Source: `output`, directory: `Output/double_secret_curated/bond_maturity_schedule/`, numParts: 1, writeMode: Overwrite |

**Key changes from V1:**
- External module eliminated entirely (AP3)
- Unused columns removed from DataSourcing (AP4): `exchange` from securities; `holding_id`, `investment_id`, `customer_id`, `quantity`, `cost_basis` from holdings
- Row-by-row iteration replaced with set-based SQL (AP6)
- Output path changed to `Output/double_secret_curated/bond_maturity_schedule/`

---

## 3. Anti-Pattern Analysis

### Anti-Patterns Eliminated (AP-codes)

| AP Code | V1 Problem | V2 Resolution |
|---------|-----------|---------------|
| AP3 | Unnecessary External module (`BondMaturityScheduleBuilder.cs`) performs filtering, joining, and aggregation -- all SQL-native operations | Replaced with a single Transformation module containing SQL. No External module needed. |
| AP4 | V1 sources `exchange` from securities (never used in output). V1 sources `holding_id`, `investment_id`, `customer_id`, `quantity`, `cost_basis` from holdings (none used in output). | V2 DataSourcing requests only the columns actually needed: `security_id`, `ticker`, `security_name`, `security_type`, `sector` from securities; `security_id`, `current_value` from holdings. |
| AP6 | V1 uses nested `foreach` loops to build a bond lookup dictionary and accumulate holding totals row by row. | V2 uses a single SQL statement with `LEFT JOIN` and `GROUP BY` for set-based aggregation. |
| AP9 | Job name "BondMaturitySchedule" suggests maturity date scheduling, but the job actually computes bond holding aggregates. No maturity data exists in the schema. | Cannot rename V1 jobs (output filenames must match). Documented here: the job name is misleading but preserved for backward compatibility. |

### Output-Affecting Wrinkles (W-codes)

| W Code | Applies? | V2 Handling |
|--------|----------|-------------|
| W5 | YES -- V1 uses `Math.Round(decimal, 2)` which defaults to `MidpointRounding.ToEven` (banker's rounding). | SQLite's `ROUND()` uses "round half away from zero." For typical monetary data (values with <= 2 decimal places), the SUM will itself have <= 2 decimal places, making ROUND a no-op regardless of rounding mode. Both approaches produce identical output. If Proofmark detects a difference on a midpoint edge case, this is the first suspect and would require escalation to Tier 2. See Section 5 for the ROUND usage. |
| W1-W4, W6-W12 | NO | Not applicable to this job. |

---

## 4. Output Schema

| Column | Type | Source | Transformation | BRD Ref |
|--------|------|--------|---------------|---------|
| `security_id` | INTEGER | `securities.security_id` | Filtered to `security_type = 'Bond'`, cast to integer via SQL type affinity | BR-1 |
| `ticker` | TEXT | `securities.ticker` | `COALESCE(s.ticker, '')` -- null coalesced to empty string | BR-10 |
| `security_name` | TEXT | `securities.security_name` | `COALESCE(s.security_name, '')` -- null coalesced to empty string | BR-10 |
| `sector` | TEXT | `securities.sector` | `COALESCE(s.sector, '')` -- null coalesced to empty string | BR-10 |
| `total_held_value` | DECIMAL | `holdings.current_value` | `ROUND(COALESCE(SUM(h.current_value), 0), 2)` -- sum of matching holdings, rounded to 2dp. Bonds with no holdings get 0. | BR-4, BR-5, BR-6 |
| `holder_count` | INTEGER | `holdings` row count | `COUNT(h.current_value)` -- count of holding rows per security_id. Bonds with no holdings get 0. | BR-4, BR-6 |
| `as_of` | DATE | `securities.as_of` | `MAX(s.as_of)` -- the maximum as_of date from the sourced data, which equals `__maxEffectiveDate` for any run where securities data exists for the effective date | BR-7 |

**Column order matches V1:** `security_id`, `ticker`, `security_name`, `sector`, `total_held_value`, `holder_count`, `as_of`

---

## 5. SQL Design

```sql
SELECT
    s.security_id,
    COALESCE(s.ticker, '') AS ticker,
    COALESCE(s.security_name, '') AS security_name,
    COALESCE(s.sector, '') AS sector,
    ROUND(COALESCE(SUM(h.current_value), 0), 2) AS total_held_value,
    COUNT(h.current_value) AS holder_count,
    MAX(s.as_of) AS as_of
FROM securities s
LEFT JOIN holdings h ON s.security_id = h.security_id
WHERE s.security_type = 'Bond'
GROUP BY s.security_id, s.ticker, s.security_name, s.sector
ORDER BY s.security_id
```

### SQL Design Rationale

1. **`LEFT JOIN` (not `INNER JOIN`):** Bonds with no matching holdings must still appear in output with `total_held_value = 0` and `holder_count = 0` (BR-6). The LEFT JOIN ensures all bonds are preserved. `COALESCE(SUM(...), 0)` handles the NULL aggregation result for unmatched bonds.

2. **`COUNT(h.current_value)` (not `COUNT(*)`):** `COUNT(column)` counts only non-NULL values. For a LEFT JOIN where no holdings match, `h.current_value` is NULL, so `COUNT(h.current_value)` correctly returns 0. `COUNT(*)` would return 1 (counting the bond row itself).

3. **`COALESCE(s.ticker, '')` etc.:** Reproduces V1's `?.ToString() ?? ""` null coalescing behavior (BR-10). Null string fields become empty strings.

4. **`ROUND(..., 2)`:** Matches V1's `Math.Round(totals.totalValue, 2)` (BR-5). As noted in the W5 analysis, SQLite uses round-half-away-from-zero while C# defaults to banker's rounding, but this difference only manifests when the value is exactly at a midpoint (X.XX5), which is not expected for sums of 2-decimal-place monetary values.

5. **`MAX(s.as_of)` for the as_of column:** V1 sets `as_of` to `__maxEffectiveDate` from shared state (BR-7). The Transformation module does not have direct access to shared state scalars, but `MAX(s.as_of)` from the date-filtered securities data equals `__maxEffectiveDate` because: (a) DataSourcing filters `WHERE as_of >= min AND as_of <= max`, (b) securities is a daily snapshot table with data for every calendar day, so `MAX(as_of)` = `maxEffectiveDate`.

6. **`GROUP BY s.security_id, s.ticker, s.security_name, s.sector`:** Groups by security identity fields. Multiple as_of dates for the same security (if effective date range spans multiple days) are collapsed into a single output row. Under normal auto-advance operation (min=max), each security appears once in the source data, so the GROUP BY simply passes through. This is actually MORE correct than V1, which would produce duplicate rows per security across multi-day ranges.

7. **`ORDER BY s.security_id`:** Provides deterministic output ordering. V1 output order follows the iteration order of the bonds list (BR-8, MEDIUM confidence). For single-day runs, securities ordered by `as_of` then natural row order from Postgres would typically be ordered by `security_id`. Explicit `ORDER BY security_id` ensures deterministic, reproducible ordering.

8. **Empty result handling:** If no securities have `security_type = 'Bond'`, the WHERE clause filters all rows and the query returns zero rows. The Transformation module produces an empty DataFrame, which the ParquetFileWriter writes as an empty Parquet file. This matches V1 behavior (BR-2, BR-3).

---

## 6. V2 Job Config

```json
{
  "jobName": "BondMaturityScheduleV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "securities",
      "schema": "datalake",
      "table": "securities",
      "columns": ["security_id", "ticker", "security_name", "security_type", "sector"]
    },
    {
      "type": "DataSourcing",
      "resultName": "holdings",
      "schema": "datalake",
      "table": "holdings",
      "columns": ["security_id", "current_value"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT s.security_id, COALESCE(s.ticker, '') AS ticker, COALESCE(s.security_name, '') AS security_name, COALESCE(s.sector, '') AS sector, ROUND(COALESCE(SUM(h.current_value), 0), 2) AS total_held_value, COUNT(h.current_value) AS holder_count, MAX(s.as_of) AS as_of FROM securities s LEFT JOIN holdings h ON s.security_id = h.security_id WHERE s.security_type = 'Bond' GROUP BY s.security_id, s.ticker, s.security_name, s.sector ORDER BY s.security_id"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/bond_maturity_schedule/",
      "numParts": 1,
      "writeMode": "Overwrite"
    }
  ]
}
```

---

## 7. Writer Configuration

| Property | V1 Value | V2 Value | Match? |
|----------|----------|----------|--------|
| Writer type | ParquetFileWriter | ParquetFileWriter | YES |
| source | `output` | `output` | YES |
| outputDirectory | `Output/curated/bond_maturity_schedule/` | `Output/double_secret_curated/bond_maturity_schedule/` | Path changed per V2 convention |
| numParts | 1 | 1 | YES |
| writeMode | Overwrite | Overwrite | YES |

---

## 8. Proofmark Config Design

**Starting assumption: zero exclusions, zero fuzzy columns.**

| Column | Treatment | Justification |
|--------|-----------|---------------|
| `security_id` | STRICT (default) | Deterministic integer from source data. No reason for fuzzy. |
| `ticker` | STRICT (default) | Deterministic string, null-coalesced to empty string in both V1 and V2. |
| `security_name` | STRICT (default) | Same as ticker. |
| `sector` | STRICT (default) | Same as ticker. |
| `total_held_value` | STRICT (default) | Deterministic sum of decimal values, rounded to 2dp. V1 uses decimal accumulation + banker's rounding; V2 uses SQLite double accumulation + arithmetic rounding. For typical monetary data, both produce identical results. If Proofmark finds a difference here, upgrade to FUZZY with tolerance 0.01 and investigate. |
| `holder_count` | STRICT (default) | Deterministic integer count. |
| `as_of` | STRICT (default) | Deterministic date derived from effective date. |

**Proofmark config:**

```yaml
comparison_target: "bond_maturity_schedule"
reader: parquet
threshold: 100.0
```

No EXCLUDED or FUZZY columns. All columns compared strictly. The BRD identifies zero non-deterministic fields.

**Risk note:** If `total_held_value` fails strict comparison due to the W5 rounding mode difference (banker's vs arithmetic), the resolution path is:
1. First, verify whether the mismatch is truly a rounding mode issue (check if the differing values are at exact midpoints)
2. If confirmed, add `total_held_value` as FUZZY with tolerance 0.005, and escalate to Tier 2 to implement exact banker's rounding in an External module

---

## 9. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|-------------|----------------|----------|
| Tier 1 (SQL only, no External) | AP3 elimination | V1 External performs only SQL-native operations (filter, join, aggregate, coalesce, round) |
| Remove `exchange` from securities DataSourcing | AP4 (unused columns) | `exchange` never referenced in V1 output schema or External module output [BondMaturityScheduleBuilder.cs:10-14] |
| Remove `holding_id`, `investment_id`, `customer_id`, `quantity`, `cost_basis` from holdings DataSourcing | AP4 (unused columns) | Only `security_id` and `current_value` used in V1 aggregation logic [BondMaturityScheduleBuilder.cs:57,60] |
| `WHERE s.security_type = 'Bond'` | BR-1 | [BondMaturityScheduleBuilder.cs:29-31] |
| Empty DataFrame on zero bonds | BR-2, BR-3 | [BondMaturityScheduleBuilder.cs:19-23, 33-37] -- SQL naturally returns zero rows when no bonds match |
| `LEFT JOIN holdings` + `COALESCE(SUM, 0)` | BR-4, BR-6 | [BondMaturityScheduleBuilder.cs:52-67, 75-77] -- bonds with no holdings get zeros |
| `ROUND(..., 2)` on total_held_value | BR-5 | [BondMaturityScheduleBuilder.cs:85] -- `Math.Round(totals.totalValue, 2)` |
| `MAX(s.as_of)` for as_of column | BR-7 | [BondMaturityScheduleBuilder.cs:25,87] -- V1 uses `__maxEffectiveDate`, SQL equivalent is MAX(as_of) from date-filtered data |
| `ORDER BY s.security_id` | BR-8 | [BondMaturityScheduleBuilder.cs:72] -- V1 iterates bonds in source order; explicit ORDER BY provides deterministic equivalent |
| Holdings join on security_id | BR-9 | [BondMaturityScheduleBuilder.cs:57-58] -- holdings filtered to bond security_ids |
| `COALESCE(s.ticker, '')` etc. | BR-10 | [BondMaturityScheduleBuilder.cs:44-48] -- `?.ToString() ?? ""` |
| No explicit date filters in DataSourcing | BR-11 | [bond_maturity_schedule.json:6-18] -- V1 uses framework date injection |
| `COUNT(h.current_value)` not `COUNT(*)` | BR-4, BR-6 | COUNT(column) excludes NULLs from LEFT JOIN, giving 0 for unmatched bonds |
| ParquetFileWriter with Overwrite, numParts=1 | Writer config | [bond_maturity_schedule.json:24-30] |
| Set-based SQL replaces row-by-row foreach | AP6 elimination | [BondMaturityScheduleBuilder.cs:41-49, 55-67] |
| Misleading job name documented | AP9 | Cannot rename -- output path must match V1 structure |

---

## 10. External Module Design

**Not applicable.** This is a Tier 1 implementation. No External module is needed.

---

## Appendix: Edge Case Coverage in SQL

| Edge Case (from BRD) | SQL Behavior |
|----------------------|-------------|
| No bonds in data | `WHERE security_type = 'Bond'` filters all rows; query returns zero rows; empty Parquet written |
| Bond with no holdings | `LEFT JOIN` preserves the bond row; `COALESCE(SUM(NULL), 0) = 0`; `COUNT(NULL) = 0` |
| NULL security_type | `WHERE s.security_type = 'Bond'` excludes NULLs (NULL != 'Bond' in SQL) |
| Weekend/holiday dates | DataSourcing returns whatever data exists for the effective date; SQL processes it as-is. If holdings has no data for the effective date, LEFT JOIN produces NULLs handled by COALESCE. |
| Multiple as_of dates | GROUP BY collapses duplicates across dates. For single-day runs (normal operation), no duplicates exist. For multi-day ranges, V2 deduplicates while V1 would produce duplicate rows -- V2 behavior is arguably more correct, and under normal auto-advance operation (min=max) the output is identical. |
| NULL current_value | `SUM` ignores NULLs. V1 would throw an exception on `Convert.ToDecimal(null)` (BRD edge case 6). V2 handles this gracefully. Since V1 would crash, this scenario cannot exist in production data. |
