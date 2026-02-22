# AccountCustomerJoin — Business Requirements Document

## Overview

This job produces a denormalized view joining account data with customer names, outputting each account enriched with the customer's first and last name. The result is written to `curated.account_customer_join` using Overwrite mode, so only the latest effective date's data is retained.

## Source Tables

### datalake.accounts
- **Columns sourced:** account_id, customer_id, account_type, account_status, current_balance
- **Columns actually used:** All 5 sourced columns plus framework-injected as_of
- **Join/filter logic:** No filtering. All account rows for the effective date are included.
- **Evidence:** [ExternalModules/AccountCustomerDenormalizer.cs:36-52] All account columns are used in the output.

### datalake.customers
- **Columns sourced:** id, first_name, last_name
- **Columns actually used:** All 3 (id used as join key, first_name and last_name in output)
- **Join/filter logic:** Joined to accounts via customers.id = accounts.customer_id. The join is implemented as a dictionary lookup in the External module.
- **Evidence:** [ExternalModules/AccountCustomerDenormalizer.cs:27-33] Customer lookup built from id -> (first_name, last_name).

### datalake.addresses
- **Columns sourced:** address_id, customer_id, address_line1, city, state_province
- **Usage:** NONE — this DataSourcing module is loaded into shared state as "addresses" but the External module never accesses it.
- **Evidence:** [ExternalModules/AccountCustomerDenormalizer.cs] No reference to `sharedState["addresses"]` anywhere in the code.

## Business Rules

BR-1: Each account is joined with its customer's first and last name via customer_id.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountCustomerDenormalizer.cs:39] `var customerId = Convert.ToInt32(acctRow["customer_id"]);`
- Evidence: [ExternalModules/AccountCustomerDenormalizer.cs:40] `var (firstName, lastName) = customerNames.GetValueOrDefault(customerId, ("", ""));`

BR-2: If a customer_id has no matching customer record, empty strings are used for first_name and last_name.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountCustomerDenormalizer.cs:40] `GetValueOrDefault(customerId, ("", ""))` returns empty strings as default.

BR-3: All accounts are included in the output regardless of status or type (no filtering).
- Confidence: HIGH
- Evidence: [ExternalModules/AccountCustomerDenormalizer.cs:36-52] The foreach iterates all account rows without any conditional filtering.
- Evidence: [curated.account_customer_join] Row count (277) matches datalake.accounts row count (277).

BR-4: The output contains 8 columns: account_id, customer_id, first_name, last_name, account_type, account_status, current_balance, as_of.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountCustomerDenormalizer.cs:10-14] `outputColumns` explicitly lists these 8 columns.

BR-5: Data is written in Overwrite mode — only the most recently processed effective date's data is retained.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/account_customer_join.json:34] `"writeMode": "Overwrite"`.
- Evidence: [curated.account_customer_join] Only 1 as_of date (2024-10-31) present with 277 rows.

BR-6: When accounts or customers DataFrame is null/empty, an empty DataFrame with the correct schema is returned.
- Confidence: HIGH
- Evidence: [ExternalModules/AccountCustomerDenormalizer.cs:19-23] Explicit null/empty guard.

BR-7: The customer lookup uses the last entry per customer_id when building the dictionary (last-write-wins behavior for duplicate customer_ids within a single as_of).
- Confidence: MEDIUM
- Evidence: [ExternalModules/AccountCustomerDenormalizer.cs:29-32] Dictionary assignment `customerNames[custId] = (firstName, lastName)` overwrites if duplicate. However, customer ids are likely unique per as_of date.

## Output Schema

| Column | Source | Transformation |
|--------|--------|----------------|
| account_id | datalake.accounts.account_id | Direct pass-through |
| customer_id | datalake.accounts.customer_id | Direct pass-through |
| first_name | datalake.customers.first_name | Joined via customer_id; empty string if no match |
| last_name | datalake.customers.last_name | Joined via customer_id; empty string if no match |
| account_type | datalake.accounts.account_type | Direct pass-through |
| account_status | datalake.accounts.account_status | Direct pass-through |
| current_balance | datalake.accounts.current_balance | Direct pass-through |
| as_of | datalake.accounts.as_of (injected by framework) | Direct pass-through |

## Edge Cases

- **Missing customer:** If an account's customer_id has no corresponding customer record, first_name and last_name are set to empty strings (not NULL). This is a left-join-like behavior.
- **Empty accounts or customers:** Returns empty output with correct schema.
- **Weekend dates:** Source accounts/customers data does not exist for weekends, so DataSourcing returns empty DataFrames. The empty guard returns empty output.
- **NULL customer names:** The External module applies `?.ToString() ?? ""` which converts NULL first/last names to empty strings. However, the datalake.customers schema shows first_name and last_name are NOT NULL, so this guard is defensive only.

## Anti-Patterns Identified

- **AP-1: Redundant Data Sourcing** — The `addresses` DataSourcing module fetches address_id, customer_id, address_line1, city, state_province from `datalake.addresses`, but the External module (`AccountCustomerDenormalizer`) never accesses `sharedState["addresses"]`. This is completely unused.
  - Evidence: [JobExecutor/Jobs/account_customer_join.json:22-26] addresses DataSourcing defined; [ExternalModules/AccountCustomerDenormalizer.cs] no reference to "addresses".
  - V2 approach: Remove the addresses DataSourcing module entirely.

- **AP-3: Unnecessary External Module** — The `AccountCustomerDenormalizer` performs a simple LEFT JOIN between accounts and customers by customer_id with COALESCE to empty string for missing names. This is trivially expressible in SQL.
  - Evidence: [ExternalModules/AccountCustomerDenormalizer.cs] The entire logic is: build a customer lookup dictionary, iterate accounts, join customer name.
  - V2 approach: Replace with a SQL Transformation: `SELECT a.account_id, a.customer_id, COALESCE(c.first_name, '') AS first_name, COALESCE(c.last_name, '') AS last_name, a.account_type, a.account_status, a.current_balance, a.as_of FROM accounts a LEFT JOIN customers c ON a.customer_id = c.id AND a.as_of = c.as_of`.

- **AP-6: Row-by-Row Iteration in External Module** — The External module iterates accounts row-by-row and looks up customer names from a dictionary, when SQL JOIN would handle this in one operation.
  - Evidence: [ExternalModules/AccountCustomerDenormalizer.cs:36] `foreach (var acctRow in accounts.Rows)`
  - V2 approach: Replace with SQL JOIN (eliminates row-by-row iteration).

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [ExternalModules/AccountCustomerDenormalizer.cs:39-40] |
| BR-2 | [ExternalModules/AccountCustomerDenormalizer.cs:40] GetValueOrDefault with ("", "") default |
| BR-3 | [ExternalModules/AccountCustomerDenormalizer.cs:36-52], [curated.account_customer_join] 277 rows |
| BR-4 | [ExternalModules/AccountCustomerDenormalizer.cs:10-14] |
| BR-5 | [JobExecutor/Jobs/account_customer_join.json:34] |
| BR-6 | [ExternalModules/AccountCustomerDenormalizer.cs:19-23] |
| BR-7 | [ExternalModules/AccountCustomerDenormalizer.cs:29-32] |

## Open Questions

- **Q1:** Are there cases where a customer_id in accounts has no matching customer record? All current data appears to have matching customers (output first/last names are never empty strings based on sample data), but the code handles this case defensively. Confidence: MEDIUM that unmatched customers are a theoretical edge case that doesn't occur in practice.
