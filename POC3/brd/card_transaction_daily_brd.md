# CardTransactionDaily — Business Requirements Document

## Overview
Produces a daily summary of card transactions grouped by card type, including transaction count, total amount, and average amount. Appends a special "MONTHLY_TOTAL" summary row on the last day of each month.

## Output Type
CsvFileWriter

## Writer Configuration
- **outputFile**: `Output/curated/card_transaction_daily.csv`
- **includeHeader**: true
- **trailerFormat**: `TRAILER|{row_count}|{date}`
- **writeMode**: Overwrite
- **lineEnding**: LF

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.card_transactions | card_txn_id, card_id, customer_id, merchant_name, merchant_category_code, amount, txn_timestamp, authorization_status | Effective date range via DataSourcing; all rows used in External | [card_transaction_daily.json:8-11], [CardTransactionDailyProcessor.cs:47-58] |
| datalake.accounts | account_id, customer_id, account_type, account_status | Effective date range via DataSourcing; **sourced but never used by External module** | [card_transaction_daily.json:14-17], [CardTransactionDailyProcessor.cs:26] AP1 comment |
| datalake.customers | id, first_name, last_name | Effective date range via DataSourcing; **sourced but never used by External module** | [card_transaction_daily.json:20-22], [CardTransactionDailyProcessor.cs:26] AP1 comment |
| datalake.cards | card_id, customer_id, card_type | Effective date range via DataSourcing; used for card_type lookup | [card_transaction_daily.json:26-29], [CardTransactionDailyProcessor.cs:36-41] |

## Business Rules

BR-1: Transactions are enriched with `card_type` via a lookup from the cards table on `card_id`.
- Confidence: HIGH
- Evidence: [CardTransactionDailyProcessor.cs:36-41] cardTypeLookup dictionary keyed by card_id

BR-2: Transactions are grouped by `card_type`, producing one output row per card type.
- Confidence: HIGH
- Evidence: [CardTransactionDailyProcessor.cs:44-58] groups dictionary keyed by card_type

BR-3: `txn_count` is the number of transactions per card type.
- Confidence: HIGH
- Evidence: [CardTransactionDailyProcessor.cs:57] `current.count + 1`

BR-4: `total_amount` is the sum of transaction amounts per card type.
- Confidence: HIGH
- Evidence: [CardTransactionDailyProcessor.cs:57] `current.total + amount`

BR-5: `avg_amount` is calculated as `total_amount / txn_count`, rounded to 2 decimal places (default .NET rounding, which is Banker's rounding / MidpointRounding.ToEven).
- Confidence: HIGH
- Evidence: [CardTransactionDailyProcessor.cs:63-64] `Math.Round(kvp.Value.total / kvp.Value.count, 2)`

BR-6: If a card_id in transactions is not found in the cards lookup, the card_type defaults to "Unknown".
- Confidence: HIGH
- Evidence: [CardTransactionDailyProcessor.cs:51] `cardTypeLookup.ContainsKey(cardId) ? cardTypeLookup[cardId] : "Unknown"`

BR-7: On the last day of the month (when `maxDate.Day == DateTime.DaysInMonth(maxDate.Year, maxDate.Month)`), an additional "MONTHLY_TOTAL" summary row is appended with the aggregate totals across all card types.
- Confidence: HIGH
- Evidence: [CardTransactionDailyProcessor.cs:78-91] end-of-month boundary logic

BR-8: The MONTHLY_TOTAL row has card_type = "MONTHLY_TOTAL", txn_count = sum of all groups' counts, total_amount = sum of all groups' totals, and avg_amount = total_amount / txn_count.
- Confidence: HIGH
- Evidence: [CardTransactionDailyProcessor.cs:80-90]

BR-9: The `accounts` and `customers` DataFrames are sourced but never used by the External module. This is explicitly noted as dead data sourcing (AP1 pattern).
- Confidence: HIGH
- Evidence: [CardTransactionDailyProcessor.cs:26] comment `// AP1: accounts and customers sourced but never used (dead-end)`

BR-10: The `as_of` value for all output rows (including MONTHLY_TOTAL) is taken from the first row of card_transactions.
- Confidence: HIGH
- Evidence: [CardTransactionDailyProcessor.cs:45] `var asOf = cardTransactions.Rows[0]["as_of"]`; used at lines 72 and 89

BR-11: No weekend fallback is applied. The end-of-month check uses `maxDate` (the original `__maxEffectiveDate`, not a weekend-adjusted value).
- Confidence: HIGH
- Evidence: [CardTransactionDailyProcessor.cs:78] uses `maxDate` directly; no weekend fallback code

BR-12: If either card_transactions or cards is null/empty, an empty DataFrame is returned.
- Confidence: HIGH
- Evidence: [CardTransactionDailyProcessor.cs:28-32]

BR-13: Several sourced columns from card_transactions are unused: card_txn_id, customer_id, merchant_name, merchant_category_code, txn_timestamp, authorization_status.
- Confidence: HIGH
- Evidence: [card_transaction_daily.json:10-11] vs [CardTransactionDailyProcessor.cs] — only card_id and amount are used

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| card_type | cards.card_type | Lookup by card_id; "Unknown" if not found; "MONTHLY_TOTAL" for summary row | [CardTransactionDailyProcessor.cs:68,83] |
| txn_count | card_transactions | COUNT per card_type | [CardTransactionDailyProcessor.cs:69,84] |
| total_amount | card_transactions.amount | SUM(amount) per card_type | [CardTransactionDailyProcessor.cs:70,85] |
| avg_amount | Derived | total_amount / txn_count, rounded to 2 dp | [CardTransactionDailyProcessor.cs:71,88] |
| as_of | card_transactions.Rows[0]["as_of"] | First row's as_of value | [CardTransactionDailyProcessor.cs:72,89] |

## Non-Deterministic Fields
None identified. All fields are deterministic given the same input data and effective date.

## Write Mode Implications
- **Overwrite** mode: The CSV file is completely replaced on each run.
- For multi-day auto-advance runs, only the last effective date's output survives.
- The trailer `{date}` token reflects `__maxEffectiveDate`.
- The trailer `{row_count}` counts all data rows including the MONTHLY_TOTAL row if present.

## Edge Cases

1. **End-of-month on weekends**: If the last day of the month falls on a Saturday or Sunday, and the effective date lands on that day, the MONTHLY_TOTAL row will be generated. However, the cards table has no weekend data, so the card_type lookup may be incomplete (using only non-weekend snapshots from the DataSourcing range). If only a single weekend date is in range, cards may have zero rows, triggering the empty output guard.
   - Confidence: MEDIUM
   - Evidence: [CardTransactionDailyProcessor.cs:78] checks maxDate; [DB: cards has weekday-only data]

2. **Cards not found for transactions**: If card_transactions includes card_ids not present in the cards DataFrame (possible if cards has no data for the effective date), those transactions get card_type = "Unknown".
   - Confidence: HIGH
   - Evidence: [CardTransactionDailyProcessor.cs:51]

3. **Trailer row count includes MONTHLY_TOTAL**: The CsvFileWriter's `{row_count}` token counts data rows. If the MONTHLY_TOTAL row is present, it adds 1 to the row count.
   - Confidence: HIGH
   - Evidence: [Architecture.md:241] row_count = data rows excluding header/trailer

4. **as_of from first transaction row**: The as_of value is whatever the first card_transactions row has. This is not necessarily __maxEffectiveDate. In single-day runs this should match, but the value depends on DataSourcing row ordering.
   - Confidence: MEDIUM
   - Evidence: [CardTransactionDailyProcessor.cs:45]

5. **Division by zero**: If txn_count is 0, avg_amount defaults to 0m. This guard applies both to per-card-type rows and the MONTHLY_TOTAL row.
   - Confidence: HIGH
   - Evidence: [CardTransactionDailyProcessor.cs:63,88] conditional check `kvp.Value.count > 0` / `totalCount > 0`

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: card_type lookup | CardTransactionDailyProcessor.cs:36-41 |
| BR-2: Group by card_type | CardTransactionDailyProcessor.cs:44-58 |
| BR-3: txn_count | CardTransactionDailyProcessor.cs:57 |
| BR-4: total_amount | CardTransactionDailyProcessor.cs:57 |
| BR-5: avg_amount rounding | CardTransactionDailyProcessor.cs:63-64 |
| BR-6: Unknown card_type fallback | CardTransactionDailyProcessor.cs:51 |
| BR-7: End-of-month MONTHLY_TOTAL | CardTransactionDailyProcessor.cs:78-91 |
| BR-8: MONTHLY_TOTAL calculation | CardTransactionDailyProcessor.cs:80-90 |
| BR-9: Dead accounts/customers | CardTransactionDailyProcessor.cs:26 |
| BR-10: as_of from first row | CardTransactionDailyProcessor.cs:45 |
| BR-11: No weekend fallback | CardTransactionDailyProcessor.cs (absence) |
| BR-12: Empty input guard | CardTransactionDailyProcessor.cs:28-32 |
| BR-13: Unused sourced columns | card_transaction_daily.json vs processor code |
| Writer config | card_transaction_daily.json:39-45 |
| Trailer format | card_transaction_daily.json:43 |

## Open Questions
None.
