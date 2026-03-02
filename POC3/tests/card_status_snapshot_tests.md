# CardStatusSnapshot — Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | Cards grouped by card_status and as_of produce one row per status per date |
| TC-02   | BR-2           | card_count is COUNT(*) per card_status/as_of group |
| TC-03   | BR-3           | All three card_status values (Active, Blocked, Expired) appear in output |
| TC-04   | BR-4           | Unused sourced columns do not appear in output |
| TC-05   | BR-5           | Output is split across 50 Parquet part files |
| TC-06   | Edge Case 1    | Weekend effective dates produce zero-row output |
| TC-07   | Edge Case 2    | 50-part split with only 3 rows — most parts empty |
| TC-08   | Edge Case 3    | No WHERE clause filtering — all card statuses included |
| TC-09   | Writer Config  | ParquetFileWriter with Overwrite mode and correct output directory |
| TC-10   | FSD Sec 3: AP4 | V2 DataSourcing sources only card_status (unused columns eliminated) |
| TC-11   | FSD Sec 3: W10 | numParts=50 preserved for output equivalence |
| TC-12   | Write Mode     | Overwrite mode replaces entire output directory on each run |
| TC-13   | Output Schema  | Output column order is card_status, card_count, as_of |
| TC-14   | Proofmark      | Proofmark config: parquet reader, 100% threshold, no EXCLUDED/FUZZY columns |

## Test Cases

### TC-01: Grouping by card_status and as_of
- **Traces to:** BR-1
- **Input conditions:** Run for a single weekday effective date (e.g., 2024-10-01) where datalake.cards has data for all three card statuses.
- **Expected output:** Exactly 3 output rows — one per card_status value (Active, Blocked, Expired), each with the same as_of value matching the effective date.
- **Verification method:** Read the output Parquet part files, reconstruct full DataFrame. Verify row count = 3. Verify each row has a distinct card_status. Verify all rows share the same as_of value.

### TC-02: card_count is COUNT(*)
- **Traces to:** BR-2
- **Input conditions:** Run for effective date 2024-10-01. Query datalake.cards to independently count rows per card_status for that as_of date.
- **Expected output:** Each output row's card_count matches the independently computed COUNT(*) for its card_status/as_of group.
- **Verification method:** Compare output card_count values against `SELECT card_status, COUNT(*) FROM datalake.cards WHERE as_of = '2024-10-01' GROUP BY card_status`. Values must match exactly.

### TC-03: All three card_status values present
- **Traces to:** BR-3
- **Input conditions:** Run for a weekday effective date where all three card statuses have at least one card.
- **Expected output:** Output contains exactly the values Active, Blocked, Expired in the card_status column. No additional values, no missing values.
- **Verification method:** Extract distinct card_status values from output and compare against the set {Active, Blocked, Expired}.

### TC-04: Unused sourced columns excluded from output
- **Traces to:** BR-4
- **Input conditions:** Run the job normally.
- **Expected output:** Output schema contains exactly three columns: card_status, card_count, as_of. No columns named card_id, customer_id, card_type, card_number_masked, or expiration_date appear.
- **Verification method:** Read Parquet file schema and verify column names are exactly [card_status, card_count, as_of].

### TC-05: Output split across 50 Parquet part files
- **Traces to:** BR-5
- **Input conditions:** Run the job for a single weekday effective date.
- **Expected output:** The output directory contains exactly 50 files named part-00000.parquet through part-00049.parquet.
- **Verification method:** List files in the output directory. Verify count = 50 and naming follows the part-NNNNN.parquet convention.

### TC-06: Weekend effective date produces zero-row output
- **Traces to:** Edge Case 1
- **Input conditions:** Run for an effective date that falls on a Saturday or Sunday (e.g., 2024-10-05 is a Saturday). The datalake.cards table has no rows for weekend as_of dates.
- **Expected output:** All 50 Parquet part files exist but contain zero data rows (schema-only files). Total row count across all parts = 0.
- **Verification method:** Read all part files, concatenate rows, verify total count = 0.

### TC-07: 50-part split with only 3 rows
- **Traces to:** Edge Case 2, W10
- **Input conditions:** Run for a single weekday effective date producing exactly 3 output rows.
- **Expected output:** Parts 0-2 each contain 1 row. Parts 3-49 each contain 0 rows (empty with schema). Row distribution follows the framework's round-robin/integer-division partitioning logic.
- **Verification method:** Read each part file individually and verify its row count. Confirm parts 0-2 have 1 row each and parts 3-49 have 0 rows each.

### TC-08: No status filtering applied
- **Traces to:** Edge Case 3
- **Input conditions:** Confirm that datalake.cards contains all three statuses for the effective date. Run the job.
- **Expected output:** All three card_status values appear in output with non-zero card_count. No rows are filtered out.
- **Verification method:** Verify the sum of all card_count values equals `SELECT COUNT(*) FROM datalake.cards WHERE as_of = '{effective_date}'`.

### TC-09: Writer configuration — ParquetFileWriter with Overwrite
- **Traces to:** BRD Writer Configuration
- **Input conditions:** Inspect the V2 job config JSON.
- **Expected output:** The V2 config specifies:
  - `"type": "ParquetFileWriter"`
  - `"outputDirectory": "Output/double_secret_curated/card_status_snapshot/"`
  - `"numParts": 50`
  - `"writeMode": "Overwrite"`
- **Verification method:** Parse the V2 job config JSON and verify each writer parameter matches the expected values.

### TC-10: AP4 elimination — V2 sources only card_status
- **Traces to:** FSD Section 3 (AP4)
- **Input conditions:** Inspect the V2 job config JSON DataSourcing module.
- **Expected output:** The DataSourcing columns list contains only `["card_status"]`. The columns card_id, customer_id, card_type, card_number_masked, and expiration_date are NOT present. The `as_of` column is automatically appended by the DataSourcing module and does not need to be explicitly listed.
- **Verification method:** Parse the V2 job config JSON and verify the DataSourcing columns array is exactly `["card_status"]`.

### TC-11: W10 preservation — numParts=50
- **Traces to:** FSD Section 3 (W10)
- **Input conditions:** Inspect the V2 job config JSON writer module.
- **Expected output:** `numParts` is set to 50 (matching V1), even though the dataset typically produces only 3 rows per day.
- **Verification method:** Parse the V2 job config JSON and verify `"numParts": 50`.

### TC-12: Overwrite mode replaces output on each run
- **Traces to:** BRD Write Mode Implications
- **Input conditions:** Run the job for effective date 2024-10-01 (producing output). Then run again for effective date 2024-10-02.
- **Expected output:** After the second run, the output directory contains only the data from 2024-10-02. The 2024-10-01 data is gone because Overwrite mode replaces the entire directory.
- **Verification method:** After the second run, read all part files, verify all rows have as_of = '2024-10-02' and no rows have as_of = '2024-10-01'.

### TC-13: Output column order
- **Traces to:** FSD Section 4 (Output Schema)
- **Input conditions:** Run the job and read the output Parquet file schema.
- **Expected output:** Column order is exactly: card_status, card_count, as_of (matching the V1 SQL SELECT clause order).
- **Verification method:** Read the Parquet file schema and verify columns are in the order [card_status, card_count, as_of].

### TC-14: Proofmark configuration validation
- **Traces to:** FSD Section 8 (Proofmark Config Design)
- **Input conditions:** Inspect the Proofmark YAML config for this job.
- **Expected output:** Config specifies:
  - `comparison_target: "card_status_snapshot"`
  - `reader: parquet`
  - `threshold: 100.0`
  - No `columns.excluded` entries
  - No `columns.fuzzy` entries
- **Verification method:** Parse the Proofmark YAML and verify all fields. Confirm no overrides are present — strict comparison is appropriate because all output fields are deterministic with no floating-point arithmetic.
