# CardAuthorizationSummary -- Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | INNER JOIN between card_transactions and cards on card_id |
| TC-02   | BR-2           | GROUP BY card_type and as_of -- one row per card type per date |
| TC-03   | BR-3           | approved_count: SUM(CASE WHEN authorization_status = 'Approved') |
| TC-04   | BR-4           | declined_count: SUM(CASE WHEN authorization_status = 'Declined') |
| TC-05   | BR-5, W4       | approval_rate: integer division truncation (always 0 or 1) |
| TC-06   | BR-6           | Dead code ROW_NUMBER removed in V2 (AP8 eliminated) |
| TC-07   | BR-7           | Dead code unused_summary CTE removed in V2 (AP8 eliminated) |
| TC-08   | BR-8           | Only 'Approved' and 'Declined' authorization_status values |
| TC-09   | BR-9           | Only 'Credit' and 'Debit' card_type values |
| TC-10   | BR-10          | INNER JOIN excludes transactions without matching card_id |
| TC-11   | --             | Trailer format: TRAILER\|{row_count}\|{date} |
| TC-12   | --             | Overwrite mode: file replaced on each execution |
| TC-13   | --             | Edge case: 100% approval rate -- approval_rate = 1 |
| TC-14   | --             | Edge case: 0% approval rate -- approval_rate = 0 |
| TC-15   | --             | Edge case: mixed approvals/declines -- approval_rate = 0 (integer truncation) |
| TC-16   | --             | Edge case: zero transactions for a card type on a date |
| TC-17   | --             | Edge case: weekend dates and cards table weekday-only data |
| TC-18   | --             | Edge case: NULL authorization_status handling |
| TC-19   | --             | Output format: correct columns, correct order, header present |
| TC-20   | --             | Output format: LF line endings |
| TC-21   | --             | Proofmark: all columns STRICT, no fuzzy or excluded overrides |
| TC-22   | BR-2           | total_count equals approved_count + declined_count (given BR-8) |
| TC-23   | --             | AP4 eliminated: unused columns removed from DataSourcing |
| TC-24   | --             | Edge case: single effective date produces single as_of in output |

## Test Cases

### TC-01: INNER JOIN between card_transactions and cards on card_id
- **Traces to:** BR-1
- **Input conditions:** card_transactions has rows for card_id=100, card_id=200. cards table has matching records for both card_ids with card_type values.
- **Expected output:** Both card_ids' transactions appear in the output, aggregated by their respective card_type. The join links each transaction to its card to obtain card_type.
- **Verification method:** Run job. Parse output CSV. Verify that transactions from both card_ids contribute to the correct card_type aggregation groups.

### TC-02: GROUP BY card_type and as_of
- **Traces to:** BR-2
- **Input conditions:** card_transactions has transactions for card_type='Credit' and card_type='Debit' on as_of=2024-10-01. Multiple transactions exist per group.
- **Expected output:** Exactly two data rows for as_of=2024-10-01: one for card_type='Credit', one for card_type='Debit'. Each row has aggregated counts.
- **Verification method:** Parse output CSV. Verify exactly two data rows, each with a distinct card_type value. Verify no duplicate card_type/as_of combinations.

### TC-03: approved_count calculation
- **Traces to:** BR-3
- **Input conditions:** For card_type='Credit' on as_of=2024-10-01, there are 8 transactions with authorization_status='Approved' and 2 with authorization_status='Declined'.
- **Expected output:** approved_count = 8 for the Credit/2024-10-01 row.
- **Verification method:** Parse output CSV. Locate the Credit/2024-10-01 row. Verify approved_count column = 8.

### TC-04: declined_count calculation
- **Traces to:** BR-4
- **Input conditions:** Same as TC-03: 8 Approved, 2 Declined for Credit on 2024-10-01.
- **Expected output:** declined_count = 2 for the Credit/2024-10-01 row.
- **Verification method:** Parse output CSV. Locate the Credit/2024-10-01 row. Verify declined_count column = 2.

### TC-05: Integer division approval_rate (W4)
- **Traces to:** BR-5, W4
- **Input conditions:** For card_type='Credit' on as_of=2024-10-01: 8 Approved out of 10 total. For card_type='Debit': 5 Approved out of 5 total (100%).
- **Expected output:** Credit approval_rate = 0 (8/10 = 0 via integer division truncation). Debit approval_rate = 1 (5/5 = 1 via integer division). The CAST(... AS INTEGER) / CAST(... AS INTEGER) expression in SQLite truncates toward zero.
- **Verification method:** Parse output CSV. Verify Credit row has approval_rate=0, Debit row has approval_rate=1. This replicates V1's W4 integer division behavior.

### TC-06: Dead code ROW_NUMBER removed (AP8)
- **Traces to:** BR-6
- **Input conditions:** V2 SQL does not contain any ROW_NUMBER() window function or `rn` column.
- **Expected output:** Output is identical to V1 because the ROW_NUMBER column was never part of the final SELECT in V1 either. Its removal has zero effect on output data.
- **Verification method:** Inspect V2 job config JSON's SQL string. Verify no ROW_NUMBER() or window function syntax is present. Run Proofmark to confirm output equivalence with V1.

### TC-07: Dead code unused_summary CTE removed (AP8)
- **Traces to:** BR-7
- **Input conditions:** V2 SQL does not contain an `unused_summary` CTE.
- **Expected output:** Output is identical to V1 because unused_summary was defined but never referenced in V1's final SELECT.
- **Verification method:** Inspect V2 job config JSON's SQL string. Verify no CTE named `unused_summary` exists. Run Proofmark to confirm output equivalence with V1.

### TC-08: Authorization status values -- only Approved and Declined
- **Traces to:** BR-8
- **Input conditions:** All card_transactions rows have authorization_status in {'Approved', 'Declined'}. No other values exist in the data.
- **Expected output:** total_count = approved_count + declined_count for every output row, because there are no transactions with a status other than these two.
- **Verification method:** Parse output CSV. For every data row, verify total_count == approved_count + declined_count.

### TC-09: Card type values -- only Credit and Debit
- **Traces to:** BR-9
- **Input conditions:** All cards rows have card_type in {'Credit', 'Debit'}.
- **Expected output:** Output contains at most two card_type values per as_of date: 'Credit' and 'Debit'. No other card_type values appear.
- **Verification method:** Parse output CSV. Collect all distinct card_type values. Verify the set is a subset of {'Credit', 'Debit'}.

### TC-10: INNER JOIN excludes unmatched transactions
- **Traces to:** BR-10
- **Input conditions:** card_transactions has rows for card_id=999. cards table has NO record for card_id=999.
- **Expected output:** Transactions for card_id=999 do not contribute to any output row. They are silently dropped by the INNER JOIN.
- **Verification method:** Run job. Verify output counts do not include the card_id=999 transactions. Compare total output transaction count against the count of transactions that have matching card_ids in the cards table.

### TC-11: Trailer format with row_count and date tokens
- **Traces to:** BRD Writer Configuration, BRD Edge Case 4
- **Input conditions:** Effective date is 2024-10-01. The transformation produces 2 data rows (one per card_type).
- **Expected output:** The last line of the file is `TRAILER|2|2024-10-01`. {row_count} resolves to 2 (data rows only, excluding header and trailer). {date} resolves to __maxEffectiveDate (2024-10-01).
- **Verification method:** Read the output CSV. Verify the last line matches the expected trailer format with correct values.

### TC-12: Overwrite mode -- file replaced on each execution
- **Traces to:** BRD Write Mode Implications
- **Input conditions:** Job runs for effective date 2024-10-01, producing output. Then runs again for effective date 2024-10-02.
- **Expected output:** After the second run, the file contains ONLY the 2024-10-02 data. The 2024-10-01 data is gone -- Overwrite mode replaces the file entirely. Only the last effective date's output survives auto-advance.
- **Verification method:** Run job for two consecutive dates. After both runs, read the output CSV. Verify it contains only the second date's data and trailer. No trace of the first date's data remains.

### TC-13: 100% approval rate -- approval_rate = 1
- **Traces to:** BR-5, W4 (edge case)
- **Input conditions:** For card_type='Debit' on a given date, all transactions have authorization_status='Approved'. Zero declined.
- **Expected output:** approved_count = total_count, declined_count = 0, approval_rate = 1 (total_count / total_count = 1 via integer division).
- **Verification method:** Parse output CSV. Verify the Debit row's approval_rate = 1.

### TC-14: 0% approval rate -- approval_rate = 0
- **Traces to:** BR-5, W4 (edge case)
- **Input conditions:** For card_type='Credit' on a given date, all transactions have authorization_status='Declined'. Zero approved.
- **Expected output:** approved_count = 0, declined_count = total_count, approval_rate = 0 (0 / total_count = 0 via integer division).
- **Verification method:** Parse output CSV. Verify the Credit row's approval_rate = 0.

### TC-15: Mixed approvals/declines -- approval_rate truncated to 0
- **Traces to:** BR-5, W4
- **Input conditions:** For card_type='Credit' on a given date: 99 Approved out of 100 total (99% approval rate).
- **Expected output:** approval_rate = 0. Even at 99% approval, integer division 99/100 = 0 (truncation toward zero). This is the W4 wrinkle -- integer division loses all granularity for rates below 100%.
- **Verification method:** Parse output CSV. Verify approval_rate = 0 for any group where approved_count < total_count.

### TC-16: Zero transactions for a card type on a date
- **Traces to:** BRD Edge Case 3
- **Input conditions:** On as_of=2024-10-01, all card_transactions are for card_type='Credit' (via their card_id). No transactions map to card_type='Debit'.
- **Expected output:** Only one data row for that date (card_type='Credit'). card_type='Debit' does NOT appear -- GROUP BY produces rows only for groups with data. No zero-fill occurs.
- **Verification method:** Parse output CSV. Verify only one data row for that as_of date. Verify no row with card_type='Debit' and zero counts exists.

### TC-17: Weekend dates and cards table weekday-only data
- **Traces to:** BRD Edge Case 2
- **Input conditions:** Effective date is a Saturday. card_transactions has data for that Saturday as_of. cards table has weekday-only data (no Saturday as_of records).
- **Expected output:** Depends on the join semantics. The V1/V2 SQL joins on card_id only (not as_of). Since DataSourcing loads a single day for gap-fill execution, card_transactions will have Saturday rows but cards will have no rows for that as_of. The INNER JOIN on card_id will match card_transactions Saturday rows with whatever cards rows are loaded. If no cards rows exist (because cards has no Saturday snapshot), zero matches occur and zero output rows result. The trailer shows `TRAILER|0|{saturday_date}`.
- **Verification method:** Run job for a Saturday effective date. Observe whether output contains data rows or is a zero-row output with trailer only. Verify behavior matches V1.

### TC-18: NULL authorization_status handling
- **Traces to:** Edge case
- **Input conditions:** A card_transaction row has authorization_status=NULL.
- **Expected output:** The CASE WHEN expressions for approved_count and declined_count both evaluate to 0 (ELSE branch) for NULL status. The row IS counted in total_count (COUNT(*) counts all rows). So total_count > approved_count + declined_count if any NULL statuses exist. However, per BR-8, only 'Approved' and 'Declined' exist in the actual data, so this case should not arise in practice.
- **Verification method:** If test data with NULL authorization_status is available, verify total_count = approved_count + declined_count + null_status_count. Otherwise, document as a theoretical edge case covered by BR-8's constraint.

### TC-19: Output format -- correct columns and column order
- **Traces to:** Output format verification (FSD Section 4)
- **Input conditions:** Standard job execution with at least one data row.
- **Expected output:** CSV header row contains exactly: `card_type,total_count,approved_count,declined_count,approval_rate,as_of` in that order. Data rows have six comma-separated values in the same order.
- **Verification method:** Read the first line of the output CSV. Verify it matches the expected header exactly (column names and order). Verify data rows have exactly 6 fields.

### TC-20: LF line endings
- **Traces to:** Output format verification (BRD Writer Configuration: lineEnding=LF)
- **Input conditions:** Standard job execution producing output CSV.
- **Expected output:** All line endings in the file are LF (\n), not CRLF (\r\n). This includes header, data rows, and trailer line.
- **Verification method:** Read the raw bytes of the output CSV. Verify no \r characters exist in the file. Every line boundary is a single \n.

### TC-21: Proofmark configuration -- all columns STRICT
- **Traces to:** Proofmark verification (FSD Section 8)
- **Input conditions:** V1 and V2 output files for the same effective date.
- **Expected output:** Proofmark comparison passes at 100.0% threshold with no fuzzy or excluded column overrides. All six columns (card_type, total_count, approved_count, declined_count, approval_rate, as_of) are compared strictly. approval_rate does NOT need fuzzy tolerance because both V1 and V2 use the same integer division expression in the same SQLite engine.
- **Verification method:** Run Proofmark with the FSD's config (reader: csv, header_rows: 1, trailer_rows: 1, threshold: 100.0, no column overrides). Verify PASS result. Config uses trailer_rows: 1 because Overwrite mode produces exactly one trailer at the end.

### TC-22: total_count consistency check
- **Traces to:** BR-2, BR-8
- **Input conditions:** Any standard execution with data.
- **Expected output:** For every data row: total_count = approved_count + declined_count. This holds because BR-8 establishes that only 'Approved' and 'Declined' authorization_status values exist. COUNT(*) counts all rows, and the two CASE-based SUMs partition those rows exhaustively.
- **Verification method:** Parse output CSV. For each data row, verify total_count == approved_count + declined_count.

### TC-23: AP4 eliminated -- unused columns removed from DataSourcing
- **Traces to:** AP4 (FSD Section 3)
- **Input conditions:** V2 job config sources card_transactions with columns ["card_txn_id", "card_id", "authorization_status"] (removed customer_id, amount). V2 sources cards with columns ["card_id", "card_type"] (removed customer_id).
- **Expected output:** Output is identical to V1. The removed columns were never referenced in the transformation SQL.
- **Verification method:** Inspect V2 job config JSON. Verify column lists match FSD Section 2. Run Proofmark to confirm output equivalence with V1.

### TC-24: Single effective date produces single as_of in output
- **Traces to:** Edge case (BRD Write Mode Implications)
- **Input conditions:** Executor gap-fills one day at a time. Job runs for a single effective date 2024-10-01.
- **Expected output:** All data rows have as_of=2024-10-01. At most two data rows (one per card_type). One trailer line with date=2024-10-01. File is created fresh (Overwrite mode).
- **Verification method:** Parse output CSV. Verify all data rows share the same as_of value. Verify trailer date matches. Verify file contains no data from other dates.
