# CardTypeDistribution -- Test Plan

## 1. Traceability Matrix

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01 | BR-1 | Cards grouped by card_type |
| TC-02 | BR-2 | card_count is total rows per card_type (across all as_of dates) |
| TC-03 | BR-3 | pct_of_total is a fraction (0 to 1), not a percentage (0 to 100) |
| TC-04 | BR-4 | Double-precision (IEEE 754) used for pct_of_total calculation |
| TC-05 | BR-5 | totalCards is count of ALL card rows, no date deduplication |
| TC-06 | BR-6 | card_transactions sourced in V1 but never used -- V2 eliminates (AP1) |
| TC-07 | BR-7 | card_status sourced but unused -- all cards regardless of status contribute |
| TC-08 | BR-8 | as_of value taken from first row of cards DataFrame |
| TC-09 | BR-9 | Empty input produces empty output DataFrame with correct schema |
| TC-10 | BR-10 | Only two card_type values exist: Credit and Debit |
| TC-11 | Writer Config | CSV with header, trailer, Overwrite mode, LF line endings |
| TC-12 | Edge Case 1 | Multi-date count inflation -- same card counted per snapshot |
| TC-13 | Edge Case 2 | Weekend effective date -- no data triggers empty guard |
| TC-14 | Edge Case 3 | Double precision serialized to CSV -- trailing digit behavior |
| TC-15 | Edge Case 4 | Trailer row_count matches data row count (expected: 2) |
| TC-16 | FSD Risk | Row ordering -- V2 ORDER BY card_type vs V1 Dictionary iteration |
| TC-17 | FSD Anti-Patterns | V2 eliminates AP3, AP4, AP6 -- Tier 1 SQL replaces External module |

---

## 2. Test Cases

### TC-01: Cards grouped by card_type (BR-1)

**Objective:** Verify that the output contains one row per distinct card_type value.

**Preconditions:** Run V2 for a single weekday effective date (e.g., 2024-10-01).

**Steps:**
1. Execute CardTypeDistributionV2 for effective date 2024-10-01.
2. Read the output CSV file at `Output/double_secret_curated/card_type_distribution.csv`.
3. Exclude header row and trailer row.
4. Count the number of data rows.

**Expected Result:** Exactly 2 data rows -- one for `Credit`, one for `Debit`. Each row has a distinct `card_type` value. No duplicate card_type rows.

**Verification:** Compare against V1 output at `Output/curated/card_type_distribution.csv`.

---

### TC-02: card_count is total rows per card_type (BR-2)

**Objective:** Verify that `card_count` reflects the total number of card rows for each card_type within the effective date range.

**Preconditions:** Run V2 for effective date 2024-10-01.

**Steps:**
1. Query the datalake to get expected counts:
   ```sql
   SELECT card_type, COUNT(*) AS expected_count
   FROM datalake.cards
   WHERE as_of = '2024-10-01'
   GROUP BY card_type;
   ```
2. Execute CardTypeDistributionV2 for 2024-10-01.
3. Parse the output CSV and extract card_count per card_type.

**Expected Result:** The `card_count` column in each output row matches the database query count for that card_type on the given effective date.

---

### TC-03: pct_of_total is a fraction, not a percentage (BR-3)

**Objective:** Verify that `pct_of_total` is between 0 and 1 (e.g., 0.5), NOT between 0 and 100 (e.g., 50.0).

**Preconditions:** Run V2 for effective date 2024-10-01.

**Steps:**
1. Execute CardTypeDistributionV2 for 2024-10-01.
2. Parse the output CSV.
3. For each data row, read the `pct_of_total` value.

**Expected Result:** All `pct_of_total` values are >= 0.0 and <= 1.0. The sum of all `pct_of_total` values equals 1.0 (within IEEE 754 double tolerance).

---

### TC-04: Double-precision arithmetic for pct_of_total (BR-4, W6)

**Objective:** Verify that pct_of_total is computed using IEEE 754 double-precision, matching V1's `double` arithmetic.

**Preconditions:** Run both V1 and V2 for the same effective date.

**Steps:**
1. Execute both V1 and V2 for effective date 2024-10-01.
2. Compare the raw `pct_of_total` string representations in the CSV output byte-for-byte.

**Expected Result:** The serialized string representation of `pct_of_total` is identical between V1 and V2. SQLite REAL and C# double are both IEEE 754 double-precision; for the same integer inputs, the division result should be bit-identical.

**Notes:** If values like 0.5 (exactly representable in IEEE 754) are expected, no epsilon issue arises. If edge cases produce values like 0.333333..., verify trailing digits match.

---

### TC-05: totalCards counts ALL rows without deduplication (BR-5)

**Objective:** Verify that the denominator in pct_of_total is the total count of all card rows across all as_of dates, with no deduplication.

**Preconditions:** Run V2 for a multi-day effective date range (e.g., 2024-10-01 to 2024-10-03).

**Steps:**
1. Query the datalake for total row count across the date range:
   ```sql
   SELECT COUNT(*) FROM datalake.cards
   WHERE as_of BETWEEN '2024-10-01' AND '2024-10-03';
   ```
2. Execute CardTypeDistributionV2 for the date range.
3. Sum the `card_count` values from the output.

**Expected Result:** The sum of all `card_count` values equals the total row count from the database query (not the count of distinct card_id values).

---

### TC-06: Dead card_transactions sourcing eliminated (BR-6, AP1)

**Objective:** Verify that V2 does NOT source the card_transactions table.

**Preconditions:** Read the V2 job config.

**Steps:**
1. Read `JobExecutor/Jobs/card_type_distribution_v2.json`.
2. Inspect the modules array for any DataSourcing entry referencing the `card_transactions` table.

**Expected Result:** No DataSourcing module references `card_transactions`. The V2 config sources only the `cards` table.

---

### TC-07: Unused card_status column eliminated (BR-7, AP4)

**Objective:** Verify that V2 does not source the `card_status` column from the cards table.

**Preconditions:** Read the V2 job config.

**Steps:**
1. Read `JobExecutor/Jobs/card_type_distribution_v2.json`.
2. Inspect the columns list in the DataSourcing module for the `cards` table.

**Expected Result:** The columns list contains only `["card_type"]`. The columns `card_id`, `customer_id`, and `card_status` are NOT present.

---

### TC-08: as_of from first row of cards (BR-8)

**Objective:** Verify that the `as_of` column in the output matches the as_of value from the first row in the cards DataFrame.

**Preconditions:** Run V2 for a single effective date.

**Steps:**
1. Execute CardTypeDistributionV2 for effective date 2024-10-01.
2. Parse the output CSV.
3. Check the `as_of` value in each data row.

**Expected Result:** All rows have the same `as_of` value, matching the effective date (2024-10-01). For a single-day run, this is the only as_of value in the DataFrame. This matches V1's behavior of taking `cards.Rows[0]["as_of"]`.

---

### TC-09: Empty input produces empty output (BR-9)

**Objective:** Verify that when the cards DataFrame is empty, the output is an empty CSV (header + trailer only, no data rows).

**Preconditions:** Identify a date with no card data (e.g., a weekend date like 2024-10-05 if Saturday, or 2024-10-06 if Sunday).

**Steps:**
1. Confirm no data exists for the target date:
   ```sql
   SELECT COUNT(*) FROM datalake.cards WHERE as_of = '2024-10-05';
   ```
2. Execute CardTypeDistributionV2 for that date.
3. Read the output CSV.

**Expected Result:**
- If V2 handles empty input gracefully: output contains only the header row and trailer row (if applicable), with zero data rows.
- If V2 throws a SQLite error (table not registered for zero-row DataFrame): the error is logged but does not affect the final output under Overwrite mode, since the last weekday's output overwrites any error state.

**Risk Note (from FSD):** V2 Tier 1 design may throw a SQL error on empty input because Transformation.RegisterTable skips zero-row DataFrames. This is a known risk documented in the FSD's Risk Register.

---

### TC-10: Only Credit and Debit card types (BR-10)

**Objective:** Verify that the output contains exactly the card types present in the data.

**Preconditions:** Run V2 for effective date 2024-10-01.

**Steps:**
1. Query the datalake:
   ```sql
   SELECT DISTINCT card_type FROM datalake.cards;
   ```
2. Execute CardTypeDistributionV2 for 2024-10-01.
3. Extract the `card_type` values from the output.

**Expected Result:** The output contains exactly the card types returned by the database query. Per BRD BR-10, this should be `Credit` and `Debit`.

---

### TC-11: Writer configuration -- CSV format verification (Writer Config)

**Objective:** Verify the output file matches all writer configuration parameters.

**Preconditions:** Run V2 for effective date 2024-12-31 (last date in range).

**Steps:**
1. Execute CardTypeDistributionV2 for 2024-12-31.
2. Read the raw output file bytes.
3. Check:
   - File exists at `Output/double_secret_curated/card_type_distribution.csv`.
   - First line is a header row with column names: `card_type,card_count,pct_of_total,as_of`.
   - Last line is a trailer matching format: `TRAILER|{row_count}|{date}`.
   - Line endings are LF (`\n`), not CRLF (`\r\n`).
   - Encoding is UTF-8 (no BOM).

**Expected Result:**
- Header row present with correct column names.
- Trailer row present in format `TRAILER|2|2024-12-31` (2 data rows, date = max effective date).
- All line endings are LF.
- File is valid UTF-8 without BOM.

---

### TC-12: Multi-date count inflation (Edge Case 1)

**Objective:** Verify that when run over multiple effective dates, card_count reflects total rows across all dates (not deduplicated).

**Preconditions:** Run V2 over a multi-day range.

**Steps:**
1. Query for counts across a date range:
   ```sql
   SELECT card_type, COUNT(*) AS total
   FROM datalake.cards
   WHERE as_of BETWEEN '2024-10-01' AND '2024-10-03'
   GROUP BY card_type;
   ```
2. Note that counts are ~3x a single-day count due to the same cards appearing in each snapshot.
3. Execute CardTypeDistributionV2 for the equivalent multi-day range.
4. Compare output card_count values.

**Expected Result:** Card counts match the un-deduplicated database query. Percentages remain correct because inflation affects numerator and denominator equally.

---

### TC-13: Weekend effective date -- no data (Edge Case 2)

**Objective:** Verify V2 behavior when the effective date falls on a weekend with no card data.

**Preconditions:** Confirm that the target weekend date has no data in datalake.cards.

**Steps:**
1. Identify a weekend date (e.g., 2024-10-05 Saturday).
2. Confirm: `SELECT COUNT(*) FROM datalake.cards WHERE as_of = '2024-10-05';` returns 0.
3. Execute CardTypeDistributionV2 for 2024-10-05.

**Expected Result:** One of two acceptable outcomes:
- V2 produces an empty output file (header + trailer only), matching V1's empty DataFrame guard.
- V2 logs a SQL error (table not registered), but under Overwrite mode the final file from the last weekday remains correct.

Compare with V1 behavior for the same date.

---

### TC-14: Double-precision CSV serialization (Edge Case 3)

**Objective:** Verify that the `pct_of_total` value serialized to CSV matches V1's serialization exactly.

**Preconditions:** Run both V1 and V2 for the same effective date.

**Steps:**
1. Execute both jobs for 2024-12-31.
2. Extract the raw pct_of_total string from each output CSV.
3. Compare byte-for-byte.

**Expected Result:** Strings match exactly. For equal-count card types (e.g., 50/50 split), the value `0.5` is exactly representable in IEEE 754 and should serialize identically. For unequal splits, verify trailing digits match.

---

### TC-15: Trailer row_count accuracy (Edge Case 4)

**Objective:** Verify the trailer's `{row_count}` token reflects the actual number of data rows.

**Preconditions:** Run V2 for effective date 2024-12-31.

**Steps:**
1. Execute CardTypeDistributionV2 for 2024-12-31.
2. Parse the output CSV.
3. Count data rows (excluding header and trailer).
4. Parse the trailer and extract the row_count value.

**Expected Result:** The trailer row_count equals the number of data rows. With 2 card types, the trailer should read `TRAILER|2|2024-12-31`.

---

### TC-16: Row ordering -- V2 deterministic vs V1 hash-dependent (FSD Risk)

**Objective:** Verify that V2's `ORDER BY card_type` produces the same row order as V1's output.

**Preconditions:** Run both V1 and V2 for the same effective date.

**Steps:**
1. Execute both jobs for 2024-12-31.
2. Compare the order of card_type values in the data rows.

**Expected Result:** Both outputs have the same row ordering. V2 uses alphabetical order (`Credit` before `Debit`). If V1's Dictionary iteration order differs, this test identifies the mismatch for resolution.

**Resolution Path (if mismatch):** Adjust V2's `ORDER BY` clause to match V1's actual output order.

---

### TC-17: Anti-pattern elimination verification (AP3, AP4, AP6)

**Objective:** Verify that V2 eliminates the identified code-quality anti-patterns.

**Preconditions:** Read the V2 job config.

**Steps:**
1. Read `JobExecutor/Jobs/card_type_distribution_v2.json`.
2. Verify:
   - **AP3 eliminated:** No `External` module in the config. Module chain is `DataSourcing -> Transformation -> CsvFileWriter`.
   - **AP4 eliminated:** DataSourcing for cards sources only `["card_type"]`, not `card_id`, `customer_id`, or `card_status`.
   - **AP1 eliminated:** No DataSourcing entry for `card_transactions`.
   - **AP6 eliminated:** Business logic is in SQL (Transformation module), not row-by-row C# iteration.

**Expected Result:** V2 config uses Tier 1 (Framework Only) with no External module, no dead sources, and no unused columns.

---

## 3. Full Date Range Verification

### TC-18: End-to-end Proofmark comparison

**Objective:** Verify V2 output is byte-identical to V1 across the full date range.

**Preconditions:** Both V1 and V2 have been run for 2024-10-01 through 2024-12-31.

**Steps:**
1. Run Proofmark comparison:
   ```bash
   python3 -m proofmark compare \
     --config POC3/proofmark_configs/card_type_distribution.yaml \
     --left Output/curated/card_type_distribution.csv \
     --right Output/double_secret_curated/card_type_distribution.csv \
     --output POC3/logs/proofmark_reports/card_type_distribution.json
   ```
2. Check exit code: 0 = PASS, 1 = FAIL, 2 = CONFIG ERROR.

**Expected Result:** Exit code 0 (PASS). Proofmark config uses `reader: csv`, `header_rows: 1`, `trailer_rows: 1`, threshold 100.0, no exclusions, no fuzzy overrides.

**If FAIL:** Consult the Proofmark report. Likely causes:
- Row ordering mismatch (TC-16) -- adjust ORDER BY.
- Double-precision serialization difference (TC-14) -- add fuzzy on pct_of_total.
- as_of value difference -- investigate DataSourcing row ordering.

---

## 4. Boundary Date Tests

### TC-19: First effective date (2024-10-01)

**Objective:** Verify correct output for the earliest date in the range.

**Steps:** Execute V2 for 2024-10-01, compare with V1 output.

**Expected Result:** Outputs match.

### TC-20: Last effective date (2024-12-31)

**Objective:** Verify correct output for the latest date in the range (final Overwrite).

**Steps:** Execute V2 for 2024-12-31, compare with V1 output.

**Expected Result:** Outputs match. This is the output that persists on disk after a full auto-advance run.

### TC-21: Month boundary (2024-10-31 / 2024-11-01)

**Objective:** Verify no anomalies at month boundaries.

**Steps:** Execute V2 for 2024-10-31 and 2024-11-01 separately, compare with V1.

**Expected Result:** Outputs match for both dates. No month-boundary special behavior expected for this job.
