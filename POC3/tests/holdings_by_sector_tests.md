# HoldingsBySector — V2 Test Plan

## Job Info
- **V2 Config**: `holdings_by_sector_v2.json`
- **Tier**: 2 (Framework + Minimal External: DataSourcing -> Transformation -> External -> no CsvFileWriter)
- **External Module**: `HoldingsBySectorV2Processor` (writes CSV directly with W7 inflated trailer count)

## Pre-Conditions
- **Data sources required:**
  - `datalake.holdings` — columns sourced in V2: `security_id`, `current_value` (plus framework-injected `as_of`)
  - `datalake.securities` — columns sourced in V2: `security_id`, `sector` (plus framework-injected `as_of`)
- **Expected schemas:**
  - `holdings`: holding_id (integer), investment_id (integer), security_id (integer), customer_id (integer), quantity (numeric), cost_basis (numeric), current_value (numeric), as_of (date)
  - `securities`: security_id (integer), ticker (varchar), security_name (varchar), security_type (varchar), sector (varchar), exchange (varchar), as_of (date)
- **Effective date range:** `firstEffectiveDate` is `2024-10-01`. Framework auto-advance controls the date range. DataSourcing injects `__minEffectiveDate` / `__maxEffectiveDate` to filter both tables by `as_of`.
- **V1 External module reference:** `ExternalModules.HoldingsBySectorWriter` (`HoldingsBySectorWriter.cs`) — performs all logic: sector lookup, grouping, aggregation, direct file I/O with inflated trailer.
- **Known data characteristics:**
  - Holdings table has ~1303 rows per effective date (observed in V1 trailer)
  - 8 distinct sectors after grouping: Consumer, Energy, Finance, Healthcare, Industrial, Real Estate, Technology, Utilities
  - Sector values contain no commas, quotes, or special characters (safe for unquoted CSV)

## Test Cases

### TC-1: Output Schema Validation
- **Traces to:** FSD Section 4
- **Expected columns (exact order):**
  1. `sector` — TEXT, from `securities.sector` via LEFT JOIN, NULL/unmatched defaulted to `"Unknown"` by COALESCE
  2. `holding_count` — INTEGER, COUNT of holdings rows per sector group
  3. `total_value` — DECIMAL(2), ROUND(SUM(holdings.current_value), 2) per sector group
  4. `as_of` — TEXT, `__maxEffectiveDate` formatted as `yyyy-MM-dd`
- **Expected header line:** `sector,holding_count,total_value,as_of`
- **Trailer row format:** `TRAILER|{input_holdings_count}|{date}` (NOT a data column — this is a footer appended after all data rows)
- **Verification method:** Read the first line of the output CSV. Confirm column names and order match exactly: `sector,holding_count,total_value,as_of`. Confirm column count is 4. Compare V2 header against V1 header byte-for-byte.

### TC-2: Row Count Equivalence
- **Traces to:** BRD BR-1, BR-5
- **V1 vs V2 must produce identical row counts (data rows, excluding header and trailer).**
- **Expected behavior:** Both V1 and V2 produce one row per distinct sector. V1 uses `.OrderBy(k => k.Key)` on a dictionary keyed by sector name. V2 uses `ORDER BY sector` in SQL. Both should yield 8 rows (one per sector: Consumer, Energy, Finance, Healthcare, Industrial, Real Estate, Technology, Utilities) assuming all holdings match a security with a non-NULL sector.
- **Verification method:** Count data rows in V1 output and V2 output (exclude header row and trailer row). Counts must be identical. Proofmark comparison (with `trailer_rows: 1`) handles this automatically.

### TC-3: Data Content Equivalence
- **Traces to:** FSD Section 5 (SQL Design), Section 3 (Anti-Pattern Analysis)
- **All values must be byte-identical to V1 output.**
- **W-codes affecting comparison:**
  - **W5 (Banker's rounding):** V1 uses `Math.Round(totalValue, 2)` with default `MidpointRounding.ToEven`. V2 uses SQLite `ROUND(SUM(...), 2)` which uses round-half-away-from-zero. If any sector's total value lands on an exact midpoint (X.XX5), the rounded value could differ by 0.01. See TC-W1.
  - **W6 (Double epsilon):** V1 accumulates with C# `decimal` (exact base-10). V2 uses SQLite REAL (64-bit IEEE 754 float) for SUM. Intermediate sums may differ due to floating-point representation. See TC-W2.
  - **W7 (Trailer inflated count):** Trailer uses input holdings count, not output row count. See TC-W3.
  - **W9 (Wrong writeMode):** Overwrite mode, prior days lost. Does not affect content, only file lifecycle. See TC-W4.
- **Verification method:** Run Proofmark with `threshold: 100.0` (strict, no tolerance). If Proofmark detects differences in `total_value`, promote to FUZZY with `tolerance: 0.01, tolerance_type: absolute` or escalate rounding into the External module using `Math.Round(x, 2, MidpointRounding.ToEven)`.
- **Trailer comparison:** Proofmark strips `trailer_rows: 1` before data comparison. Verify trailer content separately (see TC-W3).

### TC-4: Writer Configuration
- **Traces to:** FSD Section 7, BRD Writer Configuration
- **Note:** This job does NOT use CsvFileWriter. The External module (`HoldingsBySectorV2Processor`) writes the CSV file directly using StreamWriter. The V2 config has no CsvFileWriter module entry.
- **Verify all output format settings match V1:**

| Property | Expected Value | V1 Match | Evidence |
|----------|---------------|----------|----------|
| Output path | `Output/double_secret_curated/holdings_by_sector.csv` | Path updated per V2 convention | FSD Section 7 |
| Encoding | UTF-8 (StreamWriter default) | YES | HoldingsBySectorWriter.cs:55 |
| Line ending | LF (`\n`) | YES | HoldingsBySectorWriter.cs:57,63,67 |
| Header | Yes: `sector,holding_count,total_value,as_of` | YES | HoldingsBySectorWriter.cs:57 |
| Trailer | `TRAILER|{inputCount}|{dateStr}` | YES (W7) | HoldingsBySectorWriter.cs:67 |
| Write mode | Overwrite (`append: false`) | YES (W9) | HoldingsBySectorWriter.cs:55 |
| RFC 4180 quoting | None | YES | HoldingsBySectorWriter.cs:63 |

- **Verification method:**
  - Inspect V2 External module source code for StreamWriter configuration.
  - Verify output file has LF line endings using `xxd` or `od`. No `\r\n` sequences.
  - Verify header is the first line of the file and matches `sector,holding_count,total_value,as_of`.
  - Verify trailer is the last line of the file and matches `TRAILER|{count}|{date}` format.
  - Verify no RFC 4180 quoting is applied (no double-quotes around field values).
  - Run job twice for different dates. Confirm the file contains only the second run's output (Overwrite).

### TC-5: Anti-Pattern Elimination Verification
- **Traces to:** FSD Section 3

| AP-Code | Anti-Pattern | What to Verify |
|---------|-------------|----------------|
| AP3 | Unnecessary External (partial) | V1 External handles ALL logic (sourcing, grouping, writing). V2 External handles ONLY the W7 trailer file write. Verify V2 config has DataSourcing + Transformation modules upstream of External. Business logic (join, group, aggregate) is in the SQL Transformation, not the External module. |
| AP4 | Unused columns | V2 DataSourcing for `holdings` sources only `["security_id", "current_value"]` (was 7 columns: holding_id, investment_id, security_id, customer_id, quantity, cost_basis, current_value). V2 DataSourcing for `securities` sources only `["security_id", "sector"]` (was 6 columns: security_id, ticker, security_name, security_type, sector, exchange). Confirm column lists in V2 config match. |
| AP6 | Row-by-row iteration | V1 uses `foreach` loops for sector lookup dictionary and group accumulation. V2 replaces this with SQL `LEFT JOIN ... GROUP BY`. The External module's row-by-row iteration is limited to CSV I/O (inherently row-by-row). Verify the Transformation SQL uses GROUP BY, not procedural logic. |
| AP7 | Magic values (partial) | V1 hardcodes `"Unknown"` as default sector. V2 uses `COALESCE(s.sector, 'Unknown')` in SQL, which is idiomatic. Verify the External module uses named constants for output path, trailer prefix, and header line. |

- **Verification method:** Read `holdings_by_sector_v2.json` and confirm module chain is DataSourcing -> DataSourcing -> Transformation -> External. Read the V2 External module source code and confirm it handles ONLY file I/O (no joins, no grouping, no aggregation). Confirm column lists in DataSourcing modules are minimal.

### TC-6: Edge Cases
- **Traces to:** BRD Edge Cases, FSD Sections 4, 5

1. **Empty input (BR-3):** If `holdings` or `securities` is null or empty, the External module returns early with an empty DataFrame as `output` in shared state. No CSV file is written. Verify the V2 External module has this guard clause. If the Transformation produces zero rows from the SQL (e.g., empty holdings table), the External module should still handle this gracefully.

2. **Holdings with unknown security_id (BR-2, BR-9):** Holdings whose `security_id` has no match in `securities` are retained by the LEFT JOIN and grouped under sector `"Unknown"` via `COALESCE(s.sector, 'Unknown')`. Verify the SQL uses LEFT JOIN (not INNER JOIN) and COALESCE handles the NULL case.

3. **NULL sector in securities (BR-9):** If a securities row has a NULL `sector` value, `COALESCE(s.sector, 'Unknown')` maps it to `"Unknown"`. This is consistent with the unmatched case (same default). Verify both paths produce the same `"Unknown"` grouping.

4. **Cross-date aggregation (BRD Edge Case 4):** When the effective date range spans multiple days, both `holdings` and `securities` contain multiple snapshots. V1's dictionary lookup overwrites per `security_id` (last-seen wins). V2's SQL uses `AND h.as_of = s.as_of` to join within the same snapshot date, which is semantically cleaner. For single-day auto-advance (the actual execution pattern), both produce identical results. If Proofmark comparison fails for multi-day ranges, this join condition may need adjustment.

5. **NULL current_value in holdings (BRD Edge Case 5):** V1 uses `Convert.ToDecimal(null)` which would throw an exception. V2's SQL `SUM(h.current_value)` treats NULLs as ignored in aggregation. If the source data contains NULL `current_value`, V2's behavior differs from V1 (which crashes). Since V1 does not crash in practice, NULL `current_value` values are presumed absent from the data.

6. **"Real Estate" sector name (BRD Edge Case 6):** Contains a space but no commas, quotes, or special characters. Safe for unquoted CSV output. No RFC 4180 quoting is applied in either V1 or V2.

7. **Sector ordering:** V1 orders by dictionary key (`OrderBy(k => k.Key)`). V2 SQL uses `ORDER BY sector`. Both produce alphabetical ascending order. Verify the output rows are in alphabetical order by sector name: Consumer, Energy, Finance, Healthcare, Industrial, Real Estate, Technology, Utilities.

### TC-7: Proofmark Configuration
- **Traces to:** FSD Section 8
- **Expected proofmark settings:**

```yaml
comparison_target: "holdings_by_sector"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

| Setting | Expected Value | Rationale |
|---------|---------------|-----------|
| comparison_target | `holdings_by_sector` | Matches V1 output filename (without extension) |
| reader | `csv` | Output is CSV (direct file I/O in both V1 and V2) |
| threshold | `100.0` | All fields are deterministic; start strict |
| header_rows | `1` | CSV has one header row |
| trailer_rows | `1` | V1 writes one trailer row (`TRAILER|{count}|{date}`). Proofmark must strip it before data comparison. |
| EXCLUDED columns | None | No non-deterministic fields (no timestamps, no UUIDs) |
| FUZZY columns | None (initial) | Starting strict per best practices. If W5/W6 cause `total_value` divergence, promote to FUZZY with `tolerance: 0.01, tolerance_type: absolute`. |

- **Trailer handling:** Proofmark with `trailer_rows: 1` strips the last line before comparing data rows. The trailer itself must be verified separately (see TC-W3). If Proofmark compares trailers as data, the inflated count would be correctly matched since both V1 and V2 use the same inflated value.

## W-Code Test Cases

### TC-W1: W5 — Banker's Rounding vs SQLite ROUND
- **Traces to:** FSD Section 3 (W5), Section 5 (SQL Design Note 4)
- **What the wrinkle is:** V1 uses `Math.Round(totalValue, 2)` which defaults to `MidpointRounding.ToEven` (banker's rounding). SQLite `ROUND(x, 2)` uses round-half-away-from-zero. These diverge at exact midpoints: e.g., `2.5` rounds to `2` in C# (banker's) but `3` in SQLite (away-from-zero).
- **How V2 handles it:** V2 uses SQLite `ROUND(SUM(h.current_value), 2)` in the Transformation SQL. If any sector's accumulated `SUM(current_value)` lands on an exact midpoint (X.XX5), the rounded `total_value` could differ by 0.01 from V1.
- **What to verify:**
  1. Run Proofmark with `threshold: 100.0` (strict). If PASS, the rounding difference does not manifest in the current data.
  2. If Proofmark fails on `total_value`, check which sector(s) differ and whether the SUM landed on a midpoint.
  3. **Mitigation path A:** Add FUZZY to Proofmark config: `total_value` with `tolerance: 0.01, tolerance_type: absolute`.
  4. **Mitigation path B:** Move the rounding into the V2 External module using `Math.Round(x, 2, MidpointRounding.ToEven)` for exact V1 equivalence. Remove ROUND from the SQL.
- **Risk assessment:** LOW to MEDIUM. Exact midpoints in accumulated sums of 2-decimal-place monetary values are unlikely but possible.

### TC-W2: W6 — Double Epsilon (decimal vs float accumulation)
- **Traces to:** FSD Section 3 (W6), Section 5 (SQL Design Note 5)
- **What the wrinkle is:** V1 accumulates `current_value` using C# `decimal` (exact base-10 arithmetic). V2's SQL uses SQLite REAL (64-bit IEEE 754 float) for SUM. The intermediate sums may differ from `decimal` accumulation due to floating-point representation errors (e.g., `0.1 + 0.2 != 0.3` in float).
- **How V2 handles it:** V2 accepts the risk of float accumulation in SQLite. For typical 2-decimal-place monetary values summed across moderate groups (~200 rows per sector), the difference is expected to be negligible after ROUND to 2 places.
- **What to verify:**
  1. Run Proofmark with `threshold: 100.0` (strict). If PASS, the epsilon difference does not manifest.
  2. If Proofmark fails on `total_value`, compare V1 and V2 values per sector. Check if differences are exactly 0.01 (suggesting a midpoint rounding divergence from W5) or a smaller epsilon (suggesting float accumulation error).
  3. **Mitigation path:** Same as TC-W1 — FUZZY tolerance or External module rounding.
- **Risk assessment:** LOW. IEEE 754 double-precision has ~15 significant digits. Summing ~200 values with 2 decimal places stays well within precision limits.

### TC-W3: W7 — Trailer Inflated Count
- **Traces to:** FSD Section 3 (W7), Section 4 (Trailer Row), BRD BR-7
- **What the wrinkle is:** V1's trailer uses `holdings.Count` (the number of raw input holdings rows BEFORE grouping) instead of the number of grouped output rows. For example, if 1303 holdings produce 8 sector groups, the trailer reads `TRAILER|1303|{date}` instead of `TRAILER|8|{date}`.
- **How V2 handles it:** The V2 External module (`HoldingsBySectorV2Processor`) reads `holdings` DataFrame from shared state to get `holdings.Count` and writes it into the trailer. The framework's CsvFileWriter is NOT used because its `{row_count}` token would substitute `output.Count` (grouped rows), which is wrong.
- **What to verify:**
  1. Read the last line of V2 output file. Confirm it matches the pattern `TRAILER|{N}|{date}` where `{N}` is the number of raw holdings rows (e.g., 1303) and `{date}` is the `__maxEffectiveDate` in `yyyy-MM-dd` format.
  2. Query `SELECT COUNT(*) FROM datalake.holdings WHERE as_of = '{effective_date}'` and confirm the trailer count matches.
  3. Read the last line of V1 output file. Confirm V1 and V2 trailers are byte-identical.
  4. Confirm the count is NOT the number of output data rows (e.g., 8 sectors).
- **CsvFileWriter justification:** Verify the V2 config has no CsvFileWriter module. The External module is the only mechanism that can produce the inflated trailer count.

### TC-W4: W9 — Wrong writeMode (Overwrite)
- **Traces to:** FSD Section 3 (W9), BRD Write Mode Implications
- **What the wrinkle is:** V1 opens the output file with `append: false` (Overwrite). For multi-day auto-advance runs, each effective date's run replaces the previous day's output. Only the final day's data survives.
- **How V2 handles it:** The V2 External module opens StreamWriter with `append: false`, matching V1 exactly.
- **What to verify:**
  1. Run the job for effective date 2024-10-01. Record the output file content (including trailer date).
  2. Run the job for effective date 2024-10-02. Verify the output file contains ONLY 2024-10-02's data and trailer. The 2024-10-01 data is gone.
  3. Confirm the V2 External module source code uses `append: false` or equivalent Overwrite semantics.
- **Impact:** For single-day auto-advance (the standard execution pattern), this is not an issue. Only relevant for multi-day ranges where prior days' output is silently lost.

## Notes

1. **Tier 2 justification is purely for the trailer.** The only reason this job is not Tier 1 is that CsvFileWriter's `{row_count}` token substitutes `df.Count` (output rows), but V1's trailer needs the input holdings row count (before grouping). The External module is a minimal scalpel for this one I/O requirement. All business logic (join, group, aggregate) lives in the SQL Transformation.

2. **W5/W6 are the primary risk.** The biggest concern for Proofmark equivalence is whether SQLite's float-based SUM + ROUND produces the same `total_value` as V1's decimal accumulation + banker's rounding. Start strict (threshold 100.0, no FUZZY). If differences appear in Phase D, the mitigation path is well-documented: either add FUZZY tolerance on `total_value` or move the rounding into the External module using `Math.Round(x, 2, MidpointRounding.ToEven)`.

3. **AP4 column reduction is significant.** V1 sources 13 columns across both tables; V2 sources only 4. This is a major code-quality improvement that does not affect output. The 9 eliminated columns (holding_id, investment_id, customer_id, quantity, cost_basis from holdings; ticker, security_name, security_type, exchange from securities) are never referenced in V1's processing logic.

4. **Trailer date format.** Both V1 and V2 format the trailer date as `yyyy-MM-dd` from `__maxEffectiveDate`. Verify the date format matches exactly (no time component, no timezone, no alternate formatting).

5. **Empty DataFrame as "output" in shared state.** V1 stores an empty DataFrame as `output` after writing the file directly (HoldingsBySectorWriter.cs:70-71). V2's External module should do the same to maintain shared state consistency, even though no downstream module consumes it.

6. **No CsvFileWriter in the module chain.** Unlike most jobs, this V2 config has no CsvFileWriter module. The External module is the last module in the chain and handles all file output. This is explicitly justified by W7.

7. **Cross-date join condition.** V2 SQL uses `AND h.as_of = s.as_of` for the join, while V1's dictionary overwrites per `security_id` across all dates. For single-day auto-advance runs, these are equivalent. For multi-day ranges, V2's approach is semantically cleaner. If Proofmark detects differences due to this, investigate whether `sector` values for the same `security_id` differ across `as_of` dates.
