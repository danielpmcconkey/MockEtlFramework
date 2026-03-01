# ComplianceTransactionRatio — Business Requirements Document

## Overview
Computes the ratio of compliance events to transactions per event type, expressed as events per 1,000 transactions. The job bypasses the framework's CsvFileWriter by writing CSV output directly from the External module.

## Output Type
Direct file I/O via External module (bypasses framework CsvFileWriter). The External module writes a CSV file directly using `StreamWriter`.

## Writer Configuration
The job config has NO writer module. The External module (`ComplianceTransactionRatioWriter`) writes directly to disk:
- **outputFile**: `Output/curated/compliance_transaction_ratio.csv` (hardcoded in External module)
- **includeHeader**: true (manually written by module)
- **trailerFormat**: `TRAILER|{inputCount}|{date}` — NOTE: uses input row count, NOT output row count
- **writeMode**: Overwrite (StreamWriter with `append: false`)
- **lineEnding**: LF (`\n`)

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.compliance_events | event_id, customer_id, event_type, status | Effective date range via executor | [compliance_transaction_ratio.json:4-11] |
| datalake.transactions | transaction_id, account_id, amount | Effective date range via executor | [compliance_transaction_ratio.json:12-19] |

### Source Table Schemas (from database)

**compliance_events**: event_id (integer), customer_id (integer), event_type (varchar), event_date (date), status (varchar), review_date (date), as_of (date)

**transactions**: transaction_id (integer), account_id (integer), txn_timestamp (timestamp), txn_type (varchar), amount (numeric), description (varchar), as_of (date)

## Business Rules

BR-1: Compliance events are grouped by event_type with a count per type.
- Confidence: HIGH
- Evidence: [ComplianceTransactionRatioWriter.cs:33-38] — iterates complianceEvents, builds `eventGroups` dictionary keyed by event_type

BR-2: The transaction count (`txn_count`) is the total count of ALL transactions in the DataFrame, not filtered by any criteria.
- Confidence: HIGH
- Evidence: [ComplianceTransactionRatioWriter.cs:30] — `var txnCount = transactions?.Count ?? 0`

BR-3: The events_per_1000_txns ratio uses integer arithmetic: `(eventCount * 1000) / txnCount`. Both operands are `int`, producing truncated integer division.
- Confidence: HIGH
- Evidence: [ComplianceTransactionRatioWriter.cs:54] — comment "W4: Integer division" and code `int eventsPer1000 = txnCount > 0 ? (eventCount * 1000) / txnCount : 0`

BR-4: If txnCount is zero, events_per_1000_txns defaults to 0 (division-by-zero guard).
- Confidence: HIGH
- Evidence: [ComplianceTransactionRatioWriter.cs:54] — ternary `txnCount > 0 ? ... : 0`

BR-5: Output rows are ordered alphabetically by event_type.
- Confidence: HIGH
- Evidence: [ComplianceTransactionRatioWriter.cs:49] — `.OrderBy(k => k.Key)`

BR-6: The trailer row count uses the SUM of input rows from compliance_events AND transactions DataFrames, NOT the output row count. This is a known inflation issue.
- Confidence: HIGH
- Evidence: [ComplianceTransactionRatioWriter.cs:28] — `var inputCount = complianceEvents.Count + (transactions?.Count ?? 0)`; [ComplianceTransactionRatioWriter.cs:59] — `writer.Write($"TRAILER|{inputCount}|{dateStr}\n")`

BR-7: The External module writes the CSV file directly, bypassing the framework's CsvFileWriter. The "output" DataFrame stored in sharedState is empty.
- Confidence: HIGH
- Evidence: [ComplianceTransactionRatioWriter.cs:41-60] — direct `StreamWriter` usage; [ComplianceTransactionRatioWriter.cs:62] — `sharedState["output"] = new DataFrame(new List<Row>(), outputColumns)` (empty)

BR-8: NULL event_type values are coalesced to "Unknown".
- Confidence: HIGH
- Evidence: [ComplianceTransactionRatioWriter.cs:36] — `row["event_type"]?.ToString() ?? "Unknown"`

BR-9: The date in the trailer and output rows uses `__maxEffectiveDate` formatted as "yyyy-MM-dd".
- Confidence: HIGH
- Evidence: [ComplianceTransactionRatioWriter.cs:25] — `maxDate.ToString("yyyy-MM-dd")`

BR-10: No framework writer module is configured in the job JSON. The job pipeline ends with the External module.
- Confidence: HIGH
- Evidence: [compliance_transaction_ratio.json:1-25] — only 3 modules: 2x DataSourcing + 1x External, no writer

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| event_type | compliance_events.event_type | Grouped key, NULL coalesced to "Unknown" | [ComplianceTransactionRatioWriter.cs:36,50] |
| event_count | Computed | COUNT of compliance events per event_type | [ComplianceTransactionRatioWriter.cs:37,51] |
| txn_count | transactions | Total count of ALL transactions | [ComplianceTransactionRatioWriter.cs:30,52] |
| events_per_1000_txns | Computed | `(event_count * 1000) / txn_count` (integer division) | [ComplianceTransactionRatioWriter.cs:54] |
| as_of | __maxEffectiveDate | Formatted as yyyy-MM-dd string | [ComplianceTransactionRatioWriter.cs:25,55] |

## Non-Deterministic Fields
None identified. Output is ordered by event_type alphabetically.

## Write Mode Implications
- The External module uses `StreamWriter` with `append: false`, meaning each run overwrites the file completely.
- Since there is no framework writer module, the framework's write mode setting is not applicable.
- Multi-day runs will overwrite the file for each effective date, retaining only the last date's output.
- Evidence: [ComplianceTransactionRatioWriter.cs:45]

## Edge Cases

1. **Empty compliance events**: If compliance_events is null or empty, an empty output DataFrame is returned, but no CSV is written (the method exits before the writer block).
   - Evidence: [ComplianceTransactionRatioWriter.cs:18-21]

2. **Zero transactions**: If transactions is null or empty, txnCount = 0 and all events_per_1000_txns values are 0.
   - Evidence: [ComplianceTransactionRatioWriter.cs:30,54]

3. **Integer division truncation**: For small event counts relative to transaction counts, the ratio may round down to 0. Example: 1 event / 4263 transactions = (1 * 1000) / 4263 = 0 (truncated).
   - Confidence: HIGH
   - Evidence: [ComplianceTransactionRatioWriter.cs:54]; [DB query: ~4263 transactions per day, ~115 compliance events per day]

4. **Inflated trailer count**: With ~115 compliance events and ~4263 transactions, the trailer count would be ~4378, far exceeding the actual output row count (5 event types).
   - Evidence: [ComplianceTransactionRatioWriter.cs:28,59]

5. **Direct file write**: The framework has no awareness of the actual file written. The "output" DataFrame in sharedState is empty, so any downstream module expecting data from "output" would get nothing.
   - Evidence: [ComplianceTransactionRatioWriter.cs:62]

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Group by event_type | [ComplianceTransactionRatioWriter.cs:33-38] |
| BR-2: Total txn count | [ComplianceTransactionRatioWriter.cs:30] |
| BR-3: Integer division | [ComplianceTransactionRatioWriter.cs:54] |
| BR-4: Division-by-zero guard | [ComplianceTransactionRatioWriter.cs:54] |
| BR-5: Alphabetical ordering | [ComplianceTransactionRatioWriter.cs:49] |
| BR-6: Inflated trailer count | [ComplianceTransactionRatioWriter.cs:28,59] |
| BR-7: Direct file I/O | [ComplianceTransactionRatioWriter.cs:41-60] |
| BR-8: NULL coalescing to "Unknown" | [ComplianceTransactionRatioWriter.cs:36] |
| BR-9: Date format | [ComplianceTransactionRatioWriter.cs:25] |
| BR-10: No framework writer | [compliance_transaction_ratio.json] |

## Open Questions
1. Is the direct file write (bypassing CsvFileWriter) intentional? It means no RFC 4180 quoting, no configurable line endings from the framework, and an inflated trailer count.
   - Confidence: MEDIUM — the code has explicit comments about the inflated trailer (W7), suggesting awareness
