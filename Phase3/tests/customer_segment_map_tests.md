# CustomerSegmentMap — Test Plan

## Test Cases

### TC-1: Row count matches customer-segment pairs
- **Traces to:** BR-1
- **Method:** Compare `SELECT COUNT(*) FROM double_secret_curated.customer_segment_map WHERE as_of = {date}` with `SELECT COUNT(*) FROM datalake.customers_segments WHERE as_of = {date}` (assuming all segment_ids are valid).
- **Expected:** 291 rows per date

### TC-2: Join uses both segment_id and as_of
- **Traces to:** BR-2
- **Method:** Verify that segment_name and segment_code come from the segments table for the same as_of date.
- **Expected:** All rows have valid segment_name and segment_code

### TC-3: Ordering is by customer_id then segment_id
- **Traces to:** BR-3
- **Method:** Verify output is sorted by customer_id ASC, then segment_id ASC within each customer.
- **Expected:** Ordered correctly

### TC-4: Append mode — all dates present
- **Traces to:** BR-4
- **Method:** After running Oct 1-31, verify 31 distinct as_of values with 291 rows each.
- **Expected:** 31 dates, 291 rows each

### TC-5: Weekend dates have data
- **Traces to:** BR-5
- **Method:** Verify rows exist for 2024-10-05 and 2024-10-06.
- **Expected:** Rows present for weekend dates

### TC-6: Inner join excludes orphan segments
- **Traces to:** BR-6
- **Method:** Verify no rows with segment_id values that don't exist in datalake.segments for that date.
- **Expected:** All segment_ids in output exist in segments reference table

### TC-7: Full EXCEPT comparison with original
- **Traces to:** All BRs
- **Method:** For each date: `SELECT * FROM curated.customer_segment_map WHERE as_of = {date} EXCEPT SELECT * FROM double_secret_curated.customer_segment_map WHERE as_of = {date}` and vice versa.
- **Expected:** Both EXCEPT queries return 0 rows
