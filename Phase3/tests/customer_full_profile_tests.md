# CustomerFullProfile — Test Plan

## Test Cases

### TC-1: Row count matches customer count
- **Traces to:** BR-1
- **Method:** Compare `SELECT COUNT(*) FROM double_secret_curated.customer_full_profile WHERE as_of = {date}` with `SELECT COUNT(*) FROM curated.customer_demographics WHERE as_of = {date}`
- **Expected:** Counts are equal (223 on weekdays, 0 on weekends)

### TC-2: Age matches CustomerDemographics
- **Traces to:** BR-2 (AP-2 fix)
- **Method:** Verify `age` values match between `double_secret_curated.customer_full_profile` and `curated.customer_demographics` for the same customer_id and as_of.
- **Expected:** All ages match

### TC-3: Age bracket matches CustomerDemographics
- **Traces to:** BR-3 (AP-2 fix)
- **Method:** Verify `age_bracket` values match between the two tables.
- **Expected:** All brackets match

### TC-4: Primary phone matches CustomerDemographics
- **Traces to:** BR-4 (AP-2 fix)
- **Method:** Verify `primary_phone` values match.
- **Expected:** All phones match

### TC-5: Primary email matches CustomerDemographics
- **Traces to:** BR-5 (AP-2 fix)
- **Method:** Verify `primary_email` values match.
- **Expected:** All emails match

### TC-6: Segment concatenation correctness
- **Traces to:** BR-6
- **Method:** For customer 1001, verify segments = 'US retail banking,Canadian retail banking' (segment_id order: 1 then 2).
- **Expected:** Exact string match with original output

### TC-7: Customer with no segments gets empty string
- **Traces to:** BR-7
- **Method:** If any customer has no segments, verify segments = '' (empty string, not NULL).
- **Expected:** No NULL values in segments column

### TC-8: Invalid segment_ids excluded
- **Traces to:** BR-8
- **Method:** Verify segments column does not include segment_ids that are in customers_segments but not in segments reference table.
- **Expected:** Only valid segment names appear

### TC-9: Overwrite mode
- **Traces to:** BR-9
- **Method:** After running for multiple dates, verify only one as_of value exists.
- **Expected:** Single as_of date in output

### TC-10: Weekend produces empty output
- **Traces to:** BR-10
- **Method:** Run for weekend date (e.g., 2024-10-05). Customer_demographics has no rows for weekends, so output should be empty.
- **Expected:** 0 rows

### TC-11: Full EXCEPT comparison with original
- **Traces to:** All BRs
- **Method:** For each date: `SELECT * FROM curated.customer_full_profile WHERE as_of = {date} EXCEPT SELECT * FROM double_secret_curated.customer_full_profile WHERE as_of = {date}` and vice versa.
- **Expected:** Both EXCEPT queries return 0 rows
