# Test Plan: CustomerFullProfileV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Verify one output row per customer | Row count matches customer count for effective date |
| TC-2 | BR-2 | Verify age computation | Same age calculation as CustomerDemographics (year diff with birthday adjustment) |
| TC-3 | BR-3 | Verify age bracket classification | Same ranges as CustomerDemographics |
| TC-4 | BR-4 | Verify primary phone first-found | First phone per customer, not filtered by type |
| TC-5 | BR-5 | Verify primary email first-found | First email per customer, not filtered by type |
| TC-6 | BR-6 | Verify segment resolution | Comma-separated segment names from customers_segments + segments join |
| TC-7 | BR-7 | Verify empty segments | Customers with no segments get segments = "" |
| TC-8 | BR-8 | Verify unmatched segment_ids excluded | segment_ids not in segments table are silently dropped |
| TC-9 | BR-9 | Verify comma delimiter (no space) | Multiple segments joined with "," not ", " |
| TC-10 | BR-10 | Verify empty input guard | When customers is null/empty, output is empty DataFrame |
| TC-11 | BR-11 | Verify Overwrite mode | Only latest effective date's data |
| TC-12 | BR-12 | Verify segment_code unused | No segment_code column in output |
| TC-13 | BR-13 | Verify segment_name dictionary overwrite behavior | Last segment_name wins per segment_id |
| TC-14 | BR-14 | Verify NULL name and contact defaults | Names coalesced to "", phone/email default to "" |
| TC-15 | BR-1,11 | Compare V2 output to original | EXCEPT query yields zero rows |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | Zero customer rows | Empty output DataFrame |
| EC-2 | Customer with no segments | segments = "" |
| EC-3 | Customer with multiple segments | Comma-separated, no spaces (e.g., "seg1,seg2") |
| EC-4 | Customer with no phone or email | primary_phone = "", primary_email = "" |
| EC-5 | Unmatched segment_id in customers_segments | Silently excluded from segment list |
| EC-6 | Weekend effective date | Empty output |
| EC-7 | NULL first_name or last_name | Coalesced to empty string |
