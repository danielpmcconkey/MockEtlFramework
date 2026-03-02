# FundAllocationBreakdown — V2 Test Plan

## Job Info
- **V2 Config**: `fund_allocation_breakdown_v2.json`
- **Tier**: Tier 1 (Framework Only)
- **External Module**: None (V1 used `ExternalModules.FundAllocationWriter`; V2 replaces with Transformation SQL + CsvFileWriter)

## Pre-Conditions
- Data sources needed:
  - `datalake.holdings` — columns: `security_id`, `current_value`, `as_of` (auto-appended by DataSourcing)
  - `datalake.securities` — columns: `security_id`, `security_type`, `as_of` (auto-appended by DataSourcing)
- V1 also sourced `datalake.investments` (AP1 dead-end sourcing), which V2 drops entirely
- Effective date range: `firstEffectiveDate` = `2024-10-01`, auto-advanced through `2024-12-31`
- V1 baseline output must exist at `Output/curated/fund_allocation_breakdown.csv`
- V2 output writes to `Output/double_secret_curated/fund_allocation_breakdown.csv`

## Test Cases

### TC-1: Output Schema Validation
- **Expected columns** (exact order from FSD Section 4):
  1. `security_type` (TEXT)
  2. `holding_count` (INTEGER)
  3. `total_value` (REAL, 2 decimal places)
  4. `avg_value` (REAL, 2 decimal places)
  5. `as_of` (TEXT, yyyy-MM-dd format)
- Verify the header row is `security_type,holding_count,total_value,avg_value,as_of`
- Verify no extra columns are present

### TC-2: Row Count Equivalence
- V1 vs V2 must produce identical row counts (data rows only, excluding header and trailer)
- Row count equals the number of distinct `security_type` groups in the holdings-securities join
- Trailer `{row_count}` value must match the number of data rows in the file

### TC-3: Data Content Equivalence
- All values must be byte-identical to V1 output
- Key comparison areas:
  - `security_type` grouping labels (including `"Unknown"` for unmatched/NULL types)
  - `holding_count` integer values per group
  - `total_value` rounded to exactly 2 decimal places
  - `avg_value` rounded to exactly 2 decimal places
  - `as_of` date string
- Row ordering must be alphabetical by `security_type` (ASC)
- **Rounding risk**: SQLite ROUND uses half-away-from-zero; V1 C# `Math.Round` uses Banker's rounding (half-to-even). Divergence is unlikely for aggregated monetary sums but must be monitored. If mismatch detected, see Risk Register in FSD.

### TC-4: Writer Configuration
- **includeHeader**: `true` — verify header row is present as first line
- **writeMode**: `Overwrite` — verify file is replaced on each run (W9 replicated)
- **lineEnding**: `LF` — verify all line endings are `\n` (not `\r\n`)
- **trailerFormat**: `TRAILER|{row_count}|2024-10-01` — verify:
  - Trailer is the last line in the file
  - `{row_count}` is substituted with actual data row count
  - Date is hardcoded `2024-10-01` (W8 stale date replicated)
- **encoding**: UTF-8 without BOM
- **outputFile**: `Output/double_secret_curated/fund_allocation_breakdown.csv`

### TC-5: Anti-Pattern Elimination Verification
| AP-Code | What to Verify |
|---------|----------------|
| AP1 | V2 config does NOT contain a DataSourcing module for `investments`. Only `holdings` and `securities` are sourced. |
| AP3 | V2 config does NOT contain an External module. The chain is DataSourcing + DataSourcing + Transformation + CsvFileWriter. |
| AP4 | V2 `holdings` DataSourcing sources only `security_id`, `current_value` (not `holding_id`, `investment_id`, `customer_id`, `quantity`). V2 `securities` DataSourcing sources only `security_id`, `security_type` (not `ticker`, `security_name`, `sector`). |
| AP6 | No row-by-row iteration exists. All aggregation is performed by a single SQL GROUP BY query. |

### TC-6: Edge Cases
1. **Empty input behavior**: If `holdings` is empty, Transformation module's `RegisterTable` skips registration, SQL fails. CsvFileWriter would write header+trailer only. V1 returns early and writes no file. **Potential divergence** — monitor during Proofmark. In practice, datalake has data for all dates in range, so unlikely to surface.
2. **Holdings with unknown security_id**: Holdings whose `security_id` has no match in `securities` must be grouped under `"Unknown"` security_type (LEFT JOIN + COALESCE behavior).
3. **NULL security_type in securities**: Must be coalesced to `"Unknown"` (BR-12).
4. **Cross-date aggregation**: For multi-day effective date ranges, JOIN must align on both `security_id` AND `as_of` to prevent cross-date Cartesian products.
5. **Division guard**: Groups with zero holdings (impossible due to GROUP BY, but guard is present) should produce `avg_value = 0`.
6. **RFC 4180 quoting**: If any `security_type` value contains commas or quotes, CsvFileWriter may apply RFC 4180 quoting that V1's bare StreamWriter did not. Low risk for standard security types (Stock, Bond, ETF, etc.).
7. **NULL current_value in holdings**: V1 would throw `Convert.ToDecimal(null)` exception. V2 SQL `SUM(NULL)` returns NULL. Behavioral parity depends on whether such data exists.

### TC-7: Proofmark Configuration
- **Expected proofmark settings** (from FSD Section 8):
  ```yaml
  comparison_target: "fund_allocation_breakdown"
  reader: csv
  threshold: 100.0
  csv:
    header_rows: 1
    trailer_rows: 1
  ```
- **Threshold**: 100.0 (all values must be a perfect match)
- **Excluded columns**: None (all columns are deterministic)
- **Fuzzy columns**: None by default. If SQLite ROUND vs C# Math.Round divergence is detected, `total_value` and `avg_value` are candidates for fuzzy matching with absolute tolerance 0.01.

## W-Code Test Cases

### TC-W1: W8 — Trailer Stale Date
- **What the wrinkle is**: V1 hardcodes the trailer date to `2024-10-01` instead of using the current effective date. The date variable is computed but never used in the trailer format string.
- **How V2 handles it**: The CsvFileWriter `trailerFormat` is set to `"TRAILER|{row_count}|2024-10-01"` with the date hardcoded directly in the format string, deliberately avoiding the `{date}` token.
- **What to verify**:
  - For every effective date in the range (2024-10-01 through 2024-12-31), the trailer always reads `TRAILER|{n}|2024-10-01`
  - The trailer date does NOT change when the effective date changes
  - The `{row_count}` token IS substituted correctly (only the date is stale)

### TC-W2: W9 — Overwrite Write Mode
- **What the wrinkle is**: V1 opens the output file with `append: false`, so each run replaces the file entirely. In multi-day auto-advance, only the final day's output persists.
- **How V2 handles it**: CsvFileWriter configured with `writeMode: "Overwrite"`.
- **What to verify**:
  - After a multi-day auto-advance run, only the last effective date's data is present in the output file
  - The file does not contain data from prior effective dates
  - Header and trailer appear exactly once

## Notes
- **Rounding risk is the primary concern** for this job. The FSD Risk Register rates it LOW likelihood, but if Proofmark detects a mismatch on `total_value` or `avg_value`, the resolution path is: (a) add fuzzy tolerance 0.01 to Proofmark config, or (b) escalate to Tier 2 with an External module that uses `Math.Round(..., MidpointRounding.ToEven)`.
- **Empty input divergence**: V1 writes no file on empty input; V2 may write a header+trailer-only file. This only matters if the datalake has dates with zero holdings/securities rows. The FSD notes this risk and suggests monitoring during Proofmark validation.
- **Output path difference**: The only intentional V1-vs-V2 difference is the directory (`curated` vs `double_secret_curated`). Proofmark handles this via `comparison_target` mapping.
- **BRD Open Question**: Why does V1 bypass CsvFileWriter entirely? The FSD concludes it's a historical artifact (AP3). V2 proves the framework's CsvFileWriter can produce identical output.
