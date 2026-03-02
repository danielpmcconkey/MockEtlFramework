# CustomerSegmentMap — V2 Test Plan

## Job Info
- **V2 Config**: `customer_segment_map_v2.json`
- **Tier**: 1 (Framework Only)
- **External Module**: None

## Pre-Conditions
- Data sources required:
  - `datalake.customers_segments` — columns: `customer_id`, `segment_id`, `as_of` (auto-appended by DataSourcing)
  - `datalake.segments` — columns: `segment_id`, `segment_name`, `segment_code`, `as_of` (auto-appended by DataSourcing)
- Effective date range starts at `2024-10-01` (firstEffectiveDate), auto-advancing one day at a time
- Both tables must have data for the effective date being processed; INNER JOIN means missing data on either side produces no output for that association
- V1 also sources `datalake.branches` (branch_id, branch_name, city, state_province) but never uses it — V2 eliminates this (AP1)

## Test Cases

### TC-1: Output Schema Validation
- **Expected columns (exact order from FSD Section 4):**
  1. `customer_id` — int
  2. `segment_id` — int
  3. `segment_name` — string
  4. `segment_code` — string
  5. `as_of` — date
- Verify the header row in the CSV matches this exact column order
- Verify column types are consistent with V1 output (integers render without decimal points, dates render in the same format as V1)

### TC-2: Row Count Equivalence
- V1 vs V2 must produce identical row counts for each effective date
- Over a full auto-advance run (2024-10-01 through end of range), the accumulated CSV must have the same total number of data rows
- A customer with multiple segments produces one row per segment per effective date
- Append mode accumulates rows across dates — verify total line count (minus 1 header) matches V1

### TC-3: Data Content Equivalence
- All values must be byte-identical to V1 output
- No W-codes affect this job — all output is deterministic integers, strings, and dates
- Verify customer_id and segment_id are exact integer matches
- Verify segment_name and segment_code are exact string matches (case-sensitive)
- Verify as_of dates are formatted identically to V1
- Row ordering must match: `ORDER BY customer_id, segment_id` (per BR-3)

### TC-4: Writer Configuration
- **includeHeader**: `true` — header written on first file creation only; subsequent Append runs do not re-add header
- **writeMode**: `Append` — each effective date's results are appended to the existing file
- **lineEnding**: `LF` — Unix-style line endings throughout (no CRLF)
- **trailerFormat**: not configured — no trailer rows in output
- **source**: `seg_map` — writer reads from the Transformation result named "seg_map"
- **outputFile**: `Output/double_secret_curated/customer_segment_map.csv` (V2 path)

### TC-5: Anti-Pattern Elimination Verification
- **AP1 (Dead-end sourcing):** Verify V2 config does NOT contain a DataSourcing entry for the `branches` table. V1 sourced `branches` with columns `branch_id, branch_name, city, state_province` but never referenced it in the Transformation SQL. V2 must eliminate this entirely.
  - Verify V2 config has exactly 2 DataSourcing entries (customers_segments, segments), 1 Transformation, and 1 CsvFileWriter
  - Verify output is unchanged despite removing the branches DataSourcing — confirms it was truly dead-end

### TC-6: Edge Cases
- **Empty input behavior:** If either `customers_segments` or `segments` has no data for a given effective date, the INNER JOIN produces zero rows. Verify the CsvFileWriter handles this gracefully (appends nothing or appends zero data rows).
- **Customer with no segments:** Customers not present in `customers_segments` do not appear in output (by design — data is sourced from `customers_segments`, not `customers`).
- **Segment not in segments table for that as_of:** If a `customers_segments` row references a `segment_id` that has no matching `segments` row for the same `as_of`, that association is excluded (INNER JOIN behavior per BR-2). Verify this matches V1.
- **Customer with multiple segments:** Each customer-segment pair produces a separate row. A customer with 3 segments on a given date produces 3 rows. Verify all are present and ordered correctly.
- **Append mode with header:** On first write, header is included. On subsequent appends (second effective date onward), header must NOT be re-added. Verify no duplicate header rows appear mid-file.
- **Duplicate segment assignments across dates:** A customer assigned to the same segment on multiple dates produces multiple rows with different `as_of` values. Verify all are present.

### TC-7: Proofmark Configuration
- **Expected proofmark config file:** `POC3/proofmark_configs/customer_segment_map.yaml`
- **Settings from FSD Section 8:**
  - `comparison_target`: `customer_segment_map`
  - `reader`: `csv`
  - `threshold`: `100.0` (byte-identical match expected)
  - `header_rows`: `1`
  - `trailer_rows`: `0`
  - `excluded columns`: none (no non-deterministic fields)
  - `fuzzy columns`: none (all values are integers or strings — no floating-point concerns)
- Verify proofmark comparison passes at 100% threshold

## W-Code Test Cases

No W-codes apply to this job. The FSD explicitly confirms W1 through W12 are all non-applicable:
- No Sunday logic (W1)
- No weekend fallback (W2)
- No boundary rows (W3a/W3b/W3c)
- No integer division (W4)
- No rounding (W5)
- No double epsilon accumulation (W6)
- No trailer (W7, W8)
- No wrong write mode (W9)
- No Parquet output (W10)
- No header-every-append issue (W12) — CsvFileWriter handles Append mode header suppression correctly

## Notes
- This is one of the cleanest jobs in the portfolio. The only V1 issue was dead-end sourcing of the `branches` table (AP1), which has zero impact on output.
- The SQL is identical between V1 and V2 — the only config change is removing the `branches` DataSourcing entry and updating the output path.
- OQ-1 from BRD (branches table sourced but unused) is resolved by AP1 elimination.
- OQ-2 from BRD (JOIN requires matching as_of dates; gaps could drop rows) is by-design INNER JOIN behavior replicated exactly in V2. No action needed for output equivalence.
- No reviewer concerns or open questions remain for this job.
