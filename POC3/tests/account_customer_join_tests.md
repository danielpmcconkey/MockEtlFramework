# AccountCustomerJoin -- Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | Accounts joined to customers by customer_id (LEFT JOIN) |
| TC-02   | BR-2           | Addresses table is not sourced in V2 (dead-end eliminated) |
| TC-03   | BR-3           | Missing customer lookup defaults to empty strings for first_name and last_name |
| TC-04   | BR-4           | Empty accounts or customers input produces empty output |
| TC-05   | BR-5           | Every account row produces exactly one output row (left-join semantics) |
| TC-06   | BR-6           | as_of column sourced from accounts, not customers |
| TC-07   | Writer Config   | Parquet output uses Overwrite mode with 2 part files |
| TC-08   | Output Schema   | Output contains exactly 8 columns in correct order |
| TC-09   | Edge Case       | Weekend date produces empty output (overwrites prior data) |
| TC-10   | Edge Case       | NULL handling for customer name fields |
| TC-11   | Edge Case       | Overwrite mode: multi-day run retains only the last day's data |
| TC-12   | Edge Case       | Month-end boundary produces normal output |
| TC-13   | Edge Case       | Customer with multiple as_of dates in join |
| TC-14   | FSD: Tier 1     | V2 SQL LEFT JOIN produces identical output to V1 dictionary lookup |
| TC-15   | Proofmark       | Proofmark comparison passes with zero exclusions and zero fuzzy columns |
| TC-16   | FSD: SQL Design | COALESCE correctly replicates GetValueOrDefault empty-string behavior |
| TC-17   | Edge Case       | Zero-row scenario when accounts has data but customers is empty |

## Test Cases

### TC-01: Accounts joined to customers by customer_id
- **Traces to:** BR-1
- **Input conditions:** Run V2 job for a single weekday (e.g., 2024-10-01) where both accounts and customers data exist.
- **Expected output:** Each output row contains account fields enriched with the matching customer's `first_name` and `last_name`. The join key is `accounts.customer_id = customers.id`. Every account that has a matching customer shows the correct name.
- **Verification method:** Query `datalake.accounts` and `datalake.customers` for the same date and perform a manual LEFT JOIN by customer_id. Compare the result against V2 Parquet output. Verify names match for all joined rows. The FSD implements this as `LEFT JOIN customers c ON a.customer_id = c.id AND a.as_of = c.as_of` [FSD Section 5].

### TC-02: Addresses table is not sourced in V2
- **Traces to:** BR-2 (AP1 elimination)
- **Input conditions:** Inspect the V2 job config JSON.
- **Expected output:** The V2 config contains exactly two DataSourcing module entries: one for `accounts` and one for `customers`. There is no DataSourcing entry for `addresses`.
- **Verification method:** Read `account_customer_join_v2.json` and verify the modules array. Confirm no module references `addresses` table. This confirms AP1 (dead-end sourcing) is eliminated per FSD Section 3.

### TC-03: Missing customer defaults to empty strings
- **Traces to:** BR-3
- **Input conditions:** Identify an account row whose `customer_id` does not exist in the `customers` table for the same `as_of` date (or manufacture this scenario by understanding the data). Run V2 job for that date.
- **Expected output:** The output row for that account has `first_name = ""` (empty string) and `last_name = ""` (empty string). The row is still present in the output (not filtered out).
- **Verification method:** Query for accounts with customer_ids not in customers for the test date. Check the V2 output for those account_ids and verify both name fields are empty strings (not NULL, not "Unknown", not missing). The FSD uses `COALESCE(c.first_name, '') AS first_name` to replicate V1's `GetValueOrDefault(customerId, ("", ""))` [FSD Section 5]. Note: V1 uses empty string default, and COALESCE with `''` replicates this -- but COALESCE only fires on NULL (from the LEFT JOIN non-match), which is the correct semantic equivalent.

### TC-04: Empty input produces empty output
- **Traces to:** BR-4
- **Input conditions:** Run V2 job for a date where both accounts and customers have zero rows (e.g., a weekend date like 2024-10-05). Alternatively, if the data supports it, find a date where accounts is empty.
- **Expected output:** Output contains zero data rows. Since writeMode is Overwrite, the output directory is written with empty Parquet part files (or the prior output is replaced with empty content).
- **Verification method:** Read V2 Parquet output and verify zero rows. Note the FSD Section 5 discusses a theoretical divergence when accounts has rows but customers is empty (Transformation module may fail because the empty customers table is not registered in SQLite). In practice, both tables share effective dates, so if one is empty the other will be too. If this edge case is encountered during Proofmark comparison, it may require a Tier 2 resolution [FSD Section 5, empty DataFrame handling].

### TC-05: Every account row produces exactly one output row
- **Traces to:** BR-5
- **Input conditions:** Run V2 job for a single weekday. Count rows in `datalake.accounts` for that date.
- **Expected output:** The V2 output row count equals the accounts row count for that date. No account rows are dropped (even those without customer matches) and no account rows are duplicated.
- **Verification method:** Compare `SELECT COUNT(*) FROM datalake.accounts WHERE as_of = '<date>'` against the row count in V2 Parquet output. The counts must match exactly. This validates the left-join semantics: every account appears once regardless of customer match status [BRD BR-5, FSD Section 5 FROM clause].

### TC-06: as_of column sourced from accounts
- **Traces to:** BR-6
- **Input conditions:** Run V2 job for a single weekday. The SQL uses `a.as_of` (from accounts alias) not `c.as_of`.
- **Expected output:** The `as_of` value in every output row matches the accounts table's `as_of` for that row's account_id.
- **Verification method:** Inspect the V2 SQL in the job config to confirm it selects `a.as_of`. Read V2 Parquet output and verify all `as_of` values match the effective date. In practice, both tables share the same effective dates, so this is equivalent, but the specification requires the accounts-sourced value [FSD Section 5: `a.as_of`].

### TC-07: Writer configuration matches V1 (Overwrite mode, 2 parts)
- **Traces to:** BRD Writer Configuration
- **Input conditions:** Run V2 job for two consecutive weekdays (e.g., 2024-10-01 then 2024-10-02).
- **Expected output:** After the first run, output contains rows with `as_of = 2024-10-01`. After the second run, output contains ONLY rows with `as_of = 2024-10-02`. The first day's data is completely replaced (Overwrite semantics). Two part files exist in the output directory.
- **Verification method:** After first run, count rows and verify as_of values. After second run, verify only the second date's data remains. Check that `Output/double_secret_curated/account_customer_join/` contains `part-00000.parquet` and `part-00001.parquet`. Writer config must match: numParts=2, writeMode=Overwrite [FSD Section 7].

### TC-08: Output contains exactly 8 columns in correct order
- **Traces to:** BRD Output Schema
- **Input conditions:** Run V2 job for any weekday with data.
- **Expected output:** Parquet output schema contains exactly 8 columns in order: `account_id`, `customer_id`, `first_name`, `last_name`, `account_type`, `account_status`, `current_balance`, `as_of`.
- **Verification method:** Read V2 Parquet output and inspect the schema. Compare column names and order against the BRD output schema table and the FSD's output schema [FSD Section 4]. Column order is determined by the SQL SELECT clause [FSD Section 5], which must match V1's `outputColumns` list.

### TC-09: Weekend date produces empty output (overwrites prior data)
- **Traces to:** Edge Case (BRD: Weekend dates)
- **Input conditions:** Run V2 job for a weekday (confirm data exists), then run for a Saturday or Sunday.
- **Expected output:** After the weekend run, the output directory contains empty Parquet files (zero data rows). The prior weekday's data is gone because Overwrite mode replaces the entire output. This is a consequence of Overwrite semantics combined with no weekend data.
- **Verification method:** After weekday run, confirm rows exist. After weekend run, confirm zero rows. This demonstrates the BRD's noted edge case: "DataSourcing on a weekend returns empty DataFrames, producing an empty output (which overwrites any previous data)."

### TC-10: NULL handling for customer name fields
- **Traces to:** Edge Case (NULL handling)
- **Input conditions:** Query `datalake.customers` to determine if any rows have NULL `first_name` or `last_name` values. Run V2 job for a date containing such data.
- **Expected output:** If a customer has `first_name IS NULL` but is matched via the LEFT JOIN, the COALESCE in the SQL replaces it with an empty string. This matches V1's dictionary behavior where names are stored as tuples and null database values would have been read as nulls by DataSourcing. The key distinction: COALESCE fires on both (a) non-matched accounts (LEFT JOIN produces NULL) and (b) matched accounts where the customer's name column is itself NULL in the source.
- **Verification method:** Compare V1 and V2 output for rows involving customers with NULL name fields. Both should produce empty strings per COALESCE/GetValueOrDefault semantics. If V1 produces actual NULLs for matched-but-null-name customers (because it stores the raw database null in the tuple), this would be a divergence from V2's COALESCE behavior. Validate with Proofmark.

### TC-11: Overwrite mode retains only last day's data in multi-day run
- **Traces to:** Edge Case (BRD: Write Mode Implications)
- **Input conditions:** Run V2 job across a multi-day range (e.g., 2024-10-01 through 2024-10-04, which includes 4 weekdays if the executor auto-advances).
- **Expected output:** Only the last effective date's data survives in the output. For a range ending 2024-10-04, the output contains only rows with `as_of = 2024-10-04`. Prior days are overwritten by each successive run.
- **Verification method:** After the full multi-day run, read V2 Parquet output. Verify only one distinct `as_of` value exists (the final date). Count rows and compare to the single-day account count. This validates: "On multi-day gap-fill, each successive day overwrites the previous, so only the final day persists" [BRD: Write Mode Implications].

### TC-12: Month-end boundary produces normal output
- **Traces to:** Edge Case (date boundaries)
- **Input conditions:** Run V2 job for 2024-10-31 (last day of October).
- **Expected output:** Normal denormalized account-customer output. No summary rows, boundary markers, or special behavior. The FSD confirms no W3a/W3b/W3c wrinkles apply [FSD Section 3].
- **Verification method:** Verify row count equals the account count for that date. Verify no anomalous rows with aggregated values or summary labels.

### TC-13: Customer with multiple as_of dates in join
- **Traces to:** Edge Case (BRD: Duplicate customer IDs)
- **Input conditions:** This edge case applies during multi-day runs. If the executor processes a date range, both DataSourcing modules pull data for the same effective date range, and the SQL JOIN includes `a.as_of = c.as_of`.
- **Expected output:** The V2 SQL joins on both `customer_id` AND `as_of`, ensuring each account row matches only the customer row from the same date. This avoids the V1 dictionary last-write-wins problem where customer names from different dates could collide. Since Overwrite mode means only the final day survives anyway, the net effect on final output is identical.
- **Verification method:** Run V2 for a multi-day range. Verify the output (last day only due to Overwrite) contains correct customer names for that specific date. The FSD documents this design decision: "Joining on as_of in addition to the customer_id ensures that the join matches customers to accounts for the same snapshot date" [FSD Section 5, SQL Design Rationale].

### TC-14: V2 SQL LEFT JOIN produces identical output to V1 dictionary lookup
- **Traces to:** FSD Tier Justification, BR-1, BR-3, BR-5
- **Input conditions:** Run both V1 and V2 jobs for the full date range (2024-10-01 through 2024-12-31).
- **Expected output:** V2 output in `Output/double_secret_curated/account_customer_join/` matches V1 output in `Output/curated/account_customer_join/`. The V2 Tier 1 chain (DataSourcing -> Transformation SQL -> ParquetFileWriter) produces the same data as V1's External module dictionary-based approach.
- **Verification method:** Run Proofmark comparison between V1 and V2 output. Proofmark must report PASS. Since Overwrite mode is used, only the final date's output is compared. This validates the FSD's core claim that a SQL LEFT JOIN with COALESCE is functionally equivalent to V1's dictionary lookup with GetValueOrDefault [FSD Section 1].

### TC-15: Proofmark comparison with zero exclusions and zero fuzzy
- **Traces to:** FSD Proofmark Config Design
- **Input conditions:** Run Proofmark with the designed config: `reader: parquet`, `threshold: 100.0`, no `excluded_columns`, no `fuzzy_columns`.
- **Expected output:** Proofmark exits with code 0 (PASS). All 8 columns match exactly between V1 and V2 output.
- **Verification method:** Execute Proofmark with the config from FSD Section 8. Verify exit code is 0. Read the Proofmark report JSON and confirm zero mismatches across all columns. No floating-point arithmetic is performed (`current_balance` is a passthrough), so no fuzzy tolerance is needed [FSD Section 8, Fuzzy Columns: None].

### TC-16: COALESCE correctly replicates empty-string default behavior
- **Traces to:** FSD SQL Design, BR-3
- **Input conditions:** Identify accounts where customer_id has no matching customer (or run with synthetic knowledge of unmatched IDs). Run V2 job.
- **Expected output:** For unmatched accounts, the output shows `first_name = ""` and `last_name = ""` (empty strings, not NULLs). The SQL `COALESCE(c.first_name, '') AS first_name` produces an empty string when the LEFT JOIN yields NULL for `c.first_name` (no match found).
- **Verification method:** Compare V1 and V2 output for rows where the customer_id has no match. Both should show empty strings for `first_name` and `last_name`. Run the V2 SQL manually against the SQLite engine (if possible) to confirm COALESCE behavior. This is the critical behavioral equivalence test for the dictionary-to-SQL translation [FSD Section 5, SQL Design Rationale].

### TC-17: Accounts has data but customers table is empty
- **Traces to:** Edge Case (BR-4), FSD Section 5 discussion
- **Input conditions:** This scenario may not occur naturally (accounts and customers share effective dates). If it can be triggered, run V2 for a date where accounts has rows but customers has zero rows.
- **Expected output:** V1 behavior (BR-4): returns an empty DataFrame when either source is null/empty. V2 behavior: the Transformation module may fail because the `customers` SQLite table is not registered when the customers DataFrame is empty (Transformation.cs skips registration for empty DataFrames). This is a known potential divergence documented in the FSD.
- **Verification method:** If this scenario can be reproduced, compare V1 and V2 behavior. If V2 throws an error while V1 produces empty output, this would be a FAIL requiring resolution. The FSD notes this may need Tier 2 escalation [FSD Section 5, empty DataFrame handling]. In practice, this edge case is unlikely given shared effective dates, but it should be monitored during Proofmark comparison.
