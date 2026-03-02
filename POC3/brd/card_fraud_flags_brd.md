# CardFraudFlags — Business Requirements Document

## Overview
Identifies card transactions that are flagged as potentially fraudulent based on two criteria: the transaction's merchant category is classified as "High" risk AND the transaction amount exceeds $500. Outputs a detail-level record for each flagged transaction.

## Output Type
CsvFileWriter

## Writer Configuration
- **outputFile**: `Output/curated/card_fraud_flags.csv`
- **includeHeader**: true
- **writeMode**: Overwrite
- **lineEnding**: LF
- **trailerFormat**: not configured (no trailer)

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.card_transactions | card_txn_id, card_id, customer_id, merchant_name, merchant_category_code, amount, txn_timestamp | Effective date range via DataSourcing; then filtered in External by risk_level='High' AND amount > 500 | [card_fraud_flags.json:8-11], [CardFraudFlagsProcessor.cs:43-50] |
| datalake.merchant_categories | mcc_code, mcc_description, risk_level | Effective date range via DataSourcing; used for risk level lookup | [card_fraud_flags.json:14-17], [CardFraudFlagsProcessor.cs:30-39] |

## Business Rules

BR-1: Each transaction's merchant_category_code is looked up against the merchant_categories table to determine the risk_level.
- Confidence: HIGH
- Evidence: [CardFraudFlagsProcessor.cs:30-39] riskLookup dictionary keyed by mcc_code

BR-2: A transaction is flagged (included in output) only if BOTH conditions are met: risk_level == "High" AND amount > $500 (strictly greater than, not >=).
- Confidence: HIGH
- Evidence: [CardFraudFlagsProcessor.cs:50] `if (riskLevel == "High" && amount > 500m)`

BR-3: The $500 threshold is a hardcoded magic value.
- Confidence: HIGH
- Evidence: [CardFraudFlagsProcessor.cs:49] comment `// AP7: Magic value — hardcoded $500 threshold`; [CardFraudFlagsProcessor.cs:50] literal `500m`

BR-4: Transaction amounts are rounded using Banker's rounding (MidpointRounding.ToEven) to 2 decimal places before comparison and output.
- Confidence: HIGH
- Evidence: [CardFraudFlagsProcessor.cs:47] `Math.Round(Convert.ToDecimal(txn["amount"]), 2, MidpointRounding.ToEven)`

BR-5: Transactions whose merchant_category_code is not found in the merchant_categories lookup get an empty string for risk_level, and will not match the "High" filter.
- Confidence: HIGH
- Evidence: [CardFraudFlagsProcessor.cs:45] `riskLookup.ContainsKey(mccCode) ? riskLookup[mccCode] : ""`

BR-6: The output includes all transactions across all as_of dates in the effective date range — no date filtering is applied within the External module.
- Confidence: HIGH
- Evidence: [CardFraudFlagsProcessor.cs:42] `foreach (var txn in cardTransactions.Rows)` — iterates all rows without date filter

BR-7: Only two MCC codes have risk_level = "High" in the data: 5094 (Precious Metals) and 7995 (Gambling).
- Confidence: HIGH
- Evidence: [DB query: merchant_categories with risk_level='High'] returns mcc_codes 5094, 7995

BR-8: The `mcc_description` column is NOT included in the output — only `mcc_code` and `risk_level` from the lookup.
- Confidence: HIGH
- Evidence: [CardFraudFlagsProcessor.cs:10-14] output columns list does not include mcc_description

BR-9: If `card_transactions` is null or empty, an empty DataFrame with correct schema is returned.
- Confidence: HIGH
- Evidence: [CardFraudFlagsProcessor.cs:23-27]

BR-10: No weekend fallback is applied — all transactions including weekends are processed.
- Confidence: HIGH
- Evidence: [CardFraudFlagsProcessor.cs] — no weekend fallback logic present

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| card_txn_id | card_transactions.card_txn_id | Pass-through | [CardFraudFlagsProcessor.cs:53] |
| card_id | card_transactions.card_id | Pass-through | [CardFraudFlagsProcessor.cs:54] |
| customer_id | card_transactions.customer_id | Pass-through | [CardFraudFlagsProcessor.cs:55] |
| merchant_name | card_transactions.merchant_name | Pass-through | [CardFraudFlagsProcessor.cs:56] |
| mcc_code | card_transactions.merchant_category_code | Pass-through (renamed) | [CardFraudFlagsProcessor.cs:57] |
| risk_level | merchant_categories.risk_level | Lookup by mcc_code | [CardFraudFlagsProcessor.cs:58] |
| amount | card_transactions.amount | Banker's rounding to 2 decimal places | [CardFraudFlagsProcessor.cs:59] |
| txn_timestamp | card_transactions.txn_timestamp | Pass-through | [CardFraudFlagsProcessor.cs:60] |
| as_of | card_transactions.as_of | Pass-through | [CardFraudFlagsProcessor.cs:61] |

## Non-Deterministic Fields
None identified. All fields are deterministic given the same input data.

## Write Mode Implications
- **Overwrite** mode: The CSV file is completely replaced on each run.
- For multi-day auto-advance runs, only the last effective date's output survives.
- Since the External module does NOT filter by date within its logic, all transactions in the DataSourcing date range are evaluated. In single-day gap-fill, this means one day's transactions per run.

## Edge Cases

1. **Banker's rounding at $500 boundary**: A transaction with raw amount of $500.005 would round to $500.00 (Banker's rounding rounds 0.005 to 0.00 since 0 is even). This $500.00 would NOT pass the `> 500m` check. A raw amount of $500.015 would round to $500.02, passing the check.
   - Confidence: HIGH
   - Evidence: [CardFraudFlagsProcessor.cs:47,50] rounding before comparison

2. **MCC codes not in lookup**: If card_transactions has a merchant_category_code not present in merchant_categories, it gets empty risk_level and is excluded. The data shows 17 distinct MCC codes in card_transactions vs 20 in merchant_categories — all transaction MCCs are a subset of the categories table, so this is unlikely to trigger.
   - Confidence: HIGH
   - Evidence: [DB queries: 17 distinct MCCs in transactions, all 17 exist in merchant_categories]

3. **No authorization_status filter**: Both Approved and Declined transactions are evaluated for fraud flags. A declined $600 transaction at a high-risk merchant would still appear in the output.
   - Confidence: HIGH
   - Evidence: [card_fraud_flags.json:10] authorization_status not sourced; [CardFraudFlagsProcessor.cs] no status filter

4. **Merchant category duplicates across as_of dates**: The merchant_categories lookup iterates all rows without date filtering. If the same mcc_code appears with different risk_levels across dates, the last-seen value wins (dictionary overwrite).
   - Confidence: MEDIUM
   - Evidence: [CardFraudFlagsProcessor.cs:33-39] dictionary overwrite behavior

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: MCC risk lookup | CardFraudFlagsProcessor.cs:30-39 |
| BR-2: Dual filter (High + >$500) | CardFraudFlagsProcessor.cs:50 |
| BR-3: Hardcoded $500 threshold | CardFraudFlagsProcessor.cs:49-50 |
| BR-4: Banker's rounding | CardFraudFlagsProcessor.cs:47 |
| BR-5: Unknown MCC handling | CardFraudFlagsProcessor.cs:45 |
| BR-6: No date filter in External | CardFraudFlagsProcessor.cs:42 |
| BR-7: High-risk MCC codes | DB query on merchant_categories |
| BR-8: No mcc_description in output | CardFraudFlagsProcessor.cs:10-14 |
| BR-9: Empty input handling | CardFraudFlagsProcessor.cs:23-27 |
| BR-10: No weekend fallback | CardFraudFlagsProcessor.cs (absence of logic) |
| Writer config | card_fraud_flags.json:25-30 |

## Open Questions
None.
