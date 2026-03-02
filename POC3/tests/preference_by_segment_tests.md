# PreferenceBySegment -- V2 Test Plan

## Job Info
- **V2 Config**: `preference_by_segment_v2.json`
- **Tier**: 2 (Framework + External -- DataSourcing handles data access, External handles business logic and file I/O)
- **External Module**: `ExternalModules.PreferenceBySegmentV2Processor`
- **Writer**: None (External module writes CSV directly via StreamWriter)

## Pre-Conditions
- Source tables available in `datalake` schema:
  - `customer_preferences` with columns: `customer_id`, `preference_type`, `opted_in`, `as_of` (auto-appended)
  - `customers_segments` with columns: `customer_id`, `segment_id`, `as_of` (auto-appended)
  - `segments` with columns: `segment_id`, `segment_name`, `as_of` (auto-appended)
- Effective date range injected by executor (`__minEffectiveDate`, `__maxEffectiveDate`)
- V1 baseline output available at `Output/curated/preference_by_segment.csv`

## Test Cases

### TC-1: Output Schema Validation
- **Requirement**: BR-1, BR-4, BR-5, FSD Section 5
- **Expected columns (exact order):** `segment_name`, `preference_type`, `opt_in_rate`, `as_of`
- **Expected types:**
  - `segment_name`: string
  - `preference_type`: string
  - `opt_in_rate`: decimal (rounded to 2 dp, Banker's rounding)
  - `as_of`: string (formatted as `yyyy-MM-dd`)
- Verify header row contains exactly: `segment_name,preference_type,opt_in_rate,as_of`
- Verify no extra columns (e.g., no `preference_id`, no `opted_in_count`, no `total_count`)

### TC-2: Row Count Equivalence
- **Requirement**: BR-1, BR-4
- V1 and V2 must produce identical row counts (excluding header and trailer)
- One row per unique (segment_name, preference_type) combination
- Run both V1 and V2 for the full date range and compare CSV data row counts
- Verify that removing `preference_id` from DataSourcing (AP4) does not affect row count

### TC-3: Data Content Equivalence
- **Requirement**: BR-1, BR-2, BR-4, BR-5
- All values must be byte-identical to V1 output
- Compare V2 CSV at `Output/double_secret_curated/preference_by_segment.csv` against V1 at `Output/curated/preference_by_segment.csv`
- Verify `segment_name` values match exactly (string, case-sensitive)
- Verify `preference_type` values match exactly (string, case-sensitive)
- Verify `opt_in_rate` values match exactly (decimal with Banker's rounding)
- Verify `as_of` values match exactly (formatted date string)
- Verify row ordering: segment_name ASC, then preference_type ASC (BR-4)

### TC-4: File Format and Writer Verification
- **Requirement**: BR-7, FSD Section 5
- **Output path**: `Output/double_secret_curated/preference_by_segment.csv`
- **Header**: Present -- first line is `segment_name,preference_type,opt_in_rate,as_of`
- **Line ending**: LF (`\n`) -- not CRLF
- **Write mode**: Overwrite (`append: false`) -- file is replaced on each run
- **Encoding**: UTF-8
- **No RFC 4180 quoting**: Values written raw without quoting (V1 behavior)
- **No framework CsvFileWriter**: External module writes directly via StreamWriter
- Verify file structure: header line, N data lines, trailer line

### TC-5: Trailer Verification
- **Requirement**: BR-3, FSD Section 6 W7
- **Trailer format**: `TRAILER|{inputCount}|{dateStr}`
- **inputCount**: Number of rows in `customer_preferences` DataFrame BEFORE any grouping/filtering (W7 inflated count)
- **dateStr**: `__maxEffectiveDate` formatted as `yyyy-MM-dd` (BR-5)
- Verify trailer is the last line of the file
- Verify the count is the INPUT preference row count (e.g., ~11150 per date range), NOT the output row count (~40 aggregated rows)
- Verify the date matches `__maxEffectiveDate`, not some hardcoded value
- Evidence: [PreferenceBySegmentWriter.cs:28-29, 92-93]

### TC-6: Anti-Pattern Elimination Verification

#### AP3 (Unnecessary External) -- PARTIALLY ELIMINATED
- **Requirement**: FSD Section 7 AP3
- Verify V2 uses DataSourcing for all three tables (not the External module)
- Verify External module handles ONLY: dictionary-based join (BR-9), Banker's rounding (W5), and file I/O with inflated trailer (W7)
- Verify External module does NOT source data from the database directly
- Tier 1 is blocked by: BR-9 (dictionary-overwrite semantics), W5 (Banker's rounding not in SQLite), W7 (inflated trailer count)

#### AP4 (Unused columns -- preference_id) -- ELIMINATED
- **Requirement**: FSD Section 3
- Verify V2 `customer_preferences` DataSourcing columns are `["customer_id", "preference_type", "opted_in"]`
- Verify `preference_id` is NOT in V2's column list
- Verify removal has no effect on output (`preference_id` was never used in V1 processing)
- Evidence: [preference_by_segment.json:10], [PreferenceBySegmentWriter.cs:53-68]

#### AP6 (Row-by-row iteration) -- PARTIALLY ADDRESSED
- **Requirement**: FSD Section 7 AP6
- Verify External module uses LINQ-based dictionary construction where possible (e.g., `ToDictionary` for segment lookup)
- Dictionary-based lookup pattern is retained for BR-9 semantics (last-write-wins)
- The grouping loop is inherently row-by-row but is a natural aggregation pattern

#### AP7 (Magic values) -- ELIMINATED
- **Requirement**: FSD Section 7 AP7
- Verify V2 uses a named constant for the default segment name: `private const string DefaultSegmentName = "Unknown";`
- V1 used inline string `"Unknown"` at [PreferenceBySegmentWriter.cs:48, 58]

### TC-7: Edge Cases

#### TC-7a: Customer Not in customers_segments
- **Requirement**: BR-2, BRD edge case #4
- A customer_id present in `customer_preferences` but not in `customers_segments` gets segment `"Unknown"`
- Verify their preference is counted under the `"Unknown"` segment group
- Evidence: [PreferenceBySegmentWriter.cs:58] -- `custSegLookup.GetValueOrDefault(custId, "Unknown")`

#### TC-7b: Segment ID Not Found in segments Table
- **Requirement**: BR-8
- A `segment_id` in `customers_segments` that doesn't exist in `segments` gets segment name `"Unknown"`
- Verify this defaults correctly
- Evidence: [PreferenceBySegmentWriter.cs:48] -- `segmentLookup.GetValueOrDefault(segId, "Unknown")`

#### TC-7c: Customer With Multiple Segment Assignments (Dictionary Overwrite)
- **Requirement**: BR-9, FSD Section 4 "V1 behavior note on duplicate customer-segment mappings"
- When a customer has multiple entries in `customers_segments`, only the last-encountered row wins (dictionary overwrite)
- V2 must replicate this dictionary-overwrite behavior, NOT SQL JOIN behavior (which would multiply rows)
- This is non-deterministic in theory (depends on database row order), but consistent in practice since both V1 and V2 use the same DataSourcing
- If Proofmark fails, investigate row order differences in `customers_segments` as a likely cause

#### TC-7d: Empty Input (Null/Empty DataFrames)
- **Requirement**: BR-10
- If `customer_preferences`, `customers_segments`, or `segments` is null or empty, output should be empty
- Verify empty CSV with only header and trailer (zero data rows)
- Verify trailer count is 0 when `customer_preferences` is empty
- Evidence: [PreferenceBySegmentWriter.cs:22-26]

#### TC-7e: No Date Filtering Within External Module
- **Requirement**: BRD edge case #5, FSD Section 4
- V1 aggregates ALL preference rows across the entire effective date range without filtering by date
- V2 must replicate this: no WHERE clause on `as_of` within the External module's processing
- The `as_of` in the output is always `__maxEffectiveDate`, not per-row dates
- Evidence: [PreferenceBySegmentWriter.cs:53-69]

#### TC-7f: Overwrite Mode with Auto-Advance
- **Requirement**: FSD Section 6 W9, BRD Write Mode Implications
- In auto-advance mode, each run replaces the entire CSV file
- Only the last effective date's output persists on disk
- Verify behavior matches V1

#### TC-7g: opted_in = NULL
- **Requirement**: BR-1
- NULL values in `opted_in` should be handled by the aggregation logic
- Verify behavior matches V1 (likely treated as non-opted-in since `opted_in = 1` check fails on NULL)

#### TC-7h: Empty SharedState Output Key
- **Requirement**: BR-6, FSD Section 9 open question #3
- After writing the CSV, the External module must set `sharedState["output"]` to an empty DataFrame
- This prevents framework errors if downstream modules expect an `output` key
- Evidence: [PreferenceBySegmentWriter.cs:97]

### TC-8: Proofmark Configuration
- **Requirement**: FSD Section 8
- **comparison_target**: `preference_by_segment`
- **reader**: `csv`
- **threshold**: `100.0` (strict -- all columns deterministic given same row order)
- **csv.header_rows**: `1` (header row present)
- **csv.trailer_rows**: `1` (Overwrite mode -- single trailer at end of file)
- **excluded columns**: None
- **fuzzy columns**: None
- Rationale: All output columns are deterministic given the same input row order. Banker's rounding (W5) is exact in both V1 and V2 (same `Math.Round` call). Trailer content is deterministic given the same input count.
- **Risk**: BR-9 (non-deterministic segment assignment for multi-segment customers) could cause differences if DataSourcing row order changes. Start strict; if Proofmark fails, investigate row order before adding overrides.
- Expected Proofmark result: EXIT CODE 0 (PASS) with 100% match

## W-Code Test Cases

### TC-W1: W5 -- Banker's Rounding
- **What the wrinkle is:** `opt_in_rate` uses `Math.Round((decimal)optedIn / total, 2, MidpointRounding.ToEven)`. Banker's rounding rounds 0.5 to the nearest even number (e.g., 0.045 -> 0.04, 0.055 -> 0.06).
- **How V2 handles it:** External module uses the same explicit `MidpointRounding.ToEven` call. Comment: `// W5: Banker's rounding (MidpointRounding.ToEven) -- matches V1 behavior.`
- **What to verify:**
  1. V2 `opt_in_rate` matches V1 exactly for every row
  2. Verify the code uses `MidpointRounding.ToEven` explicitly, not the C# default (which happens to be ToEven but should be explicit per FSD)
  3. If any midpoint values exist (e.g., ratio = 0.xx5 exactly), both V1 and V2 round identically
  4. Verify the rounding is NOT done in SQLite (which uses round-half-away-from-zero)

### TC-W2: W7 -- Trailer Inflated Count
- **What the wrinkle is:** The trailer row count uses the INPUT preference row count (`customer_preferences.Count` before grouping), not the OUTPUT row count (number of aggregated segment/preference groups). For a typical run, this is ~11150 vs ~40.
- **How V2 handles it:** External module captures `customerPreferences.Count` before any processing. Comment: `// W7: Trailer uses INPUT row count (inflated), not output row count.`
- **What to verify:**
  1. Trailer count matches V1's trailer count exactly
  2. The count is captured from the raw `customer_preferences` DataFrame, not after any filtering or grouping
  3. Verify the count is NOT the output DataFrame's row count
  4. Compare the full trailer line byte-for-byte between V1 and V2

### TC-W3: W9 -- Overwrite Mode
- **What the wrinkle is:** External module uses `append: false`, so each execution replaces the entire CSV. In multi-day runs, only the last day's output persists.
- **How V2 handles it:** External module uses `new StreamWriter(outputPath, append: false)`. Comment: `// W9: Overwrite mode -- prior days' data is lost on each run.`
- **What to verify:**
  1. After a multi-day auto-advance run, only one set of data rows exists in the file (from the last effective date)
  2. Only one header row exists at the top of the file
  3. Only one trailer row exists at the bottom of the file
  4. The trailer date matches `__maxEffectiveDate` from the final run

## Notes
- This is a Tier 2 job with no framework writer. The External module handles both business logic and file I/O because (a) BR-9 requires dictionary-overwrite semantics that SQL JOINs cannot replicate, (b) W5 requires Banker's rounding that SQLite does not provide, and (c) W7 requires an inflated trailer count that CsvFileWriter cannot produce.
- The Transformation SQL module was considered during FSD design but ultimately removed because the dictionary-overwrite semantics for customer-to-segment mapping cannot be expressed in SQL.
- The highest-risk area for Proofmark comparison failure is BR-9 (non-deterministic segment assignment when a customer has multiple segment mappings). Both V1 and V2 use the same DataSourcing, so row order should be consistent, but this should be investigated first if comparison fails.
- AP4 (`preference_id` removal) and AP7 (named constant for `"Unknown"`) are clean eliminations with zero output impact. Verify these in the V2 code during code review.
