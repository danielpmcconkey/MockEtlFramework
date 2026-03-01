# ComplianceOpenItems — Business Requirements Document

## Overview
Produces a list of open compliance events enriched with customer name information. Used to track unresolved compliance items requiring attention, with weekend fallback logic to avoid stale weekend data.

## Output Type
ParquetFileWriter

## Writer Configuration
- **outputDirectory**: `Output/curated/compliance_open_items/`
- **numParts**: 1
- **writeMode**: Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.compliance_events | event_id, customer_id, event_type, event_date, status, review_date | Effective date range via executor; further filtered by status = 'Open' and target date | [compliance_open_items.json:4-11] |
| datalake.customers | id, prefix, first_name, last_name, suffix | Effective date range via executor; used for name lookup only | [compliance_open_items.json:12-19] |

### Source Table Schemas (from database)

**compliance_events**: event_id (integer), customer_id (integer), event_type (varchar), event_date (date), status (varchar), review_date (date), as_of (date)

**customers**: id (integer), prefix (varchar), first_name (varchar), last_name (varchar), sort_name (varchar), suffix (varchar), birthdate (date), as_of (date)

## Business Rules

BR-1: Only compliance events with status = 'Open' are included in the output.
- Confidence: HIGH
- Evidence: [ComplianceOpenItemsBuilder.cs:49-53] — explicit filter `status == "Open"`

BR-2: Weekend fallback — on Saturday, the target date is shifted back 1 day (to Friday). On Sunday, the target date is shifted back 2 days (to Friday). Weekday dates are used as-is.
- Confidence: HIGH
- Evidence: [ComplianceOpenItemsBuilder.cs:26-29] — `DayOfWeek.Saturday => AddDays(-1)`, `DayOfWeek.Sunday => AddDays(-2)`

BR-3: Only compliance_events rows whose `as_of` matches the target date (after weekend fallback) are included.
- Confidence: HIGH
- Evidence: [ComplianceOpenItemsBuilder.cs:48] — `.Where(r => ((DateOnly)r["as_of"]) == targetDate)`

BR-4: Customer names are enriched via lookup by customer_id (customers.id). If no matching customer is found, first_name and last_name default to empty strings.
- Confidence: HIGH
- Evidence: [ComplianceOpenItemsBuilder.cs:34-44, 59-60] — `customerLookup.GetValueOrDefault(customerId, ("", ""))`

BR-5: The `prefix` and `suffix` columns are sourced from customers but never used in the output. They are dead-end columns.
- Confidence: HIGH
- Evidence: [ComplianceOpenItemsBuilder.cs:31] — comment "AP4: unused columns sourced — review_date from compliance_events, prefix/suffix from customers"; output schema only includes first_name and last_name

BR-6: The `review_date` column is sourced from compliance_events but never used in the output. It is a dead-end column.
- Confidence: HIGH
- Evidence: [ComplianceOpenItemsBuilder.cs:31] — comment about unused review_date; not present in output columns

BR-7: If compliance_events is null or empty, an empty output DataFrame is produced.
- Confidence: HIGH
- Evidence: [ComplianceOpenItemsBuilder.cs:19-22]

BR-8: The output `as_of` column is set to the target date (after weekend fallback), not the original `__maxEffectiveDate`.
- Confidence: HIGH
- Evidence: [ComplianceOpenItemsBuilder.cs:72] — `["as_of"] = targetDate`

BR-9: The customer lookup does not filter by as_of date — it uses ALL rows from the customers DataFrame. If multiple as_of dates exist in the customers DataFrame, the last-seen customer_id wins (due to dictionary overwrite).
- Confidence: HIGH
- Evidence: [ComplianceOpenItemsBuilder.cs:37-43] — iterates all `customers.Rows` without date filter, uses `customerLookup[id] = ...` which overwrites

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| event_id | compliance_events.event_id | Direct passthrough | [ComplianceOpenItemsBuilder.cs:63] |
| customer_id | compliance_events.customer_id | Converted to int via Convert.ToInt32 | [ComplianceOpenItemsBuilder.cs:59,64] |
| first_name | customers.first_name | Lookup by customer_id, default "" | [ComplianceOpenItemsBuilder.cs:60,65] |
| last_name | customers.last_name | Lookup by customer_id, default "" | [ComplianceOpenItemsBuilder.cs:60,66] |
| event_type | compliance_events.event_type | Direct passthrough | [ComplianceOpenItemsBuilder.cs:67] |
| event_date | compliance_events.event_date | Direct passthrough | [ComplianceOpenItemsBuilder.cs:68] |
| status | compliance_events.status | Direct passthrough (always "Open" due to filter) | [ComplianceOpenItemsBuilder.cs:69] |
| as_of | Computed | Set to targetDate (after weekend fallback) | [ComplianceOpenItemsBuilder.cs:72] |

## Non-Deterministic Fields
None identified. Output row order follows the order of filtered rows from the compliance_events DataFrame.

## Write Mode Implications
- **Overwrite** mode: each run replaces all part files in the output directory. Multi-day runs will only retain the last effective date's output.
- Evidence: [compliance_open_items.json:29]

## Edge Cases

1. **Weekend fallback**: Saturday and Sunday dates are mapped back to Friday's data. If Friday's data doesn't exist in the as_of column, zero rows are produced.
   - Evidence: [ComplianceOpenItemsBuilder.cs:26-29, 48]

2. **Empty input**: If compliance_events is null or empty, empty output is produced.
   - Evidence: [ComplianceOpenItemsBuilder.cs:19-22]

3. **Missing customer**: If a compliance event references a customer_id not found in the customers table, the name fields default to empty strings (no row is dropped).
   - Evidence: [ComplianceOpenItemsBuilder.cs:60]

4. **Duplicate customer_id across dates**: If customers DataFrame has multiple rows per customer (multiple as_of dates), the last-iterated row wins the lookup. This may produce inconsistent name resolution depending on DataFrame order.
   - Confidence: MEDIUM
   - Evidence: [ComplianceOpenItemsBuilder.cs:37-43]

5. **NULL handling**: NULL first_name or last_name in customers produces empty string in output.
   - Evidence: [ComplianceOpenItemsBuilder.cs:41-42] — `?.ToString() ?? ""`

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Open/Escalated filter | [ComplianceOpenItemsBuilder.cs:49-53] |
| BR-2: Weekend fallback | [ComplianceOpenItemsBuilder.cs:26-29] |
| BR-3: Target date filter | [ComplianceOpenItemsBuilder.cs:48] |
| BR-4: Customer name enrichment | [ComplianceOpenItemsBuilder.cs:34-44, 59-60] |
| BR-5: Unused prefix/suffix | [ComplianceOpenItemsBuilder.cs:31], [compliance_open_items.json:17] |
| BR-6: Unused review_date | [ComplianceOpenItemsBuilder.cs:31], [compliance_open_items.json:10] |
| BR-7: Empty input handling | [ComplianceOpenItemsBuilder.cs:19-22] |
| BR-8: as_of = targetDate | [ComplianceOpenItemsBuilder.cs:72] |
| BR-9: Customer lookup unfiltered by date | [ComplianceOpenItemsBuilder.cs:37-43] |

## Open Questions
1. Why are `prefix`, `suffix`, and `review_date` sourced but unused? Possible future requirements or configuration error.
   - Confidence: LOW
