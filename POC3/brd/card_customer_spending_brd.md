# CardCustomerSpending — Business Requirements Document

## Overview
Produces a per-customer spending summary (transaction count and total amount) for card transactions, enriched with customer name. Applies weekend fallback logic to use Friday's data when the effective date falls on a Saturday or Sunday.

## Output Type
ParquetFileWriter

## Writer Configuration
- **outputDirectory**: `Output/curated/card_customer_spending/`
- **numParts**: 1
- **writeMode**: Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.card_transactions | card_txn_id, card_id, customer_id, amount, txn_timestamp | Effective date range via DataSourcing; then filtered in External to targetDate only | [card_customer_spending.json:8-11], [CardCustomerSpendingProcessor.cs:36-37] |
| datalake.customers | id, prefix, first_name, last_name, suffix | Effective date range via DataSourcing; used for name lookup | [card_customer_spending.json:14-17], [CardCustomerSpendingProcessor.cs:49-58] |
| datalake.accounts | account_id, customer_id, account_type | Effective date range via DataSourcing; **sourced but never used by External module** | [card_customer_spending.json:20-23], [CardCustomerSpendingProcessor.cs — no reference to "accounts"] |

## Business Rules

BR-1: Weekend fallback — if `__maxEffectiveDate` is Saturday, targetDate is shifted to Friday (maxDate - 1 day). If Sunday, shifted to Friday (maxDate - 2 days). Weekday dates are used as-is.
- Confidence: HIGH
- Evidence: [CardCustomerSpendingProcessor.cs:18-20]

BR-2: Card transactions are filtered to only those rows where `as_of == targetDate` (post-weekend-fallback).
- Confidence: HIGH
- Evidence: [CardCustomerSpendingProcessor.cs:36-37] `.Where(r => ((DateOnly)r["as_of"]) == targetDate)`

BR-3: Transactions are grouped by `customer_id`, producing one output row per customer.
- Confidence: HIGH
- Evidence: [CardCustomerSpendingProcessor.cs:61-72] grouping logic with Dictionary keyed by customer_id

BR-4: `txn_count` is the number of card transactions per customer for the target date.
- Confidence: HIGH
- Evidence: [CardCustomerSpendingProcessor.cs:70] `current.count + 1`

BR-5: `total_spending` is the sum of `amount` values per customer for the target date.
- Confidence: HIGH
- Evidence: [CardCustomerSpendingProcessor.cs:70] `current.total + amount`

BR-6: Customer names are looked up from the `customers` DataFrame by joining on `customer_id = id`.
- Confidence: HIGH
- Evidence: [CardCustomerSpendingProcessor.cs:47-58] customerLookup dictionary keyed by customers.id

BR-7: The `accounts` DataFrame is sourced by the job config but never referenced in the External module. This is dead data sourcing.
- Confidence: HIGH
- Evidence: [card_customer_spending.json:20-23] sources accounts; [CardCustomerSpendingProcessor.cs] — no reference to "accounts" in code

BR-8: If `card_transactions` is null or empty, an empty DataFrame with the correct schema is returned.
- Confidence: HIGH
- Evidence: [CardCustomerSpendingProcessor.cs:29-33]

BR-9: If no transactions match the target date after filtering, an empty DataFrame is returned.
- Confidence: HIGH
- Evidence: [CardCustomerSpendingProcessor.cs:40-44]

BR-10: The `prefix` and `suffix` columns are sourced from customers but not used in the output — only `first_name` and `last_name` are included.
- Confidence: HIGH
- Evidence: [card_customer_spending.json:16] sources prefix/suffix; [CardCustomerSpendingProcessor.cs:10-13] output columns only include first_name, last_name

BR-11: The output `as_of` column is set to `targetDate` (the weekend-adjusted date), not the original `__maxEffectiveDate`.
- Confidence: HIGH
- Evidence: [CardCustomerSpendingProcessor.cs:86] `["as_of"] = targetDate`

BR-12: Customer lookup uses the entire customers DataFrame across all as_of dates (no date filtering on customers). If a customer's name changes across dates, the last-seen version wins (dictionary overwrite).
- Confidence: MEDIUM
- Evidence: [CardCustomerSpendingProcessor.cs:49-58] iterates all customer rows without date filtering

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | card_transactions.customer_id | Grouped by | [CardCustomerSpendingProcessor.cs:80] |
| first_name | customers.first_name | Lookup by customer_id; "" if not found | [CardCustomerSpendingProcessor.cs:82] |
| last_name | customers.last_name | Lookup by customer_id; "" if not found | [CardCustomerSpendingProcessor.cs:83] |
| txn_count | card_transactions | COUNT per customer | [CardCustomerSpendingProcessor.cs:84] |
| total_spending | card_transactions.amount | SUM(amount) per customer | [CardCustomerSpendingProcessor.cs:85] |
| as_of | Derived | Set to targetDate (weekend-adjusted) | [CardCustomerSpendingProcessor.cs:86] |

## Non-Deterministic Fields
None identified. All fields are deterministic given the same input data and effective date.

## Write Mode Implications
- **Overwrite** mode: The entire output directory is replaced on each run.
- For multi-day auto-advance runs, only the last effective date's output survives.
- Since the External module filters to a single targetDate, each run produces output for one date only.

## Edge Cases

1. **Weekend effective dates**: On weekends, the module falls back to Friday's date. card_transactions has data for all 7 days (including weekends), so Saturday/Sunday transactions exist but are ignored in favor of Friday's data. This means weekend transaction data is never processed.
   - Confidence: HIGH
   - Evidence: [CardCustomerSpendingProcessor.cs:18-20], [DB: card_transactions has weekend rows]

2. **Customer not found**: If a customer_id in card_transactions has no match in customers, first_name and last_name default to empty strings.
   - Confidence: HIGH
   - Evidence: [CardCustomerSpendingProcessor.cs:77] `GetValueOrDefault(kvp.Key, ("", ""))`

3. **Customers table has no weekend data**: The customers table only has weekday snapshots. Since the customer lookup is not date-filtered, this doesn't affect results — all available customer rows are used regardless of as_of.
   - Confidence: HIGH
   - Evidence: [DB: customers has weekday-only data], [CardCustomerSpendingProcessor.cs:49-58]

4. **Amount precision**: Amounts are converted via `Convert.ToDecimal`, preserving the numeric precision from PostgreSQL.
   - Confidence: HIGH
   - Evidence: [CardCustomerSpendingProcessor.cs:65]

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Weekend fallback | CardCustomerSpendingProcessor.cs:18-20 |
| BR-2: Filter to target date | CardCustomerSpendingProcessor.cs:36-37 |
| BR-3: Group by customer | CardCustomerSpendingProcessor.cs:61-72 |
| BR-4: txn_count | CardCustomerSpendingProcessor.cs:70 |
| BR-5: total_spending | CardCustomerSpendingProcessor.cs:70 |
| BR-6: Customer name lookup | CardCustomerSpendingProcessor.cs:47-58 |
| BR-7: Dead accounts sourcing | card_customer_spending.json:20-23, processor code |
| BR-8: Empty input handling | CardCustomerSpendingProcessor.cs:29-33 |
| BR-9: No matching date handling | CardCustomerSpendingProcessor.cs:40-44 |
| BR-10: Unused prefix/suffix columns | card_customer_spending.json:16 vs processor output |
| BR-11: as_of uses targetDate | CardCustomerSpendingProcessor.cs:86 |
| BR-12: Unfiltered customer lookup | CardCustomerSpendingProcessor.cs:49-58 |

## Open Questions
None.
