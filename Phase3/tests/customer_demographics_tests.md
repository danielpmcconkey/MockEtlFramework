# CustomerDemographics — Test Plan

## Test Cases

### TC-1: Row count matches customer count
- **Traces to:** BR-1
- **Method:** Compare `SELECT COUNT(*) FROM double_secret_curated.customer_demographics WHERE as_of = {date}` with `SELECT COUNT(*) FROM datalake.customers WHERE as_of = {date}`
- **Expected:** Counts are equal (223 on weekdays)

### TC-2: Age calculation correctness
- **Traces to:** BR-2
- **Method:** For a sample customer with known birthdate, verify age = floor((as_of - birthdate) in years). Check customer 1001 (birthdate 1985-03-12, as_of 2024-10-31): age should be 39.
- **Expected:** age = 39

### TC-3: Age bracket assignment
- **Traces to:** BR-3
- **Method:** Verify age brackets for boundary cases:
  - Age 25 -> '18-25'
  - Age 26 -> '26-35'
  - Age 35 -> '26-35'
  - Age 36 -> '36-45'
  - Age 45 -> '36-45'
  - Age 46 -> '46-55'
  - Age 55 -> '46-55'
  - Age 56 -> '56-65'
  - Age 65 -> '56-65'
  - Age 66 -> '65+'
- **Expected:** Each bracket matches the defined ranges

### TC-4: Primary phone selection
- **Traces to:** BR-4
- **Method:** For customer 1001 who has two phones (phone_id 1 and 2), verify primary_phone matches phone_id=1's number.
- **Expected:** primary_phone = '(865) 555-3216'

### TC-5: Primary email selection
- **Traces to:** BR-5
- **Method:** For customer 1001 who has two emails (email_id 1 and 2), verify primary_email matches email_id=1's address.
- **Expected:** primary_email = 'ethan.carter46@hotmail.com'

### TC-6: Missing phone/email defaults to empty string
- **Traces to:** BR-4, BR-5
- **Method:** Verify customers with no phone/email records get '' (empty string) not NULL.
- **Expected:** `WHERE primary_phone IS NULL` returns 0 rows; `WHERE primary_phone = ''` may return rows

### TC-7: Overwrite mode — only latest date persists
- **Traces to:** BR-6
- **Method:** After running for multiple dates, verify only one as_of value exists in the output table.
- **Expected:** `SELECT DISTINCT as_of` returns exactly 1 row

### TC-8: Weekend date produces empty output
- **Traces to:** BR-7
- **Method:** Run for a weekend date (e.g., 2024-10-05). Verify 0 rows produced.
- **Expected:** 0 rows for weekend as_of dates (customers table has no weekend data)

### TC-9: Birthdate passthrough
- **Traces to:** BR-8
- **Method:** Compare birthdate values between source and output for sample customers.
- **Expected:** Birthdates match exactly

### TC-10: Full EXCEPT comparison with original
- **Traces to:** All BRs
- **Method:** For each date, run: `SELECT * FROM curated.customer_demographics WHERE as_of = {date} EXCEPT SELECT * FROM double_secret_curated.customer_demographics WHERE as_of = {date}` and vice versa.
- **Expected:** Both EXCEPT queries return 0 rows
