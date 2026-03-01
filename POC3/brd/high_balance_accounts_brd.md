# HighBalanceAccounts — Business Requirements Document

## Overview
Filters accounts to identify those with a current balance exceeding $10,000, joining with customer names to produce an enriched high-balance account listing. Output is written to CSV with Overwrite mode (no trailer).

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/high_balance_accounts.csv`
- **includeHeader**: true
- **writeMode**: Overwrite
- **lineEnding**: LF
- **trailerFormat**: Not specified (no trailer)

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.accounts | account_id, customer_id, account_type, account_status, current_balance | Effective date range (injected) | [high_balance_accounts.json:8-10] |
| datalake.customers | id, first_name, last_name | Effective date range (injected) | [high_balance_accounts.json:13-15] |

## Business Rules

BR-1: An account qualifies as "high balance" if its current_balance is strictly greater than 10,000 (not >= 10,000).
- Confidence: HIGH
- Evidence: [HighBalanceFilter.cs:39] — `if (balance > 10000)`

BR-2: Customer names are looked up via a dictionary keyed by customer ID. If no match found, first_name and last_name default to empty strings.
- Confidence: HIGH
- Evidence: [HighBalanceFilter.cs:26-33,42] — customerNames dictionary with GetValueOrDefault

BR-3: The account_status column is sourced but NOT included in the output columns. The filter does not consider account_status — all statuses qualify if balance > 10,000.
- Confidence: HIGH
- Evidence: [HighBalanceFilter.cs:10-14] — outputColumns does not include account_status; [HighBalanceFilter.cs:39] — only balance check

BR-4: If either accounts or customers is null or empty, an empty DataFrame is produced.
- Confidence: HIGH
- Evidence: [HighBalanceFilter.cs:19-23]

BR-5: The balance comparison uses decimal arithmetic via Convert.ToDecimal.
- Confidence: HIGH
- Evidence: [HighBalanceFilter.cs:38] — `var balance = Convert.ToDecimal(acctRow["current_balance"]);`

BR-6: The as_of value is set to `__maxEffectiveDate` from shared state, not the per-row account date.
- Confidence: HIGH
- Evidence: [HighBalanceFilter.cs:52] — `["as_of"] = sharedState["__maxEffectiveDate"]`

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| account_id | accounts.account_id | Passthrough | [HighBalanceFilter.cs:46] |
| customer_id | accounts.customer_id | Passthrough | [HighBalanceFilter.cs:47] |
| account_type | accounts.account_type | Passthrough | [HighBalanceFilter.cs:48] |
| current_balance | accounts.current_balance | Passthrough (only rows > 10000) | [HighBalanceFilter.cs:49] |
| first_name | customers.first_name | Lookup by customer_id; empty if missing | [HighBalanceFilter.cs:50] |
| last_name | customers.last_name | Lookup by customer_id; empty if missing | [HighBalanceFilter.cs:51] |
| as_of | __maxEffectiveDate | From shared state | [HighBalanceFilter.cs:52] |

## Non-Deterministic Fields
None identified.

## Write Mode Implications
- **Overwrite mode**: Each run replaces the CSV. Only the latest effective date's high-balance accounts persist.
- Multi-day gap-fill: only the last day survives.

## Edge Cases
- **Exactly $10,000 balance**: Accounts with balance == 10,000 are EXCLUDED (strictly greater than, not >=).
- **Negative balances**: Credit accounts can have negative balances (e.g., -2688.00). These are never > 10,000 so they are always excluded.
- **Weekend dates**: Accounts is weekday-only. Weekend dates produce empty output.
- **No qualifying accounts**: If no accounts exceed the threshold, the output is an empty CSV with header only.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Balance threshold > 10000 | HighBalanceFilter.cs:39 |
| Customer name lookup with defaults | HighBalanceFilter.cs:26-33,42 |
| account_status sourced but not in output | HighBalanceFilter.cs:10-14 |
| Decimal comparison | HighBalanceFilter.cs:38 |
| Overwrite write mode | high_balance_accounts.json:30 |
| No trailer | high_balance_accounts.json (no trailerFormat) |
| First effective date 2024-10-01 | high_balance_accounts.json:3 |

## Open Questions
1. Is the threshold of $10,000 (strictly greater than) the correct business rule, or should it be >= $10,000? (Confidence: MEDIUM — the threshold value and comparison operator are both hard-coded)
