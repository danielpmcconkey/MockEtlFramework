# Test Plan: CustomerAddressDeltasV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Compares current date vs previous day | Delta detection between consecutive days |
| TC-2 | BR-2 | NEW address detected | address_id in current but not previous gets change_type="NEW" |
| TC-3 | BR-3 | UPDATED address detected | Changed field values get change_type="UPDATED" |
| TC-4 | BR-4 | All 8 compare fields checked | customer_id, address_line1, city, state_province, postal_code, country, start_date, end_date |
| TC-5 | BR-5 | Normalized comparison | Nulls as "", dates as yyyy-MM-dd, strings trimmed |
| TC-6 | BR-6 | DELETED not detected | Addresses in previous but not current are ignored |
| TC-7 | BR-7 | Baseline sentinel on first date | When no previous data, null sentinel with record_count=0 |
| TC-8 | BR-8 | record_count on all rows | Every row has correct total delta count |
| TC-9 | BR-9 | No-delta sentinel | When no changes found, null sentinel with record_count=0 |
| TC-10 | BR-10 | Customer names via snapshot fallback | Names from most recent customer snapshot <= date |
| TC-11 | BR-11 | Ordered by address_id ASC | Output rows in address_id order |
| TC-12 | BR-12 | Append mode | Data accumulates across dates |
| TC-13 | BR-13 | Country trimmed, dates formatted | Correct formatting |
| TC-14 | BR-15 | Missing customer name defaults to "" | Empty string when no customer record |
| TC-15 | BR-1-15 | V2 output matches original for each date Oct 1-31 | EXCEPT query returns zero rows |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | First effective date (baseline) | Sentinel null row with record_count=0 |
| EC-2 | No changes between consecutive days | Sentinel null row with record_count=0 |
| EC-3 | Multiple changes on same day | All changes reported, record_count = total |
| EC-4 | Address field changed from null to value | Detected as UPDATED |
| EC-5 | Beyond data range (no addresses) | Baseline sentinel row emitted |
