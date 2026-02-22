# Test Plan: CustomerDemographicsV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Verify one output row per customer | Row count matches customer count for effective date |
| TC-2 | BR-2 | Verify age computation | Age = asOfDate.Year - birthdate.Year, decremented if birthday not yet passed |
| TC-3 | BR-3 | Verify age bracket classification | Age <26 => "18-25", <=35 => "26-35", <=45 => "36-45", <=55 => "46-55", <=65 => "56-65", >65 => "65+" |
| TC-4 | BR-4 | Verify primary phone is first found | First phone_number row per customer_id used |
| TC-5 | BR-5 | Verify primary email is first found | First email_address row per customer_id used |
| TC-6 | BR-6 | Verify empty string for missing phone | Customers with no phone numbers get primary_phone = "" |
| TC-7 | BR-7 | Verify empty string for missing email | Customers with no emails get primary_email = "" |
| TC-8 | BR-8 | Verify birthdate passed through | birthdate in output matches customers.birthdate |
| TC-9 | BR-9 | Verify empty input guard | When customers is null/empty, output is empty DataFrame |
| TC-10 | BR-10 | Verify unused data sourcing | Output has no segment, prefix, sort_name, suffix columns |
| TC-11 | BR-11 | Verify Overwrite mode | Only latest effective date's data exists |
| TC-12 | BR-12 | Verify NULL name coalescing | NULL first_name/last_name become empty string |
| TC-13 | BR-1,11 | Compare V2 output to original | EXCEPT query yields zero rows |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | Zero customer rows | Empty output DataFrame |
| EC-2 | Customer with no phone numbers | primary_phone = "" (not NULL) |
| EC-3 | Customer with no email addresses | primary_email = "" (not NULL) |
| EC-4 | Customer with multiple phones | Only first phone used |
| EC-5 | Customer with birthday on as_of date | Age correctly computed (birthday counts) |
| EC-6 | Customer aged exactly 26 | age_bracket = "26-35" |
| EC-7 | Customer aged exactly 65 | age_bracket = "56-65" |
| EC-8 | Customer aged 66 | age_bracket = "65+" |
| EC-9 | Weekend effective date | Empty output (customers weekday-only) |
