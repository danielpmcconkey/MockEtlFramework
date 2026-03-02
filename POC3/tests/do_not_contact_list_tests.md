# DoNotContactList -- V2 Test Plan

## Job Info
- **V2 Config**: `do_not_contact_list_v2.json`
- **Tier**: Tier 1 (Framework Only)
- **External Module**: None (V1 used `ExternalModules.DoNotContactProcessor`, eliminated via AP3)

## Pre-Conditions
- **Data sources required:**
  - `datalake.customer_preferences` -- columns: `customer_id`, `opted_in`, `as_of` (auto-appended). V1 also sourced `preference_id` and `preference_type` but V2 drops them (AP4 elimination).
  - `datalake.customers` -- columns: `id`, `first_name`, `last_name`, `as_of` (auto-appended).
- **Effective date range:** `firstEffectiveDate` = `2024-10-01`. Auto-advance runs one date at a time (`minEffectiveDate == maxEffectiveDate`).
- **Date considerations:** Sunday dates trigger W1 (Sunday skip), producing empty output. All other days process normally.

## Test Cases

### TC-1: Output Schema Validation
- **Expected columns (exact order per FSD Section 4):**
  1. `customer_id` -- INTEGER
  2. `first_name` -- TEXT
  3. `last_name` -- TEXT
  4. `as_of` -- TEXT (date string, yyyy-MM-dd format)
- **Verification:** Parse the V2 CSV header row and confirm column names and order match exactly. Confirm data types by inspecting representative rows (integer for customer_id, string for names and as_of).

### TC-2: Row Count Equivalence
- V1 and V2 must produce identical row counts for every effective date in the auto-advance range.
- On Sundays (W1), both V1 and V2 must produce 0 data rows (header + trailer only).
- On non-Sunday dates, row counts must match exactly.
- **Verification method:** Run both V1 and V2 across the full effective date range. Compare row counts per date. Proofmark threshold is 100.0 (strict match).

### TC-3: Data Content Equivalence
- All values must be byte-identical to V1 output for every effective date.
- Column values to verify:
  - `customer_id`: Integer values match V1's dictionary-key output order.
  - `first_name` / `last_name`: String values match, including NULL-to-empty-string coalescing (COALESCE in V2 matches `?.ToString() ?? ""` in V1).
  - `as_of`: Date string from `MIN(cp.as_of)` matches V1's `prefs.Rows[0]["as_of"]` (identical in single-date execution).
- **Row ordering:** Ordered by `customer_id ASC`. V1's dictionary iteration order correlates with ascending customer_id. V2 SQL uses explicit `ORDER BY cp.customer_id`.
- **W-codes affecting comparison:** W1 (Sunday skip) -- on Sundays, both produce empty output; no data rows to compare.
- **Verification method:** Proofmark byte-level comparison with threshold 100.0.

### TC-4: Writer Configuration
- **Verify these CsvFileWriter properties match V1:**

| Property | Expected Value | V1 Reference |
|----------|---------------|--------------|
| source | `output` | [do_not_contact_list.json:26] |
| includeHeader | `true` | [do_not_contact_list.json:28] |
| trailerFormat | `TRAILER\|{row_count}\|{date}` | [do_not_contact_list.json:29] |
| writeMode | `Overwrite` | [do_not_contact_list.json:30] |
| lineEnding | `LF` | [do_not_contact_list.json:31] |
| outputFile | `Output/double_secret_curated/do_not_contact_list.csv` | Path change per V2 convention (V1: `Output/curated/...`) |

- **Trailer verification:** Confirm `{row_count}` substitution matches actual data row count and `{date}` substitution uses `__maxEffectiveDate`.
- **Line ending verification:** Confirm output uses LF (`\n`), not CRLF (`\r\n`).

### TC-5: Anti-Pattern Elimination Verification

| AP-code | What to Verify |
|---------|---------------|
| AP3 (Unnecessary External module) | V2 config contains NO `External` module entry. The module chain is `DataSourcing -> DataSourcing -> Transformation -> CsvFileWriter`. The `DoNotContactProcessor.cs` assembly is not referenced. |
| AP4 (Unused columns) | V2 DataSourcing for `customer_preferences` sources only `["customer_id", "opted_in"]`. V1's `preference_id` and `preference_type` are removed. Confirm these columns do not appear in the V2 config. |
| AP6 (Row-by-row iteration) | V2 uses a SQL Transformation module with `GROUP BY` / `HAVING` / `INNER JOIN`. No `foreach` loops exist. Confirm the Transformation SQL is set-based. |

### TC-6: Edge Cases

| Edge Case | Expected Behavior | Verification |
|-----------|-------------------|--------------|
| Sunday execution (W1) | 0 data rows. CSV contains header + trailer with `TRAILER\|0\|{date}`. File is overwritten (Overwrite mode). | Run on a Sunday date (e.g., 2024-10-06). Confirm output has header row, zero data rows, and trailer with row_count=0. |
| Saturday execution | Normal processing. Saturday is NOT skipped (only Sunday is). | Run on a Saturday date (e.g., 2024-10-05). Confirm output contains data rows matching V1. |
| Customer with mixed preferences | Customer NOT included. If a customer has 5 preferences and 4 are opted out but 1 is opted in, `COUNT(*) != SUM(opted_out)` fails the HAVING clause. | Verify that partially opted-out customers do not appear in output. |
| Customer with zero preferences | Not included. No rows to aggregate means no GROUP BY result. | Verify absent from output. |
| Customer in preferences but not in customers table | Excluded by INNER JOIN. V1 uses `customerLookup.ContainsKey` check. | Verify that orphaned preference records (customer_id not in customers) produce no output row. |
| NULL first_name or last_name | Coalesced to empty string `""`. V2 uses `COALESCE(c.first_name, '')`. V1 uses `?.ToString() ?? ""`. | Verify NULL names render as empty strings in output, not as "NULL" or null literal. |
| Empty customer_preferences table | Empty output. Transformation produces zero rows. CsvFileWriter writes header + trailer. | Verify graceful handling -- no runtime error, output is header + trailer only. |
| Empty customers table | Empty output. INNER JOIN produces zero rows regardless of preferences data. | Verify graceful handling. |
| Overwrite mode across multi-day auto-advance | Only the last effective date's output persists. Prior days' files are overwritten. | Run auto-advance across multiple dates. Confirm final output file contains only the last date's results. |

### TC-7: Proofmark Configuration
- **Expected proofmark settings from FSD Section 8:**

```yaml
comparison_target: "do_not_contact_list"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

- **Threshold:** 100.0 (strict byte-identical match). No tolerance for differences.
- **Excluded columns:** None. All columns are deterministic.
- **Fuzzy columns:** None. No floating-point arithmetic or rounding involved.
- **Rationale:** All output values (customer_id, first_name, last_name, as_of) are integers or strings sourced directly from data. No computed floating-point values exist.

## W-Code Test Cases

### TC-W1: Sunday Skip (W1)
- **What the wrinkle is:** V1 returns an empty DataFrame when `maxEffectiveDate` falls on a Sunday [DoNotContactProcessor.cs:20-24]. The CsvFileWriter then writes a header row and a trailer with `row_count=0`.
- **How V2 handles it:** SQL WHERE clause `strftime('%w', cp.as_of) != '0'` filters out all rows when the effective date is Sunday. Since auto-advance sets `minEffectiveDate == maxEffectiveDate`, all rows in `customer_preferences` share the same `as_of`. On Sundays, every row is excluded, producing an empty result set.
- **What to verify:**
  1. Run V2 on a known Sunday date (e.g., 2024-10-06, 2024-10-13, 2024-10-20, 2024-10-27).
  2. Confirm output CSV has exactly 1 header row and 1 trailer row, with 0 data rows.
  3. Confirm trailer reads `TRAILER|0|2024-10-06` (or the appropriate Sunday date).
  4. Compare byte-for-byte against V1 output for the same Sunday date.
  5. Confirm non-Sunday dates (e.g., 2024-10-07, Monday) produce normal data output.

## Notes
- **AP4 column trimming impact:** V2 removes `preference_id` and `preference_type` from the DataSourcing columns list. These columns were never used in V1's business logic -- they were sourced but ignored. Confirm that removing them does not affect DataSourcing's row filtering or `as_of` auto-append behavior.
- **Multi-date preference counting (BRD OQ-1):** The BRD notes that V1 aggregates preferences across all dates without partitioning. In practice, auto-advance runs one date at a time, so each execution only sees one date's preferences. V2's SQL replicates this single-date behavior. If the executor ever runs with a multi-date range, the aggregation behavior would differ from a per-date partitioned approach. This is a replicated V1 characteristic, not a V2 bug.
- **Sunday overwrite (BRD OQ-2):** On Sundays, Overwrite mode replaces the existing file with an empty result. This is V1's actual behavior. V2 reproduces it faithfully.
- **firstEffectiveDate:** V2 uses `2024-10-01` matching V1 exactly.
