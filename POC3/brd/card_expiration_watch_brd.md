# CardExpirationWatch — Business Requirements Document

## Overview
Identifies cards expiring within the next 90 days relative to the effective date, enriched with customer name and days-until-expiry calculation. Supports proactive card renewal outreach by surfacing cards approaching expiration.

## Output Type
ParquetFileWriter

## Writer Configuration
- **outputDirectory**: `Output/curated/card_expiration_watch/`
- **numParts**: 1
- **writeMode**: Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.cards | card_id, customer_id, card_type, expiration_date | Effective date range via DataSourcing; then filtered in External to 0-90 days until expiry | [card_expiration_watch.json:8-11], [CardExpirationWatchProcessor.cs:52-58] |
| datalake.customers | id, first_name, last_name | Effective date range via DataSourcing; used for name lookup | [card_expiration_watch.json:14-17], [CardExpirationWatchProcessor.cs:37-48] |

## Business Rules

BR-1: Weekend fallback — if `__maxEffectiveDate` is Saturday, targetDate is shifted to Friday (maxDate - 1 day). If Sunday, shifted to Friday (maxDate - 2 days). Weekday dates are used as-is.
- Confidence: HIGH
- Evidence: [CardExpirationWatchProcessor.cs:19-21]

BR-2: A card is included in the output if its expiration_date is between 0 and 90 days from the targetDate (inclusive on both ends: `daysUntilExpiry >= 0 && daysUntilExpiry <= 90`).
- Confidence: HIGH
- Evidence: [CardExpirationWatchProcessor.cs:58] `if (daysUntilExpiry >= 0 && daysUntilExpiry <= 90)`

BR-3: `days_until_expiry` is calculated as the difference in day numbers: `expirationDate.DayNumber - targetDate.DayNumber`. This produces a simple calendar-day difference.
- Confidence: HIGH
- Evidence: [CardExpirationWatchProcessor.cs:56] `var daysUntilExpiry = expirationDate.DayNumber - targetDate.DayNumber`

BR-4: Cards that have already expired (daysUntilExpiry < 0) are excluded.
- Confidence: HIGH
- Evidence: [CardExpirationWatchProcessor.cs:58] condition requires `daysUntilExpiry >= 0`

BR-5: Cards expiring more than 90 days out are excluded.
- Confidence: HIGH
- Evidence: [CardExpirationWatchProcessor.cs:58] condition requires `daysUntilExpiry <= 90`

BR-6: Customer names are looked up from the customers DataFrame by joining on `customer_id = id`.
- Confidence: HIGH
- Evidence: [CardExpirationWatchProcessor.cs:37-48] customerLookup keyed by customers.id

BR-7: The expiration_date value is handled for both `DateOnly` and `DateTime` types — if it comes as DateTime, it is converted to DateOnly.
- Confidence: HIGH
- Evidence: [CardExpirationWatchProcessor.cs:55] `rawExp is DateOnly d ? d : DateOnly.FromDateTime((DateTime)rawExp!)`

BR-8: All card rows from the cards DataFrame are evaluated (no pre-filtering by as_of date or card_status). Every card across all as_of dates in the effective date range is checked.
- Confidence: HIGH
- Evidence: [CardExpirationWatchProcessor.cs:52] `foreach (var card in cards.Rows)` — no date or status filter

BR-9: The output `as_of` is set to `targetDate` (weekend-adjusted), not the original effective date.
- Confidence: HIGH
- Evidence: [CardExpirationWatchProcessor.cs:73] `["as_of"] = targetDate`

BR-10: If `cards` DataFrame is null or empty, an empty DataFrame with correct schema is returned.
- Confidence: HIGH
- Evidence: [CardExpirationWatchProcessor.cs:30-34]

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| card_id | cards.card_id | Pass-through | [CardExpirationWatchProcessor.cs:65] |
| customer_id | cards.customer_id | Converted to int | [CardExpirationWatchProcessor.cs:66] |
| first_name | customers.first_name | Lookup by customer_id; "" if not found | [CardExpirationWatchProcessor.cs:67] |
| last_name | customers.last_name | Lookup by customer_id; "" if not found | [CardExpirationWatchProcessor.cs:68] |
| card_type | cards.card_type | Pass-through | [CardExpirationWatchProcessor.cs:69] |
| expiration_date | cards.expiration_date | DateOnly (converted from DateTime if needed) | [CardExpirationWatchProcessor.cs:70] |
| days_until_expiry | Derived | expirationDate.DayNumber - targetDate.DayNumber | [CardExpirationWatchProcessor.cs:71] |
| as_of | Derived | Set to targetDate (weekend-adjusted) | [CardExpirationWatchProcessor.cs:73] |

## Non-Deterministic Fields
None identified. All fields are deterministic given the same input data and effective date.

## Write Mode Implications
- **Overwrite** mode: The entire output directory is replaced on each run.
- For multi-day auto-advance runs, only the last effective date's output survives.
- The `days_until_expiry` value changes with each effective date, so historical runs will produce different results.

## Edge Cases

1. **Duplicate card rows across as_of dates**: Since the cards DataFrame spans the full effective date range and no as_of filter is applied within the External module, the same card_id may appear multiple times (once per as_of snapshot). Each snapshot is independently evaluated against the 90-day threshold. This can produce duplicate card_id entries in the output with the same expiry info but different source as_of dates (though the output as_of is always targetDate).
   - Confidence: HIGH
   - Evidence: [CardExpirationWatchProcessor.cs:52] iterates all rows; [DB: cards has ~2880 rows per as_of date]

2. **Weekend effective dates**: The cards table has weekday-only data. The weekend fallback to Friday means the targetDate will always be a date with data available in cards. However, the DataSourcing effective date range still includes the weekend date, and cards won't have rows for that as_of — only weekday as_of rows are loaded.
   - Confidence: HIGH
   - Evidence: [DB: cards has weekday-only data], [CardExpirationWatchProcessor.cs:19-21]

3. **Customer not found**: If a card's customer_id doesn't match any customer, names default to empty strings.
   - Confidence: HIGH
   - Evidence: [CardExpirationWatchProcessor.cs:61] `GetValueOrDefault(custId, ("", ""))`

4. **Expiration on targetDate itself**: A card with `expiration_date == targetDate` has `days_until_expiry = 0` and IS included (>= 0 check passes).
   - Confidence: HIGH
   - Evidence: [CardExpirationWatchProcessor.cs:58] `daysUntilExpiry >= 0`

5. **No card_status filter**: Expired, Blocked, and Active cards are all evaluated. A card with card_status = 'Expired' but an expiration_date within 90 days would still appear in the output.
   - Confidence: HIGH
   - Evidence: [CardExpirationWatchProcessor.cs:52-58] no card_status check

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Weekend fallback | CardExpirationWatchProcessor.cs:19-21 |
| BR-2: 0-90 day window | CardExpirationWatchProcessor.cs:58 |
| BR-3: days_until_expiry calc | CardExpirationWatchProcessor.cs:56 |
| BR-4: Expired cards excluded | CardExpirationWatchProcessor.cs:58 |
| BR-5: Far-future cards excluded | CardExpirationWatchProcessor.cs:58 |
| BR-6: Customer name lookup | CardExpirationWatchProcessor.cs:37-48 |
| BR-7: DateTime handling | CardExpirationWatchProcessor.cs:55 |
| BR-8: No as_of/status filter | CardExpirationWatchProcessor.cs:52 |
| BR-9: as_of = targetDate | CardExpirationWatchProcessor.cs:73 |
| BR-10: Empty input handling | CardExpirationWatchProcessor.cs:30-34 |

## Open Questions

1. **Duplicate cards across snapshots**: Since all as_of snapshots of the cards table are iterated without deduplication, a card will appear in the output once per as_of date where it exists in the effective date range. In single-day gap-fill runs, this is typically a single snapshot, but for multi-day ranges it could produce many duplicate entries. Is this intentional?
   - Confidence: MEDIUM — likely a non-issue for normal single-day runs but could affect backfill scenarios.
