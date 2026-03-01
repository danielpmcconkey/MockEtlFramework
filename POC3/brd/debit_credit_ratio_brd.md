# DebitCreditRatio — Business Requirements Document

## Overview
Calculates debit-to-credit ratios per account, including transaction counts and amounts for each type. Uses an External module to perform the computation with known arithmetic quirks (integer division for count ratio, double-precision for amount ratio). Output is a single Parquet file, overwritten each run.

## Output Type
ParquetFileWriter (data assembled by External module `DebitCreditRatioCalculator`)

## Writer Configuration
- **source**: `output`
- **outputDirectory**: `Output/curated/debit_credit_ratio/`
- **numParts**: 1
- **writeMode**: Overwrite

## Source Tables

| Table | Columns | Filters | Evidence |
|-------|---------|---------|----------|
| datalake.transactions | transaction_id, account_id, txn_type, amount, description | Effective date range injected by executor via shared state (DataSourcing module) | [debit_credit_ratio.json:6-10] |
| datalake.accounts | account_id, customer_id, interest_rate, credit_limit | Effective date range injected by executor via shared state (DataSourcing module) | [debit_credit_ratio.json:12-17] |

Note: The `accounts` table columns `interest_rate` and `credit_limit` are sourced but **not used** in the External module computation. Only `account_id` and `customer_id` are referenced.

## Business Rules

BR-1: Transactions are aggregated per `account_id`. Each account gets one output row.
- Confidence: HIGH
- Evidence: [DebitCreditRatioCalculator.cs:35-51] `stats` dictionary keyed by `account_id`

BR-2: `debit_count` counts transactions where `txn_type == "Debit"`. `credit_count` counts transactions where `txn_type == "Credit"`.
- Confidence: HIGH
- Evidence: [DebitCreditRatioCalculator.cs:47-50]

BR-3: `debit_credit_ratio` uses **integer division** (`debitCount / creditCount`), which truncates to zero when `debitCount < creditCount`. This is a known quirk (W4).
- Confidence: HIGH
- Evidence: [DebitCreditRatioCalculator.cs:60-61] Comment: "W4: Integer division — debit_count / credit_count (both int) -> truncates to 0"

BR-4: When `credit_count` is zero, `debit_credit_ratio` defaults to 0.
- Confidence: HIGH
- Evidence: [DebitCreditRatioCalculator.cs:61] `creditCount > 0 ? debitCount / creditCount : 0`

BR-5: `debit_amount` and `credit_amount` are summed using **double-precision arithmetic** (not decimal), which may introduce floating-point epsilon errors. This is a known quirk (W6).
- Confidence: HIGH
- Evidence: [DebitCreditRatioCalculator.cs:41] `double amount = Convert.ToDouble(row["amount"]);`; Comment: "W6: Use double arithmetic (epsilon errors)"

BR-6: `amount_ratio` = `debit_amount / credit_amount` using double division. When `credit_amount` is zero, defaults to 0.0.
- Confidence: HIGH
- Evidence: [DebitCreditRatioCalculator.cs:64] `double amountRatio = creditAmount > 0.0 ? debitAmount / creditAmount : 0.0;`

BR-7: `customer_id` is looked up from the `accounts` DataFrame. If an account has no match, `customer_id` defaults to 0.
- Confidence: HIGH
- Evidence: [DebitCreditRatioCalculator.cs:58] `accountToCustomer.GetValueOrDefault(accountId, 0)`

BR-8: `as_of` is taken from the first transaction encountered for each account (the `as_of` of whichever row initializes the stats entry).
- Confidence: HIGH
- Evidence: [DebitCreditRatioCalculator.cs:44] `stats[accountId] = (0, 0, 0.0, 0.0, row["as_of"])`

BR-9: If either the `transactions` or `accounts` DataFrame is null or empty, an empty output DataFrame is produced.
- Confidence: HIGH
- Evidence: [DebitCreditRatioCalculator.cs:19-23]

BR-10: Transactions with `txn_type` other than "Debit" or "Credit" are counted in the stats dictionary (they initialize an entry) but do not increment debit or credit counts/amounts. They contribute to `as_of` if they are the first row for that account.
- Confidence: MEDIUM
- Evidence: [DebitCreditRatioCalculator.cs:43-50] The `stats.ContainsKey` check initializes regardless of txn_type, but only Debit/Credit branches update counts. Database shows only "Debit" and "Credit" types exist.

BR-11: Output row order is **non-deterministic** — it follows the enumeration order of the `stats` dictionary, which depends on insertion order of account_ids as encountered in the transactions DataFrame.
- Confidence: MEDIUM
- Evidence: [DebitCreditRatioCalculator.cs:53-78] No explicit sorting; iterates `stats` dictionary.

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| account_id | transactions.account_id | Direct (aggregation key) | [DebitCreditRatioCalculator.cs:68] |
| customer_id | accounts (via lookup) | Lookup from account_id -> customer_id; default 0 if missing | [DebitCreditRatioCalculator.cs:69] |
| debit_count | transactions | Count where txn_type == "Debit" | [DebitCreditRatioCalculator.cs:70] |
| credit_count | transactions | Count where txn_type == "Credit" | [DebitCreditRatioCalculator.cs:71] |
| debit_credit_ratio | Computed | Integer division: debit_count / credit_count (truncated); 0 if credit_count=0 | [DebitCreditRatioCalculator.cs:72] |
| debit_amount | transactions.amount | Sum of amount where txn_type == "Debit" (double precision) | [DebitCreditRatioCalculator.cs:73] |
| credit_amount | transactions.amount | Sum of amount where txn_type == "Credit" (double precision) | [DebitCreditRatioCalculator.cs:74] |
| amount_ratio | Computed | Double division: debit_amount / credit_amount; 0.0 if credit_amount=0 | [DebitCreditRatioCalculator.cs:75] |
| as_of | transactions.as_of | From first transaction encountered for the account | [DebitCreditRatioCalculator.cs:76] |

## Non-Deterministic Fields
- **Row ordering**: Output rows follow dictionary enumeration order, which may vary between runs if the input DataFrame ordering changes.
- **debit_amount, credit_amount, amount_ratio**: Due to double-precision arithmetic (W6), floating-point representation may cause minor epsilon differences across platforms or runtimes.

## Write Mode Implications
- **Overwrite** mode: Each run completely replaces the output directory contents. Only the latest effective date's results persist.
- With `numParts: 1`, a single `part-00000.parquet` file is produced.

## Edge Cases

1. **Empty transactions or accounts**: Returns an empty DataFrame with the correct column schema. [DebitCreditRatioCalculator.cs:19-23]

2. **Account with only Debits**: `credit_count = 0`, so `debit_credit_ratio = 0` (division guard). `credit_amount = 0.0`, so `amount_ratio = 0.0`.

3. **Account with only Credits**: `debit_count = 0`, `debit_credit_ratio = 0` (integer division 0/N = 0). `debit_amount = 0.0`, `amount_ratio = 0.0`.

4. **Integer division truncation (W4)**: If an account has 3 debits and 5 credits, `debit_credit_ratio = 3 / 5 = 0` (integer truncation). This is a known behavior, not a bug to fix.

5. **Double precision epsilon (W6)**: Amounts like 1235.00 converted to `double` may have representation differences. Sums may not exactly match `decimal` arithmetic.

6. **Account in transactions but not in accounts**: `customer_id` defaults to 0. The account's transactions are still included.

7. **Unused sourced columns**: `interest_rate`, `credit_limit`, `description`, and `transaction_id` are sourced but not used by the External module.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Per-account aggregation | [DebitCreditRatioCalculator.cs:35-51] |
| Debit/Credit count split | [DebitCreditRatioCalculator.cs:47-50] |
| Integer division for count ratio (W4) | [DebitCreditRatioCalculator.cs:60-61] |
| Zero-credit guard | [DebitCreditRatioCalculator.cs:61] |
| Double precision amounts (W6) | [DebitCreditRatioCalculator.cs:41, 48-50] |
| Amount ratio computation | [DebitCreditRatioCalculator.cs:64] |
| Customer lookup with default 0 | [DebitCreditRatioCalculator.cs:26-32, 58] |
| Empty input guard | [DebitCreditRatioCalculator.cs:19-23] |
| Overwrite write mode | [debit_credit_ratio.json:29] |
| 1 Parquet part | [debit_credit_ratio.json:28] |
| firstEffectiveDate = 2024-10-01 | [debit_credit_ratio.json:3] |

## Open Questions
None.
