# InvestmentAccountOverview — V2 Test Plan

## Job Info
- **V2 Config**: `investment_account_overview_v2.json`
- **Tier**: Tier 2 (Framework + Minimal External — SCALPEL)
- **External Module**: `ExternalModules.InvestmentAccountOverviewV2Processor`

## Pre-Conditions
- **Data sources**: `datalake.investments` (investment_id, customer_id, account_type, current_value, risk_profile, as_of) and `datalake.customers` (id, first_name, last_name, as_of)
- **Effective date range**: Injected by executor via `__minEffectiveDate` / `__maxEffectiveDate`
- **V1 baseline output**: `Output/curated/investment_account_overview.csv` must exist for Proofmark comparison
- **Note**: V1 sources `advisor_id` from investments and `prefix`, `suffix` from customers — V2 eliminates these (AP1, AP4). Tests must confirm these columns are NOT sourced.

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | FSD Section 4  | Output schema: 8 columns in exact order with correct types |
| TC-02   | All            | Row count equivalence between V1 and V2 |
| TC-03   | All            | Data content equivalence between V1 and V2 |
| TC-04   | Writer Config  | CsvFileWriter: header, trailer, LF, Overwrite |
| TC-05   | AP1, AP3, AP4, AP6 | Anti-pattern elimination verification |
| TC-06   | BR-2, Edge Cases | Edge case handling |
| TC-07   | FSD Section 8  | Proofmark configuration correctness |
| TC-W1   | W1             | Sunday skip — empty DataFrame on Sundays |
| TC-08   | BR-2           | Empty input guard (null/empty investments or customers) |
| TC-09   | BR-3           | Customer name lookup via LEFT JOIN semantics |
| TC-10   | BR-4           | 1:1 investment-to-output row mapping |
| TC-11   | BR-5           | Row-level as_of from investment row, not __maxEffectiveDate |
| TC-12   | BR-6           | No rounding on current_value |
| TC-13   | BR-9           | Trailer format: TRAILER|{row_count}|{date} |
| TC-14   | BR-10          | Effective date injection via DataSourcing |
| TC-15   | Edge Case 3    | Investment with no matching customer |
| TC-16   | Edge Case 4    | Customer with no investments |
| TC-17   | Edge Case 5    | NULL current_value handling |
| TC-18   | Edge Case 6    | Multi-day effective date range — row-level as_of |
| TC-19   | Edge Case 8    | __maxEffectiveDate fallback to DateTime.Today |
| TC-20   | Edge Case 2    | Saturday effective date — no special handling |

## Test Cases

### TC-01: Output Schema Validation
- **Traces to:** FSD Section 4
- **Input conditions:** Standard job run on a weekday with investment data.
- **Expected output:** Output CSV contains exactly 8 columns in this order:
  1. `investment_id` (int — Convert.ToInt32)
  2. `customer_id` (int — Convert.ToInt32)
  3. `first_name` (string — null coalesced to "")
  4. `last_name` (string — null coalesced to "")
  5. `account_type` (string — null coalesced to "")
  6. `current_value` (decimal — Convert.ToDecimal, no rounding)
  7. `risk_profile` (string — null coalesced to "")
  8. `as_of` (date — row-level from investment)
- **Verification method:** Read the header line of the output CSV. Verify column names and order match exactly. Compare V2 header against V1 header. Verify column count is exactly 8. Confirm no extra columns (e.g., no `advisor_id`, no `prefix`, no `suffix`).

### TC-02: Row Count Equivalence
- **Traces to:** All BRs
- **Input conditions:** Run V1 and V2 for the same effective date range (weekday dates).
- **Expected output:** V2 output row count equals V1 output row count for every effective date. Since this is a 1:1 mapping from investments, the output row count should equal the number of investment rows returned by DataSourcing for that date.
- **Verification method:** Proofmark comparison at 100.0% threshold. Count data rows (excluding header and trailer) in both output files. Any difference indicates a join or guard logic divergence.

### TC-03: Data Content Equivalence
- **Traces to:** All BRs
- **Input conditions:** Run V1 and V2 for the same effective date range.
- **Expected output:** Every data row in V2 matches the corresponding V1 row across all 8 columns. W1 (Sunday skip) is the only wrinkle — on Sundays, both V1 and V2 produce empty output.
- **Verification method:** Proofmark strict comparison (threshold 100.0, no excluded columns, no fuzzy columns). The Proofmark config must use `header_rows: 1` and `trailer_rows: 1` to skip the header and trailer during comparison.

### TC-04: Writer Configuration
- **Traces to:** BRD Writer Config, FSD Section 7
- **Input conditions:** Standard job run producing output.
- **Expected output:**
  - File format: CSV
  - File location: `Output/double_secret_curated/investment_account_overview.csv`
  - First line is a header row with column names
  - Last line is a trailer row: `TRAILER|{row_count}|{date}`
  - Line endings are LF (Unix-style, `\n`), NOT CRLF
  - Write mode is Overwrite — running the job twice replaces the file entirely
- **Verification method:**
  - Verify file exists at expected path
  - Read first line and confirm it matches expected column headers
  - Read last line and confirm it matches `TRAILER|{N}|{YYYY-MM-DD}` format where N = data row count
  - Check line endings with `xxd` or hex inspection — no `\r\n` sequences
  - Run job twice for different dates and confirm the file only contains the second run's output

### TC-05: Anti-Pattern Elimination Verification
- **Traces to:** FSD Section 3 (AP1, AP3, AP4, AP6)
- **Input conditions:** Review V2 job config and External module source code.
- **Expected verifications:**
  - **AP1 (Dead-end sourcing):** V2 config does NOT source `advisor_id` from investments. V1 config included `advisor_id` in the investments DataSourcing columns list — confirm it is absent from V2.
  - **AP3 (Unnecessary External — partial):** V2 uses Tier 2 (DataSourcing handles data retrieval, External handles only Sunday guard + empty guard + LEFT JOIN). V1 was Tier 3 (External did everything including implicit data sourcing). The External module is justified because W1 requires access to `__maxEffectiveDate` (unavailable in SQLite) and empty DataFrames cause Transformation table registration failures.
  - **AP4 (Unused columns):** V2 config does NOT source `prefix` or `suffix` from customers. V1 config included both — confirm they are absent from V2. V2 config does NOT source `advisor_id` from investments. V2 sources only: investments (investment_id, customer_id, account_type, current_value, risk_profile) and customers (id, first_name, last_name).
  - **AP6 (Row-by-row iteration — partial):** V2 External uses Dictionary-based hash-join (`ToDictionary` for customer lookup). This is the idiomatic C# pattern. Full SQL elimination is blocked by W1. The iteration pattern is clean and O(n).
- **Verification method:** Diff V2 config against V1 config. Inspect V2 External module source code for unnecessary data access. Confirm the module does not reference `advisor_id`, `prefix`, or `suffix`.

### TC-06: Edge Cases
- **Traces to:** BR-2, Edge Cases 1-8
- **Input conditions:** See individual edge case test cases TC-15 through TC-20.
- **Expected output:** See individual test cases.
- **Verification method:** Umbrella case — see specific test cases below.

### TC-07: Proofmark Configuration
- **Traces to:** FSD Section 8
- **Input conditions:** Proofmark config for this job.
- **Expected output:** Config specifies:
  - `comparison_target: "investment_account_overview"`
  - `reader: csv`
  - `threshold: 100.0`
  - `csv.header_rows: 1`
  - `csv.trailer_rows: 1`
  - No EXCLUDED columns
  - No FUZZY columns
- **Verification method:** Read the Proofmark YAML config at `POC3/proofmark_configs/investment_account_overview.yaml` and verify all fields match the FSD's Proofmark config design. No overrides needed: all output columns are deterministic, `current_value` uses decimal (no float accumulation), no runtime timestamps.

## W-Code Test Cases

### TC-W1: Sunday Skip (W1)
- **Traces to:** W1, BR-1, FSD Section 10
- **W-Code behavior:** V1 checks `__maxEffectiveDate.DayOfWeek == DayOfWeek.Sunday` and returns an empty DataFrame if true. No data is processed or output. The CsvFileWriter will produce a file with only a header and trailer (0 data rows).
- **V2 handling:** V2 External module reproduces the same guard clause. Comment: `// V1 behavior: no output on Sundays (W1)`.
- **Input conditions:** Run V2 for an effective date range where `__maxEffectiveDate` falls on a Sunday (e.g., 2024-10-06 is a Sunday).
- **Expected output:**
  - Output CSV contains a header row and a trailer row, with zero data rows between them
  - Trailer shows `TRAILER|0|2024-10-06` (row count = 0, date = the Sunday effective date)
  - The Sunday guard fires BEFORE any data processing — investments and customers DataFrames are not iterated
- **Verification method:**
  - Run for a Sunday date and verify the output file has exactly 2 lines: header + trailer
  - Compare against V1 output for the same Sunday — both should be header + trailer only
  - Verify the trailer row_count is 0 and the date matches __maxEffectiveDate
  - Run for the adjacent Monday and verify non-empty output (confirming the skip is Sunday-only, not weekend-wide)
- **Edge note:** The Sunday check uses `__maxEffectiveDate`, not `__minEffectiveDate`. In a multi-day range ending on Sunday, the skip still triggers. Verify this behavior matches V1.

## Additional Test Cases

### TC-08: Empty Input Guard
- **Traces to:** BR-2
- **Input conditions:** Run for a date where either investments or customers returns null/empty from DataSourcing. This could happen on:
  - Dates outside the datalake's data range
  - Weekends (datalake typically has no weekend data per BRD observation)
- **Expected output:** Empty DataFrame with correct 8-column schema. CsvFileWriter produces header + trailer with 0 data rows.
- **Verification method:** Identify a date in the effective range with no investment data. Run V2 and verify header-only + trailer output. Compare against V1 for the same date.
- **Critical note:** V1 returns empty output if EITHER investments OR customers is null/empty. This means if there are investments but zero customers, the output is empty — NOT a LEFT JOIN with empty names. V2 must reproduce this behavior exactly. Verify: if a date has investments but no customers, V2 produces zero rows (not rows with empty names).

### TC-09: Customer Name Lookup (LEFT JOIN Semantics)
- **Traces to:** BR-3
- **Input conditions:** Investment rows where:
  - Investment I1 has customer_id matching a customer in the customers table
  - Investment I2 has customer_id NOT matching any customer (see TC-15)
- **Expected output:**
  - I1's output row has the matched customer's first_name and last_name
  - I2's output row has first_name = "" and last_name = "" (empty strings, not NULL)
- **Verification method:** Query investments and customers for the test date. Identify investments with and without matching customers. Verify the output rows have correct name values. Null coalescing ensures no NULL values in the output — only empty strings.

### TC-10: 1:1 Investment-to-Output Mapping
- **Traces to:** BR-4
- **Input conditions:** Standard run with N investment rows for the effective date.
- **Expected output:** Exactly N data rows in the output (one per investment). No aggregation, no filtering (except Sunday skip and empty guard), no deduplication.
- **Verification method:** Count investment rows from DataSourcing for the test date. Count data rows in output (exclude header and trailer). Numbers must match exactly. If they don't, investigate whether the customer lookup or guard logic is incorrectly filtering.

### TC-11: Row-Level as_of Preservation
- **Traces to:** BR-5
- **Input conditions:** Multi-day effective date range (e.g., 2024-10-01 through 2024-10-03). Each day's investments have their own as_of value from the source data.
- **Expected output:** Each output row's `as_of` matches the investment row's own `as_of` value — NOT `__maxEffectiveDate`. For a 3-day range, the output should contain rows with as_of = 2024-10-01, as_of = 2024-10-02, and as_of = 2024-10-03 (assuming data exists for all three days).
- **Verification method:** Run for a multi-day range. Read the output and verify `as_of` values span multiple dates. If all rows had `as_of = __maxEffectiveDate`, that would be a bug — the External module must use `row["as_of"]`, not `__maxEffectiveDate`.

### TC-12: No Rounding on current_value
- **Traces to:** BR-6
- **Input conditions:** Investment rows with current_value values that have varying decimal precision (e.g., 1234.5678, 99999.99, 0.01).
- **Expected output:** The output current_value matches the source value exactly after `Convert.ToDecimal`. No rounding, truncation, or formatting is applied. The decimal precision in the output matches what `Convert.ToDecimal` produces from the source.
- **Verification method:** Query `datalake.investments` for the test date and note specific current_value values. Compare against the output CSV's current_value column. Values must be identical (accounting for CSV string representation of decimals). No W5 (Banker's rounding) or W6 (double epsilon) applies to this job.

### TC-13: Trailer Format
- **Traces to:** BR-9
- **Input conditions:** Standard run producing N data rows.
- **Expected output:** The last line of the CSV file is `TRAILER|N|YYYY-MM-DD` where:
  - N = number of data rows (not including header or trailer)
  - YYYY-MM-DD = `__maxEffectiveDate` value
- **Verification method:** Read the last line of the output file. Parse the three pipe-delimited segments. Verify row_count matches the actual data row count. Verify the date matches the effective date used for the run. This is handled by the framework's CsvFileWriter — the External module does not generate the trailer.

### TC-14: Effective Date Injection
- **Traces to:** BR-10
- **Input conditions:** Run for a specific effective date range.
- **Expected output:** DataSourcing pulls only rows within the injected date range. The investments and customers DataFrames contain only rows where `as_of` falls within `__minEffectiveDate` and `__maxEffectiveDate`.
- **Verification method:** Query `datalake.investments` and `datalake.customers` for the same date range. Compare row counts against what DataSourcing returns. Verify no out-of-range as_of values appear in the output.

### TC-15: Investment with No Matching Customer
- **Traces to:** Edge Case 3
- **Input conditions:** An investment row where `customer_id` does not match any `id` in the customers table for the same effective date.
- **Expected output:** The investment still appears in the output with `first_name = ""` and `last_name = ""`. It is NOT filtered out — this is LEFT JOIN semantics from the investment side.
- **Verification method:** Query for investment customer_ids that have no match in customers for the test date. Verify those investments appear in the output with empty name fields. Compare against V1 output.

### TC-16: Customer with No Investments
- **Traces to:** Edge Case 4
- **Input conditions:** A customer who exists in the customers table but has no corresponding investment rows.
- **Expected output:** The customer does NOT appear in the output. Iteration is over investments, not customers. This is a LEFT JOIN from investments to customers — customers without investments are simply never referenced.
- **Verification method:** Query for customer ids that exist in customers but not in investments for the test date. Verify none of those customer_ids appear in the output.

### TC-17: NULL current_value Handling
- **Traces to:** Edge Case 5
- **Input conditions:** An investment row where current_value is NULL.
- **Expected output:** `Convert.ToDecimal(null)` behavior — per BRD, this would throw an exception. If the test data contains NULL current_value rows, this is a potential runtime failure.
- **Verification method:** Query `SELECT COUNT(*) FROM datalake.investments WHERE current_value IS NULL AND as_of BETWEEN '{min}' AND '{max}'`. If count > 0, the job should fail with an exception on V1 and V2 identically. If count = 0, this is a theoretical edge case — document and move on. No defensive code should be added (V2 must match V1 behavior, including crashes).

### TC-18: Multi-Day Date Range — Row-Level as_of
- **Traces to:** Edge Case 6
- **Input conditions:** Effective date range spanning 3+ weekdays.
- **Expected output:** Output contains investment rows from all dates in the range. Each row preserves its own as_of value. The output is NOT filtered to a single date. The customer lookup uses the full customers DataFrame across all dates.
- **Verification method:** Run for a 3-day range. Verify the output contains rows with distinct as_of values from multiple days. Verify the total row count matches the sum of per-day investment counts.
- **Note on customer lookup**: V1 builds the customer dictionary from ALL customer rows across the date range. If the same customer_id appears on multiple as_of dates, the last one processed overwrites prior entries. V2 must reproduce this Dictionary overwrite behavior. The matched name could come from any as_of date's customer row — typically the last date processed.

### TC-19: __maxEffectiveDate Fallback
- **Traces to:** Edge Case 8
- **Input conditions:** A scenario where `__maxEffectiveDate` is not present in shared state (defensive edge case).
- **Expected output:** The External module falls back to `DateOnly.FromDateTime(DateTime.Today)`. The Sunday check is evaluated against today's date.
- **Verification method:** This is primarily verified by code inspection. The V2 External module should include the same fallback pattern as V1: `sharedState.ContainsKey("__maxEffectiveDate") ? (DateOnly)sharedState["__maxEffectiveDate"] : DateOnly.FromDateTime(DateTime.Today)`. In practice, the executor always injects `__maxEffectiveDate`, so this is purely defensive. Verify by reading the External module source code.

### TC-20: Saturday Effective Date
- **Traces to:** Edge Case 2
- **Input conditions:** Run for a Saturday effective date.
- **Expected output:** No special handling for Saturdays. The job processes normally. Since the datalake typically has no weekend data, DataSourcing will likely return empty DataFrames, triggering the empty input guard (BR-2) and producing a header + trailer with 0 data rows.
- **Verification method:** Run for a Saturday date. Verify the output is a header + trailer file with 0 rows (due to empty source data). Confirm the Sunday skip did NOT trigger (Saturday != Sunday). Compare against V1 for the same Saturday date.

## Notes

- **W1 (Sunday skip) is the defining characteristic of this job.** It is the reason the job requires a Tier 2 External module rather than a pure Tier 1 SQL approach. The Sunday guard must execute before any data access to avoid table registration failures in Transformation.
- **The empty input guard has a subtle implication.** If investments exist but customers is empty, V1 produces zero output — NOT a LEFT JOIN with empty names. This is because V1 checks `customers == null || customers.Count == 0` and returns early. V2 must reproduce this exact behavior.
- **Customer Dictionary overwrite in multi-day ranges.** When the date range spans multiple days, the same customer_id may appear in the customers DataFrame multiple times (once per as_of date). The Dictionary build loop overwrites prior entries. The name used in the output comes from whichever as_of date's customer row was processed last. V2 must use the same Dictionary-based approach to ensure identical overwrites.
- **No fuzzy comparison needed.** current_value uses `Convert.ToDecimal` (exact), not float accumulation. All string fields use null coalescing to "". All integer fields use `Convert.ToInt32`. No W-codes introduce numeric drift.
- **AP4 is the highest-impact fix.** Removing `advisor_id`, `prefix`, and `suffix` from DataSourcing reduces database I/O and memory usage with zero output impact.
