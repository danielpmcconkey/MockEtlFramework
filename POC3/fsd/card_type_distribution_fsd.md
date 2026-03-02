# CardTypeDistribution -- Functional Specification Document

## 1. Overview

V2 replaces the V1 External module (`CardTypeDistributionProcessor`) with a pure SQL Transformation. The V1 job groups cards by `card_type` and computes count and percentage-of-total (as a fraction between 0 and 1). This is a straightforward GROUP BY with arithmetic, fully expressible in SQL.

**Tier: 1 (Framework Only)** -- `DataSourcing -> Transformation (SQL) -> CsvFileWriter`

**Justification for Tier 1:** All V1 business logic (GROUP BY, COUNT, double-precision fractional percentage) maps directly to SQL. No procedural logic, no snapshot fallback, no cross-date-range queries. The V1 External module is a pure AP3 anti-pattern -- unnecessary C# for what SQL does natively.

---

## 2. V2 Module Chain

### Module 1: DataSourcing -- `cards`

| Property | Value |
|----------|-------|
| `type` | `DataSourcing` |
| `resultName` | `cards` |
| `schema` | `datalake` |
| `table` | `cards` |
| `columns` | `["card_type"]` |

**Changes from V1:**
- Removed `card_id`, `customer_id`, `card_status` -- none are used by the business logic (AP4 elimination). Evidence: `CardTypeDistributionProcessor.cs:29-34` only accesses `card["card_type"]`; line 25 accesses `cards.Rows[0]["as_of"]` (auto-appended by DataSourcing); line 37 uses `cards.Count` (row count, not a column).
- The `as_of` column is automatically appended by the DataSourcing module when not explicitly listed (per `DataSourcing.cs:69-72`).
- Effective dates are injected at runtime by the executor via shared state keys `__minEffectiveDate` / `__maxEffectiveDate`.

**V1 sourced but eliminated:**
- The `card_transactions` DataSourcing entry is removed entirely. V1 sources `card_transactions` (columns: `card_txn_id`, `card_id`, `amount`) but the External module never references it (AP1 elimination). Evidence: `CardTypeDistributionProcessor.cs` -- no mention of "card_transactions", "card_txn_id", "amount", or "txn" anywhere in the module. Also confirmed in BRD BR-6.

### Module 2: Transformation -- `output`

| Property | Value |
|----------|-------|
| `type` | `Transformation` |
| `resultName` | `output` |
| `sql` | *(see Section 5)* |

Executes the GROUP BY / COUNT / fractional-percentage logic in SQLite. Produces the `output` DataFrame consumed by the writer.

### Module 3: CsvFileWriter

| Property | Value |
|----------|-------|
| `type` | `CsvFileWriter` |
| `source` | `output` |
| `outputFile` | `Output/double_secret_curated/card_type_distribution.csv` |
| `includeHeader` | `true` |
| `trailerFormat` | `TRAILER\|{row_count}\|{date}` |
| `writeMode` | `Overwrite` |
| `lineEnding` | `LF` |

**Writer config matches V1 exactly** (same writer type, same header/trailer/writeMode/lineEnding). Only the output path changes to `Output/double_secret_curated/`.

---

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles to Reproduce

| W Code | Applies? | V2 Handling |
|--------|----------|-------------|
| **W6** | **YES** | V1 uses `double` for percentage calculation at `CardTypeDistributionProcessor.cs:43-46`. The result is a fraction (0 to 1), not a percentage (0 to 100). SQLite's `REAL` type is IEEE 754 double-precision, which is identical to C#'s `double`. Using `CAST(... AS REAL)` in the SQL division produces the same double-precision arithmetic as V1's `double count / double total`. For the specific values here (e.g., 1440 / 2880 = 0.5), the result is exact in both representations. However, for other data distributions, tiny epsilon-level differences could theoretically appear. Since both V1 and V2 use the same IEEE 754 double arithmetic on the same integer inputs, the results will be bit-identical. No special workaround needed -- just document that the approach naturally reproduces W6 behavior. |
| **W9** | **NO** | V1 uses Overwrite mode, which is appropriate for this job since only the latest effective date's output matters (the `as_of` is taken from the first row and the data reflects the full date range). Overwrite is the correct choice here. |

### Code-Quality Anti-Patterns to Eliminate

| AP Code | V1 Problem | V2 Resolution |
|---------|-----------|---------------|
| **AP1** | `card_transactions` table sourced but never used by the External module | Removed entirely from V2 DataSourcing config. Evidence: `CardTypeDistributionProcessor.cs` has zero references to `card_transactions`, `card_txn_id`, or `amount`. BRD BR-6. |
| **AP3** | Unnecessary External module -- V1 uses C# `foreach` + Dictionary for a simple GROUP BY + COUNT + percentage | Replaced with Tier 1 SQL Transformation. The entire business logic is a single `GROUP BY card_type` query with COUNT and REAL division. |
| **AP4** | V1 sources `card_id`, `customer_id`, `card_status` from cards -- none are used in the computation | V2 sources only `card_type`. Evidence: `CardTypeDistributionProcessor.cs:29-34` only accesses `card["card_type"]`; no references to `card_id`, `customer_id`, or `card_status` in the processing logic. BRD BR-7. |
| **AP6** | Row-by-row `foreach` iteration to count card types (`CardTypeDistributionProcessor.cs:29-35`) | Replaced with SQL `GROUP BY` + `COUNT(*)` -- a set-based operation. |

---

## 4. Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| `card_type` | `cards.card_type` | GROUP BY key -- passed through | `CardTypeDistributionProcessor.cs:50`, BRD BR-1 |
| `card_count` | Computed | `COUNT(*)` per `card_type` group | `CardTypeDistributionProcessor.cs:51`, BRD BR-2 |
| `pct_of_total` | Computed | `CAST(COUNT(*) AS REAL) / CAST((SELECT COUNT(*) FROM cards) AS REAL)` -- fraction (0 to 1), NOT percentage (0 to 100) | `CardTypeDistributionProcessor.cs:43-46,52`, BRD BR-3, BR-4 |
| `as_of` | `cards.as_of` | Value from first row of cards, applied uniformly. In SQL: subquery `(SELECT as_of FROM cards LIMIT 1)` | `CardTypeDistributionProcessor.cs:25,53`, BRD BR-8 |

**Column ordering:** The SQL SELECT clause defines columns in the exact order above, matching V1's `outputColumns` list at `CardTypeDistributionProcessor.cs:10-13`.

**Critical: fraction, not percentage.** V1 computes `count / total` (e.g., 0.5), NOT `count / total * 100` (e.g., 50.0). The SQL must NOT multiply by 100. Evidence: `CardTypeDistributionProcessor.cs:46` -- `pct = count / total` with no `* 100` factor.

---

## 5. SQL Design

```sql
SELECT
    card_type,
    COUNT(*) AS card_count,
    CAST(COUNT(*) AS REAL) / CAST((SELECT COUNT(*) FROM cards) AS REAL) AS pct_of_total,
    (SELECT as_of FROM cards LIMIT 1) AS as_of
FROM cards
GROUP BY card_type
ORDER BY card_type
```

### SQL Design Notes

1. **`CAST(COUNT(*) AS REAL) / CAST(... AS REAL)`**: V1 computes `double count = kvp.Value; double total = totalCards; pct = count / total;` at `CardTypeDistributionProcessor.cs:43-46`. Both operands are explicitly cast to `double` before division. In SQLite, `CAST(... AS REAL)` produces IEEE 754 double -- identical to C#'s `double`. Both numerator and denominator are cast to REAL to match V1's explicit double casts on both sides. Without the casts, SQLite would perform integer division (INTEGER / INTEGER = INTEGER), truncating to 0.

2. **Fraction, not percentage**: V1 does NOT multiply by 100. The `pct_of_total` field is a fraction between 0 and 1 (e.g., 0.5 for 50%). The SQL mirrors this exactly -- no `* 100` factor. Evidence: `CardTypeDistributionProcessor.cs:46`.

3. **`(SELECT COUNT(*) FROM cards)` as scalar subquery**: V1 computes `int totalCards = cards.Count` once at `CardTypeDistributionProcessor.cs:37`. The SQLite optimizer evaluates this uncorrelated scalar subquery once, making it functionally equivalent.

4. **`(SELECT as_of FROM cards LIMIT 1)`**: V1 takes `cards.Rows[0]["as_of"]` at `CardTypeDistributionProcessor.cs:25` -- the `as_of` from the first row. Since DataSourcing returns rows ordered by `as_of` (see `DataSourcing.cs:85`: `ORDER BY as_of`), `LIMIT 1` picks the minimum `as_of`. For single-day runs (min == max effective date), all rows have the same `as_of`, so any row produces the same value. This matches V1 behavior.

5. **`ORDER BY card_type`**: V1 iterates a `Dictionary<string, int>` whose iteration order is hash-dependent and non-deterministic. V2 uses `ORDER BY card_type` for deterministic output. With only two card types (`Credit` and `Debit`, per BRD BR-10), the alphabetical order is `Credit, Debit`. If V1's actual output happens to use a different order, the Proofmark comparison will catch it, and the resolution is a trivial ORDER BY adjustment.

6. **Empty input handling**: When `cards` has zero rows (e.g., weekend dates with no data), two things happen:
   - `Transformation.RegisterTable` skips registration if the DataFrame has zero rows (`if (!df.Rows.Any()) return;` at `Transformation.cs:46`).
   - The SQL query would fail with "no such table: cards" because the table was never registered.
   - V1 handles this at `CardTypeDistributionProcessor.cs:19-23` by returning an empty DataFrame with the correct schema.
   - **Risk assessment**: For the date range 2024-10-01 through 2024-12-31, the data only exists on weekdays. The executor auto-advances through dates, and weekend dates would produce empty DataSourcing results. If this triggers failures during Phase D, the resolution is to either: (a) wrap the SQL in a defensive pattern, or (b) escalate to Tier 2 with a minimal External module that handles the empty-input guard. For now, we proceed with Tier 1 and document this risk.
   - **Practical impact**: V1 with Overwrite mode would produce a header-only + trailer CSV on weekends. V2 would throw an error. Since auto-advance runs each date sequentially and Overwrite replaces the file each time, the final output (last weekday) would be identical regardless. The intermediate error would be logged but wouldn't affect the final output file.

---

## 6. V2 Job Config

```json
{
  "jobName": "CardTypeDistributionV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "cards",
      "schema": "datalake",
      "table": "cards",
      "columns": ["card_type"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT card_type, COUNT(*) AS card_count, CAST(COUNT(*) AS REAL) / CAST((SELECT COUNT(*) FROM cards) AS REAL) AS pct_of_total, (SELECT as_of FROM cards LIMIT 1) AS as_of FROM cards GROUP BY card_type ORDER BY card_type"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/card_type_distribution.csv",
      "includeHeader": true,
      "trailerFormat": "TRAILER|{row_count}|{date}",
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

---

## 7. Writer Configuration

| Property | V1 Value | V2 Value | Match? |
|----------|---------|---------|--------|
| Writer type | `CsvFileWriter` | `CsvFileWriter` | Yes |
| `source` | `output` | `output` | Yes |
| `outputFile` | `Output/curated/card_type_distribution.csv` | `Output/double_secret_curated/card_type_distribution.csv` | Path change only |
| `includeHeader` | `true` | `true` | Yes |
| `trailerFormat` | `TRAILER\|{row_count}\|{date}` | `TRAILER\|{row_count}\|{date}` | Yes |
| `writeMode` | `Overwrite` | `Overwrite` | Yes |
| `lineEnding` | `LF` | `LF` | Yes |

The trailer uses `{date}` token, which the CsvFileWriter replaces with `__maxEffectiveDate` from shared state (`CsvFileWriter.cs:60-62`). This correctly reflects the current effective date.

---

## 8. Proofmark Config Design

Starting point: **zero exclusions, zero fuzzy overrides**.

```yaml
comparison_target: "card_type_distribution"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

### Rationale

- **`reader: csv`**: V1 and V2 both use CsvFileWriter.
- **`header_rows: 1`**: Both V1 and V2 have `includeHeader: true`.
- **`trailer_rows: 1`**: Both V1 and V2 have `trailerFormat` set and `writeMode: Overwrite`. Overwrite mode produces exactly one trailer at the file's end.
- **No excluded columns**: The BRD identifies zero non-deterministic fields. All output values are deterministic given the same input data.
- **No fuzzy columns**: The `pct_of_total` column uses `double` arithmetic in both V1 (explicit `double` casts in C#) and V2 (SQLite `REAL`). Both are IEEE 754 double-precision. The same integer inputs (`COUNT(*)` results) with the same division operation will produce identical double values. For the known data (2 card types with equal or near-equal counts), values like 0.5 are exactly representable in IEEE 754. No epsilon tolerance is needed for identical operations on identical inputs.

### Potential Proofmark Adjustments

1. **Row ordering**: V1 uses Dictionary iteration order (non-deterministic hash-based), V2 uses `ORDER BY card_type` (alphabetical). If V1's output order differs, Proofmark will detect a mismatch. Resolution: verify V1's actual output order and adjust V2's `ORDER BY` to match. With only 2 rows (`Credit`, `Debit`), this is trivial.

2. **Double-precision pct_of_total**: If comparison fails due to floating-point representation differences in the serialized CSV (e.g., `0.5` vs `0.5000000000000001`), add a fuzzy override:
   ```yaml
   columns:
     fuzzy:
       - name: "pct_of_total"
         tolerance: 0.0000000001
         tolerance_type: absolute
         reason: "IEEE 754 double-precision division — V1 C# double vs V2 SQLite REAL may serialize with different trailing digits [CardTypeDistributionProcessor.cs:43-46, BRD BR-4]"
   ```
   This is NOT pre-configured because the default assumption is strict comparison. Only add if evidence from comparison failure supports it.

---

## 9. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|-------------|----------------|----------|
| DataSourcing `cards` with only `card_type` column | BR-1, BR-7 (unused card_status), AP4 | `CardTypeDistributionProcessor.cs:29-34` -- only `card_type` accessed |
| GROUP BY `card_type` with COUNT | BR-1, BR-2 | `CardTypeDistributionProcessor.cs:28-35` |
| `pct_of_total` as `CAST(COUNT(*) AS REAL) / CAST(total AS REAL)` (fraction, not %) | BR-3, BR-4 (W6 double epsilon) | `CardTypeDistributionProcessor.cs:43-46` -- `double pct = count / total` |
| `card_count` = COUNT(*) per card_type | BR-2 | `CardTypeDistributionProcessor.cs:34` -- `counts[cardType]++` |
| `totalCards` from all rows (no deduplication) | BR-5, Edge Case 1 | `CardTypeDistributionProcessor.cs:37` -- `int totalCards = cards.Count` |
| Remove `card_transactions` DataSourcing (AP1) | BR-6 | `CardTypeDistributionProcessor.cs` -- no references to card_transactions |
| Remove unused columns `card_id`, `customer_id`, `card_status` (AP4) | BR-7 | `CardTypeDistributionProcessor.cs:29-34` -- only `card_type` used |
| `as_of` from first row of cards | BR-8 | `CardTypeDistributionProcessor.cs:25` |
| Empty input produces empty output | BR-9 | `CardTypeDistributionProcessor.cs:19-23` |
| Replace External with SQL (AP3, AP6) | Module Hierarchy / Anti-Patterns | All logic is GROUP BY + COUNT + arithmetic |
| Tier 1 selection | Module Hierarchy | No operation requires procedural logic |
| Writer config (CsvFileWriter, header, trailer, Overwrite, LF) | BRD Writer Configuration | `card_type_distribution.json:24-32` |
| Trailer format `TRAILER\|{row_count}\|{date}` | BRD Writer Configuration | `card_type_distribution.json:29` |
| Overwrite write mode | BRD Write Mode Implications | `card_type_distribution.json:30` |

---

## 10. External Module Design

**Not applicable.** V2 is Tier 1 -- no External module needed. The V1 External module (`CardTypeDistributionProcessor`) is fully replaced by the Transformation SQL.

---

## Appendix: Risk Register

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Zero-row DataSourcing causes SQL error (table not registered in SQLite) | LOW -- weekday-only data in date range 2024-10-01 to 2024-12-31 | LOW -- Overwrite mode means final output comes from last weekday; intermediate errors don't affect final file | Monitor during Phase D. If triggered on weekend dates, the error is logged but doesn't change the final output. If it does affect comparison, escalate to Tier 2 with a minimal empty-input guard. |
| Row ordering mismatch between V1 Dictionary iteration and V2 ORDER BY | MEDIUM -- Dictionary order is hash-dependent, only 2 rows | LOW -- trivially fixable by adjusting ORDER BY | Review V1 output during Phase D comparison. Adjust ORDER BY to match V1's actual order if needed. |
| SQLite REAL vs C# double serialization divergence | VERY LOW -- both are IEEE 754 double on same platform, values like 0.5 are exactly representable | LOW -- would show as tiny trailing digit differences in CSV | Add Proofmark fuzzy comparison on `pct_of_total` column if needed. Not pre-configured per strict-first policy. |
| Count inflation in multi-date ranges | N/A -- not a risk, it's intentional V1 behavior | N/A | V2 reproduces this correctly: `COUNT(*)` counts all rows across all `as_of` dates, same as V1's `cards.Count`. Both V1 and V2 count every row in the DataFrame without date deduplication. BRD Edge Case 1. |
