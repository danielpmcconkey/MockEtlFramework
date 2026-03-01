# CardSpendingByMerchant — Business Requirements Document

## Overview
Aggregates card transaction spending by merchant category code (MCC), producing a summary of transaction count and total spending per MCC, enriched with MCC description. Provides visibility into spending patterns across merchant categories.

## Output Type
ParquetFileWriter

## Writer Configuration
- **outputDirectory**: `Output/curated/card_spending_by_merchant/`
- **numParts**: 1
- **writeMode**: Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.card_transactions | card_txn_id, card_id, customer_id, merchant_name, merchant_category_code, amount, txn_timestamp, authorization_status | Effective date range via DataSourcing; all rows used (no additional filter in External) | [card_spending_by_merchant.json:8-11], [CardSpendingByMerchantProcessor.cs:43-54] |
| datalake.merchant_categories | mcc_code, mcc_description, risk_level | Effective date range via DataSourcing; used for MCC description lookup | [card_spending_by_merchant.json:14-17], [CardSpendingByMerchantProcessor.cs:29-38] |

## Business Rules

BR-1: Transactions are grouped by `merchant_category_code`, producing one output row per MCC.
- Confidence: HIGH
- Evidence: [CardSpendingByMerchantProcessor.cs:43-54] Dictionary keyed by mcc_code

BR-2: `txn_count` is the total number of transactions per MCC across all dates in the effective date range.
- Confidence: HIGH
- Evidence: [CardSpendingByMerchantProcessor.cs:52] `current.count + 1`

BR-3: `total_spending` is the sum of `amount` values per MCC across all dates.
- Confidence: HIGH
- Evidence: [CardSpendingByMerchantProcessor.cs:52] `current.total + amount`

BR-4: MCC description is looked up from merchant_categories by mcc_code. If not found, empty string is used.
- Confidence: HIGH
- Evidence: [CardSpendingByMerchantProcessor.cs:59] `mccLookup.ContainsKey(kvp.Key) ? mccLookup[kvp.Key] : ""`

BR-5: The `as_of` value for all output rows is taken from the first row of card_transactions (`cardTransactions.Rows[0]["as_of"]`). This means all output rows share the same as_of regardless of which dates the transactions span.
- Confidence: HIGH
- Evidence: [CardSpendingByMerchantProcessor.cs:40] `var asOf = cardTransactions.Rows[0]["as_of"]`

BR-6: All transactions are included regardless of authorization_status — both Approved and Declined transactions contribute to spending totals.
- Confidence: HIGH
- Evidence: [CardSpendingByMerchantProcessor.cs:43-54] no authorization_status filter despite the column being sourced

BR-7: The `risk_level` column is sourced from merchant_categories but NOT included in the output.
- Confidence: HIGH
- Evidence: [card_spending_by_merchant.json:17] sources risk_level; [CardSpendingByMerchantProcessor.cs:10-13] output columns don't include risk_level

BR-8: No weekend fallback is applied — all transaction dates including weekends are aggregated.
- Confidence: HIGH
- Evidence: [CardSpendingByMerchantProcessor.cs] — no weekend fallback logic present

BR-9: If `card_transactions` is null or empty, an empty DataFrame with correct schema is returned.
- Confidence: HIGH
- Evidence: [CardSpendingByMerchantProcessor.cs:23-26]

BR-10: Several sourced columns are unused by the External module: card_txn_id, card_id, customer_id, merchant_name, txn_timestamp, authorization_status (from card_transactions) and risk_level (from merchant_categories).
- Confidence: HIGH
- Evidence: [card_spending_by_merchant.json:10-11,17] vs [CardSpendingByMerchantProcessor.cs] — only merchant_category_code, amount, mcc_code, mcc_description are used

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| mcc_code | card_transactions.merchant_category_code | Grouped by | [CardSpendingByMerchantProcessor.cs:62] |
| mcc_description | merchant_categories.mcc_description | Lookup by mcc_code; "" if not found | [CardSpendingByMerchantProcessor.cs:63] |
| txn_count | card_transactions | COUNT per MCC | [CardSpendingByMerchantProcessor.cs:64] |
| total_spending | card_transactions.amount | SUM(amount) per MCC | [CardSpendingByMerchantProcessor.cs:65] |
| as_of | card_transactions.Rows[0]["as_of"] | First row's as_of value | [CardSpendingByMerchantProcessor.cs:66] |

## Non-Deterministic Fields
None identified. All fields are deterministic given the same input data.

## Write Mode Implications
- **Overwrite** mode: The entire output directory is replaced on each run.
- For multi-day auto-advance runs, only the last effective date's output survives.
- Since no date filter is applied within the External module, all transactions in the DataSourcing date range are aggregated. For single-day gap-fill, this is one day's data.

## Edge Cases

1. **as_of from first row**: The as_of value is taken from the very first row of card_transactions rather than being derived from __maxEffectiveDate or computed per group. If DataSourcing returns data for multiple dates, the output as_of will be whatever date the first row happens to have. The ordering of rows from DataSourcing is not guaranteed by the job config.
   - Confidence: HIGH
   - Evidence: [CardSpendingByMerchantProcessor.cs:40]

2. **MCC codes not in lookup**: Transaction MCC codes not found in merchant_categories get empty description strings. Per database query, all 17 transaction MCCs are a subset of the 20 categories.
   - Confidence: HIGH
   - Evidence: [DB queries], [CardSpendingByMerchantProcessor.cs:59]

3. **Merchant category duplicates across as_of**: The MCC lookup iterates all merchant_categories rows without date filtering. If descriptions differ across snapshots, the last-seen value wins (dictionary overwrite).
   - Confidence: MEDIUM
   - Evidence: [CardSpendingByMerchantProcessor.cs:31-38]

4. **Declined transactions included in spending**: Both Approved and Declined transactions are summed. Declined transactions may represent amounts that were NOT actually charged. This could inflate spending totals.
   - Confidence: HIGH
   - Evidence: [CardSpendingByMerchantProcessor.cs:43-54] no status filter

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Group by MCC | CardSpendingByMerchantProcessor.cs:43-54 |
| BR-2: txn_count | CardSpendingByMerchantProcessor.cs:52 |
| BR-3: total_spending | CardSpendingByMerchantProcessor.cs:52 |
| BR-4: MCC description lookup | CardSpendingByMerchantProcessor.cs:59 |
| BR-5: as_of from first row | CardSpendingByMerchantProcessor.cs:40 |
| BR-6: No authorization_status filter | CardSpendingByMerchantProcessor.cs:43-54 |
| BR-7: risk_level not in output | CardSpendingByMerchantProcessor.cs:10-13 |
| BR-8: No weekend fallback | CardSpendingByMerchantProcessor.cs (absence) |
| BR-9: Empty input handling | CardSpendingByMerchantProcessor.cs:23-26 |
| BR-10: Unused sourced columns | card_spending_by_merchant.json vs processor code |
| Writer config | card_spending_by_merchant.json:23-28 |

## Open Questions
None.
