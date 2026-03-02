# TransactionSizeBuckets -- Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | Five amount buckets with correct CASE WHEN boundaries |
| TC-02   | BR-2           | Aggregation groups by amount_bucket and as_of |
| TC-03   | BR-3           | txn_count is COUNT(*) per bucket per date |
| TC-04   | BR-4           | total_amount is ROUND(SUM(amount), 2) per bucket per date |
| TC-05   | BR-5           | avg_amount is ROUND(AVG(amount), 2) per bucket per date |
| TC-06   | BR-6           | Output ordered by as_of ASC, amount_bucket ASC (string sort) |
| TC-07   | BR-7, AP8      | Unused ROW_NUMBER() window function eliminated in V2 SQL |
| TC-08   | BR-8           | Bucket boundaries use half-open intervals (>= lower, < upper) |
| TC-09   | BR-9           | Negative amounts fall to ELSE '1000+' bucket |
| TC-10   | AP1            | Dead-end accounts DataSourcing entry removed |
| TC-11   | AP4            | Unused columns (transaction_id, txn_type) removed from DataSourcing |
| TC-12   | AP8            | CTE chain collapsed to single flat SELECT |
| TC-13   | Writer Config  | CsvFileWriter: Overwrite mode, LF line ending, header, no trailer |
| TC-14   | Edge Case      | No transactions for an effective date produces header-only CSV |
| TC-15   | Edge Case      | All transactions in one bucket produces single data row per date |
| TC-16   | Edge Case      | String sort order on amount_bucket is lexicographic, not numeric |
| TC-17   | Edge Case      | Multi-day auto-advance retains only final day's data (Overwrite) |
| TC-18   | Proofmark      | Proofmark comparison passes at 100% threshold with zero exclusions |

## Test Cases

### TC-01: Five amount buckets with correct CASE WHEN boundaries
- **Traces to:** BR-1
- **Input conditions:** Run V2 job for a date with transactions spanning all five amount ranges (e.g., 2024-10-01 where amounts range from 20.00 upward).
- **Expected output:** Output contains rows with `amount_bucket` values from exactly these five labels: `0-25`, `25-100`, `100-500`, `500-1000`, `1000+`. The bucket assignment logic is: amount >= 0 AND < 25 -> `0-25`; >= 25 AND < 100 -> `25-100`; >= 100 AND < 500 -> `100-500`; >= 500 AND < 1000 -> `500-1000`; everything else -> `1000+`.
- **Verification method:** Compare V2 output bucket labels against a direct SQL query of `datalake.transactions` applying the same CASE WHEN logic for the same effective date. Verify no unexpected bucket labels appear. Cross-reference with V1 output to confirm identical bucket assignments [FSD Section 4].

### TC-02: Aggregation groups by amount_bucket and as_of
- **Traces to:** BR-2
- **Input conditions:** Run V2 job for a multi-day range (e.g., 2024-10-01 through 2024-10-03, but note Overwrite means only last day survives).
- **Expected output:** Each row represents a unique combination of (amount_bucket, as_of). No duplicate (bucket, date) pairs exist. Rows are aggregates, not individual transactions.
- **Verification method:** Read V2 CSV output and verify that the combination of `amount_bucket` and `as_of` is unique per row. Compare row count to `SELECT COUNT(DISTINCT amount_bucket) FROM ...` for the final effective date.

### TC-03: txn_count is COUNT(*) per bucket per date
- **Traces to:** BR-3
- **Input conditions:** Run V2 job for a single weekday (e.g., 2024-10-01).
- **Expected output:** Each row's `txn_count` matches the count of transactions whose amount falls in that bucket for that date. Sum of all txn_count values across buckets equals total transaction count for the date.
- **Verification method:** Query `datalake.transactions` for the same date, apply the CASE WHEN bucket logic, and COUNT(*) per bucket. Compare results to V2 output row by row. Also verify: `SUM(txn_count)` across all buckets equals `SELECT COUNT(*) FROM datalake.transactions WHERE as_of = '<date>'` [FSD Section 4, SQL Design].

### TC-04: total_amount is ROUND(SUM(amount), 2) per bucket per date
- **Traces to:** BR-4
- **Input conditions:** Run V2 job for a single weekday.
- **Expected output:** Each row's `total_amount` equals `ROUND(SUM(amount), 2)` for all transactions in that bucket on that date. Rounding is standard SQLite rounding (away from zero at .5).
- **Verification method:** Independently compute `ROUND(SUM(amount), 2)` per bucket from `datalake.transactions` for the date. Compare to V2 output. Note: W5 (banker's rounding) does NOT apply to this job -- SQLite ROUND uses standard rounding here [FSD Section 6].

### TC-05: avg_amount is ROUND(AVG(amount), 2) per bucket per date
- **Traces to:** BR-5
- **Input conditions:** Run V2 job for a single weekday.
- **Expected output:** Each row's `avg_amount` equals `ROUND(AVG(amount), 2)` for all transactions in that bucket on that date.
- **Verification method:** Independently compute `ROUND(SUM(amount) * 1.0 / COUNT(*), 2)` per bucket from source data. Compare to V2 output. Confirm both V1 and V2 produce the same result since both use SQLite ROUND [FSD Section 4].

### TC-06: Output ordered by as_of ASC, amount_bucket ASC (string sort)
- **Traces to:** BR-6
- **Input conditions:** Run V2 job for a date that produces multiple buckets.
- **Expected output:** Rows are sorted first by `as_of` ascending, then by `amount_bucket` ascending using lexicographic (string) ordering. The bucket order within a date is: `0-25`, `100-500`, `1000+`, `25-100`, `500-1000` -- NOT numeric order.
- **Verification method:** Read V2 CSV and verify row order matches lexicographic sort. Compare to V1 output row order. This is also covered by TC-16 [FSD Section 4, BRD Edge Case 4].

### TC-07: Unused ROW_NUMBER() window function eliminated
- **Traces to:** BR-7, AP8
- **Input conditions:** Inspect V2 job config JSON.
- **Expected output:** V2 SQL contains no `ROW_NUMBER()` call, no `PARTITION BY`, and no `rn` alias. The V1 vestigial window function is gone.
- **Verification method:** Read the V2 job config and inspect the SQL string. Search for `ROW_NUMBER`, `PARTITION BY`, and ` rn `. None should be present. Output must still match V1 since the window function never affected results [FSD Section 4, AP8 elimination].

### TC-08: Bucket boundaries use half-open intervals
- **Traces to:** BR-8
- **Input conditions:** Run V2 job for a date where transaction amounts land exactly on bucket boundaries (e.g., amounts of exactly 25.00, 100.00, 500.00, 1000.00).
- **Expected output:** Boundary amounts are assigned to the higher bucket: amount = 25 -> `25-100`, amount = 100 -> `100-500`, amount = 500 -> `500-1000`, amount = 1000 -> `1000+`. Amount = 0 -> `0-25`.
- **Verification method:** Query `datalake.transactions` for rows with `amount IN (25.00, 100.00, 500.00, 1000.00)` on the test date. Verify V2 assigns them to the expected buckets. Compare against V1 output for the same rows [BRD Edge Case 3].

### TC-09: Negative amounts fall to ELSE '1000+' bucket
- **Traces to:** BR-9
- **Input conditions:** This is a theoretical edge case -- BRD notes current data minimum is 20.00. If test data with negative amounts were present, the CASE WHEN's first branch (`amount >= 0`) would exclude them, and they'd fall to ELSE `1000+`.
- **Expected output:** Any negative amounts would be classified as `1000+`.
- **Verification method:** Verify via SQL analysis that the CASE WHEN logic in V2 matches V1: the first branch requires `amount >= 0`, so negatives reach ELSE. Confirm with `SELECT MIN(amount) FROM datalake.transactions` that no negative amounts exist in current data. Document as a theoretical edge case validated by code inspection [BRD Edge Case 7].

### TC-10: Dead-end accounts DataSourcing entry removed (AP1)
- **Traces to:** AP1 elimination
- **Input conditions:** Inspect V2 job config JSON.
- **Expected output:** V2 config contains exactly one DataSourcing module entry: `table: "transactions"`. There is no DataSourcing entry for `accounts`.
- **Verification method:** Read the V2 job config and verify the modules array contains only one DataSourcing entry. V1 sourced `accounts` (account_id, customer_id, account_type) but the SQL never referenced it [FSD Section 3, AP1].

### TC-11: Unused columns removed from DataSourcing (AP4)
- **Traces to:** AP4 elimination
- **Input conditions:** Inspect V2 job config JSON.
- **Expected output:** V2 DataSourcing `columns` array is `["account_id", "amount"]`. The columns `transaction_id` and `txn_type` are absent. (`as_of` is auto-appended by the framework and does not appear in the columns array.)
- **Verification method:** Read the V2 job config and verify the columns list. V1 sourced `transaction_id` and `txn_type` but neither was used in aggregation or output [FSD Section 3, AP4].

### TC-12: CTE chain collapsed to single flat SELECT (AP8)
- **Traces to:** AP8 elimination
- **Input conditions:** Inspect V2 job config SQL.
- **Expected output:** V2 SQL is a single SELECT statement with inline CASE WHEN in SELECT and GROUP BY. No CTEs (`WITH ... AS`), no `txn_detail`, no `bucketed`, no `summary` aliases.
- **Verification method:** Read V2 job config SQL string. Verify it does not contain `WITH`, `txn_detail`, `bucketed`, or `summary`. Verify the CASE WHEN bucket logic, aggregation functions, GROUP BY, and ORDER BY are all in a single query. Output must be identical to V1 despite the structural simplification [FSD Section 4, AP8].

### TC-13: Writer configuration matches V1 behavior
- **Traces to:** BRD Writer Configuration
- **Input conditions:** Inspect V2 job config and run the job.
- **Expected output:** V2 config specifies: `type: CsvFileWriter`, `source: "size_buckets"`, `includeHeader: true`, `writeMode: "Overwrite"`, `lineEnding: "LF"`, no `trailerFormat`. Output file is at `Output/double_secret_curated/transaction_size_buckets.csv`.
- **Verification method:** Read V2 job config and verify all writer parameters. Run the job and confirm: (1) output file exists at the V2 path, (2) first line is a header row with column names, (3) no trailer line exists at end of file, (4) file uses LF line endings (no CR), (5) file is completely replaced on each run [FSD Section 5].

### TC-14: No transactions for effective date produces header-only CSV
- **Traces to:** BRD Edge Case 1
- **Input conditions:** Run V2 job for a date where `datalake.transactions` has zero rows (e.g., a weekend date if no weekend data exists).
- **Expected output:** Output CSV contains only the header line: `amount_bucket,txn_count,total_amount,avg_amount,as_of`. Zero data rows.
- **Verification method:** Run V2 for the empty date. Read the output CSV and confirm it has exactly 1 line (the header). Verify the same behavior in V1 output. GROUP BY on zero rows produces zero output rows; CsvFileWriter emits header + 0 data rows [FSD Section 4].

### TC-15: All transactions in one bucket produces single data row
- **Traces to:** BRD Edge Case 2
- **Input conditions:** If a date exists where all transaction amounts fall in the same bucket range, or verify via analysis.
- **Expected output:** Output contains only one data row for that date (plus the header). Buckets with zero transactions do NOT appear -- there are no zero-count filler rows.
- **Verification method:** Query source data to find a date (if one exists) where all amounts are in one range. If not available, verify by code analysis that GROUP BY does not produce rows for empty buckets (SQL GROUP BY naturally omits groups with no matching rows) [BRD Edge Case 2].

### TC-16: String sort order on amount_bucket is lexicographic
- **Traces to:** BRD Edge Case 4, BR-6
- **Input conditions:** Run V2 job for a date with all 5 buckets populated.
- **Expected output:** Within a single `as_of` date, rows appear in this order: `0-25`, `100-500`, `1000+`, `25-100`, `500-1000`. This is lexicographic string sort, NOT numeric sort (which would be `0-25`, `25-100`, `100-500`, `500-1000`, `1000+`).
- **Verification method:** Read V2 CSV and verify the bucket ordering within a date. Compare to V1 output to confirm the same non-numeric ordering. This is a V1 quirk preserved for output equivalence [FSD Section 4, Open Question 2].

### TC-17: Multi-day auto-advance retains only final day (Overwrite)
- **Traces to:** BRD Write Mode Implications
- **Input conditions:** Run V2 job for a multi-day range (e.g., 2024-10-01 through 2024-10-03).
- **Expected output:** After the auto-advance completes, the output CSV contains only the final day's data (2024-10-03). Data from 2024-10-01 and 2024-10-02 is gone, overwritten by each subsequent run.
- **Verification method:** Run V2 for the date range. Read the output CSV and verify all `as_of` values equal the final date. Confirm only one date's worth of data is present. This matches V1 Overwrite behavior [FSD Section 5].

### TC-18: Proofmark comparison passes at 100% threshold
- **Traces to:** FSD Proofmark Config
- **Input conditions:** Run both V1 and V2 jobs for the full effective date range. Run Proofmark with the designed config: `reader: csv`, `threshold: 100.0`, `header_rows: 1`, `trailer_rows: 0`.
- **Expected output:** Proofmark reports PASS. All rows match exactly between `Output/curated/transaction_size_buckets.csv` and `Output/double_secret_curated/transaction_size_buckets.csv`. Zero mismatches, zero exclusions, zero fuzzy columns.
- **Verification method:** Execute Proofmark with the config from FSD Section 8. Verify exit code is 0. Confirm the report shows 100% match across all 5 columns (`amount_bucket`, `txn_count`, `total_amount`, `avg_amount`, `as_of`). No wrinkles (W-codes) apply to this job, so exact match is expected with no special handling [FSD Sections 6, 8].
