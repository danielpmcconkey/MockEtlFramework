# Test Plan: CustomerSegmentMapV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Verify INNER JOIN on segment_id AND as_of | Only matching rows from both tables appear in output |
| TC-2 | BR-2 | Verify inner join semantics | No NULL segment_name or segment_code in output (unmatched rows excluded) |
| TC-3 | BR-3 | Verify output columns | customer_id, segment_id, segment_name, segment_code, as_of |
| TC-4 | BR-4 | Verify ORDER BY customer_id, segment_id | Output rows ordered correctly |
| TC-5 | BR-5 | Verify Append mode | Multiple effective dates accumulate rows |
| TC-6 | BR-6 | Verify branches table unused | Output has no branch-related columns |
| TC-7 | BR-7 | Verify pure SQL Transformation | No External module processing needed |
| TC-8 | BR-8 | Verify consistent row counts per date | 291 rows per effective date (matching original) |
| TC-9 | BR-9 | Verify date alignment in join | as_of from customers_segments matches as_of from segments in each output row |
| TC-10 | BR-1,5 | Compare V2 output to original | EXCEPT query yields zero rows for all accumulated dates |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | Segment_id in customers_segments not in segments | Row excluded by INNER JOIN |
| EC-2 | Weekend effective date | Both tables have data every day, so output produced on weekends |
| EC-3 | Duplicate customer-segment mapping for same as_of | Duplicate rows appear in output |
| EC-4 | Multiple effective dates | Rows accumulate (Append mode) |
