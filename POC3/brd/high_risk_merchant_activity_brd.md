# HighRiskMerchantActivity — Business Requirements Document

## Overview
Extracts all card transactions at merchants classified as "High" risk, enriched with merchant category description. Produces a detail-level record for each qualifying transaction, supporting risk monitoring and regulatory reporting.

## Output Type
CsvFileWriter

## Writer Configuration
- **outputFile**: `Output/curated/high_risk_merchant_activity.csv`
- **includeHeader**: true
- **writeMode**: Overwrite
- **lineEnding**: LF
- **trailerFormat**: not configured (no trailer)

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.card_transactions | card_txn_id, card_id, customer_id, merchant_name, merchant_category_code, amount, txn_timestamp | Effective date range via DataSourcing; then filtered in External by risk_level='High' | [high_risk_merchant_activity.json:8-11], [HighRiskMerchantActivityProcessor.cs:44-65] |
| datalake.merchant_categories | mcc_code, mcc_description, risk_level | Effective date range via DataSourcing; used for risk level and description lookup | [high_risk_merchant_activity.json:14-17], [HighRiskMerchantActivityProcessor.cs:30-41] |

## Business Rules

BR-1: Each transaction's merchant_category_code is looked up against the merchant_categories table to determine risk_level and mcc_description.
- Confidence: HIGH
- Evidence: [HighRiskMerchantActivityProcessor.cs:30-41] mccLookup dictionary with (description, riskLevel) tuple

BR-2: A transaction is included in the output only if its MCC code has risk_level == "High".
- Confidence: HIGH
- Evidence: [HighRiskMerchantActivityProcessor.cs:52] `if (mccInfo.riskLevel == "High")`

BR-3: The "High" risk level string is a hardcoded magic value.
- Confidence: HIGH
- Evidence: [HighRiskMerchantActivityProcessor.cs:51] comment `// AP7: Magic value — hardcoded risk level string`

BR-4: Transactions whose MCC code is NOT found in the merchant_categories lookup are skipped entirely (excluded from output).
- Confidence: HIGH
- Evidence: [HighRiskMerchantActivityProcessor.cs:48] `if (!mccLookup.ContainsKey(mccCode)) continue;`

BR-5: Only two MCC codes have risk_level = "High": 5094 (Precious Metals) and 7995 (Gambling).
- Confidence: HIGH
- Evidence: [DB query: merchant_categories WHERE risk_level='High'] returns 5094, 7995

BR-6: No amount threshold is applied — all transactions at high-risk merchants are included regardless of amount (unlike CardFraudFlags which adds a $500 threshold).
- Confidence: HIGH
- Evidence: [HighRiskMerchantActivityProcessor.cs:44-65] no amount filter

BR-7: No weekend fallback is applied — all transaction dates including weekends are processed.
- Confidence: HIGH
- Evidence: [HighRiskMerchantActivityProcessor.cs] — no weekend fallback logic

BR-8: The `amount` field passes through without rounding (unlike CardFraudFlags which applies Banker's rounding).
- Confidence: HIGH
- Evidence: [HighRiskMerchantActivityProcessor.cs:60] `["amount"] = txn["amount"]` — direct pass-through

BR-9: If `card_transactions` is null or empty, an empty DataFrame with correct schema is returned.
- Confidence: HIGH
- Evidence: [HighRiskMerchantActivityProcessor.cs:23-27]

BR-10: The card_id and customer_id columns are sourced from card_transactions but NOT included in the output.
- Confidence: HIGH
- Evidence: [high_risk_merchant_activity.json:10] sources card_id, customer_id; [HighRiskMerchantActivityProcessor.cs:10-13] output columns don't include them

BR-11: The risk_level column is NOT included in the output schema despite being used for filtering.
- Confidence: HIGH
- Evidence: [HighRiskMerchantActivityProcessor.cs:10-13] output columns: card_txn_id, merchant_name, mcc_code, mcc_description, amount, txn_timestamp, as_of

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| card_txn_id | card_transactions.card_txn_id | Pass-through | [HighRiskMerchantActivityProcessor.cs:55] |
| merchant_name | card_transactions.merchant_name | Pass-through | [HighRiskMerchantActivityProcessor.cs:56] |
| mcc_code | card_transactions.merchant_category_code | Pass-through (renamed) | [HighRiskMerchantActivityProcessor.cs:57] |
| mcc_description | merchant_categories.mcc_description | Lookup by mcc_code | [HighRiskMerchantActivityProcessor.cs:58] |
| amount | card_transactions.amount | Pass-through (no rounding) | [HighRiskMerchantActivityProcessor.cs:59] |
| txn_timestamp | card_transactions.txn_timestamp | Pass-through | [HighRiskMerchantActivityProcessor.cs:60] |
| as_of | card_transactions.as_of | Pass-through | [HighRiskMerchantActivityProcessor.cs:61] |

## Non-Deterministic Fields
None identified. All fields are deterministic given the same input data.

## Write Mode Implications
- **Overwrite** mode: The CSV file is completely replaced on each run.
- For multi-day auto-advance runs, only the last effective date's output survives.
- Since no date filtering is applied within the External module, all transactions in the DataSourcing date range are evaluated.

## Edge Cases

1. **MCC codes not in merchant_categories**: If a transaction has an MCC code not found in the lookup, it is skipped entirely (even if it might be high-risk). Per DB analysis, all 17 transaction MCC codes are a subset of the 20 categories, and neither high-risk MCC code (5094, 7995) appears in the transaction MCC codes, which means no high-risk transactions exist in the current data.
   - Confidence: HIGH
   - Evidence: [DB: card_transactions MCCs = {4511, 4814, 5200, 5311, 5411, 5541, 5691, 5732, 5812, 5814, 5912, 5942, 5944, 5999, 7011, 7832, 8011}; high-risk MCCs = {5094, 7995}] — no overlap means output is likely empty

2. **Potentially empty output**: Given that the high-risk MCC codes (5094/Precious Metals, 7995/Gambling) do not appear in the card_transactions data, this job may produce an empty output file (header only, no data rows) for all dates.
   - Confidence: HIGH
   - Evidence: [DB: DISTINCT merchant_category_code in card_transactions doesn't include 5094 or 7995]

3. **Merchant category duplicates across as_of**: The MCC lookup iterates all rows without date filtering. If descriptions or risk_levels differ across snapshots, the last-seen value wins (dictionary overwrite).
   - Confidence: MEDIUM
   - Evidence: [HighRiskMerchantActivityProcessor.cs:33-41]

4. **No authorization_status filter**: The job does not source or filter by authorization_status, so both approved and declined transactions at high-risk merchants would be included.
   - Confidence: HIGH
   - Evidence: [high_risk_merchant_activity.json:10-11] authorization_status not sourced

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: MCC lookup | HighRiskMerchantActivityProcessor.cs:30-41 |
| BR-2: risk_level='High' filter | HighRiskMerchantActivityProcessor.cs:52 |
| BR-3: Hardcoded risk level | HighRiskMerchantActivityProcessor.cs:51 |
| BR-4: Unknown MCC skipped | HighRiskMerchantActivityProcessor.cs:48 |
| BR-5: High-risk MCC codes | DB query on merchant_categories |
| BR-6: No amount threshold | HighRiskMerchantActivityProcessor.cs:44-65 (absence) |
| BR-7: No weekend fallback | HighRiskMerchantActivityProcessor.cs (absence) |
| BR-8: No amount rounding | HighRiskMerchantActivityProcessor.cs:60 |
| BR-9: Empty input handling | HighRiskMerchantActivityProcessor.cs:23-27 |
| BR-10: card_id/customer_id not in output | HighRiskMerchantActivityProcessor.cs:10-13 |
| BR-11: risk_level not in output | HighRiskMerchantActivityProcessor.cs:10-13 |
| Writer config | high_risk_merchant_activity.json:23-28 |

## Open Questions

1. **Always-empty output**: The high-risk MCC codes (5094, 7995) do not appear in the card_transactions data. Unless new transaction data includes these codes, this job will always produce an empty CSV (header only). Is this expected behavior or a data gap?
   - Confidence: HIGH that output is currently empty; LOW confidence on whether this is intentional.
