# CustomerFullProfile — V2 Test Plan

## Job Info
- **V2 Config**: `customer_full_profile_v2.json`
- **Tier**: Tier 1 — Framework Only (DataSourcing -> Transformation -> ParquetFileWriter)
- **External Module**: None (V1 used `FullProfileAssembler.cs`; eliminated as AP3)

## Pre-Conditions
- Data sources needed:
  - `datalake.customers` — columns: `id`, `first_name`, `last_name`, `birthdate`, `as_of`
  - `datalake.phone_numbers` — columns: `phone_id`, `customer_id`, `phone_number`, `as_of`
  - `datalake.email_addresses` — columns: `email_id`, `customer_id`, `email_address`, `as_of`
  - `datalake.customers_segments` — columns: `customer_id`, `segment_id`, `as_of`
  - `datalake.segments` — columns: `segment_id`, `segment_name`, `as_of`
- Effective date range: starts at `2024-10-01`, framework injects `__minEffectiveDate` / `__maxEffectiveDate`
- V1 sources additional columns (`phone_type`, `email_type`, `segment_code`) that V2 drops (AP4)
- Customers table is weekday-only; phone_numbers, email_addresses, and customers_segments have data on all days including weekends

## Test Cases

### TC-1: Output Schema Validation
- **Expected columns (exact order per FSD Section 4):**
  1. `customer_id` — INTEGER
  2. `first_name` — TEXT
  3. `last_name` — TEXT
  4. `age` — INTEGER
  5. `age_bracket` — TEXT
  6. `primary_phone` — TEXT
  7. `primary_email` — TEXT
  8. `segments` — TEXT
  9. `as_of` — TEXT/DATE
- **Verification:** Read V2 Parquet output and confirm column names, order, and types match exactly
- **Pass criteria:** All 9 columns present in exact order; no extra columns; types match

### TC-2: Row Count Equivalence
- Run V1 (`CustomerFullProfile`) and V2 (`CustomerFullProfileV2`) for the same effective date range
- Compare row counts from V1 output (`Output/curated/customer_full_profile/`) vs V2 output (`Output/double_secret_curated/customer_full_profile/`)
- **Pass criteria:** Identical row counts for every effective date in the range
- **Note:** On weekend dates, V1 produces empty Parquet output (External module returns empty DataFrame). V2 may produce a framework error because the SQL references a `customers` table that was never registered (empty DataFrames are skipped by `RegisterTable`). Compare only weekday outputs for row count equivalence. See FSD Section 5, SQL Design Notes point 3 for full analysis.

### TC-3: Data Content Equivalence
- All values must be byte-identical to V1 output for deterministic columns: `customer_id`, `first_name`, `last_name`, `age`, `age_bracket`, `as_of`
- **Potentially non-deterministic columns** (per BRD Non-Deterministic Fields):
  - `primary_phone` — "first encountered" depends on DataFrame iteration order vs V2's `ORDER BY phone_id`
  - `primary_email` — same concern as primary_phone, V2 uses `ORDER BY email_id`
  - `segments` — comma-separated segment names; V1 dictionary iteration order vs V2 `GROUP_CONCAT` without explicit ORDER BY
- **Pass criteria:** Proofmark comparison at 100% threshold. If non-deterministic columns cause failures, apply EXCLUDED treatment per FSD Section 8 and re-run.
- **No W-codes affect comparison** (FSD Section 3 confirms no output-affecting wrinkles)

### TC-4: Writer Configuration
- **Writer type:** ParquetFileWriter
- **Expected settings:**
  - `source`: `output`
  - `outputDirectory`: `Output/double_secret_curated/customer_full_profile/` (V2 path; V1 was `Output/curated/customer_full_profile/`)
  - `numParts`: 2
  - `writeMode`: Overwrite
- **Verification:** Confirm V2 config JSON matches these settings exactly
- **Pass criteria:** All writer parameters match; output directory contains exactly 2 Parquet part files after a successful run

### TC-5: Anti-Pattern Elimination Verification

| AP-Code | What to Verify | Pass Criteria |
|---------|---------------|---------------|
| AP3 | V2 config contains NO External module entry | No `"type": "External"` in `customer_full_profile_v2.json`; all logic expressed in SQL Transformation |
| AP4 | V2 DataSourcing configs exclude `phone_type` (phone_numbers), `email_type` (email_addresses), `segment_code` (segments) | Column lists in V2 config do not include these three columns |
| AP6 | No row-by-row iteration; logic is set-based SQL | V2 uses a single Transformation module with SQL containing CTEs, window functions, JOINs, and GROUP_CONCAT instead of C# foreach loops |

### TC-6: Edge Cases

| Edge Case | Description | Expected Behavior | Source |
|-----------|-------------|-------------------|--------|
| Empty customers (weekend dates) | `customers` table has no rows for weekend dates | V1: empty output DataFrame. V2: SQL may fail on missing `customers` table (Transformation skips empty DataFrame registration). Overwrite mode means this does not corrupt weekday output. | BRD Edge Cases; FSD Section 5 Note 3 |
| Customer with no phone | Customer exists in `customers` but has no entry in `phone_numbers` | `primary_phone` = empty string (LEFT JOIN + COALESCE) | BR-6 |
| Customer with no email | Customer exists in `customers` but has no entry in `email_addresses` | `primary_email` = empty string (LEFT JOIN + COALESCE) | BR-6 |
| Customer with no segments | Customer exists but has no entries in `customers_segments` | `segments` = empty string (LEFT JOIN + COALESCE) | BR-6 |
| Multiple phones per customer | Customer has >1 phone number | Only the first phone (by `phone_id` order) is kept; others discarded | BR-1 |
| Multiple emails per customer | Customer has >1 email address | Only the first email (by `email_id` order) is kept; others discarded | BR-2 |
| Unknown segment_id | `customers_segments` references a `segment_id` not in `segments` table | Segment is filtered out (INNER JOIN in customer_segs CTE excludes unmatched segment_ids) | BRD Edge Cases |
| Birthday on as_of date | Customer's birthdate falls exactly on the effective date | Age calculated correctly (birthday has occurred; no decrement) | BRD Edge Cases |
| Birthday day after as_of | Customer's birthdate is the day after the effective date | Age decremented by 1 (birthday has not occurred yet) | BRD Edge Cases |
| Age bracket boundaries | Customer age is exactly 25, 26, 35, 36, 45, 46, 55, 56, 65, 66 | Brackets: <26 -> "18-25", <=35 -> "26-35", <=45 -> "36-45", <=55 -> "46-55", <=65 -> "56-65", >65 -> "65+" | BR-4 |
| NULL first_name or last_name | Customer row has NULL name fields | Empty string in output (COALESCE) | BR-6 |

### TC-7: Proofmark Configuration
- **Expected Proofmark settings (FSD Section 8):**
  ```yaml
  comparison_target: "customer_full_profile"
  reader: parquet
  threshold: 100.0
  ```
- **Initial approach:** Strict comparison on all columns
- **Potential overrides (apply only if strict comparison fails):**
  - `segments` column -> EXCLUDED (non-deterministic comma-separated ordering; V1 dictionary iteration vs V2 GROUP_CONCAT)
  - `primary_phone` column -> EXCLUDED (non-deterministic "first encountered" selection; V1 DataFrame order vs V2 ORDER BY phone_id)
  - `primary_email` column -> EXCLUDED (same as primary_phone; V2 ORDER BY email_id)
- **Threshold:** 100.0 (full strict match required)
- **Excluded columns:** None initially; add with documented reason only upon Proofmark failure
- **Fuzzy columns:** None identified

## W-Code Test Cases

No W-codes apply to this job per FSD Section 3. The FSD explicitly evaluated and dismissed:

| W-Code | Why Not Applicable |
|--------|-------------------|
| W1/W2 | No Sunday skip or weekend fallback logic; empty output on weekends is natural DataSourcing behavior |
| W4 | No percentage or division calculations |
| W5 | No rounding operations |
| W6 | No monetary accumulation |
| W7/W8 | Parquet output; no trailers |
| W9 | Overwrite mode is appropriate for complete snapshot output |
| W10 | `numParts: 2` is reasonable |

No TC-W test cases required.

## Notes

1. **Weekend behavior divergence:** V1 gracefully produces empty output on weekends via the External module's null guard (`FullProfileAssembler.cs:18-22`). V2 may produce a Transformation error because SQLite cannot query a table that was never registered (empty DataFrames are skipped). The FSD resolves this by noting that Overwrite mode means only the last run's output persists, and weekend errors do not affect final weekday output. However, if the test harness runs comparisons on weekend dates, expect V1=empty output vs V2=error. **Test weekday dates only for equivalence.**

2. **Non-deterministic column strategy:** Start with strict Proofmark comparison. If `segments`, `primary_phone`, or `primary_email` fail, apply EXCLUDED treatment with the documented reason from FSD Section 8. Do not preemptively exclude — evidence first.

3. **Row ordering:** V2 adds `ORDER BY c.id` for deterministic output. V1 iterates in DataFrame order (natural DB order). These should align since DataSourcing returns rows ordered by `as_of` and natural insertion order correlates with ascending `id`. If Proofmark detects row ordering differences, investigate whether the Proofmark reader handles Parquet row ordering or whether the comparison is order-insensitive.

4. **Open questions from BRD:**
   - BRD Question 1: "Primary" phone/email selection is by iteration order, not by `phone_type`/`email_type`. V2 uses `ORDER BY phone_id`/`email_id` which should match natural order. No business-level "primary" definition was found.
   - BRD Question 2: `segment_code` is sourced but unused. V2 eliminates it (AP4). No impact on output.
