# AccountTypeDistribution -- Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | Accounts grouped by account_type with correct count per type |
| TC-02   | BR-2           | Percentage computed as (typeCount / totalAccounts) * 100.0 using double-precision |
| TC-03   | BR-3           | total_accounts reflects total row count across all account types |
| TC-04   | BR-4           | Branches table sourced in V1 but removed in V2 -- V2 must not source branches |
| TC-05   | BR-5           | as_of column populated from first row of accounts DataFrame |
| TC-06   | BR-6           | Empty/null accounts input produces empty DataFrame with correct schema |
| TC-07   | BR-7           | Three output rows produced for current data (Checking, Savings, Credit) |
| TC-08   | BR-8           | Trailer uses END prefix, not TRAILER |
| TC-09   | BR-1, BR-2     | Percentage values sum to approximately 100% across all rows |
| TC-10   | BR-6           | Weekend date produces zero data rows and empty output |
| TC-11   | BR-2           | Floating-point percentage is unrounded double (no truncation or rounding applied) |
| TC-12   | --             | Output column order matches spec exactly |
| TC-13   | --             | CSV header row present with correct column names |
| TC-14   | --             | Line endings are LF (not CRLF) |
| TC-15   | --             | Overwrite mode: only last effective date's output persists in file |
| TC-16   | --             | Proofmark strict comparison (no FUZZY or EXCLUDED columns) |
| TC-17   | --             | Row ordering non-determinism between V1 and V2 |
| TC-18   | BR-5           | as_of formatted as MM/dd/yyyy (DateOnly rendering) |
| TC-19   | BR-6           | Null accounts DataFrame (not just empty) handled gracefully |

## Test Cases

### TC-01: Group by account_type with correct count
- **Traces to:** BR-1
- **Input conditions:** Accounts DataFrame for a weekday effective date (e.g., 2024-10-01) containing rows with account_type values of Checking, Savings, and Credit.
- **Expected output:** One output row per distinct account_type. The `account_count` column for each row equals the number of input rows with that account_type.
- **Verification method:** Run V2 job for a single weekday. Count rows in V2 output. Compare each account_type's `account_count` against a direct SQL count: `SELECT account_type, COUNT(*) FROM datalake.accounts WHERE as_of = '2024-10-01' GROUP BY account_type`.

### TC-02: Double-precision percentage calculation
- **Traces to:** BR-2
- **Input conditions:** Same weekday input as TC-01. With 3 account types the percentages will involve repeating decimals (e.g., if evenly distributed: 33.333...).
- **Expected output:** Each row's `percentage` = `(account_count / total_accounts) * 100.0`, computed as IEEE 754 double. For example, if Checking has 500 of 1500 total accounts, percentage = 33.33333333333333 (not 33.33).
- **Verification method:** For each output row, recompute `(double)account_count / total_accounts * 100.0` in C# or Python and compare bit-for-bit against V2 output. V1 uses identical `(double)typeCount / totalAccounts * 100.0` so results must match exactly.

### TC-03: total_accounts is the total row count
- **Traces to:** BR-3
- **Input conditions:** Accounts DataFrame with N total rows across all types.
- **Expected output:** Every output row has `total_accounts` = N. This value is the same for all rows -- it is the total count, not the per-type count.
- **Verification method:** Verify `total_accounts` in every output row matches `SELECT COUNT(*) FROM datalake.accounts WHERE as_of = '2024-10-01'`. Confirm all rows share the same `total_accounts` value.

### TC-04: Branches table not sourced in V2
- **Traces to:** BR-4
- **Input conditions:** V2 job config.
- **Expected output:** The V2 job config contains only one DataSourcing module (for `accounts`). No DataSourcing module for `branches` is present.
- **Verification method:** Inspect V2 job config JSON. Confirm only `accounts` table appears in DataSourcing modules. The V1 BRD notes branches were sourced but unused -- V2 eliminates this (AP1).

### TC-05: as_of from first row of accounts
- **Traces to:** BR-5
- **Input conditions:** Accounts DataFrame for effective date 2024-10-01. The `as_of` value on the first row will be the effective date.
- **Expected output:** All output rows have `as_of` set to the same value: the as_of from accounts.Rows[0]. For a single-day run this is the effective date itself.
- **Verification method:** Confirm every output row's `as_of` column matches the effective date used in the run. Verify all rows share the identical as_of value.

### TC-06: Empty accounts produces empty output with correct schema
- **Traces to:** BR-6
- **Input conditions:** Effective date is a weekend (e.g., 2024-10-05, Saturday) where datalake.accounts has no data.
- **Expected output:** The External module produces a DataFrame with zero rows but columns `["account_type", "account_count", "total_accounts", "percentage", "as_of"]`. The CSV file contains only a header row plus the END trailer.
- **Verification method:** Run V2 for 2024-10-05. Inspect the output CSV. Confirm: (1) header row present, (2) zero data rows, (3) trailer is `END|0`. Compare against V1 output for the same date -- both should be empty.

### TC-07: Three output rows for current data
- **Traces to:** BR-7
- **Input conditions:** Weekday effective date with all three account types present (Checking, Savings, Credit).
- **Expected output:** Exactly 3 data rows in the output, one per account_type.
- **Verification method:** Run V2 for a weekday date. Count output rows (excluding header and trailer). Confirm count = 3. Cross-reference with `SELECT DISTINCT account_type FROM datalake.accounts WHERE as_of = '2024-10-01'` to confirm 3 distinct types exist.

### TC-08: END trailer format
- **Traces to:** BR-8
- **Input conditions:** Any weekday effective date producing 3 output rows.
- **Expected output:** The last line of the CSV is `END|3` (using `END` prefix, pipe separator, and row count = 3). Not `TRAILER|3`.
- **Verification method:** Read the final line of the output CSV file. Verify it matches the pattern `END|{N}` where N is the number of data rows (excluding header and trailer).

### TC-09: Percentages sum to approximately 100%
- **Traces to:** BR-1, BR-2
- **Input conditions:** Any weekday effective date with data.
- **Expected output:** The sum of all `percentage` values across output rows equals approximately 100.0 (within floating-point tolerance, e.g., 99.99999999999999 or 100.0).
- **Verification method:** Parse all percentage values from V2 output, sum them, and verify the result is within 1e-10 of 100.0.

### TC-10: Weekend date produces zero-row output
- **Traces to:** BR-6
- **Input conditions:** Run for Saturday 2024-10-05 and Sunday 2024-10-06.
- **Expected output:** For each weekend date, the output CSV contains a header row and `END|0` trailer, with zero data rows between them.
- **Verification method:** Run V2 for both weekend dates. Inspect each output file. Verify header, zero data rows, and trailer `END|0`. Since write mode is Overwrite, the file is replaced each run -- only the last run's output persists.

### TC-11: Percentage is unrounded double
- **Traces to:** BR-2
- **Input conditions:** A weekday where at least one account type does not divide evenly into the total (which is virtually guaranteed with 3 types).
- **Expected output:** The percentage column contains full double-precision values such as `33.333333333333336`, NOT rounded values like `33.33` or `33.3`.
- **Verification method:** Parse percentage values from V2 output. Verify they contain more than 2 decimal places. Confirm no rounding function is applied. Compare against V1 output for the same date.

### TC-12: Output column order
- **Traces to:** FSD Section 4
- **Input conditions:** Any run that produces data.
- **Expected output:** The CSV header row lists columns in this exact order: `account_type,account_count,total_accounts,percentage,as_of`.
- **Verification method:** Read the first line of the output CSV. Split by comma. Verify the order matches exactly: `["account_type", "account_count", "total_accounts", "percentage", "as_of"]`.

### TC-13: CSV header row present
- **Traces to:** BRD Writer Configuration (includeHeader: true)
- **Input conditions:** Any V2 run.
- **Expected output:** The first line of the CSV is the header: `account_type,account_count,total_accounts,percentage,as_of`.
- **Verification method:** Read the first line of the output CSV. Confirm it matches the expected header string exactly.

### TC-14: LF line endings
- **Traces to:** BRD Writer Configuration (lineEnding: LF)
- **Input conditions:** Any V2 run that produces output.
- **Expected output:** All line endings in the output file are `\n` (LF), NOT `\r\n` (CRLF).
- **Verification method:** Read the raw bytes of the output file. Search for `\r` (0x0D). Confirm zero occurrences.

### TC-15: Overwrite mode -- only last date persists
- **Traces to:** BRD Write Mode Implications
- **Input conditions:** Run V2 for two consecutive weekdays (e.g., 2024-10-01, then 2024-10-02). The executor auto-advances, processing each day sequentially.
- **Expected output:** After both runs complete, the output CSV contains only 2024-10-02's data (the second day overwrites the first). The file has exactly 1 header + 3 data rows + 1 trailer.
- **Verification method:** After the full run, read the output file. Verify the `as_of` column values all correspond to 2024-10-02 (or whichever was the last effective date processed). Verify no 2024-10-01 data remains.

### TC-16: Proofmark strict comparison -- no FUZZY or EXCLUDED columns
- **Traces to:** FSD Section 8
- **Input conditions:** V1 and V2 output for the same effective date.
- **Expected output:** All columns are compared strictly (no fuzzy tolerance, no exclusions). Proofmark config has `header_rows: 1`, `trailer_rows: 1`, `threshold: 100.0`, and no `columns` overrides.
- **Verification method:** Run Proofmark with the FSD-specified config against V1 and V2 outputs for the same date. Expect 100% match. If row ordering causes failure, document it as a known issue per FSD Risk Register.

### TC-17: Row ordering non-determinism
- **Traces to:** BRD Edge Cases, FSD Risk Register
- **Input conditions:** V1 output uses Dictionary iteration (non-deterministic order). V2 uses LINQ GroupBy (also non-deterministic order).
- **Expected output:** Both V1 and V2 should produce the same 3 rows, but potentially in different order.
- **Verification method:** Sort both V1 and V2 output rows by `account_type` before comparing. If Proofmark does not support order-independent comparison, confirm whether V2 needs an explicit `.OrderBy(account_type)` added to the External module (per FSD Risk Register mitigation).

### TC-18: as_of formatted as MM/dd/yyyy
- **Traces to:** BR-5, FSD Section 4
- **Input conditions:** Run for 2024-10-01.
- **Expected output:** The `as_of` column in the CSV output shows `10/01/2024` (MM/dd/yyyy format), matching CsvFileWriter's rendering of DateOnly objects.
- **Verification method:** Read the output CSV. Parse the `as_of` column value. Verify it matches the format `MM/dd/yyyy`. Compare against V1 output for the same date.

### TC-19: Null accounts DataFrame handled gracefully
- **Traces to:** BR-6
- **Input conditions:** The accounts DataFrame is null in shared state (e.g., DataSourcing returns null rather than an empty DataFrame due to a connectivity issue or data gap).
- **Expected output:** Same as TC-06: empty DataFrame with correct schema produced, CSV has header + `END|0` trailer, no crash or unhandled exception.
- **Verification method:** This is a defensive code path. Verify by code inspection of the V2 External module that the null check is present: `if (accounts == null || accounts.Count == 0)`. If testable at runtime, mock a null accounts entry in shared state and confirm graceful handling.
