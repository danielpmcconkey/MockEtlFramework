# AccountCustomerJoin — Business Requirements Document

## Overview
Produces a denormalized view joining accounts with customer names, enriching each account record with the customer's first and last name. Output is written to Parquet with Overwrite mode, representing the latest snapshot only.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `output`
- **outputDirectory**: `Output/curated/account_customer_join/`
- **numParts**: 2
- **writeMode**: Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.accounts | account_id, customer_id, account_type, account_status, current_balance | Effective date range (injected) | [account_customer_join.json:8-10] |
| datalake.customers | id, first_name, last_name | Effective date range (injected) | [account_customer_join.json:13-16] |
| datalake.addresses | address_id, customer_id, address_line1, city, state_province | Effective date range (injected) | [account_customer_join.json:19-22] |

## Business Rules

BR-1: The join is performed between accounts and customers using accounts.customer_id = customers.id. Customer names are looked up via a dictionary keyed by customer ID.
- Confidence: HIGH
- Evidence: [AccountCustomerDenormalizer.cs:26-33] — builds customerNames dictionary keyed by Convert.ToInt32(custRow["id"])

BR-2: The addresses DataFrame is sourced but NOT used by the External module. It is loaded into shared state but never referenced in the code.
- Confidence: HIGH
- Evidence: [AccountCustomerDenormalizer.cs:8-57] — no reference to "addresses" anywhere in Execute method

BR-3: If a customer_id from accounts has no match in the customers lookup, the first_name and last_name default to empty strings.
- Confidence: HIGH
- Evidence: [AccountCustomerDenormalizer.cs:40] — `GetValueOrDefault(customerId, ("", ""))`

BR-4: If either accounts or customers is null or empty, an empty DataFrame with the correct schema is produced.
- Confidence: HIGH
- Evidence: [AccountCustomerDenormalizer.cs:19-23] — null/empty check for both

BR-5: Output iterates over accounts rows — every account row produces exactly one output row regardless of customer match status (left-join semantics via dictionary lookup with defaults).
- Confidence: HIGH
- Evidence: [AccountCustomerDenormalizer.cs:36-53] — foreach over accounts.Rows, always adds to outputRows

BR-6: The as_of value comes from the accounts DataFrame, not the customers DataFrame.
- Confidence: HIGH
- Evidence: [AccountCustomerDenormalizer.cs:51] — `["as_of"] = acctRow["as_of"]`

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| account_id | accounts.account_id | None (passthrough) | [AccountCustomerDenormalizer.cs:44] |
| customer_id | accounts.customer_id | None (passthrough) | [AccountCustomerDenormalizer.cs:45] |
| first_name | customers.first_name | Lookup by customer_id; empty string if missing | [AccountCustomerDenormalizer.cs:46] |
| last_name | customers.last_name | Lookup by customer_id; empty string if missing | [AccountCustomerDenormalizer.cs:47] |
| account_type | accounts.account_type | None (passthrough) | [AccountCustomerDenormalizer.cs:48] |
| account_status | accounts.account_status | None (passthrough) | [AccountCustomerDenormalizer.cs:49] |
| current_balance | accounts.current_balance | None (passthrough) | [AccountCustomerDenormalizer.cs:50] |
| as_of | accounts.as_of | None (passthrough) | [AccountCustomerDenormalizer.cs:51] |

## Non-Deterministic Fields
None identified.

## Write Mode Implications
- **Overwrite mode**: Each run replaces the entire Parquet output directory. Only the most recent effective date's data survives.
- On multi-day gap-fill, each successive day overwrites the previous, so only the final day persists.

## Edge Cases
- **Weekend dates**: Both accounts and customers are weekday-only. DataSourcing on a weekend returns empty DataFrames, producing an empty output (which overwrites any previous data).
- **Customer lookup miss**: Accounts referencing a customer_id not in the customers table will produce rows with empty string names. No error or warning.
- **Duplicate customer IDs**: The dictionary build uses last-write-wins. If a customer ID appears multiple times (e.g., multiple as_of dates in a multi-day range), only the last encountered name is kept.
- Confidence: MEDIUM — depends on whether executor runs single-day or multi-day ranges

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Join accounts to customers by customer_id | AccountCustomerDenormalizer.cs:26-40 |
| Addresses sourced but unused | AccountCustomerDenormalizer.cs (no reference) |
| Left-join semantics with empty string defaults | AccountCustomerDenormalizer.cs:40 |
| Overwrite write mode | account_customer_join.json:36 |
| 2 Parquet part files | account_customer_join.json:35 |
| First effective date 2024-10-01 | account_customer_join.json:3 |

## Open Questions
1. Why is the addresses table sourced if it is never used? Possible vestigial config. (Confidence: LOW)
2. In multi-day runs, the customer lookup dictionary may contain entries from multiple as_of dates since it iterates all rows. This could mix customer names from different dates if names change over time. (Confidence: MEDIUM — depends on data characteristics)
