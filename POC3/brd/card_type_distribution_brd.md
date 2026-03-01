# CardTypeDistribution — Business Requirements Document

## Overview
Calculates the distribution of cards by card type (Credit/Debit), producing each type's count and percentage of total. Percentage is computed using double-precision floating point, introducing potential epsilon-level precision variations.

## Output Type
CsvFileWriter

## Writer Configuration
- **outputFile**: `Output/curated/card_type_distribution.csv`
- **includeHeader**: true
- **trailerFormat**: `TRAILER|{row_count}|{date}`
- **writeMode**: Overwrite
- **lineEnding**: LF

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.cards | card_id, customer_id, card_type, card_status | Effective date range via DataSourcing; all rows used (no status filter) | [card_type_distribution.json:8-11], [CardTypeDistributionProcessor.cs:29-35] |
| datalake.card_transactions | card_txn_id, card_id, amount | Effective date range via DataSourcing; **sourced but never used by External module** | [card_type_distribution.json:14-17], [CardTypeDistributionProcessor.cs — no reference to card_transactions] |

## Business Rules

BR-1: Cards are grouped by `card_type`, counting the number of cards per type.
- Confidence: HIGH
- Evidence: [CardTypeDistributionProcessor.cs:28-35] counts dictionary keyed by card_type

BR-2: `card_count` is the number of card rows per card_type (including all as_of snapshots).
- Confidence: HIGH
- Evidence: [CardTypeDistributionProcessor.cs:34] `counts[cardType]++`

BR-3: `pct_of_total` is calculated as `card_count / total_cards` using double-precision floating point division. The result is a fraction (0 to 1), not a percentage (0 to 100).
- Confidence: HIGH
- Evidence: [CardTypeDistributionProcessor.cs:43-46] `double pct = count / total` where both are doubles

BR-4: The use of double (rather than decimal) for percentage calculation introduces IEEE 754 floating-point epsilon issues. The result may have tiny precision artifacts (e.g., 0.4999999999 instead of 0.5).
- Confidence: HIGH
- Evidence: [CardTypeDistributionProcessor.cs:39] comment `// W6: Double epsilon — use double instead of decimal for percentage`

BR-5: `totalCards` is the total count of ALL card rows across all card types and all as_of dates (`cards.Count`).
- Confidence: HIGH
- Evidence: [CardTypeDistributionProcessor.cs:37] `int totalCards = cards.Count`

BR-6: The `card_transactions` DataFrame is sourced but never used by the External module. Dead data sourcing.
- Confidence: HIGH
- Evidence: [card_type_distribution.json:14-17] sources card_transactions; [CardTypeDistributionProcessor.cs] — no reference to card_transactions

BR-7: The `card_status` column is sourced but not used — all cards regardless of status contribute to the distribution.
- Confidence: HIGH
- Evidence: [card_type_distribution.json:11] sources card_status; [CardTypeDistributionProcessor.cs:29-35] no status filter

BR-8: The `as_of` value for all output rows is taken from the first row of cards.
- Confidence: HIGH
- Evidence: [CardTypeDistributionProcessor.cs:25] `var asOf = cards.Rows[0]["as_of"]`

BR-9: If `cards` is null or empty, an empty DataFrame with correct schema is returned.
- Confidence: HIGH
- Evidence: [CardTypeDistributionProcessor.cs:19-23]

BR-10: Only two card_type values exist: 'Credit' and 'Debit'.
- Confidence: HIGH
- Evidence: [DB query: `SELECT DISTINCT card_type FROM datalake.cards`]

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| card_type | cards.card_type | Grouped by | [CardTypeDistributionProcessor.cs:50] |
| card_count | cards | COUNT per card_type | [CardTypeDistributionProcessor.cs:51] |
| pct_of_total | Derived | (double)card_count / (double)totalCards — fraction, not percentage | [CardTypeDistributionProcessor.cs:52] |
| as_of | cards.Rows[0]["as_of"] | First row's as_of value | [CardTypeDistributionProcessor.cs:53] |

## Non-Deterministic Fields
None identified. However, the double-precision pct_of_total may exhibit platform-dependent floating-point representation differences in serialized output (e.g., different trailing digits).

## Write Mode Implications
- **Overwrite** mode: The CSV file is completely replaced on each run.
- For multi-day auto-advance runs, only the last effective date's output survives.
- The trailer `{date}` token reflects `__maxEffectiveDate`.

## Edge Cases

1. **Multi-date count inflation**: Since cards are counted across ALL as_of dates in the DataSourcing range, the card_count and totalCards values include duplicates of the same card from different snapshots. For a single-day run this is fine, but for multi-day ranges the counts would be inflated (e.g., 2880 cards x 5 dates = 14400 total). The percentages would still be correct since the inflation applies equally to numerator and denominator.
   - Confidence: HIGH
   - Evidence: [CardTypeDistributionProcessor.cs:28-37] counts all rows without date deduplication

2. **Weekend effective dates**: The cards table has no weekend data. If the effective date is a weekend with no fallback logic, DataSourcing may return empty data, triggering the empty output guard.
   - Confidence: HIGH
   - Evidence: [DB: cards weekday-only], [CardTypeDistributionProcessor.cs:19-23] empty guard

3. **Double precision in CSV**: The pct_of_total is a double value serialized to CSV. The CsvFileWriter will produce its default string representation, which may include many decimal places (e.g., 0.5000000000000001).
   - Confidence: MEDIUM
   - Evidence: [CardTypeDistributionProcessor.cs:39] W6 comment about double epsilon

4. **Trailer row_count**: With only 2 card types, the trailer row_count will typically be 2.
   - Confidence: HIGH
   - Evidence: [BR-10: two card types], [card_type_distribution.json:30] trailer format

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Group by card_type | CardTypeDistributionProcessor.cs:28-35 |
| BR-2: card_count | CardTypeDistributionProcessor.cs:34 |
| BR-3: pct_of_total as fraction | CardTypeDistributionProcessor.cs:43-46 |
| BR-4: Double epsilon concern | CardTypeDistributionProcessor.cs:39 (W6 comment) |
| BR-5: totalCards from all rows | CardTypeDistributionProcessor.cs:37 |
| BR-6: Dead card_transactions sourcing | card_type_distribution.json:14-17 vs processor |
| BR-7: Unused card_status | card_type_distribution.json:11 vs processor |
| BR-8: as_of from first row | CardTypeDistributionProcessor.cs:25 |
| BR-9: Empty input guard | CardTypeDistributionProcessor.cs:19-23 |
| BR-10: Two card types | DB query on datalake.cards |
| Writer config | card_type_distribution.json:22-29 |
| Trailer format | card_type_distribution.json:27 |

## Open Questions
None.
