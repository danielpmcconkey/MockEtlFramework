# TransactionAnomalyFlags -- Test Plan

**Job:** TransactionAnomalyFlagsV2
**BRD:** `POC3/brd/transaction_anomaly_flags_brd.md`
**FSD:** `POC3/fsd/transaction_anomaly_flags_fsd.md`

---

## Test Cases

### Happy Path

| ID | Description | Expected Behavior | BRD Ref | FSD Ref |
|----|-------------|-------------------|---------|---------|
| TAF-001 | Per-account mean and stddev computed across all transaction amounts for the account | Output `account_mean` and `account_stddev` reflect the population mean and population standard deviation of all amounts for the given `account_id` in the DataFrame | BR-2, BR-3 | Sec 4, Step 3 |
| TAF-002 | Deviation factor formula | `deviation_factor` = `|amount - account_mean| / account_stddev`, rounded to 2dp | BR-4 | Sec 4, Step 4 |
| TAF-003 | Anomaly threshold -- strict greater than 3.0 | Only transactions with `deviation_factor > 3.0` appear in output. Transactions with `deviation_factor == 3.0` are excluded. | BR-5 | Sec 4, Step 4 |
| TAF-004 | Customer ID resolved via accounts lookup | `customer_id` in output matches `accounts.customer_id` for the transaction's `account_id` | BR-1 | Sec 4, Step 1 |
| TAF-005 | Output schema column order | Columns appear in order: `transaction_id`, `account_id`, `customer_id`, `amount`, `account_mean`, `account_stddev`, `deviation_factor`, `as_of` | BRD Output Schema | FSD Appendix: Output Schema |
| TAF-006 | Banker's rounding on all numeric fields | `amount`, `account_mean`, `account_stddev`, and `deviation_factor` are rounded to 2 decimal places using `MidpointRounding.ToEven` | BR-7 | Sec 6, W5 |
| TAF-007 | Mixed decimal/double stddev computation | Stddev computed via V1's exact path: subtract in decimal, cast to double for squaring, average in double, `Math.Sqrt` in double, cast back to decimal. IEEE 754 artifacts in `account_stddev` and `deviation_factor` must match V1 bit-for-bit. | BR-8 | Sec 4, Step 3; Sec 6, W6 |
| TAF-008 | Overwrite mode -- only last date's output survives | After a multi-day run (2024-10-01 through 2024-12-31), the output file contains only the rows from the final effective date's execution | BRD Write Mode | FSD Sec 5 |
| TAF-009 | Row ordering matches V1 | Output rows appear in the same order as V1 (iteration order of transactions exceeding the threshold, no explicit sort) | BRD Non-Deterministic Fields | FSD Sec 9, Open Q2 |

### Edge Cases

| ID | Description | Expected Behavior | BRD Ref | FSD Ref |
|----|-------------|-------------------|---------|---------|
| TAF-010 | Zero stddev -- all same amounts | Accounts where every transaction has an identical amount produce `stddev = 0`. All transactions for that account are excluded from output (no division by zero). | BR-6, Edge Case 1-2 | Sec 4, Step 4 |
| TAF-011 | Single transaction per account | An account with exactly one transaction has population stddev = 0, so it is excluded entirely. | Edge Case 2 | Sec 4, Step 4 |
| TAF-012 | Unresolvable customer_id | If a transaction's `account_id` is not found in the `accounts` table, `customer_id` defaults to `0` | BR-12 | Sec 4, Step 4 |
| TAF-013 | Empty transactions input | If the `transactions` DataFrame is null or empty, output is an empty CSV (header only, no data rows) | BR-11 | Sec 4 |
| TAF-014 | Empty accounts input | If the `accounts` DataFrame is null or empty, output is empty (no customer_id resolution possible, but also no transactions to flag unless transactions is also empty) | BR-11 | Sec 4 |
| TAF-015 | Cross-date baseline | Statistics are computed across ALL transactions in the DataFrame regardless of `as_of` date. If the date range spans multiple days, the statistical baseline includes cross-date data. | Edge Case 5 | Sec 9, Open Q1 |
| TAF-016 | Banker's rounding at midpoint | Values at exactly 0.5 midpoints use ToEven rounding (e.g., 2.345 rounds to 2.34, 2.355 rounds to 2.36). Verify V2 matches V1. | BR-7, Edge Case 4 | Sec 6, W5 |

### Anti-Pattern Elimination Verification

| ID | Description | Expected Behavior | Anti-Pattern | FSD Ref |
|----|-------------|-------------------|-------------|---------|
| TAF-020 | Dead-end `customers` table removed (AP1) | V2 job config does NOT include a DataSourcing module for `datalake.customers`. Output is unaffected because `customers` was never used in V1 output. | AP1 | Sec 3 (customers REMOVED) |
| TAF-021 | Unused `txn_type` column removed (AP4) | V2 DataSourcing for `transactions` sources only `transaction_id`, `account_id`, `amount` -- no `txn_type`. Output is unaffected because `txn_type` was never in V1 output schema. | AP4 | Sec 3 (transactions) |
| TAF-022 | Magic threshold replaced with named constant (AP7) | V2 External module uses a named constant (e.g., `const decimal DeviationThreshold = 3.0m`) instead of a hardcoded `3.0m` literal. Output value is identical. | AP7 | Sec 7 |
| TAF-023 | Per-account collection uses LINQ GroupBy (AP6) | V2 replaces V1's manual dictionary-building loop with LINQ `GroupBy`/`ToDictionary` for per-account amount collection. Output is identical. | AP6 | Sec 7 |
| TAF-024 | Tier 2 External justified | V2 uses Tier 2 (DataSourcing + External + Writer) because SQLite lacks SQRT and cannot replicate V1's mixed-precision decimal/double path. External handles ONLY statistical computation and flagging. | AP3 (verified not applicable) | Sec 2 |

### Writer Config Verification

| ID | Description | Expected Behavior | BRD Ref | FSD Ref |
|----|-------------|-------------------|---------|---------|
| TAF-030 | Writer type is CsvFileWriter | V2 uses CsvFileWriter, matching V1 | BRD Output Type | FSD Sec 5 |
| TAF-031 | includeHeader = true | Output CSV has a header row: `transaction_id,account_id,customer_id,amount,account_mean,account_stddev,deviation_factor,as_of` | BRD Writer Config | FSD Sec 5 |
| TAF-032 | writeMode = Overwrite | Each run replaces the prior file. Multi-day run output contains only the last day's data. | BRD Writer Config | FSD Sec 5 |
| TAF-033 | lineEnding = LF | All line endings in the output file are LF (`\n`), not CRLF | BRD Writer Config | FSD Sec 5 |
| TAF-034 | No trailer | No trailer row is appended to the output file | BRD Writer Config | FSD Sec 5 |
| TAF-035 | Output path | V2 writes to `Output/double_secret_curated/transaction_anomaly_flags.csv` | -- | FSD Sec 5, Appendix |

### Proofmark Comparison Expectations

| ID | Description | Expected Behavior | FSD Ref |
|----|-------------|-------------------|---------|
| TAF-040 | Proofmark config: reader = csv | Config uses `reader: csv` for CSV output | Sec 8 |
| TAF-041 | Proofmark config: header_rows = 1 | Config specifies `header_rows: 1` (includeHeader = true) | Sec 8 |
| TAF-042 | Proofmark config: trailer_rows = 0 | Config specifies `trailer_rows: 0` (no trailer) | Sec 8 |
| TAF-043 | Proofmark config: threshold = 100.0 | Strict match required -- all fields deterministic | Sec 8 |
| TAF-044 | Proofmark config: no EXCLUDED columns | No non-deterministic fields; no columns excluded | Sec 8 |
| TAF-045 | Proofmark config: no FUZZY columns (start strict) | Start with no FUZZY overrides. If `account_stddev` or `deviation_factor` show epsilon-level differences due to IEEE 754 path differences, add FUZZY with tight tolerance as fallback. | Sec 8 |
| TAF-046 | Full Proofmark pass (exit code 0) | After running V1 and V2 for the full date range (2024-10-01 through 2024-12-31), Proofmark comparison of `Output/curated/transaction_anomaly_flags.csv` vs `Output/double_secret_curated/transaction_anomaly_flags.csv` returns exit code 0. | Sec 8 |

---

## Risk Notes

- **Highest-risk columns for comparison:** `account_stddev` and `deviation_factor`. These pass through double-precision arithmetic. V2 must replicate the exact C# expression path from V1 to produce bit-identical IEEE 754 results. If Proofmark fails on these columns, the resolution should check whether the `decimal` -> `double` -> `decimal` conversion path differs subtly (e.g., operator precedence, intermediate rounding).
- **Row ordering:** Neither V1 nor V2 applies an explicit sort. If Proofmark fails on row order, adding `OrderBy` on `transaction_id` or `(as_of, transaction_id)` is a safe resolution (FSD Open Q2).
