# Test Plan: CustomerAddressHistoryV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Null customer_id addresses excluded | No rows with null customer_id |
| TC-2 | BR-2 | Output has 7 columns | customer_id, address_line1, city, state_province, postal_code, country, as_of |
| TC-3 | BR-3 | Ordered by customer_id | Ascending customer_id order |
| TC-4 | BR-4 | Append mode | Data accumulates across dates |
| TC-5 | BR-7 | No address_id in output | address_id column absent |
| TC-6 | BR-1-8 | V2 output matches original for each date Oct 1-31 | EXCEPT query returns zero rows |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | All addresses have null customer_id | Empty result, no rows written |
| EC-2 | Multiple addresses per customer | All included, no deduplication |
| EC-3 | Null address fields (city, postal_code) | Pass through unchanged |
