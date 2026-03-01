# LargeWireReport — Business Requirements Document

## Overview
Filters wire transfers exceeding $10,000 and enriches them with customer name information to produce a CSV report of large wires for the effective date range. Used for regulatory or compliance monitoring purposes.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/large_wire_report.csv`
- **includeHeader**: true
- **writeMode**: Overwrite
- **lineEnding**: LF
- **trailerFormat**: not specified (no trailer)

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.wire_transfers | wire_id, customer_id, direction, amount, counterparty_name, status | Effective date range (injected by executor) | [large_wire_report.json:8-12] |
| datalake.customers | id, first_name, last_name | Effective date range (injected by executor) | [large_wire_report.json:14-18] |

### Table Schemas (from database)

**wire_transfers**: wire_id (integer), customer_id (integer), account_id (integer), direction (varchar: Inbound/Outbound), amount (numeric, range ~1012-49959), counterparty_name (varchar), counterparty_bank (varchar), status (varchar: Completed/Pending/Rejected), wire_timestamp (timestamp), as_of (date).

**customers**: id (integer), prefix (varchar), first_name (varchar), last_name (varchar), sort_name (varchar), suffix (varchar), birthdate (date), as_of (date).

## Business Rules

BR-1: Wire transfers are filtered to include only those with `amount > 10000` (strictly greater than; $10,000.00 exactly is excluded).
- Confidence: HIGH
- Evidence: [LargeWireReportBuilder.cs:44] — `if (amount > 10000)`

BR-2: Customer names are looked up via `customer_id` → `customers.id` join. If a wire's customer_id has no matching customer record, first_name and last_name default to empty strings.
- Confidence: HIGH
- Evidence: [LargeWireReportBuilder.cs:26-36, 47] — `customerLookup.GetValueOrDefault(customerId, ("", ""))`

BR-3: The `amount` is rounded to 2 decimal places using **banker's rounding** (MidpointRounding.ToEven).
- Confidence: HIGH
- Evidence: [LargeWireReportBuilder.cs:50] — `Math.Round(amount, 2, MidpointRounding.ToEven)`; code comment "W5: banker's rounding"

BR-4: All wire statuses are included (Completed, Pending, Rejected). No status filter is applied.
- Confidence: HIGH
- Evidence: [LargeWireReportBuilder.cs:40-65] — no status check in the filtering logic

BR-5: All wire directions are included (Inbound, Outbound). No direction filter is applied.
- Confidence: HIGH
- Evidence: [LargeWireReportBuilder.cs:40-65] — no direction check

BR-6: Empty output (zero-row DataFrame with correct schema) is produced if `wire_transfers` is null or empty.
- Confidence: HIGH
- Evidence: [LargeWireReportBuilder.cs:19-23]

BR-7: The customer lookup dictionary is keyed by integer `id`. For duplicate customer IDs across dates (since data is snapshot-based), the last-seen row's name wins.
- Confidence: HIGH
- Evidence: [LargeWireReportBuilder.cs:31] — dictionary assignment `customerLookup[id] = ...` overwrites

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| wire_id | wire_transfers.wire_id | Direct pass-through | [LargeWireReportBuilder.cs:54] |
| customer_id | wire_transfers.customer_id | Cast to int via Convert.ToInt32 | [LargeWireReportBuilder.cs:46, 55] |
| first_name | customers.first_name | Looked up by customer_id; empty string if not found | [LargeWireReportBuilder.cs:47, 56] |
| last_name | customers.last_name | Looked up by customer_id; empty string if not found | [LargeWireReportBuilder.cs:47, 57] |
| direction | wire_transfers.direction | Direct pass-through | [LargeWireReportBuilder.cs:58] |
| amount | wire_transfers.amount | Banker's rounding to 2 dp | [LargeWireReportBuilder.cs:50, 59] |
| counterparty_name | wire_transfers.counterparty_name | Direct pass-through | [LargeWireReportBuilder.cs:60] |
| status | wire_transfers.status | Direct pass-through | [LargeWireReportBuilder.cs:61] |
| as_of | wire_transfers.as_of | Direct pass-through from source row | [LargeWireReportBuilder.cs:62] |

## Non-Deterministic Fields
None identified. All values are deterministic given the same input data.

## Write Mode Implications
**Overwrite** mode: Each effective date run replaces the output CSV file entirely. In multi-day gap-fill scenarios, only the last day's output survives. Each run's output contains large wires for that day's effective date range only.

## Edge Cases

1. **$10,000 exact threshold**: Wires with exactly $10,000.00 are excluded (strict greater-than comparison).
   - Evidence: [LargeWireReportBuilder.cs:44] — `amount > 10000`

2. **Unknown customer**: If `customer_id` from wire_transfers has no match in customers, first_name and last_name are empty strings in the output. The wire is still included.
   - Evidence: [LargeWireReportBuilder.cs:47] — `GetValueOrDefault(customerId, ("", ""))`

3. **Customer name null handling**: Customer first_name/last_name that are null in the source become empty strings.
   - Evidence: [LargeWireReportBuilder.cs:33-34] — `?.ToString() ?? ""`

4. **Banker's rounding**: For amounts ending in exactly .X05 (midpoint), rounding goes to the nearest even digit. E.g., 10000.505 → 10000.50 (not 10000.51). This differs from standard "round half up" behavior.
   - Evidence: [LargeWireReportBuilder.cs:50] — explicit `MidpointRounding.ToEven`

5. **Multi-date data**: Since data comes from a date range, the same wire may appear on multiple as_of dates (snapshot pattern). All occurrences are included if they exceed the threshold.
   - Evidence: Source data is snapshot-based per Architecture.md

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Amount > 10000 threshold | [LargeWireReportBuilder.cs:44] |
| BR-2: Customer name lookup | [LargeWireReportBuilder.cs:26-36, 47] |
| BR-3: Banker's rounding | [LargeWireReportBuilder.cs:50] |
| BR-4: No status filter | [LargeWireReportBuilder.cs:40-65] |
| BR-5: No direction filter | [LargeWireReportBuilder.cs:40-65] |
| BR-6: Empty output guard | [LargeWireReportBuilder.cs:19-23] |
| BR-7: Last-write-wins customer lookup | [LargeWireReportBuilder.cs:31] |
| Output: CSV, Overwrite, LF, header | [large_wire_report.json:24-30] |

## Open Questions

1. **All statuses included**: Should Rejected wires be excluded from a "large wire report"? Including rejected wires could flag transfers that never actually moved money.
   - Confidence: MEDIUM — no filter exists, business intent unclear

2. **Data range**: With observed wire amounts ranging from ~$1,012 to ~$49,959, a $10,000 threshold captures roughly the upper half of the wire amount distribution. Is this threshold correct?
   - Confidence: HIGH — threshold is hard-coded and consistent; [LargeWireReportBuilder.cs:44]
