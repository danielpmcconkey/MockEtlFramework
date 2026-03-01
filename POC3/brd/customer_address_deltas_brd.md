# CustomerAddressDeltas -- Business Requirements Document

## Overview
Detects day-over-day changes in customer address records by comparing the current effective date's address snapshot against the previous day's snapshot. Produces delta records (NEW or UPDATED) with change type, full address details, and customer name. This is a self-sourcing External module that queries the database directly instead of relying on DataSourcing modules.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `output`
- **outputDirectory**: `Output/curated/customer_address_deltas/`
- **numParts**: 1
- **writeMode**: Append

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.addresses | address_id, customer_id, address_line1, city, state_province, postal_code, country, start_date, end_date | Filtered by `as_of = currentDate` and `as_of = previousDate` (current - 1 day) | [CustomerAddressDeltaProcessor.cs:149-153] |
| datalake.customers | id, first_name, last_name | Filtered by `as_of <= currentDate`, DISTINCT ON (id) ORDER BY as_of DESC (most recent name) | [CustomerAddressDeltaProcessor.cs:177-180] |

Note: This job has NO DataSourcing modules in its config. The External module directly queries PostgreSQL via NpgsqlConnection.

## Business Rules

BR-1: The current effective date is read from `__minEffectiveDate` (not `__maxEffectiveDate`). The previous date is computed as `currentDate.AddDays(-1)`.
- Confidence: HIGH
- Evidence: [CustomerAddressDeltaProcessor.cs:25-26] -- `var currentDate = (DateOnly)sharedState[DataSourcing.MinDateKey]; var previousDate = currentDate.AddDays(-1);`

BR-2: The module directly queries PostgreSQL for address data (bypassing DataSourcing), comparing two snapshots keyed by `as_of` date.
- Confidence: HIGH
- Evidence: [CustomerAddressDeltaProcessor.cs:28-33] -- Opens NpgsqlConnection and calls FetchAddresses for both dates.

BR-3: On the baseline day (first effective date), when no previous-day snapshot exists (previousAddresses.Count == 0), the output is a single row with all fields null except `as_of` (the current date as "yyyy-MM-dd" string) and `record_count` (0).
- Confidence: HIGH
- Evidence: [CustomerAddressDeltaProcessor.cs:36-56] -- baseline guard produces null-filled row.

BR-4: A "NEW" delta is detected when an address_id exists in the current snapshot but not in the previous snapshot.
- Confidence: HIGH
- Evidence: [CustomerAddressDeltaProcessor.cs:80-82] -- `if (!previousByAddressId.TryGetValue(addressId, out var previous)) changeType = "NEW"`.

BR-5: An "UPDATED" delta is detected when an address_id exists in both snapshots and any of the compare fields have changed.
- Confidence: HIGH
- Evidence: [CustomerAddressDeltaProcessor.cs:83-86] -- `else if (HasFieldChanged(current, previous)) changeType = "UPDATED"`.

BR-6: The compare fields for change detection are: customer_id, address_line1, city, state_province, postal_code, country, start_date, end_date. Field comparison is string-based after normalization.
- Confidence: HIGH
- Evidence: [CustomerAddressDeltaProcessor.cs:10-14] -- `CompareFields` array. [CustomerAddressDeltaProcessor.cs:200-210] -- `HasFieldChanged` uses `Normalize` for string comparison.

BR-7: DELETED addresses (present in previous but not current) are NOT detected. The module only iterates over `currentByAddressId`.
- Confidence: HIGH
- Evidence: [CustomerAddressDeltaProcessor.cs:76] -- `foreach (var (addressId, current) in currentByAddressId.OrderBy(kv => kv.Key))`.

BR-8: Customer names are fetched using DISTINCT ON (id) with as_of <= currentDate, ordering by as_of DESC. This returns the most recent name for each customer up to the current date.
- Confidence: HIGH
- Evidence: [CustomerAddressDeltaProcessor.cs:177-180] -- SQL query with `DISTINCT ON (id)` and `as_of <= @date ORDER BY id, as_of DESC`.

BR-9: Customer name is formatted as "first_name last_name" (space-separated).
- Confidence: HIGH
- Evidence: [CustomerAddressDeltaProcessor.cs:194] -- `names[id] = $"{firstName} {lastName}"`.

BR-10: The `country` field is trimmed in the output (`.Trim()`), while other string fields are not explicitly trimmed in the output row construction.
- Confidence: HIGH
- Evidence: [CustomerAddressDeltaProcessor.cs:104] -- `current["country"]?.ToString()?.Trim()`.

BR-11: Date fields (start_date, end_date) are formatted as "yyyy-MM-dd" strings. Null dates remain null.
- Confidence: HIGH
- Evidence: [CustomerAddressDeltaProcessor.cs:105-106,221-227] -- `FormatDate` method.

BR-12: The `as_of` field is stored as a string ("yyyy-MM-dd") not a DateOnly.
- Confidence: HIGH
- Evidence: [CustomerAddressDeltaProcessor.cs:107] -- `currentDate.ToString("yyyy-MM-dd")`.

BR-13: The `record_count` field is set to the total number of delta rows, and this value is stamped on every output row.
- Confidence: HIGH
- Evidence: [CustomerAddressDeltaProcessor.cs:112,137-140] -- `recordCount = deltaRows.Count`, then loop sets `record_count = recordCount` on every row.

BR-14: When there are no deltas (no NEW or UPDATED records), a single null-filled row is produced with `as_of` and `record_count = 0`.
- Confidence: HIGH
- Evidence: [CustomerAddressDeltaProcessor.cs:114-133] -- no-delta guard.

BR-15: Delta rows are ordered by address_id ascending.
- Confidence: HIGH
- Evidence: [CustomerAddressDeltaProcessor.cs:76] -- `.OrderBy(kv => kv.Key)`.

BR-16: The Normalize function trims string values and converts DateTime/DateOnly to "yyyy-MM-dd" for comparison. Null/DBNull normalize to empty string.
- Confidence: HIGH
- Evidence: [CustomerAddressDeltaProcessor.cs:213-219] -- Normalize method.

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| change_type | Computed | "NEW" or "UPDATED", null on baseline/no-delta rows | [CustomerAddressDeltaProcessor.cs:78-86] |
| address_id | addresses.address_id | Pass-through | [CustomerAddressDeltaProcessor.cs:97] |
| customer_id | addresses.customer_id | Pass-through | [CustomerAddressDeltaProcessor.cs:98] |
| customer_name | customers.first_name + " " + customers.last_name | Concatenated, most recent name as of current date | [CustomerAddressDeltaProcessor.cs:92-93,99] |
| address_line1 | addresses.address_line1 | Pass-through | [CustomerAddressDeltaProcessor.cs:100] |
| city | addresses.city | Pass-through | [CustomerAddressDeltaProcessor.cs:101] |
| state_province | addresses.state_province | Pass-through | [CustomerAddressDeltaProcessor.cs:102] |
| postal_code | addresses.postal_code | Pass-through | [CustomerAddressDeltaProcessor.cs:103] |
| country | addresses.country | Trimmed | [CustomerAddressDeltaProcessor.cs:104] |
| start_date | addresses.start_date | Formatted as "yyyy-MM-dd" string, or null | [CustomerAddressDeltaProcessor.cs:105] |
| end_date | addresses.end_date | Formatted as "yyyy-MM-dd" string, or null | [CustomerAddressDeltaProcessor.cs:106] |
| as_of | __minEffectiveDate | Formatted as "yyyy-MM-dd" string | [CustomerAddressDeltaProcessor.cs:107] |
| record_count | Computed | Total number of delta rows in this run | [CustomerAddressDeltaProcessor.cs:112,137-140] |

## Non-Deterministic Fields
None identified.

## Write Mode Implications
- **Append**: Each effective date's delta is appended to the existing Parquet output. Over multiple auto-advance runs, the output accumulates one delta set per day. This is critical -- append mode means historical deltas are preserved.

## Edge Cases
- **Baseline day (first run)**: Previous date has no data. Output is a single row with all fields null except as_of and record_count=0.
- **No changes detected**: Output is a single null-filled row with record_count=0 for that day.
- **Deleted addresses**: NOT detected. Only NEW and UPDATED changes are captured.
- **Weekend dates**: No special weekend handling. If no data exists for the previous day (e.g., Saturday checking against Friday), the comparison proceeds normally and detects any changes.
- **Country field trimming**: The `country` column in the database is `character` (fixed-width), which may have trailing spaces. Only this field is explicitly trimmed in output.
- **Customer name for unknown customer**: Falls back to empty string via `GetValueOrDefault(customerId, "")`.
- **Multiple address records per customer**: Each address is tracked independently by address_id.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Uses __minEffectiveDate | [CustomerAddressDeltaProcessor.cs:25] |
| Direct DB query (no DataSourcing) | [CustomerAddressDeltaProcessor.cs:28-33] |
| Baseline day null row | [CustomerAddressDeltaProcessor.cs:36-56] |
| NEW detection | [CustomerAddressDeltaProcessor.cs:80-82] |
| UPDATED detection | [CustomerAddressDeltaProcessor.cs:83-86] |
| Compare fields list | [CustomerAddressDeltaProcessor.cs:10-14] |
| No DELETE detection | [CustomerAddressDeltaProcessor.cs:76] |
| Customer name DISTINCT ON | [CustomerAddressDeltaProcessor.cs:177-180] |
| Country trimming | [CustomerAddressDeltaProcessor.cs:104] |
| Append write mode | [customer_address_deltas.json:15] |
| record_count on every row | [CustomerAddressDeltaProcessor.cs:137-140] |

## Open Questions
- OQ-1: DELETED addresses (present yesterday, gone today) are not detected. Whether this is by design or an oversight is unclear. Confidence: MEDIUM -- the code only iterates current addresses.
- OQ-2: The initial record_count set during row construction (line 108) is a running index, not the final count. It gets overwritten at lines 137-140. The intermediate value is never visible in output but is technically set incorrectly during construction. Confidence: LOW -- cosmetic only.
