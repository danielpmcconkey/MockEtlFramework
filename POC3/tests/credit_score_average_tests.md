# CreditScoreAverage -- Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | Average score computed across all bureaus per customer using decimal arithmetic |
| TC-02   | BR-2           | Per-bureau scores extracted by case-insensitive matching; missing bureau is NULL |
| TC-03   | BR-3           | Only customers with scores AND in customers table appear in output |
| TC-04   | BR-4           | Empty input (null or zero-row credit_scores or customers) produces empty output with correct schema |
| TC-05   | BR-5           | as_of value comes from customers table, not credit_scores |
| TC-06   | BR-6 / AP1     | Segments table is NOT sourced in V2 (dead-end eliminated) |
| TC-07   | BR-7           | Customer name uses last-entry-per-id pattern (dictionary overwrite) |
| TC-08   | BR-8           | Iteration is score-driven; customers without scores are excluded |
| TC-09   | Writer Config   | CSV output uses Overwrite mode, CRLF line endings, header, and CONTROL trailer |
| TC-10   | Output Schema   | Output contains exactly 8 columns in correct order |
| TC-11   | Edge Case       | Missing bureau score results in empty CSV field (NULL/DBNull.Value) |
| TC-12   | Edge Case       | Customer with scores but no matching customer record excluded |
| TC-13   | Edge Case       | Weekend date with no data produces header + trailer only (zero data rows) |
| TC-14   | Edge Case       | Multi-day Overwrite run retains only last day's output |
| TC-15   | Edge Case       | NULL first_name or last_name coalesced to empty string |
| TC-16   | Edge Case       | Non-integer average score preserves decimal precision |
| TC-17   | Edge Case       | Month-end and quarter-end boundary dates produce normal output |
| TC-18   | FSD: Tier 2     | V2 uses Tier 2 (DataSourcing -> Transformation -> External -> Writer) with justified rationale |
| TC-19   | FSD: AP3        | External module scope reduced to decimal division and DateOnly reconstruction only |
| TC-20   | FSD: AP4        | credit_score_id column removed from V2 DataSourcing (unused column eliminated) |
| TC-21   | FSD: W9         | Overwrite write mode reproduced with documentation comment |
| TC-22   | Proofmark       | Proofmark comparison passes with header_rows: 1, trailer_rows: 1, zero exclusions |
| TC-23   | OQ-2           | Multiple scores per bureau per customer: last wins for individual bureau, all contribute to average |
| TC-24   | Non-Det Fields  | Trailer {timestamp} is non-deterministic; handled by Proofmark trailer_rows stripping |

## Test Cases

### TC-01: Average score computed across all bureaus using decimal arithmetic
- **Traces to:** BR-1
- **Input conditions:** Run V2 job for a single weekday (e.g., 2024-10-01). The datalake.credit_scores table contains scores for multiple bureaus per customer.
- **Expected output:** Each output row's `avg_score` equals the arithmetic mean of all score values for that customer_id on the effective date, computed using C# `decimal` division (not IEEE 754 double). For a customer with scores 720, 680, 750 the average is `(720 + 680 + 750) / 3 = 716.666666666666666666666666667m` (28+ significant digits of decimal precision).
- **Verification method:** Read V2 CSV output. For a sample of customers, manually compute `SUM(score) / COUNT(score)` using decimal arithmetic and compare with the `avg_score` field. Verify the value is NOT the double-precision result (which would differ after ~15 significant digits). Compare with V1 output to confirm identical precision. The FSD documents this as the primary reason for Tier 2 [FSD Section 1].

### TC-02: Per-bureau scores with case-insensitive matching and NULL default
- **Traces to:** BR-2
- **Input conditions:** Run V2 job for a date. The datalake.credit_scores table has bureau values `'Equifax'`, `'TransUnion'`, `'Experian'` (mixed case per CHECK constraint).
- **Expected output:** Each output row has `equifax_score`, `transunion_score`, and `experian_score` populated correctly. Bureau matching is case-insensitive (LOWER comparison). If a customer has no score for a given bureau, that field is empty in the CSV (rendered from DBNull.Value/NULL).
- **Verification method:** Read V2 CSV output. For each customer, verify bureau scores match their source records from `datalake.credit_scores`. Check that the SQL uses `LOWER(cs.bureau)` for matching [FSD Section 5]. Compare with V1 output.

### TC-03: Only customers with scores AND in customers table appear
- **Traces to:** BR-3
- **Input conditions:** Run V2 job for a date. Verify the join behavior between credit_scores and customers.
- **Expected output:** The output contains only customers who: (a) have at least one credit score entry for the effective date, AND (b) have a matching record in the customers table for the same effective date. Customers with scores but no customer record are excluded. Customers with a customer record but no scores are excluded.
- **Verification method:** Identify customer_ids in credit_scores that are NOT in customers for the same date (if any exist). Verify those customer_ids are absent from V2 output. Verify no customer_ids appear in V2 output that lack credit_score entries. The FSD implements this via INNER JOIN [FSD Section 5].

### TC-04: Empty input produces empty output with correct schema
- **Traces to:** BR-4
- **Input conditions:** Run V2 job for a date where credit_scores or customers has zero rows (e.g., a weekend date with no data).
- **Expected output:** The CSV output file contains a header row and a trailer row, but zero data rows. The header contains the 8 column names in correct order. The trailer follows the format `CONTROL|{date}|0|{timestamp}` (with row_count = 0). The External module's empty guard produces an empty DataFrame with the correct schema [FSD Section 10, BR-4].
- **Verification method:** Read the CSV file. Verify exactly 2 lines: header and trailer. Verify the header matches the output schema column names. Verify the trailer row_count field is `0`.

### TC-05: as_of value from customers table, not credit_scores
- **Traces to:** BR-5
- **Input conditions:** Run V2 job for a single weekday.
- **Expected output:** The `as_of` column in each output row is sourced from the customers table's `as_of` field (via `c.as_of` in the SQL JOIN). For the standard date range where both tables have matching dates, this produces the effective date. The as_of value is rendered by CsvFileWriter's FormatField method on a DateOnly object, producing a locale-dependent format (matching V1 behavior).
- **Verification method:** Read V2 output. Verify all `as_of` values are consistent and match the effective date. The FSD's SQL selects `c.as_of` from the customers table [FSD Section 5, point 5]. Compare with V1 output.

### TC-06: Segments table not sourced in V2 (AP1 elimination)
- **Traces to:** BR-6, AP1
- **Input conditions:** Inspect the V2 job config JSON (`credit_score_average_v2.json`).
- **Expected output:** The V2 config contains exactly 2 DataSourcing modules (credit_scores and customers). There is NO DataSourcing entry for the `segments` table. V1 sourced segments but never used it; V2 eliminates this dead-end data source.
- **Verification method:** Read the V2 job config and verify the modules array. Confirm no module references the `segments` table. This validates AP1 elimination per FSD Section 3.

### TC-07: Customer name uses last-entry-per-id (dictionary overwrite)
- **Traces to:** BR-7
- **Input conditions:** Run V2 job for a date. In V1, the customer name dictionary overwrites without checking for existing keys, so the last customer row encountered per customer_id wins.
- **Expected output:** If multiple customer rows exist per customer_id per as_of (which the data observations suggest does not happen -- one record per id per day), the last row processed determines the name. In practice, with one record per id per as_of, this is moot. The SQL GROUP BY on customer_id, first_name, last_name, as_of handles this correctly when there is one record per combination [FSD Section 5, Traceability BR-7].
- **Verification method:** Query `datalake.customers` grouped by `id, as_of` with `HAVING COUNT(*) > 1` to check for duplicates. If none exist, this behavior is not exercisable but the design handles it correctly. If duplicates exist, verify V2 output matches V1 output for affected customers.

### TC-08: Score-driven iteration excludes customers without scores
- **Traces to:** BR-8
- **Input conditions:** Run V2 job for a date where some customers in the customers table have no entries in credit_scores.
- **Expected output:** Customers without any credit score records for the effective date do NOT appear in the output, even if they exist in the customers table. The INNER JOIN ensures only customers with scores are included.
- **Verification method:** Identify customer_ids present in `datalake.customers` but absent from `datalake.credit_scores` for the same date (if any). Verify those customer_ids are absent from V2 output. The FSD's INNER JOIN implements this [FSD Section 5].

### TC-09: Writer configuration matches V1
- **Traces to:** BRD Writer Configuration
- **Input conditions:** Run V2 job for a single weekday.
- **Expected output:** The output CSV file has: (a) a header row as the first line, (b) data rows with CRLF line endings, (c) a trailer line matching `CONTROL|{date}|{row_count}|{timestamp}`, (d) the file is overwritten on each run (not appended). Verify each configuration parameter: `includeHeader: true`, `lineEnding: "CRLF"`, `trailerFormat: "CONTROL|{date}|{row_count}|{timestamp}"`, `writeMode: "Overwrite"`.
- **Verification method:** Read the raw bytes of the V2 CSV output. Verify line endings are `\r\n` (CRLF). Verify the first line is the header. Verify the last line matches the trailer format. Run the job for two consecutive dates and confirm the file only contains the second day's data (Overwrite semantics) [FSD Section 7].

### TC-10: Output contains exactly 8 columns in correct order
- **Traces to:** BRD Output Schema
- **Input conditions:** Run V2 job for a single weekday.
- **Expected output:** The CSV header contains exactly 8 columns in this order: `customer_id`, `first_name`, `last_name`, `avg_score`, `equifax_score`, `transunion_score`, `experian_score`, `as_of`. No extra columns.
- **Verification method:** Read the V2 CSV header line. Parse column names and verify order matches the BRD output schema. Compare with V1 output header. The FSD specifies this order in Section 4.

### TC-11: Missing bureau score results in empty CSV field
- **Traces to:** Edge Case (BRD: Missing bureau score)
- **Input conditions:** Run V2 job for a date where at least one customer is missing a score for one or more bureaus.
- **Expected output:** The missing bureau's column value is empty in the CSV (two consecutive commas or a comma at line end). This results from NULL/DBNull.Value being rendered as an empty string by CsvFileWriter.
- **Verification method:** Read V2 CSV output. Find a customer missing a bureau score (if one exists in the data). Verify the corresponding column is empty. Note: data observations indicate exactly 3 scores per customer per day [FSD Appendix A], so this case may not be exercisable with current data, but the design handles it. Compare with V1 output if the case exists.

### TC-12: Customer with scores but no matching customer record excluded
- **Traces to:** Edge Case (BRD: Scores with no matching customer)
- **Input conditions:** Run V2 job for a date. Check if any customer_ids exist in credit_scores but not in customers for the same date.
- **Expected output:** Those customer_ids are excluded from output. The INNER JOIN between credit_scores and customers filters them out [FSD Section 5].
- **Verification method:** Query `SELECT DISTINCT cs.customer_id FROM datalake.credit_scores cs LEFT JOIN datalake.customers c ON cs.customer_id = c.id AND cs.as_of = c.as_of WHERE c.id IS NULL AND cs.as_of = @date`. If any rows exist, verify they are absent from V2 output. Compare with V1 output.

### TC-13: Weekend date with no data produces header + trailer only
- **Traces to:** Edge Case (weekend/zero-row)
- **Input conditions:** Run V2 job for a Saturday or Sunday (e.g., 2024-10-05 or 2024-10-06) where no credit_scores or customers data exists.
- **Expected output:** The CSV output contains exactly a header line and a trailer line. No data rows. The trailer shows `row_count = 0`. The empty DataFrame from the External module's BR-4 guard is written by CsvFileWriter.
- **Verification method:** Read the V2 CSV file. Count lines (excluding blank trailing lines). Verify 2 lines: header and trailer. Verify trailer row_count is `0`. Compare with V1 output for the same weekend date.

### TC-14: Multi-day Overwrite run retains only last day's output
- **Traces to:** Edge Case (BRD: Write Mode Implications)
- **Input conditions:** Run V2 job for a multi-day range (e.g., 2024-10-01 through 2024-10-03) using auto-advance.
- **Expected output:** After the run completes, the CSV file contains ONLY the data from the last effective date processed (2024-10-03). Data from 2024-10-01 and 2024-10-02 has been overwritten. The header and trailer reflect only the last day's row count and date.
- **Verification method:** Read the V2 CSV file after the multi-day run. Verify `as_of` values are all from the last date. Verify row count matches the number of qualifying customers for only that date. The FSD documents this as W9 [FSD Section 3].

### TC-15: NULL first_name or last_name coalesced to empty string
- **Traces to:** Edge Case (BRD: NULL handling)
- **Input conditions:** Run V2 job for a date where some customers have NULL first_name or last_name in the customers table.
- **Expected output:** The `first_name` and `last_name` fields in the CSV output are empty strings (not the literal "NULL" or missing). The SQL's `COALESCE(c.first_name, '')` handles this [FSD Section 5].
- **Verification method:** Query `datalake.customers WHERE first_name IS NULL OR last_name IS NULL` for the effective date. If such customers exist and have credit scores, verify their output rows have empty strings for the null name fields. Compare with V1 output (V1 uses `?.ToString() ?? ""`).

### TC-16: Non-integer average score preserves decimal precision
- **Traces to:** Edge Case (decimal precision)
- **Input conditions:** Run V2 job for a date. Per data observations, 1459 of 2230 customers have non-integer average scores (sum not divisible by 3) [FSD Appendix A].
- **Expected output:** The `avg_score` field for these customers shows full decimal precision (not rounded to a fixed number of decimal places, and not truncated by IEEE 754 double precision). For example, a customer with scores summing to 2150 across 3 bureaus has `avg_score = 716.666666666666666666666666667` (C# decimal representation).
- **Verification method:** Read V2 CSV output. For customers with non-integer averages, verify the decimal representation matches C# `decimal` division (`SUM / COUNT` as decimal). Compare with V1 output to confirm identical precision. This is the core validation for the Tier 2 justification [FSD Section 1].

### TC-17: Month-end and quarter-end boundary dates produce normal output
- **Traces to:** Edge Case (date boundaries)
- **Input conditions:** Run V2 job for 2024-10-31 (month-end) and 2024-12-31 (quarter-end).
- **Expected output:** Normal output rows with no special summary rows or boundary behavior. No W3a/W3b/W3c wrinkles apply [FSD Section 3].
- **Verification method:** Verify row counts match expected qualifying customers for those dates. Verify no extra rows with aggregated values exist. Compare with V1 output.

### TC-18: V2 Tier 2 implementation produces identical output to V1
- **Traces to:** FSD Tier Justification
- **Input conditions:** Run both V1 and V2 jobs for the full date range (2024-10-01 through 2024-12-31). Since writeMode is Overwrite, only the last date's output survives for each.
- **Expected output:** V2 output in `Output/double_secret_curated/credit_score_average.csv` is data-identical to V1 output in `Output/curated/credit_score_average.csv` (excluding the trailer's timestamp token which is non-deterministic). The Tier 2 chain (DataSourcing -> Transformation -> External -> CsvFileWriter) produces the same data as V1's chain (DataSourcing -> External -> CsvFileWriter).
- **Verification method:** Run Proofmark comparison. Proofmark must report PASS with 100% threshold. The trailer is stripped via `trailer_rows: 1` in the Proofmark config, avoiding the non-deterministic timestamp comparison [FSD Section 8].

### TC-19: External module scope reduced to decimal division and DateOnly only
- **Traces to:** FSD Section 10 (AP3 partial elimination)
- **Input conditions:** Inspect V2 External module source code (`CreditScoreAverageV2Processor.cs`).
- **Expected output:** The External module performs exactly two operations: (a) computing `avg_score` via decimal division of `score_sum / score_count`, and (b) reconstructing `as_of` from a string to a `DateOnly` object. It does NOT perform any data sourcing, joining, grouping, filtering, or conditional aggregation -- all of that is handled by DataSourcing and Transformation upstream.
- **Verification method:** Read the V2 External module source code. Verify it reads `grouped_scores` (not raw tables), computes only `avg_score` and `as_of` conversion, and produces `output`. Verify it does not open a database connection. This confirms AP3 partial elimination per FSD Section 3.

### TC-20: credit_score_id column removed from V2 DataSourcing
- **Traces to:** FSD Section 3 (AP4 elimination)
- **Input conditions:** Inspect the V2 job config JSON (`credit_score_average_v2.json`).
- **Expected output:** The DataSourcing module for credit_scores has `columns: ["customer_id", "bureau", "score"]`. The column `credit_score_id` is absent. V1 sourced `credit_score_id` but never used it in the External module.
- **Verification method:** Read the V2 job config. Verify the credit_scores DataSourcing columns array. Confirm `credit_score_id` is not listed. This validates AP4 elimination per FSD Section 3.

### TC-21: Overwrite write mode documented as W9
- **Traces to:** FSD Section 3 (W9)
- **Input conditions:** Inspect V2 job config and FSD documentation.
- **Expected output:** The V2 job config uses `writeMode: "Overwrite"`, matching V1 exactly. The FSD and/or code contain a comment documenting that V1 uses Overwrite mode, which means prior days' data is lost on each run. This is W9 -- a potentially wrong writeMode that is preserved for output equivalence.
- **Verification method:** Read V2 job config and verify `writeMode: "Overwrite"`. Read FSD Section 3 and verify W9 is documented. Optionally inspect V2 source code for a comment about the Overwrite behavior.

### TC-22: Proofmark comparison passes with header and trailer stripping
- **Traces to:** FSD Proofmark Config Design (Section 8)
- **Input conditions:** Run Proofmark with the designed config: `reader: csv`, `threshold: 100.0`, `header_rows: 1`, `trailer_rows: 1`, no `excluded_columns`, no `fuzzy_columns`.
- **Expected output:** Proofmark exits with code 0 (PASS). All 8 data columns match exactly between V1 and V2 output. The header and trailer rows are stripped before comparison, so the non-deterministic `{timestamp}` in the trailer does not cause a mismatch.
- **Verification method:** Execute Proofmark with the config from FSD Section 8. Verify exit code is 0. Read the Proofmark report JSON and confirm zero mismatches across all columns. This validates both the data equivalence and the Proofmark config design (header_rows: 1, trailer_rows: 1).

### TC-23: Multiple scores per bureau per customer -- last wins for bureau, all for average
- **Traces to:** OQ-2
- **Input conditions:** Verify whether any customer has duplicate bureau entries on the same date. Per data observations, there are no duplicates [FSD Appendix A], but the design must handle it.
- **Expected output:** If duplicates exist: the `MAX(CASE WHEN LOWER(bureau) = 'equifax' THEN score END)` in SQL returns the highest score for that bureau (MAX semantics). The average includes all scores. If no duplicates exist (as observed), this test verifies the design handles the case without error.
- **Verification method:** Query `SELECT customer_id, bureau, COUNT(*) FROM datalake.credit_scores WHERE as_of = @date GROUP BY customer_id, bureau HAVING COUNT(*) > 1`. If duplicates found, verify V2 output's individual bureau column uses MAX and average uses all entries. If no duplicates, confirm the query returns zero rows and document as non-exercisable.

### TC-24: Trailer timestamp is non-deterministic and handled by Proofmark
- **Traces to:** BRD Non-Deterministic Fields
- **Input conditions:** Run V2 job and inspect the trailer line.
- **Expected output:** The trailer follows the format `CONTROL|{date}|{row_count}|{timestamp}` where `{timestamp}` is the UTC time at execution (ISO 8601). This value differs between V1 and V2 runs. The Proofmark config strips the trailer (`trailer_rows: 1`) before comparison, so this does not cause a mismatch.
- **Verification method:** Read the last line of the V2 CSV. Verify it matches the regex `^CONTROL\|\d{4}-\d{2}-\d{2}\|\d+\|.+$`. Verify the timestamp portion is a valid ISO 8601 datetime. Confirm the Proofmark config has `trailer_rows: 1` to handle this non-deterministic field [FSD Section 8].
