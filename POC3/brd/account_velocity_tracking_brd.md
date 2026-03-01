# AccountVelocityTracking — Business Requirements Document

## Overview
Tracks daily transaction velocity per account, aggregating transaction counts and total amounts grouped by account and date. Unlike other jobs, this External module writes CSV output directly via file I/O (bypassing the framework's CsvFileWriter), appending with headers re-emitted on each run. The job config has NO framework writer module.

## Output Type
Direct file I/O via External module (no framework writer in the module chain)

## Writer Configuration
- **Output path**: `Output/curated/account_velocity_tracking.csv`
- **Write mode**: Append (via `StreamWriter` with `append: true`)
- **Line ending**: LF (`writer.NewLine = "\n"`)
- **Header**: Re-emitted on every run (not just first run)
- **No trailer**: No trailer line is produced
- **No framework writer**: The job config has only 3 modules (2 DataSourcing + 1 External). There is no ParquetFileWriter or CsvFileWriter module.

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.transactions | transaction_id, account_id, txn_timestamp, txn_type, amount, description | Effective date range (injected) | [account_velocity_tracking.json:8-10] |
| datalake.accounts | account_id, customer_id, credit_limit, apr | Effective date range (injected) | [account_velocity_tracking.json:13-16] |

## Business Rules

BR-1: Transactions are grouped by the composite key (account_id, as_of date). For each group, the transaction count and sum of amounts are computed.
- Confidence: HIGH
- Evidence: [AccountVelocityTracker.cs:38-49] — groups dictionary keyed by (accountId, txnDate)

BR-2: The customer_id for each account is resolved via a lookup dictionary from the accounts DataFrame. If an account_id has no match, customer_id defaults to 0.
- Confidence: HIGH
- Evidence: [AccountVelocityTracker.cs:29-35] — accountToCustomer dictionary; [AccountVelocityTracker.cs:57] — `GetValueOrDefault(accountId, 0)`

BR-3: The amount is accumulated as decimal via `Convert.ToDecimal`, then rounded to 2 decimal places using `Math.Round(total, 2)`.
- Confidence: HIGH
- Evidence: [AccountVelocityTracker.cs:49] — decimal conversion; [AccountVelocityTracker.cs:65] — `Math.Round(total, 2)`

BR-4: Output rows are ordered by txn_date ascending, then by account_id ascending.
- Confidence: HIGH
- Evidence: [AccountVelocityTracker.cs:53] — `.OrderBy(k => k.Key.txnDate).ThenBy(k => k.Key.accountId)`

BR-5: The as_of column in the output is set to the __maxEffectiveDate (formatted as yyyy-MM-dd string), NOT the transaction's as_of date.
- Confidence: HIGH
- Evidence: [AccountVelocityTracker.cs:25-26] — `maxDate.ToString("yyyy-MM-dd")`; [AccountVelocityTracker.cs:66] — uses `dateStr`

BR-6: The txn_date column uses the transaction row's as_of value (converted to string), which may differ from the output's as_of if the effective date range spans multiple days.
- Confidence: HIGH
- Evidence: [AccountVelocityTracker.cs:42] — `row["as_of"]?.ToString() ?? dateStr`

BR-7: The External module writes the CSV directly, then sets `sharedState["output"]` to an EMPTY DataFrame. The framework never writes output — this is an intentional bypass.
- Confidence: HIGH
- Evidence: [AccountVelocityTracker.cs:71-73] — calls WriteDirectCsv then sets output to empty DataFrame

BR-8: The direct CSV write appends to the file with the header re-emitted each time. Over multiple runs, the file will have interleaved headers among data rows.
- Confidence: HIGH
- Evidence: [AccountVelocityTracker.cs:84,88] — `append: true` and unconditional header write

BR-9: The credit_limit and apr columns from accounts are sourced but never used in the External module.
- Confidence: HIGH
- Evidence: [AccountVelocityTracker.cs:28-35] — only account_id and customer_id are extracted from accounts

BR-10: Transactions have data on ALL days including weekends (unlike accounts which are weekday-only). Transaction counts vary by day (~4,200-4,350 per day).
- Confidence: HIGH
- Evidence: [DB query: transactions GROUP BY as_of shows Oct 5-6 present; accounts GROUP BY as_of skips weekends]

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| account_id | transactions (group key) | Convert.ToInt32 | [AccountVelocityTracker.cs:55] |
| customer_id | accounts.customer_id | Lookup by account_id; 0 if missing | [AccountVelocityTracker.cs:57] |
| txn_date | transactions.as_of | ToString; falls back to maxDate string | [AccountVelocityTracker.cs:42,62] |
| txn_count | Computed | COUNT of transactions per (account_id, txn_date) | [AccountVelocityTracker.cs:63] |
| total_amount | Computed | SUM of amount per group, rounded to 2 decimals | [AccountVelocityTracker.cs:64-65] |
| as_of | __maxEffectiveDate | Formatted as yyyy-MM-dd string | [AccountVelocityTracker.cs:25-26,66] |

## Non-Deterministic Fields
None identified. All fields are deterministic from the input data.

## Write Mode Implications
- **Append mode (direct I/O)**: Data accumulates across runs. Each run appends a header line followed by data rows.
- This means the file will contain multiple header rows interspersed with data — one header per run.
- No framework-level writer is involved, so no Overwrite/Append semantics from the framework apply.
- There is no mechanism to prevent duplicate data if a date is re-run.

## Edge Cases
- **Weekend transactions**: Transactions exist on weekends. If accounts don't have weekend data, the accountToCustomer lookup may be empty on weekends (accounts sourced for weekend as_of returns 0 rows). This would cause all customer_id values to default to 0.
- **Repeated headers**: The file accumulates interleaved headers with data, which may cause issues for downstream consumers expecting a single header row.
- **Empty input**: If both transactions and accounts are empty, an empty CSV is appended (just the header line).
- **Null as_of on transactions**: Falls back to maxDate string if as_of is null.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Group by (account_id, txn_date) | AccountVelocityTracker.cs:38-49 |
| Direct CSV write, not framework writer | AccountVelocityTracker.cs:71-73, account_velocity_tracking.json (no writer module) |
| Append with repeated headers | AccountVelocityTracker.cs:84,88 |
| Output sorted by txn_date, account_id | AccountVelocityTracker.cs:53 |
| Empty DataFrame set as output | AccountVelocityTracker.cs:73 |
| credit_limit, apr sourced but unused | AccountVelocityTracker.cs (no reference) |
| First effective date 2024-10-01 | account_velocity_tracking.json:3 |

## Open Questions
1. The repeated header on every append run is likely a bug — downstream consumers would need to handle multiple header lines. (Confidence: HIGH — this is unusual behavior)
2. Weekend account data absence means customer_id lookups will fail on weekends, defaulting all to 0. Is this intentional? (Confidence: MEDIUM)
3. Why are credit_limit and apr sourced from accounts but never used? (Confidence: LOW)
