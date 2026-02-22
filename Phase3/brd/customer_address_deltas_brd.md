# BRD: CustomerAddressDeltas

## Overview
This job detects day-over-day changes in customer address data by comparing the current date's address snapshot against the previous day's snapshot. It identifies NEW addresses (address_id not present yesterday) and UPDATED addresses (field values changed), producing a change log for downstream consumption.

## Source Tables

| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| addresses | datalake | address_id, customer_id, address_line1, city, state_province, postal_code, country, start_date, end_date | Fetched for current date AND previous date (current - 1 day) | [ExternalModules/CustomerAddressDeltaProcessor.cs:31-32,147-153] `WHERE as_of = @date` |
| customers | datalake | id, first_name, last_name | Snapshot fallback: `DISTINCT ON (id) WHERE as_of <= @date ORDER BY id, as_of DESC` for current date only | [ExternalModules/CustomerAddressDeltaProcessor.cs:33,176-181] |

## Business Rules

BR-1: The job compares address snapshots between the current effective date and the previous day (effective_date - 1).
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:26] `var previousDate = currentDate.AddDays(-1);`
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:31-32] `FetchAddresses(connection, currentDate)` and `FetchAddresses(connection, previousDate)`

BR-2: An address is classified as "NEW" if its address_id exists in the current snapshot but not in the previous snapshot.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:80-82] `if (!previousByAddressId.TryGetValue(addressId, out var previous)) changeType = "NEW";`

BR-3: An address is classified as "UPDATED" if its address_id exists in both snapshots but any of the compared fields have changed.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:83-86] `else if (HasFieldChanged(current, previous)) changeType = "UPDATED";`

BR-4: The fields compared for change detection are: customer_id, address_line1, city, state_province, postal_code, country, start_date, end_date.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:10-14] `CompareFields` array lists exactly these 8 fields

BR-5: Field comparison normalizes values: nulls become empty string, dates are formatted as "yyyy-MM-dd", strings are trimmed.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:213-218] `Normalize()` method: null/DBNull -> "", DateTime -> "yyyy-MM-dd", else ToString().Trim()

BR-6: Addresses that exist in the previous snapshot but NOT in the current snapshot (DELETED) are NOT detected.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:76] Loop iterates `currentByAddressId` only -- previous-only addresses are never checked

BR-7: On the first effective date (baseline day), when no previous snapshot exists, a single sentinel row with all-null fields except as_of and record_count=0 is emitted.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:36-56] `if (previousAddresses.Count == 0)` creates null sentinel row

BR-8: When deltas exist, every output row carries a record_count equal to the total number of delta rows for that date.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:112,136-140] `int recordCount = deltaRows.Count;` then sets on every row

BR-9: When no deltas are detected (but previous snapshot exists), a single sentinel row with all-null fields except as_of and record_count=0 is emitted.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:114-133] Zero-delta case creates null sentinel row

BR-10: Customer names are looked up using snapshot fallback (most recent as_of <= current date), concatenated as "first_name last_name".
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:176-181] `WHERE as_of <= @date ORDER BY id, as_of DESC`
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:194] `names[id] = $"{firstName} {lastName}";`

BR-11: Output rows are ordered by address_id ascending.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:76] `currentByAddressId.OrderBy(kv => kv.Key)`

BR-12: Data is written in Append mode -- each date's deltas accumulate.
- Confidence: HIGH
- Evidence: [customer_address_deltas.json:14] `"writeMode": "Append"`

BR-13: The country field is trimmed in output; dates are formatted as "yyyy-MM-dd" strings.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:104] `country?.ToString()?.Trim()`
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:105-106] `FormatDate(current["start_date"])`, `FormatDate(current["end_date"])`

BR-14: The job is an External-only pipeline with no DataSourcing modules; it manages its own database queries directly.
- Confidence: HIGH
- Evidence: [customer_address_deltas.json:6-9] Only module is type "External" with CustomerAddressDeltaProcessor

BR-15: If a customer has no name record in the customers table, customer_name defaults to empty string.
- Confidence: HIGH
- Evidence: [ExternalModules/CustomerAddressDeltaProcessor.cs:92] `customerNames.GetValueOrDefault(customerId, "")`

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| change_type | Computed | "NEW" or "UPDATED" (or null in sentinel rows) | [CustomerAddressDeltaProcessor.cs:80-86] |
| address_id | addresses.address_id | Direct pass-through | [CustomerAddressDeltaProcessor.cs:97] |
| customer_id | addresses.customer_id | Direct pass-through | [CustomerAddressDeltaProcessor.cs:98] |
| customer_name | customers.first_name + " " + customers.last_name | Concatenated string via snapshot fallback | [CustomerAddressDeltaProcessor.cs:92-93,99] |
| address_line1 | addresses.address_line1 | Direct pass-through | [CustomerAddressDeltaProcessor.cs:100] |
| city | addresses.city | Direct pass-through | [CustomerAddressDeltaProcessor.cs:101] |
| state_province | addresses.state_province | Direct pass-through | [CustomerAddressDeltaProcessor.cs:102] |
| postal_code | addresses.postal_code | Direct pass-through | [CustomerAddressDeltaProcessor.cs:103] |
| country | addresses.country | Trimmed | [CustomerAddressDeltaProcessor.cs:104] |
| start_date | addresses.start_date | Formatted as "yyyy-MM-dd" | [CustomerAddressDeltaProcessor.cs:105] |
| end_date | addresses.end_date | Formatted as "yyyy-MM-dd" or null | [CustomerAddressDeltaProcessor.cs:106] |
| as_of | effective_date | Formatted as "yyyy-MM-dd" string | [CustomerAddressDeltaProcessor.cs:107] |
| record_count | Computed | Total delta rows for the date (0 for sentinel) | [CustomerAddressDeltaProcessor.cs:112,136-140] |

## Edge Cases

- **NULL handling**: Null fields in addresses are preserved as-is in output. Null customer names default to empty string. FormatDate returns null for null/DBNull inputs.
  - Evidence: [CustomerAddressDeltaProcessor.cs:221-226] FormatDate returns null for null/DBNull
- **Weekend/date fallback**: Addresses have data for all 31 days including weekends. The previous date comparison always looks at (effective_date - 1), which means on Monday it compares with Sunday (not Friday).
  - Evidence: [datalake.addresses] All 31 dates present; [CustomerAddressDeltaProcessor.cs:26] `currentDate.AddDays(-1)`
- **Zero-row behavior**: When no changes detected, sentinel row with nulls + record_count=0 is emitted. When on baseline day (no previous data), same sentinel row.
  - Evidence: [CustomerAddressDeltaProcessor.cs:36-56,114-133]
- **Beyond data range**: When the job runs for dates beyond the datalake data range (after Oct 31), both current and previous addresses will be empty, triggering the baseline sentinel row. This produces ongoing null sentinel rows indefinitely.
  - Evidence: [curated.customer_address_deltas] Shows 510 rows spanning dates far beyond Oct 31, all with record_count=0 after the initial period

## Traceability Matrix

| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [CustomerAddressDeltaProcessor.cs:26,31-32] |
| BR-2 | [CustomerAddressDeltaProcessor.cs:80-82] |
| BR-3 | [CustomerAddressDeltaProcessor.cs:83-86] |
| BR-4 | [CustomerAddressDeltaProcessor.cs:10-14] |
| BR-5 | [CustomerAddressDeltaProcessor.cs:213-218] |
| BR-6 | [CustomerAddressDeltaProcessor.cs:76] |
| BR-7 | [CustomerAddressDeltaProcessor.cs:36-56] |
| BR-8 | [CustomerAddressDeltaProcessor.cs:112,136-140] |
| BR-9 | [CustomerAddressDeltaProcessor.cs:114-133] |
| BR-10 | [CustomerAddressDeltaProcessor.cs:176-181,194] |
| BR-11 | [CustomerAddressDeltaProcessor.cs:76] |
| BR-12 | [customer_address_deltas.json:14] |
| BR-13 | [CustomerAddressDeltaProcessor.cs:104-106] |
| BR-14 | [customer_address_deltas.json:6-9] |
| BR-15 | [CustomerAddressDeltaProcessor.cs:92] |

## Open Questions

- **No DELETED detection**: The job does not detect addresses that existed yesterday but are missing today. This may be intentional (tracking new/changed only) or an oversight. Confidence: HIGH (behavior is clear from code).
- **Record_count bug during delta row construction**: During the loop that builds delta rows, `record_count` is set to `deltaRows.Count` (line 108) which is the count BEFORE the current row is added. This value is later overwritten (lines 136-140) with the correct total. The intermediate incorrect value has no effect on final output. Confidence: HIGH (no bug in output).
- **Beyond data range behavior**: After datalake data ends, the job continues producing sentinel null rows for every date. This is a consequence of auto-advance and the External module handling empty data gracefully. Confidence: HIGH.
