# HighBalanceAccounts -- Business Requirements Document

## Overview

This job identifies all accounts with a current balance exceeding $10,000 and produces a denormalized output that includes the account holder's name. The output is written in Overwrite mode, meaning only the latest effective date's snapshot persists in the curated table.

## Source Tables

### datalake.accounts
- **Columns used**: `account_id`, `customer_id`, `account_type`, `current_balance`, `as_of`
- **Column sourced but unused**: `account_status` (see AP-4)
- **Filter**: `current_balance > 10000` applied in the External module
- **Evidence**: [ExternalModules/HighBalanceFilter.cs:39] `if (balance > 10000)`

### datalake.customers
- **Columns used**: `id`, `first_name`, `last_name`
- **Join logic**: Customers are joined to accounts via `customer_id = id`, using a dictionary lookup built from the customers DataFrame
- **Evidence**: [ExternalModules/HighBalanceFilter.cs:29-31] builds `customerNames` dictionary keyed by `custId` from `customers.Rows`; [ExternalModules/HighBalanceFilter.cs:42] lookups via `customerNames.GetValueOrDefault(customerId, ("", ""))`

## Business Rules

BR-1: Only accounts with `current_balance > 10000` are included in the output.
- Confidence: HIGH
- Evidence: [ExternalModules/HighBalanceFilter.cs:39] `if (balance > 10000)`
- Evidence: [curated.high_balance_accounts] `SELECT MIN(current_balance)` yields 10270.00 (all values > 10000)

BR-2: Each output row includes the account holder's first and last name from the customers table.
- Confidence: HIGH
- Evidence: [ExternalModules/HighBalanceFilter.cs:42-43] customer name lookup by `customer_id`
- Evidence: [ExternalModules/HighBalanceFilter.cs:49-50] `["first_name"] = firstName`, `["last_name"] = lastName`

BR-3: If no matching customer is found for an account, the first_name and last_name default to empty strings.
- Confidence: HIGH
- Evidence: [ExternalModules/HighBalanceFilter.cs:42] `customerNames.GetValueOrDefault(customerId, ("", ""))`

BR-4: The output uses Overwrite mode -- each run truncates and replaces the entire target table.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/high_balance_accounts.json:28] `"writeMode": "Overwrite"`

BR-5: There is no explicit filter on account_type or account_status. All account types are eligible if balance exceeds threshold.
- Confidence: HIGH
- Evidence: [ExternalModules/HighBalanceFilter.cs:36-55] The only filter condition is `balance > 10000`; no account_type or account_status check exists
- Note: In practice, only Savings accounts have balances > 10000 in the current data. Checking max balance is $5,017 and Credit max balance is $85. This is a data coincidence, not a business rule.
- Evidence: [datalake.accounts] `SELECT account_type, COUNT(*) FROM datalake.accounts WHERE current_balance > 10000 AND as_of = '2024-10-31' GROUP BY account_type` yields only Savings (54 rows)

BR-6: If accounts or customers DataFrames are null or empty, an empty output DataFrame is produced.
- Confidence: HIGH
- Evidence: [ExternalModules/HighBalanceFilter.cs:19-23] null/empty check returns empty DataFrame

## Output Schema

| Column | Source | Transformation |
|--------|--------|----------------|
| account_id | datalake.accounts.account_id | Pass-through |
| customer_id | datalake.accounts.customer_id | Pass-through |
| account_type | datalake.accounts.account_type | Pass-through |
| current_balance | datalake.accounts.current_balance | Pass-through (only rows where > 10000) |
| first_name | datalake.customers.first_name | Looked up by customer_id; defaults to "" if not found |
| last_name | datalake.customers.last_name | Looked up by customer_id; defaults to "" if not found |
| as_of | datalake.accounts.as_of | Pass-through from accounts rows |

## Edge Cases

- **No matching customer**: first_name and last_name default to empty strings (not NULL)
- **Empty accounts or customers DataFrame**: Returns empty output DataFrame
- **NULL balance**: Would cause `Convert.ToDecimal` to throw; no explicit NULL guard. However, `current_balance` is defined as NOT NULL in the source schema, so this cannot occur.
- **Overwrite mode**: Only the last effective date's data persists. Earlier dates are overwritten.

## Anti-Patterns Identified

- **AP-3: Unnecessary External Module** -- The HighBalanceFilter External module performs a simple filter (`balance > 10000`) and a LEFT JOIN equivalent (accounts to customers). This is straightforward SQL: `SELECT a.account_id, a.customer_id, a.account_type, a.current_balance, COALESCE(c.first_name, '') AS first_name, COALESCE(c.last_name, '') AS last_name, a.as_of FROM accounts a LEFT JOIN customers c ON a.customer_id = c.id AND a.as_of = c.as_of WHERE a.current_balance > 10000`. V2 approach: Replace with a SQL Transformation.

- **AP-4: Unused Columns Sourced** -- The accounts DataSourcing includes `account_status` in its columns list, but the External module never references `account_status`. V2 approach: Remove `account_status` from the DataSourcing columns.

- **AP-6: Row-by-Row Iteration in External Module** -- The External module iterates over accounts rows one by one with a `foreach` loop to filter and join. This is a set-based operation expressible as a single SQL query. V2 approach: Replace with SQL Transformation (see AP-3).

- **AP-7: Hardcoded Magic Values** -- The threshold `10000` appears as a literal in the filter condition without explanation of its business meaning. V2 approach: Document that 10000 is the high-balance threshold in the FSD; add SQL comment.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [ExternalModules/HighBalanceFilter.cs:39] `if (balance > 10000)` |
| BR-2 | [ExternalModules/HighBalanceFilter.cs:42-50] customer lookup and output assignment |
| BR-3 | [ExternalModules/HighBalanceFilter.cs:42] `GetValueOrDefault(customerId, ("", ""))` |
| BR-4 | [JobExecutor/Jobs/high_balance_accounts.json:28] `"writeMode": "Overwrite"` |
| BR-5 | [ExternalModules/HighBalanceFilter.cs:36-55] only filter is balance > 10000 |
| BR-6 | [ExternalModules/HighBalanceFilter.cs:19-23] null/empty guard |

## Open Questions

- None. All business rules are directly observable in code with HIGH confidence.
