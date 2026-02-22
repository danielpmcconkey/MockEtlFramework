# Test Plan: CustomerContactInfoV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Phone numbers mapped correctly | contact_type='Phone', contact_subtype=phone_type, contact_value=phone_number |
| TC-2 | BR-2 | Email addresses mapped correctly | contact_type='Email', contact_subtype=email_type, contact_value=email_address |
| TC-3 | BR-3 | UNION ALL preserves duplicates | All records from both tables included |
| TC-4 | BR-4 | Ordered by customer_id, contact_type, contact_subtype | Correct sort order |
| TC-5 | BR-5 | Append mode | Data accumulates across dates |
| TC-6 | BR-8 | phone_id and email_id not in output | Only 5 output columns |
| TC-7 | BR-9 | No filtering applied | All records pass through |
| TC-8 | BR-10 | Output for all 31 days | 750 rows per date |
| TC-9 | BR-1-10 | V2 output matches original for each date Oct 1-31 | EXCEPT query returns zero rows |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | No phone or email records for a date | Empty result, no rows written |
| EC-2 | Null customer_id in source | Null passes through unchanged |
| EC-3 | Null phone_number or email_address | Null in contact_value |
