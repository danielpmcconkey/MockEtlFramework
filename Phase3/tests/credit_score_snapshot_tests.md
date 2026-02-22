# Test Plan: CreditScoreSnapshotV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | All credit score records pass through | Row count matches source |
| TC-2 | BR-2 | Output has exactly 5 columns | credit_score_id, customer_id, bureau, score, as_of |
| TC-3 | BR-3 | Overwrite mode | Only most recent date's data persists |
| TC-4 | BR-4 | Empty input returns empty DataFrame | No rows when credit_scores empty |
| TC-5 | BR-6 | No transformation applied | All values match source exactly |
| TC-6 | BR-1-6 | V2 output matches original for each date Oct 1-31 | EXCEPT query returns zero rows |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | No credit scores for date (weekend) | Empty DataFrame, table truncated |
| EC-2 | Null values in source fields | Null values pass through unchanged |
