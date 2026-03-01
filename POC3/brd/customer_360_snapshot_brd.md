# Customer360Snapshot -- Business Requirements Document

## Overview
Produces a comprehensive 360-degree customer snapshot aggregating account counts and balances, card counts, and investment counts and values per customer. Implements weekend fallback logic to use Friday's data when the effective date falls on a weekend. Output is a Parquet file.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `output`
- **outputDirectory**: `Output/curated/customer_360_snapshot/`
- **numParts**: 1
- **writeMode**: Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.customers | id, prefix, first_name, last_name, suffix | Effective date range (injected by executor); then filtered in-code to targetDate | [customer_360_snapshot.json:8-11] |
| datalake.accounts | account_id, customer_id, current_balance, interest_rate, credit_limit, apr | Effective date range (injected by executor); filtered in-code to targetDate | [customer_360_snapshot.json:14-17] |
| datalake.cards | card_id, customer_id, card_number_masked | Effective date range (injected by executor); filtered in-code to targetDate | [customer_360_snapshot.json:20-23] |
| datalake.investments | investment_id, customer_id, current_value | Effective date range (injected by executor); filtered in-code to targetDate | [customer_360_snapshot.json:26-29] |

## Business Rules

BR-1: Weekend fallback -- if `__maxEffectiveDate` falls on Saturday, use Friday (date - 1). If Sunday, use Friday (date - 2). Weekdays use the date as-is.
- Confidence: HIGH
- Evidence: [Customer360SnapshotBuilder.cs:30-32] -- explicit `DayOfWeek.Saturday` and `DayOfWeek.Sunday` checks with AddDays adjustments.

BR-2: All source DataFrames are filtered to the `targetDate` (after weekend fallback) within the External module code, not via DataSourcing filters.
- Confidence: HIGH
- Evidence: [Customer360SnapshotBuilder.cs:35,43,54,66] -- `.Where(r => ((DateOnly)r["as_of"]) == targetDate)` applied to each source.

BR-3: Account count and total balance are aggregated per customer. Balances are summed across all accounts for the target date.
- Confidence: HIGH
- Evidence: [Customer360SnapshotBuilder.cs:38-48] -- `accountCountByCustomer` and `balanceByCustomer` dictionaries.

BR-4: Card count is a simple count of card records per customer for the target date.
- Confidence: HIGH
- Evidence: [Customer360SnapshotBuilder.cs:51-58] -- `cardCountByCustomer` dictionary.

BR-5: Investment count and total value are aggregated per customer. Values are summed across all investments for the target date.
- Confidence: HIGH
- Evidence: [Customer360SnapshotBuilder.cs:61-71] -- `investmentCountByCustomer` and `investmentValueByCustomer` dictionaries.

BR-6: Customers with no accounts, cards, or investments for the target date get 0 for all count/balance fields (via `GetValueOrDefault`).
- Confidence: HIGH
- Evidence: [Customer360SnapshotBuilder.cs:85-88] -- `GetValueOrDefault(customerId, 0)` for counts and `0m` for balances.

BR-7: `total_balance` and `total_investment_value` are rounded to 2 decimal places using `Math.Round`.
- Confidence: HIGH
- Evidence: [Customer360SnapshotBuilder.cs:86,89] -- `Math.Round(..., 2)`.

BR-8: The output `as_of` is set to `targetDate` (the weekend-adjusted date), not the original `__maxEffectiveDate`.
- Confidence: HIGH
- Evidence: [Customer360SnapshotBuilder.cs:90] -- `["as_of"] = targetDate`.

BR-9: Output is customer-driven. Only customers present on the target date produce rows.
- Confidence: HIGH
- Evidence: [Customer360SnapshotBuilder.cs:76] -- iterates `filteredCustomers` (customers filtered to targetDate).

BR-10: When customers DataFrame is null or empty, the output is an empty DataFrame with correct schema.
- Confidence: HIGH
- Evidence: [Customer360SnapshotBuilder.cs:23-26] -- null/empty guard.

BR-11: Some sourced columns (prefix, suffix, interest_rate, credit_limit, apr, card_number_masked) are loaded by DataSourcing but NOT used in the External module output.
- Confidence: HIGH
- Evidence: [Customer360SnapshotBuilder.cs:10-15] -- output columns do not include prefix, suffix. The code never references interest_rate, credit_limit, apr, or card_number_masked.

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | customers.id | Cast to int via Convert.ToInt32 | [Customer360SnapshotBuilder.cs:78] |
| first_name | customers.first_name | ToString with null coalesce to "" | [Customer360SnapshotBuilder.cs:82] |
| last_name | customers.last_name | ToString with null coalesce to "" | [Customer360SnapshotBuilder.cs:83] |
| account_count | accounts | Count of account rows per customer for targetDate, default 0 | [Customer360SnapshotBuilder.cs:85] |
| total_balance | accounts.current_balance | Sum of current_balance per customer, rounded to 2 decimals, default 0 | [Customer360SnapshotBuilder.cs:86] |
| card_count | cards | Count of card rows per customer for targetDate, default 0 | [Customer360SnapshotBuilder.cs:87] |
| investment_count | investments | Count of investment rows per customer for targetDate, default 0 | [Customer360SnapshotBuilder.cs:88] |
| total_investment_value | investments.current_value | Sum of current_value per customer, rounded to 2 decimals, default 0 | [Customer360SnapshotBuilder.cs:89] |
| as_of | __maxEffectiveDate | Weekend-adjusted targetDate | [Customer360SnapshotBuilder.cs:90] |

## Non-Deterministic Fields
None identified.

## Write Mode Implications
- **Overwrite**: Each execution replaces the entire output directory. For multi-day auto-advance runs, only the last effective date's output survives on disk.

## Edge Cases
- **Weekend effective date**: Saturday maps to Friday (date-1), Sunday maps to Friday (date-2). If no data exists for Friday, the output will have no customer rows.
- **Customer with no related records**: Gets zeros for all aggregated columns.
- **Unused sourced columns**: prefix, suffix, interest_rate, credit_limit, apr, card_number_masked are loaded but not used in output.
- **Data exists outside target date**: DataSourcing loads a range, but the External module further filters to only the targetDate. Rows from other dates in the range are discarded.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Weekend fallback logic | [Customer360SnapshotBuilder.cs:30-32] |
| In-code date filtering | [Customer360SnapshotBuilder.cs:35,43,54,66] |
| Account aggregation | [Customer360SnapshotBuilder.cs:38-48] |
| Card counting | [Customer360SnapshotBuilder.cs:51-58] |
| Investment aggregation | [Customer360SnapshotBuilder.cs:61-71] |
| Default zeros for missing data | [Customer360SnapshotBuilder.cs:85-88] |
| Balance rounding | [Customer360SnapshotBuilder.cs:86,89] |
| as_of uses targetDate | [Customer360SnapshotBuilder.cs:90] |
| Parquet output, 1 part | [customer_360_snapshot.json:39-43] |
| Overwrite mode | [customer_360_snapshot.json:43] |

## Open Questions
- OQ-1: Several columns are sourced from the database (prefix, suffix, interest_rate, credit_limit, apr, card_number_masked) but never appear in the output. This may be intentional (future use) or dead configuration. Confidence: MEDIUM.
