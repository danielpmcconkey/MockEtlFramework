# ComplianceEventSummary — Business Requirements Document

## Overview
Produces a summary count of compliance events grouped by event type and status for each effective date. Used for compliance monitoring dashboards to track event volumes by category.

## Output Type
CsvFileWriter

## Writer Configuration
- **outputFile**: `Output/curated/compliance_event_summary.csv`
- **includeHeader**: true
- **trailerFormat**: `TRAILER|{row_count}|{date}`
- **writeMode**: Overwrite
- **lineEnding**: LF

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.compliance_events | event_id, customer_id, event_type, status | Effective date range injected by executor via `__minEffectiveDate`/`__maxEffectiveDate` | [compliance_event_summary.json:4-11] |
| datalake.accounts | account_id, customer_id, account_type, account_status | Effective date range injected by executor | [compliance_event_summary.json:12-19] |

### Source Table Schemas (from database)

**compliance_events**: event_id (integer), customer_id (integer), event_type (varchar), event_date (date), status (varchar), review_date (date), as_of (date)

**accounts**: account_id (integer), customer_id (integer), account_type (varchar), account_status (varchar), open_date (date), current_balance (numeric), interest_rate (numeric), credit_limit (numeric), apr (numeric), as_of (date)

## Business Rules

BR-1: Events are counted by unique (event_type, status) combination across all rows in the compliance_events DataFrame for the effective date.
- Confidence: HIGH
- Evidence: [ComplianceEventSummaryBuilder.cs:36-46] — iterates all rows, builds dictionary keyed by (eventType, status) tuple, increments count

BR-2: On Sundays (based on `__maxEffectiveDate`), the job produces an empty output DataFrame with zero data rows.
- Confidence: HIGH
- Evidence: [ComplianceEventSummaryBuilder.cs:18-23] — explicit `DayOfWeek.Sunday` check returns empty DataFrame

BR-3: If the compliance_events DataFrame is null or empty, the job produces an empty output DataFrame.
- Confidence: HIGH
- Evidence: [ComplianceEventSummaryBuilder.cs:25-29]

BR-4: The `accounts` DataFrame is sourced but never used in the External module logic. It is a dead-end data source.
- Confidence: HIGH
- Evidence: [ComplianceEventSummaryBuilder.cs:31] — comment "AP1: accounts sourced but never used (dead-end)"; no reference to accounts data in any computation

BR-5: The `as_of` value is taken from the first row of the compliance_events DataFrame and applied uniformly to all output rows.
- Confidence: HIGH
- Evidence: [ComplianceEventSummaryBuilder.cs:33] — `complianceEvents.Rows[0]["as_of"]`

BR-6: NULL event_type values are coalesced to empty string (""). NULL status values are coalesced to empty string ("").
- Confidence: HIGH
- Evidence: [ComplianceEventSummaryBuilder.cs:39-40] — `?.ToString() ?? ""`

BR-7: Event types in the data are: AML_FLAG, ID_VERIFICATION, KYC_REVIEW, PEP_CHECK, SANCTIONS_SCREEN
- Confidence: HIGH
- Evidence: [DB query: SELECT DISTINCT event_type FROM datalake.compliance_events]

BR-8: Status values in the data are: Cleared, Escalated, Open
- Confidence: HIGH
- Evidence: [DB query: SELECT DISTINCT status FROM datalake.compliance_events]

BR-9: The trailer line uses the `{row_count}` token which substitutes the count of data rows (excluding header and trailer), and `{date}` which substitutes `__maxEffectiveDate`.
- Confidence: HIGH
- Evidence: [compliance_event_summary.json:29] — `"trailerFormat": "TRAILER|{row_count}|{date}"`; [Architecture.md:241] — trailer token definitions

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| event_type | compliance_events.event_type | None (direct passthrough, NULL coalesced to "") | [ComplianceEventSummaryBuilder.cs:39,52] |
| status | compliance_events.status | None (direct passthrough, NULL coalesced to "") | [ComplianceEventSummaryBuilder.cs:40,53] |
| event_count | Computed | COUNT of rows per (event_type, status) group | [ComplianceEventSummaryBuilder.cs:46,54] |
| as_of | compliance_events.as_of | Taken from first row of input | [ComplianceEventSummaryBuilder.cs:33,55] |

## Non-Deterministic Fields
- Output row order is non-deterministic: rows are iterated from a `Dictionary<(string, string), int>`, whose enumeration order is not guaranteed.
  - Evidence: [ComplianceEventSummaryBuilder.cs:49] — `foreach (var kvp in counts)` iterates dictionary without ordering

## Write Mode Implications
- **Overwrite** mode: each run replaces the entire output file. Multi-day runs will only retain the last effective date's output since the file is overwritten each time.
- Evidence: [compliance_event_summary.json:30]

## Edge Cases

1. **Sunday skip**: If `__maxEffectiveDate` falls on Sunday, output is an empty CSV (header + trailer only, zero data rows).
   - Evidence: [ComplianceEventSummaryBuilder.cs:18-23]

2. **Empty input**: If compliance_events is null or has zero rows, output is an empty CSV (header + trailer only).
   - Evidence: [ComplianceEventSummaryBuilder.cs:25-29]

3. **NULL field handling**: NULL event_type or status values produce empty-string keys in the grouping, which are valid output rows.
   - Evidence: [ComplianceEventSummaryBuilder.cs:39-40]

4. **as_of from first row**: If the DataFrame contains rows from multiple as_of dates (multi-day effective date range), the as_of in output is always from the first row, which may not represent all dates.
   - Confidence: MEDIUM
   - Evidence: [ComplianceEventSummaryBuilder.cs:33]

5. **Saturday handling**: Unlike Sunday, Saturday is NOT skipped — data is processed normally.
   - Confidence: HIGH
   - Evidence: [ComplianceEventSummaryBuilder.cs:18-23] — only Sunday is checked

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Group by (event_type, status) | [ComplianceEventSummaryBuilder.cs:36-46] |
| BR-2: Sunday skip | [ComplianceEventSummaryBuilder.cs:18-23] |
| BR-3: Empty input handling | [ComplianceEventSummaryBuilder.cs:25-29] |
| BR-4: Dead-end accounts source | [ComplianceEventSummaryBuilder.cs:31], [compliance_event_summary.json:12-19] |
| BR-5: as_of from first row | [ComplianceEventSummaryBuilder.cs:33] |
| BR-6: NULL coalescing | [ComplianceEventSummaryBuilder.cs:39-40] |
| BR-7: Event type domain | [DB query on datalake.compliance_events] |
| BR-8: Status domain | [DB query on datalake.compliance_events] |
| BR-9: Trailer format | [compliance_event_summary.json:29], [Architecture.md:241] |

## Open Questions
1. The `accounts` DataFrame is sourced but entirely unused. Is this intentional (defensive sourcing for future use) or a configuration error?
   - Confidence: LOW — code comment says "dead-end" but no production context available
