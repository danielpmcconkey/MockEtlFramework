# CustomerAddressDeltas — Business Requirements Document

## Overview

Detects day-over-day changes in customer addresses by comparing the current effective date's address snapshot against the previous day's snapshot. Outputs rows for NEW addresses (not present yesterday) and UPDATED addresses (present yesterday but with changed field values). Output is appended daily.

## Source Tables

| Table | Schema | Columns Used | Purpose |
|-------|--------|-------------|---------|
| `datalake.addresses` | datalake | address_id, customer_id, address_line1, city, state_province, postal_code, country, start_date, end_date | Current and previous day address snapshots |
| `datalake.customers` | datalake | id, first_name, last_name | Customer name lookup with snapshot fallback |

## Business Rules

BR-1: Addresses from the current effective date are compared against addresses from the previous day (effective_date - 1).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:26] `var previousDate = currentDate.AddDays(-1);`
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:31-32] Both dates are fetched

BR-2: An address is classified as "NEW" if its address_id exists in the current snapshot but not in the previous snapshot.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:80-82] `if (!previousByAddressId.TryGetValue(addressId, out var previous))` -> changeType = "NEW"

BR-3: An address is classified as "UPDATED" if its address_id exists in both snapshots but any of the compare fields have changed.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:83-86] `else if (HasFieldChanged(current, previous))` -> changeType = "UPDATED"

BR-4: The compare fields for detecting changes are: customer_id, address_line1, city, state_province, postal_code, country, start_date, end_date.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:11-15] `CompareFields` array definition

BR-5: Field comparison normalizes values: NULLs and DBNull become empty string, DateTime/DateOnly are formatted as 'yyyy-MM-dd', other values are trimmed. Comparison is case-sensitive (StringComparison.Ordinal).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:213-218] Normalize method
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:207] `string.Equals(currentVal, previousVal, StringComparison.Ordinal)`

BR-6: Customer names are resolved with snapshot fallback (most recent customer record on or before the effective date). Format is "first_name last_name".
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:175-181] `SELECT DISTINCT ON (id) id, first_name, last_name FROM datalake.customers WHERE as_of <= @date ORDER BY id, as_of DESC`
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:194] `names[id] = $"{firstName} {lastName}";`

BR-7: On the first effective date (baseline), when no previous snapshot exists (previous day returns 0 rows), a single null-row is emitted with only as_of set and record_count = 0.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:36-56] `if (previousAddresses.Count == 0)` -> null row
- Evidence: [curated.customer_address_deltas] Oct 1 has 1 row with all nulls except as_of and record_count = 0

BR-8: When there are deltas, every row includes a `record_count` field set to the total number of delta rows for that date.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:112,136-140] `int recordCount = deltaRows.Count;` then set on every row

BR-9: When there are no deltas (but previous snapshot exists), a single null-row is emitted with as_of and record_count = 0.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:114-133] Zero delta case produces null row

BR-10: Output is ordered by address_id ascending.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:76] `currentByAddressId.OrderBy(kv => kv.Key)` — iteration over dictionary sorted by address_id

BR-11: The country field is trimmed in the output. Dates are formatted as 'yyyy-MM-dd'.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:104] `current["country"]?.ToString()?.Trim()`
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:105-106] FormatDate on start_date, end_date

BR-12: Output uses Append mode — each daily run appends delta rows.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/customer_address_deltas.json:14] `"writeMode": "Append"`

BR-13: Addresses that were deleted (existed yesterday but not today) are NOT reported. Only NEW and UPDATED are detected.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:76-89] Only iterates over `currentByAddressId` — addresses in previous but not current are never examined.

## Output Schema

| Column | Source | Transformation |
|--------|--------|----------------|
| change_type | Computed | "NEW" or "UPDATED" (or NULL in zero-row case) |
| address_id | addresses.address_id | Direct (from current snapshot) |
| customer_id | addresses.customer_id | Direct (from current snapshot) |
| customer_name | customers.first_name + " " + last_name | Concatenated; snapshot fallback; empty string if not found |
| address_line1 | addresses.address_line1 | Direct |
| city | addresses.city | Direct |
| state_province | addresses.state_province | Direct |
| postal_code | addresses.postal_code | Direct |
| country | addresses.country | Trimmed |
| start_date | addresses.start_date | Formatted as 'yyyy-MM-dd' |
| end_date | addresses.end_date | Formatted as 'yyyy-MM-dd'; NULL if no end date |
| as_of | Effective date | Formatted as 'yyyy-MM-dd' |
| record_count | Computed | Count of all delta rows for the date |

## Edge Cases

- **Baseline (first day, Oct 1)**: Previous date is Sep 30 which has no data in datalake.addresses. Returns single null row with record_count = 0.
- **No changes for a day**: Single null row with record_count = 0 (BR-9).
- **Customer not found**: customer_name is empty string (not NULL). Evidence: [line 92] `customerNames.GetValueOrDefault(customerId, "")`.
- **Deleted addresses**: Not detected — only NEW and UPDATED are reported (BR-13).
- **Weekend data**: Addresses have data every day including weekends, so deltas can occur on any day.

## Anti-Patterns Identified

- **AP-3: Unnecessary External Module** — PARTIALLY justified. The External module performs multi-query access with snapshot fallback for customer names and compares two different date snapshots. However, comparing two date snapshots side-by-side (current vs previous day) could potentially be done in SQL with a self-join or FULL OUTER JOIN. The snapshot fallback for customer names adds complexity. V2 approach: This External module is borderline justified due to the two-date comparison with snapshot fallback. Consider keeping as External or implementing as SQL with careful self-join logic.

- **AP-7: Hardcoded Magic Values** — The number `-1` for previous day offset is implicit business logic (compare against yesterday). Evidence: [line 26] `currentDate.AddDays(-1)`. V2 approach: Add a comment explaining the day-over-day comparison window.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [ExternalModules/CustomerAddressDeltaProcessor.cs:26,31-32] |
| BR-2 | [ExternalModules/CustomerAddressDeltaProcessor.cs:80-82] |
| BR-3 | [ExternalModules/CustomerAddressDeltaProcessor.cs:83-86] |
| BR-4 | [ExternalModules/CustomerAddressDeltaProcessor.cs:11-15] |
| BR-5 | [ExternalModules/CustomerAddressDeltaProcessor.cs:207,213-218] |
| BR-6 | [ExternalModules/CustomerAddressDeltaProcessor.cs:175-181,194] |
| BR-7 | [ExternalModules/CustomerAddressDeltaProcessor.cs:36-56] |
| BR-8 | [ExternalModules/CustomerAddressDeltaProcessor.cs:112,136-140] |
| BR-9 | [ExternalModules/CustomerAddressDeltaProcessor.cs:114-133] |
| BR-10 | [ExternalModules/CustomerAddressDeltaProcessor.cs:76] |
| BR-11 | [ExternalModules/CustomerAddressDeltaProcessor.cs:104-106] |
| BR-12 | [JobExecutor/Jobs/customer_address_deltas.json:14] |
| BR-13 | [ExternalModules/CustomerAddressDeltaProcessor.cs:76-89] |

## Open Questions

- **Address deletions**: The job does not detect deleted addresses (present in previous snapshot but absent in current). This may be intentional (only tracking additions and changes) or an oversight. Confidence: MEDIUM that it is intentional.
- **Country trimming inconsistency**: Only the `country` field is trimmed in the output; other address fields (address_line1, city, etc.) are not trimmed. However, in the database these are VARCHAR NOT NULL, so trailing spaces are less likely. Confidence: HIGH that this is a minor inconsistency.
