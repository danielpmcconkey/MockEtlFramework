# CardExpirationWatch — Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01 | BR-1 | Weekend fallback: Saturday shifts to Friday |
| TC-02 | BR-1 | Weekend fallback: Sunday shifts to Friday |
| TC-03 | BR-1 | Weekday effective date used as-is |
| TC-04 | BR-2 | Cards within 0-90 day expiry window are included |
| TC-05 | BR-3 | days_until_expiry = expirationDate.DayNumber - targetDate.DayNumber |
| TC-06 | BR-4 | Expired cards (days_until_expiry < 0) are excluded |
| TC-07 | BR-5 | Cards expiring > 90 days out are excluded |
| TC-08 | BR-6 | Customer names resolved via LEFT JOIN on customer_id = id |
| TC-09 | BR-7 | expiration_date DateTime-to-DateOnly conversion handled |
| TC-10 | BR-8 | All card rows across all as_of snapshots evaluated (no as_of/status filter) |
| TC-11 | BR-9 | Output as_of is weekend-adjusted targetDate |
| TC-12 | BR-10 | Empty cards input produces empty output with correct schema |
| TC-13 | BR-2 | Boundary: card expiring on targetDate itself (days_until_expiry = 0) included |
| TC-14 | BR-2 | Boundary: card expiring exactly 90 days from targetDate included |
| TC-15 | BR-2 | Boundary: card expiring 91 days from targetDate excluded |
| TC-16 | BR-4 | Boundary: card expired yesterday (days_until_expiry = -1) excluded |
| TC-17 | BR-6 | Customer not found defaults names to empty strings |
| TC-18 | BR-8 | Duplicate card rows across as_of snapshots produce duplicate output rows |
| TC-19 | BR-8 | Cards with any card_status (Active, Expired, Blocked) are evaluated |
| TC-20 | — | Output column order and schema verification |
| TC-21 | — | Type coercion: External module converts string dates to DateOnly and long to int |
| TC-22 | — | Proofmark comparison: strict match with no FUZZY/EXCLUDED columns |
| TC-23 | BR-3 | julianday precision for days_until_expiry calculation |
| TC-24 | BR-6, BR-12 (implied) | Customer deduplication: latest as_of snapshot wins |
| TC-25 | — | Zero-row output produces valid Parquet |
| TC-26 | — | Write mode Overwrite: only last effective date's output survives |
| TC-27 | BR-1 | Weekend effective date with no card data: empty output |

## Test Cases

### TC-01: Weekend fallback — Saturday to Friday
- **Traces to:** BR-1
- **Input conditions:** `__maxEffectiveDate` is a Saturday (e.g., 2024-10-05). The cards table has weekday-only data, so no `as_of = 2024-10-05` rows exist.
- **Expected output:** The `target` CTE computes `target_date = date(MAX(as_of), '-1 day')`. Since cards only has weekday data, `MAX(as_of)` within the effective range would be `2024-10-04` (Friday) and weekend fallback is a no-op, OR the cards DataFrame is empty (no weekend rows) and the query returns zero rows. Per BRD Edge Case #2, the cards table has weekday-only data, so weekend effective dates effectively produce Friday-based or empty output.
- **Verification method:** Run V2 for effective date 2024-10-05. Verify output `as_of` values. Compare with V1 output.

### TC-02: Weekend fallback — Sunday to Friday
- **Traces to:** BR-1
- **Input conditions:** `__maxEffectiveDate` is a Sunday (e.g., 2024-10-06). The cards table has no Sunday as_of rows.
- **Expected output:** Same behavior as TC-01: weekend fallback logic fires, `date(MAX(as_of), '-2 days')` shifts to Friday. If no card data exists for the weekend effective date range, output is empty.
- **Verification method:** Run V2 for effective date 2024-10-06. Verify output or empty result matches V1.

### TC-03: Weekday effective date used as-is
- **Traces to:** BR-1
- **Input conditions:** `__maxEffectiveDate` is a weekday (e.g., 2024-10-03, a Thursday). The cards table has rows for this as_of date.
- **Expected output:** `target_date = MAX(as_of)` with no adjustment. Cards are evaluated against 2024-10-03 as the reference date for expiry calculations.
- **Verification method:** Run V2 for 2024-10-03. Verify `as_of` in output matches 2024-10-03. Confirm `days_until_expiry` calculations use this date.

### TC-04: Cards within 0-90 day expiry window included
- **Traces to:** BR-2
- **Input conditions:** targetDate = 2024-10-04. Card A has `expiration_date = 2024-10-20` (16 days out). Card B has `expiration_date = 2024-12-15` (72 days out). Both are within 0-90 days.
- **Expected output:** Both Card A and Card B appear in the output with correct `days_until_expiry` values (16 and 72, respectively).
- **Verification method:** Query V2 output. Verify both cards present. Verify `days_until_expiry` is non-negative and <= 90 for all output rows.

### TC-05: days_until_expiry calculation
- **Traces to:** BR-3
- **Input conditions:** targetDate = 2024-10-04. Card with `expiration_date = 2024-11-15`.
- **Expected output:** `days_until_expiry = julianday('2024-11-15') - julianday('2024-10-04') = 42`. V1 computes `DateOnly(2024,11,15).DayNumber - DateOnly(2024,10,4).DayNumber = 42`. Both should agree.
- **Verification method:** For each output row, independently compute `(expiration_date - targetDate).days` and compare against V2's `days_until_expiry`. All should match exactly.

### TC-06: Expired cards excluded (days_until_expiry < 0)
- **Traces to:** BR-4
- **Input conditions:** targetDate = 2024-10-04. Card has `expiration_date = 2024-09-30` (4 days ago, `days_until_expiry = -4`).
- **Expected output:** This card does NOT appear in the output. The `WHERE` clause `days_until_expiry >= 0` excludes it.
- **Verification method:** Query datalake.cards for cards with expiration_date before the targetDate. Verify none appear in V2 output.

### TC-07: Far-future cards excluded (days_until_expiry > 90)
- **Traces to:** BR-5
- **Input conditions:** targetDate = 2024-10-04. Card has `expiration_date = 2025-06-01` (240 days out).
- **Expected output:** This card does NOT appear in the output. The `WHERE` clause `days_until_expiry <= 90` excludes it.
- **Verification method:** Query datalake.cards for cards with expiration_date more than 90 days from targetDate. Verify none appear in V2 output.

### TC-08: Customer name lookup via LEFT JOIN
- **Traces to:** BR-6
- **Input conditions:** Card belongs to customer_id 100. customers table has `id = 100` with `first_name = 'Alice'`, `last_name = 'Johnson'`.
- **Expected output:** Output row for the card shows `first_name = 'Alice'` and `last_name = 'Johnson'`.
- **Verification method:** Join V2 output back to deduplicated customers (MAX as_of per id). Confirm name fields match.

### TC-09: expiration_date type handling
- **Traces to:** BR-7
- **Input conditions:** The V1 External module handles both `DateOnly` and `DateTime` types for expiration_date. In V2, SQLite receives the expiration_date as a string (via DataSourcing -> Transformation), and the External type-coercion module converts it back to `DateOnly` via `DateOnly.Parse()`.
- **Expected output:** Output `expiration_date` column is of type `DateOnly` in the final Parquet file, matching V1.
- **Verification method:** Inspect V2 output Parquet schema. Confirm `expiration_date` is a Date type, not a String. Compare schema against V1 baseline.

### TC-10: All card rows evaluated regardless of as_of or status
- **Traces to:** BR-8
- **Input conditions:** Cards table has rows across multiple as_of dates within the effective range. Cards include all statuses (Active, Expired, Blocked). No `WHERE` clause filters on `cards.as_of` or any status column.
- **Expected output:** Every card row from every as_of snapshot within the effective range is independently evaluated against the 90-day window. Cards of all statuses are candidates for inclusion.
- **Verification method:** Count qualifying cards from all as_of snapshots in datalake.cards (applying the 0-90 day expiry filter). Compare this total count against V2 output row count.

### TC-11: Output as_of is weekend-adjusted targetDate
- **Traces to:** BR-9
- **Input conditions:** Effective date is a weekday, e.g., 2024-10-04 (Friday). targetDate = 2024-10-04.
- **Expected output:** All output rows have `as_of = 2024-10-04`.
- **Verification method:** Read V2 output Parquet. Assert all `as_of` values match the targetDate. For weekend runs, confirm `as_of` reflects the Friday fallback date, not the original weekend date.

### TC-12: Empty cards input produces empty output
- **Traces to:** BR-10
- **Input conditions:** The effective date range yields zero rows in the cards table (e.g., running for a date before any card data exists).
- **Expected output:** Output is an empty DataFrame/Parquet with the correct 8-column schema: `card_id, customer_id, first_name, last_name, card_type, expiration_date, days_until_expiry, as_of`.
- **Verification method:** Run V2 for a date with no cards data. Confirm output Parquet exists, has zero data rows, and has all 8 columns with correct types.

### TC-13: Boundary — card expiring on targetDate itself (days = 0)
- **Traces to:** BR-2
- **Input conditions:** targetDate = 2024-10-04. Card has `expiration_date = 2024-10-04`. `days_until_expiry = 0`.
- **Expected output:** This card IS included in the output. The condition `days_until_expiry >= 0` is satisfied.
- **Verification method:** Identify or construct a card with expiration_date equal to targetDate. Verify it appears in V2 output with `days_until_expiry = 0`.

### TC-14: Boundary — card expiring exactly 90 days out
- **Traces to:** BR-2
- **Input conditions:** targetDate = 2024-10-04. Card has `expiration_date = 2025-01-02` (exactly 90 days from 2024-10-04). `days_until_expiry = 90`.
- **Expected output:** This card IS included. The condition `days_until_expiry <= 90` is satisfied at the boundary.
- **Verification method:** Identify or construct a card with expiration_date exactly 90 days from targetDate. Verify it appears in V2 output with `days_until_expiry = 90`.

### TC-15: Boundary — card expiring 91 days out (excluded)
- **Traces to:** BR-2
- **Input conditions:** targetDate = 2024-10-04. Card has `expiration_date = 2025-01-03` (91 days out). `days_until_expiry = 91`.
- **Expected output:** This card is NOT included. The condition `days_until_expiry <= 90` excludes it.
- **Verification method:** Identify cards with expiry 91+ days from targetDate. Verify they do NOT appear in V2 output.

### TC-16: Boundary — card expired yesterday (days = -1)
- **Traces to:** BR-4
- **Input conditions:** targetDate = 2024-10-04. Card has `expiration_date = 2024-10-03`. `days_until_expiry = -1`.
- **Expected output:** This card is NOT included. The condition `days_until_expiry >= 0` excludes cards that expired even one day ago.
- **Verification method:** Identify recently-expired cards. Verify they do NOT appear in V2 output.

### TC-17: Customer not found — defaults to empty strings
- **Traces to:** BR-6
- **Input conditions:** A card has `customer_id = 99999` which does not exist in the customers table.
- **Expected output:** Output row shows `first_name = ''` and `last_name = ''`. The `LEFT JOIN` with `COALESCE(..., '')` handles missing customers.
- **Verification method:** If orphan customer_ids exist in cards data, verify V2 output shows empty strings. Also validated via Proofmark V1-V2 comparison (V1 uses `GetValueOrDefault` with `("", "")` default).

### TC-18: Duplicate card rows across as_of snapshots
- **Traces to:** BR-8, Edge Case #1
- **Input conditions:** Card 500 exists in cards for as_of dates 2024-10-01, 2024-10-02, 2024-10-03, and 2024-10-04. The card's expiration_date qualifies for the 0-90 day window in each snapshot. Effective date range covers all four dates.
- **Expected output:** Card 500 appears 4 times in the output (once per as_of snapshot). Each row independently passes the 0-90 day filter. All output rows have `as_of = targetDate`, not the source `as_of`.
- **Verification method:** Count occurrences of a specific card_id in V2 output. Verify the count equals the number of as_of snapshots for that card within the effective range that pass the expiry filter. Compare against V1 output to confirm identical duplication behavior.

### TC-19: Cards of all statuses evaluated
- **Traces to:** BR-8, Edge Case #5
- **Input conditions:** Cards table includes cards with status 'Active', 'Expired', and 'Blocked'. Some cards in each status category have expiration_dates within the 0-90 day window.
- **Expected output:** Cards from ALL status categories appear in the output, provided they pass the 0-90 day expiry filter. No card_status column exists in the output. The V1 and V2 logic do not filter on card_status.
- **Verification method:** Query datalake.cards for cards with different statuses that qualify. Verify they all appear in V2 output. Note: card_status is not even sourced in V2 (only card_id, customer_id, card_type, expiration_date are sourced), so status filtering is structurally impossible.

### TC-20: Output column order and schema verification
- **Traces to:** Output Schema (BRD/FSD)
- **Input conditions:** Any normal run producing at least one output row.
- **Expected output:** Parquet output columns in exact order: `card_id, customer_id, first_name, last_name, card_type, expiration_date, days_until_expiry, as_of`. Column types: card_id (string), customer_id (INT32), first_name (string), last_name (string), card_type (string), expiration_date (DATE/DateOnly), days_until_expiry (INT32), as_of (DATE/DateOnly).
- **Verification method:** Read V2 output Parquet schema. Compare column names, order, and types against V1 baseline Parquet schema.

### TC-21: Type coercion — External module converts types correctly
- **Traces to:** FSD Section 9 (External Module Design)
- **Input conditions:** The Transformation module produces `output_raw` with: `expiration_date` as string, `as_of` as string, `customer_id` as long, `days_until_expiry` as long. The External module reads `output_raw` and writes `output`.
- **Expected output:** The `output` DataFrame has: `expiration_date` as DateOnly, `as_of` as DateOnly, `customer_id` as int (Int32), `days_until_expiry` as int (Int32). String columns (`card_id`, `first_name`, `last_name`, `card_type`) pass through unchanged.
- **Verification method:** Inspect V2 output Parquet column types. Verify they match V1 exactly. Specifically: `expiration_date` and `as_of` should be Date type in Parquet (not String). `customer_id` and `days_until_expiry` should be INT32 (not INT64).

### TC-22: Proofmark comparison — strict match expected
- **Traces to:** FSD Proofmark Config
- **Input conditions:** V1 and V2 jobs run for the same effective date.
- **Expected output:** Proofmark reports 100% match with zero overrides. No FUZZY columns. No EXCLUDED columns. All output fields are deterministic per BRD ("Non-Deterministic Fields: None identified").
- **Verification method:** Run Proofmark with config: `comparison_target: "card_expiration_watch"`, `reader: parquet`, `threshold: 100.0`. Verify PASS result.

### TC-23: julianday precision for days_until_expiry
- **Traces to:** BR-3
- **Input conditions:** Various cards with different expiration_dates relative to targetDate.
- **Expected output:** V2's `CAST(julianday(expiration_date) - julianday(target_date) AS INTEGER)` matches V1's `expirationDate.DayNumber - targetDate.DayNumber` for every card. SQLite's `julianday()` returns a REAL (double) for date-only strings. The difference of two julian day values for date-only inputs should produce exact integers (no fractional component), so `CAST AS INTEGER` should be lossless.
- **Verification method:** Compare V1 and V2 `days_until_expiry` values for all cards across multiple effective dates. Per FSD Section 11, the risk of julianday precision issues is LOW, but verify with actual data.

### TC-24: Customer deduplication — latest as_of wins
- **Traces to:** BR-6, FSD SQL Design Note 2
- **Input conditions:** Customer 200 has `first_name = 'Bob'` on `as_of = 2024-10-01` and `first_name = 'Robert'` on `as_of = 2024-10-04`. Both snapshots are within the effective date range.
- **Expected output:** Output rows for cards belonging to customer 200 show `first_name = 'Robert'` (the MAX as_of snapshot wins). This matches V1's dictionary last-writer-wins behavior.
- **Verification method:** Identify customers with name changes across snapshots. Verify V2 output uses the name from the latest as_of date. Compare against V1 output.

### TC-25: Zero-row output produces valid Parquet
- **Traces to:** BR-10
- **Input conditions:** A run that produces zero output rows (no qualifying cards within 90-day window, or empty cards table).
- **Expected output:** A valid Parquet file is written with the correct 8-column schema but zero data rows. The External module handles empty `output_raw` gracefully (foreach over empty rows produces empty `output`).
- **Verification method:** Run V2 for a scenario producing zero qualifying cards. Verify output Parquet file exists, is readable, has zero rows, and has all 8 columns with correct types.

### TC-26: Overwrite mode — last date wins in multi-day runs
- **Traces to:** Write Mode Implications (BRD)
- **Input conditions:** Auto-advance run covering effective dates 2024-10-01 through 2024-10-04. Each date may produce different output because `days_until_expiry` changes with each effective date.
- **Expected output:** After the run completes, the output directory contains only 2024-10-04's results. Earlier dates' outputs are overwritten. The `days_until_expiry` values reflect 2024-10-04 as the targetDate.
- **Verification method:** Run V2 in auto-advance mode across multiple dates. After completion, verify output `as_of` values are all from the final effective date. Verify `days_until_expiry` values are consistent with the final targetDate.

### TC-27: Weekend effective date with weekday-only card data
- **Traces to:** BR-1, Edge Case #2
- **Input conditions:** Effective date is Saturday 2024-10-05. The cards table has weekday-only as_of data — no rows for `as_of = 2024-10-05`. The DataSourcing effective date range includes the weekend date.
- **Expected output:** If the cards DataFrame is empty (because no card rows match the weekend as_of within the effective range), the output is empty per BR-10. If the effective range includes the preceding Friday's data, the `MAX(as_of)` in the `target` CTE picks up Friday, and the weekend fallback logic adjusts accordingly (potentially a no-op since MAX is already Friday). The key behavior: weekend dates do not crash the job and produce either empty or Friday-based output.
- **Verification method:** Run V2 for a weekend date. Compare output against V1 to confirm identical behavior. Document whether the output is empty or contains Friday-based results, depending on the effective date range configuration.
