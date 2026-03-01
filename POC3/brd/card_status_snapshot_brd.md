# CardStatusSnapshot — Business Requirements Document

## Overview
Produces a daily count of cards grouped by card_status, providing a snapshot view of the card portfolio's status distribution over time. Simple aggregation with no External module — uses only SQL Transformation.

## Output Type
ParquetFileWriter

## Writer Configuration
- **outputDirectory**: `Output/curated/card_status_snapshot/`
- **numParts**: 50
- **writeMode**: Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.cards | card_id, customer_id, card_type, card_number_masked, expiration_date, card_status | Effective date range via DataSourcing | [card_status_snapshot.json:8-11] |

## Business Rules

BR-1: Cards are grouped by `card_status` and `as_of`, producing one row per status per date.
- Confidence: HIGH
- Evidence: [card_status_snapshot.json:15] SQL `GROUP BY c.card_status, c.as_of`

BR-2: `card_count` is the COUNT(*) of cards per card_status/as_of group.
- Confidence: HIGH
- Evidence: [card_status_snapshot.json:15] SQL `COUNT(*) AS card_count`

BR-3: Only three card_status values exist: 'Active', 'Blocked', 'Expired'.
- Confidence: HIGH
- Evidence: [DB query: `SELECT DISTINCT card_status FROM datalake.cards`] returns exactly {Active, Blocked, Expired}

BR-4: Several sourced columns are NOT used in the SQL: card_id, customer_id, card_type, card_number_masked, expiration_date. Only card_status and as_of (implicit) are used.
- Confidence: HIGH
- Evidence: [card_status_snapshot.json:10] sources 6 columns; [card_status_snapshot.json:15] SQL only references card_status and as_of

BR-5: The output is split across 50 Parquet part files. Given the small number of output rows (3 statuses x N dates), most part files will likely be empty or contain very few rows.
- Confidence: HIGH
- Evidence: [card_status_snapshot.json:20] `"numParts": 50`

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| card_status | cards.card_status | Grouped by | [card_status_snapshot.json:15] |
| card_count | cards | COUNT(*) per card_status/as_of group | [card_status_snapshot.json:15] |
| as_of | cards.as_of | Grouped by (pass-through from DataSourcing) | [card_status_snapshot.json:15] |

## Non-Deterministic Fields
None identified. All fields are deterministic given the same input data and effective date range.

## Write Mode Implications
- **Overwrite** mode: The entire output directory is replaced on each run.
- For multi-day auto-advance runs, only the last effective date's output survives.
- Since this is a pure SQL transformation with no External module date filtering, the output includes rows for every as_of date within the effective date range. For single-day gap-fill, this is one date's data.

## Edge Cases

1. **Cards table has weekday-only data**: The cards table has no Saturday/Sunday snapshots. If the effective date falls on a weekend, DataSourcing will find no matching rows, and the output will be empty (no card status rows for that date).
   - Confidence: HIGH
   - Evidence: [DB: cards table has weekday-only as_of dates (no dow 0 or 6)]

2. **50-part file split with few rows**: With only 3 card statuses x 1 date = 3 rows in a typical single-day run, splitting into 50 parts means most part files will be empty. The ParquetFileWriter distributes rows round-robin across parts per Architecture.md.
   - Confidence: HIGH
   - Evidence: [card_status_snapshot.json:20] numParts=50 with ~3 expected output rows

3. **No status filtering**: All card statuses are included — Active, Blocked, and Expired cards all appear with their counts.
   - Confidence: HIGH
   - Evidence: [card_status_snapshot.json:15] no WHERE clause

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Group by card_status, as_of | card_status_snapshot.json:15 (SQL GROUP BY) |
| BR-2: card_count = COUNT(*) | card_status_snapshot.json:15 (SQL COUNT) |
| BR-3: Three status values | DB query on datalake.cards |
| BR-4: Unused sourced columns | card_status_snapshot.json:10 vs :15 |
| BR-5: 50 part files | card_status_snapshot.json:20 |
| Writer config | card_status_snapshot.json:18-22 |

## Open Questions
None.
