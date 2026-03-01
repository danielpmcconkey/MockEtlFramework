# TransactionAnomalyFlags — Business Requirements Document

## Overview
Detects anomalous transactions by computing per-account statistical baselines (mean and standard deviation of transaction amounts) and flagging individual transactions that deviate more than 3 standard deviations from the account mean.

## Output Type
CsvFileWriter

## Writer Configuration
- **outputFile**: `Output/curated/transaction_anomaly_flags.csv`
- **includeHeader**: true
- **trailerFormat**: None (no trailer)
- **writeMode**: Overwrite
- **lineEnding**: LF

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.transactions | transaction_id, account_id, txn_type, amount | Effective date range via executor; filtered by deviation > 3.0 stddev | [transaction_anomaly_flags.json:4-11] |
| datalake.accounts | account_id, customer_id | Effective date range via executor; used for customer_id resolution | [transaction_anomaly_flags.json:12-18] |
| datalake.customers | id, first_name, last_name | Effective date range via executor | [transaction_anomaly_flags.json:19-25] |

### Source Table Schemas (from database)

**transactions**: transaction_id (integer), account_id (integer), txn_timestamp (timestamp), txn_type (varchar), amount (numeric), description (varchar), as_of (date)

**accounts**: account_id (integer), customer_id (integer), account_type (varchar), account_status (varchar), open_date (date), current_balance (numeric), interest_rate (numeric), credit_limit (numeric), apr (numeric), as_of (date)

**customers**: id (integer), prefix (varchar), first_name (varchar), last_name (varchar), sort_name (varchar), suffix (varchar), birthdate (date), as_of (date)

## Business Rules

BR-1: Customer ID is resolved via account-to-customer lookup: transaction.account_id -> accounts.customer_id.
- Confidence: HIGH
- Evidence: [TransactionAnomalyFlagger.cs:27-33] — builds `accountToCustomer` dictionary

BR-2: Per-account statistics are computed across ALL transaction amounts for each account_id: mean (average) and standard deviation (population stddev).
- Confidence: HIGH
- Evidence: [TransactionAnomalyFlagger.cs:36-49] — collects all amounts per account; [TransactionAnomalyFlagger.cs:53-59] — computes mean and stddev

BR-3: Standard deviation uses population variance (divides by N, not N-1) via `amounts.Select(...).Average()`.
- Confidence: HIGH
- Evidence: [TransactionAnomalyFlagger.cs:57] — `.Average()` on squared deviations is population variance

BR-4: The deviation factor is computed as: `|amount - mean| / stddev`.
- Confidence: HIGH
- Evidence: [TransactionAnomalyFlagger.cs:71] — `Math.Abs(amount - mean) / stddev`

BR-5: A transaction is flagged as anomalous if its deviation factor is strictly greater than 3.0.
- Confidence: HIGH
- Evidence: [TransactionAnomalyFlagger.cs:74] — `if (deviationFactor > 3.0m)`

BR-6: If an account's standard deviation is 0 (all transactions have the same amount), ALL transactions for that account are skipped — no anomalies are flagged.
- Confidence: HIGH
- Evidence: [TransactionAnomalyFlagger.cs:69] — `if (stddev == 0m) continue;`

BR-7: Banker's rounding (MidpointRounding.ToEven) is applied to amount, account_mean, account_stddev, and deviation_factor, all rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [TransactionAnomalyFlagger.cs:84-87] — `Math.Round(..., 2, MidpointRounding.ToEven)` on all four fields

BR-8: The standard deviation computation mixes `decimal` and `double` types: amounts are `decimal`, but variance computation casts to `double` for the squared difference, then back to `decimal` via `Math.Sqrt`.
- Confidence: HIGH
- Evidence: [TransactionAnomalyFlagger.cs:57-58] — `(double)(a - (decimal)mean) * (double)(a - (decimal)mean)` then `(decimal)Math.Sqrt(variance)`

BR-9: The `customers` DataFrame is sourced but never directly used in output columns. Customer names (first_name, last_name) are NOT in the output schema despite being sourced.
- Confidence: HIGH
- Evidence: [TransactionAnomalyFlagger.cs:10-14] — output columns do not include first_name or last_name; [TransactionAnomalyFlagger.cs:18] — customers is loaded but only used to verify non-null/non-empty (not in actual output)

BR-10: Wait — actually customers IS used: customer_id is resolved from accounts, not customers. The customers DataFrame is loaded and checked for null/empty but its data is never read. It's a dead-end source.
- Confidence: HIGH
- Evidence: [TransactionAnomalyFlagger.cs:18-20] — customers is null-checked but never iterated; customer_id comes from accountToCustomer dictionary

BR-11: If transactions or accounts DataFrame is null/empty, empty output is produced.
- Confidence: HIGH
- Evidence: [TransactionAnomalyFlagger.cs:20-24]

BR-12: If customer_id cannot be resolved (account_id not in accounts), it defaults to 0.
- Confidence: HIGH
- Evidence: [TransactionAnomalyFlagger.cs:76] — `accountToCustomer.GetValueOrDefault(accountId, 0)`

BR-13: The `txn_type` column is sourced from transactions but not included in the output schema.
- Confidence: HIGH
- Evidence: [transaction_anomaly_flags.json:10] — `txn_type` in DataSourcing columns; [TransactionAnomalyFlagger.cs:10-14] — not in outputColumns

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| transaction_id | transactions.transaction_id | Convert.ToInt32 | [TransactionAnomalyFlagger.cs:42,81] |
| account_id | transactions.account_id | Convert.ToInt32 | [TransactionAnomalyFlagger.cs:41,82] |
| customer_id | Computed | Resolved via accounts lookup, default 0 | [TransactionAnomalyFlagger.cs:76,83] |
| amount | transactions.amount | Banker's rounding to 2 dp | [TransactionAnomalyFlagger.cs:84] |
| account_mean | Computed | Mean of all transaction amounts for the account, banker's rounding to 2 dp | [TransactionAnomalyFlagger.cs:56,85] |
| account_stddev | Computed | Population stddev of transaction amounts for the account, banker's rounding to 2 dp | [TransactionAnomalyFlagger.cs:57-58,86] |
| deviation_factor | Computed | |amount - mean| / stddev, banker's rounding to 2 dp | [TransactionAnomalyFlagger.cs:71,87] |
| as_of | transactions.as_of | Direct passthrough | [TransactionAnomalyFlagger.cs:88] |

## Non-Deterministic Fields
None identified. Output row order follows the iteration order of transactions that exceed the 3.0 deviation threshold.

## Write Mode Implications
- **Overwrite** mode: each run replaces the entire output file. Multi-day runs retain only the last effective date's output.
- Evidence: [transaction_anomaly_flags.json:35]

## Edge Cases

1. **Zero stddev**: Accounts where all transactions have exactly the same amount produce stddev = 0 and are entirely excluded from output (no division by zero).
   - Evidence: [TransactionAnomalyFlagger.cs:69]

2. **Single transaction per account**: An account with exactly one transaction has stddev = 0 (population variance of single value = 0), so it is excluded.
   - Confidence: HIGH
   - Evidence: Mathematical property + [TransactionAnomalyFlagger.cs:69]

3. **Mixed precision**: The stddev calculation involves decimal->double->decimal conversions. This may introduce floating-point precision loss.
   - Confidence: MEDIUM
   - Evidence: [TransactionAnomalyFlagger.cs:57-58]

4. **Banker's rounding**: MidpointRounding.ToEven may produce different results from standard rounding for values at exactly 0.5 midpoints (e.g., 2.345 rounds to 2.34, not 2.35).
   - Confidence: HIGH
   - Evidence: [TransactionAnomalyFlagger.cs:84-87]

5. **No date filtering within module**: Statistics are computed across ALL transaction amounts in the DataFrame (may span multiple as_of dates). This means the baseline includes cross-date data.
   - Confidence: HIGH
   - Evidence: [TransactionAnomalyFlagger.cs:38-49] — no date filter

6. **Dead-end customers**: The customers DataFrame is loaded and null-checked but never used for enrichment. Customer names do not appear in output.
   - Evidence: [TransactionAnomalyFlagger.cs:18-20]

7. **Data range**: Current transaction amounts range from 20 to 4200. With a mean of ~910, a transaction would need to deviate by > 3 stddevs to be flagged.
   - Evidence: [DB query: MIN=20, MAX=4200, AVG≈910]

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Account-to-customer lookup | [TransactionAnomalyFlagger.cs:27-33] |
| BR-2: Per-account statistics | [TransactionAnomalyFlagger.cs:36-59] |
| BR-3: Population stddev | [TransactionAnomalyFlagger.cs:57] |
| BR-4: Deviation factor formula | [TransactionAnomalyFlagger.cs:71] |
| BR-5: 3.0 threshold | [TransactionAnomalyFlagger.cs:74] |
| BR-6: Zero stddev exclusion | [TransactionAnomalyFlagger.cs:69] |
| BR-7: Banker's rounding | [TransactionAnomalyFlagger.cs:84-87] |
| BR-8: Mixed decimal/double computation | [TransactionAnomalyFlagger.cs:57-58] |
| BR-9: Dead-end customers (no output use) | [TransactionAnomalyFlagger.cs:10-14, 18] |
| BR-10: Customers not used in output | [TransactionAnomalyFlagger.cs] |
| BR-11: Empty input guard | [TransactionAnomalyFlagger.cs:20-24] |
| BR-12: Default customer_id = 0 | [TransactionAnomalyFlagger.cs:76] |
| BR-13: Unused txn_type column | [transaction_anomaly_flags.json:10], [TransactionAnomalyFlagger.cs:10-14] |

## Open Questions
1. Why is the customers DataFrame sourced but never used for enrichment? The output includes customer_id but not customer names. Was name enrichment planned but not implemented?
   - Confidence: MEDIUM
2. Should the anomaly detection use a per-date baseline (stats computed per as_of date) rather than a cross-date baseline?
   - Confidence: LOW
