# RepeatOverdraftCustomers -- Test Plan

## References
- BRD: `POC3/brd/repeat_overdraft_customers_brd.md`
- FSD: `POC3/fsd/repeat_overdraft_customers_fsd.md`

---

## Happy Path Tests

### TC-01: Repeat overdraft customers appear in output
- **Description:** Customers with 2+ overdraft events across the effective date range are present in the Parquet output with correct `customer_id`, `overdraft_count`, and `total_overdraft_amount`.
- **Expected:** Proofmark comparison PASS at 100% threshold. Every output row has `overdraft_count >= 2`. Row count matches V1.
- **Traces to:** BR-2, BR-3

### TC-02: Customer name lookup uses last-loaded values
- **Description:** When a customer has name records across multiple `as_of` dates, the output contains the name from the latest `as_of` date (dictionary overwrite / last-loaded-wins).
- **Expected:** `first_name` and `last_name` in V2 output match V1 for all customers. Proofmark strict comparison covers this.
- **Traces to:** BR-1, FSD Section 4 (SQL Design Notes -- BR-1)

### TC-03: Total overdraft amount is correct decimal sum
- **Description:** `total_overdraft_amount` per customer equals the SUM of `overdraft_amount` from all that customer's events across the date range.
- **Expected:** V2 sums match V1 sums exactly. Proofmark strict comparison at 100% threshold.
- **Traces to:** BR-2, BR-6, FSD Section 4 (SQL Design Notes -- BR-2)

### TC-04: as_of value is MIN(as_of) from overdraft_events
- **Description:** The `as_of` column in every output row contains the minimum `as_of` date from the overdraft_events source (i.e., the first row's `as_of` after DataSourcing's ORDER BY).
- **Expected:** All output rows share the same `as_of` value, matching V1. Proofmark strict comparison covers this.
- **Traces to:** BR-4, EC-3, FSD Section 4 (SQL Design Notes -- BR-4)

### TC-05: Output schema matches V1 exactly
- **Description:** Output Parquet contains exactly 6 columns: `customer_id`, `first_name`, `last_name`, `overdraft_count`, `total_overdraft_amount`, `as_of`, in that order.
- **Expected:** Proofmark Parquet reader validates schema alignment. Column count and names match.
- **Traces to:** BRD Output Schema, FSD Section 4 (Column mapping implied by SELECT order)

### TC-06: Writer config matches V1
- **Description:** V2 uses ParquetFileWriter with `numParts=1`, `writeMode=Overwrite`, outputting to `Output/double_secret_curated/repeat_overdraft_customers/`.
- **Expected:** Output directory contains a single Parquet part file. Proofmark reads it successfully as Parquet.
- **Traces to:** BRD Writer Configuration, FSD Section 5

---

## Edge Case Tests

### TC-07: Single-overdraft customers excluded
- **Description:** Customers with exactly 1 overdraft event do not appear in the output.
- **Expected:** No row with `overdraft_count < 2` exists in output. V2 matches V1 row count.
- **Traces to:** BR-3, EC-5

### TC-08: Missing customer fallback to empty strings
- **Description:** If a `customer_id` in overdraft_events has no matching record in the customers table (for the date range), `first_name` and `last_name` default to empty strings.
- **Expected:** V2 COALESCE produces empty strings matching V1's `("", "")` fallback. Proofmark strict match.
- **Traces to:** BR-5, FSD Section 4 (SQL Design Notes -- BR-5)

### TC-09: Cross-date counting behavior
- **Description:** A customer with 1 overdraft on day X and 1 overdraft on day Y (different dates) qualifies as a repeat (count=2) because counting spans the entire effective date range, not per-date.
- **Expected:** V2 aggregates across all dates in range, matching V1. Proofmark comparison PASS.
- **Traces to:** EC-2, BR-2

### TC-10: Overwrite mode -- only final run output survives
- **Description:** On multi-day auto-advance, each run overwrites the Parquet directory. The final output reflects the full cumulative date range (because DataSourcing uses min-to-max effective dates).
- **Expected:** After a full run (2024-10-01 through 2024-12-31), a single set of Parquet files exists. Proofmark compares this final output.
- **Traces to:** EC-6, BRD Write Mode Implications, FSD Section 5

### TC-11: Empty overdraft_events produces no output rows
- **Description:** If overdraft_events has zero rows in the effective date range, V1 returns an empty DataFrame. V2's SQL should also produce zero rows (GROUP BY on empty set = empty result).
- **Expected:** Both V1 and V2 produce a Parquet file with zero data rows. Proofmark comparison PASS (both empty).
- **Traces to:** EC-4, FSD Open Question OQ-2

---

## Anti-Pattern Elimination Verification

### TC-12: AP3 eliminated -- no External module in V2
- **Description:** V2 job config uses Tier 1 (DataSourcing -> Transformation -> ParquetFileWriter) with no External module entry.
- **Expected:** V2 JSON config has no `External` module. The job runs successfully using only framework modules.
- **Traces to:** FSD Section 2 (Tier 1), FSD Section 7 (AP3)

### TC-13: AP4 eliminated -- unused columns removed
- **Description:** V2 DataSourcing for overdraft_events sources only `customer_id` and `overdraft_amount` (2 columns), not the 7 columns V1 sourced.
- **Expected:** V2 JSON config's overdraft_events columns list contains exactly `["customer_id", "overdraft_amount"]`. Output is still byte-identical to V1.
- **Traces to:** EC-7, FSD Section 3 (Source 1), FSD Section 7 (AP4)

### TC-14: AP6 eliminated -- no row-by-row iteration
- **Description:** V2 uses SQL GROUP BY, JOIN, and HAVING instead of C# foreach loops.
- **Expected:** No External module code exists for this job. All logic is in the Transformation SQL.
- **Traces to:** FSD Section 7 (AP6)

### TC-15: AP7 eliminated -- magic threshold documented
- **Description:** The repeat threshold of 2 is documented with a SQL comment in the Transformation SQL, unlike V1's undocumented `< 2` literal.
- **Expected:** V2 SQL contains an inline comment identifying the `HAVING COUNT(*) >= 2` as the repeat-overdraft business threshold.
- **Traces to:** FSD Section 4, FSD Section 7 (AP7)

---

## Writer Config Verification

### TC-16: Parquet output -- single part file
- **Description:** Output directory contains exactly 1 Parquet part file (numParts=1).
- **Expected:** `Output/double_secret_curated/repeat_overdraft_customers/` contains one `.parquet` file.
- **Traces to:** BRD Writer Configuration (numParts: 1), FSD Section 5

### TC-17: Write mode is Overwrite
- **Description:** Each run replaces the output directory contents entirely.
- **Expected:** After a multi-day run, only one set of output files exists (no accumulation across runs).
- **Traces to:** BRD Writer Configuration (writeMode: Overwrite), FSD Section 5

---

## Proofmark Comparison Expectations

### TC-18: Proofmark config -- strict Parquet comparison
- **Description:** The Proofmark config uses `reader: parquet`, `threshold: 100.0`, with zero excluded columns and zero fuzzy columns.
- **Expected:** Config file at `POC3/proofmark_configs/repeat_overdraft_customers.yaml` matches FSD Section 8.
- **Traces to:** FSD Section 8, BRD Non-Deterministic Fields (none)

### TC-19: Proofmark full comparison PASS
- **Description:** Running Proofmark with `--left Output/curated/repeat_overdraft_customers/` and `--right Output/double_secret_curated/repeat_overdraft_customers/` produces exit code 0 (PASS).
- **Expected:** 100% row match. Zero mismatches. If row ordering causes a mismatch, FSD OQ-1 prescribes adding `ORDER BY customer_id` to the SQL.
- **Traces to:** FSD Section 8, FSD Open Question OQ-1

---

## Risk Items (from FSD Open Questions)

### TC-20: Row ordering (OQ-1)
- **Description:** V1 output order depends on Dictionary insertion order; V2 depends on SQL implicit ordering. Parquet has no inherent row order, so Proofmark should sort before comparing.
- **Expected:** Proofmark handles row ordering internally. If it does not and comparison fails on ordering alone, the mitigation is to add `ORDER BY customer_id` to the V2 SQL.
- **Traces to:** FSD OQ-1, EC-8

### TC-21: Decimal precision through SQLite (OQ-4)
- **Description:** V1 uses C# `decimal` for SUM; V2 uses SQLite's SUM (IEEE 754 double internally). For typical overdraft amounts, precision should not differ.
- **Expected:** Proofmark strict comparison PASS. If precision mismatches occur on `total_overdraft_amount`, the mitigation is to add it as a fuzzy column with tolerance 0.01 (absolute).
- **Traces to:** FSD OQ-4, BR-6
