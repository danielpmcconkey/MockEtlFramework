# Test Plan: CreditScoreAverageV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Average score computed across all bureaus per customer | avg_score = mean of all bureau scores for customer |
| TC-2 | BR-2 | Individual bureau scores in separate columns | equifax_score, transunion_score, experian_score populated correctly |
| TC-3 | BR-3 | Bureau matching is case-insensitive | "Equifax", "equifax", "EQUIFAX" all map to equifax_score |
| TC-4 | BR-4 | Only customers in both credit_scores and customers appear | Customers with scores but no customer record excluded |
| TC-5 | BR-5 | Missing bureau score is DBNull.Value | If customer has no Equifax score, equifax_score = DBNull.Value |
| TC-6 | BR-6 | as_of comes from customers DataFrame | Output as_of matches customers row as_of |
| TC-7 | BR-7 | Overwrite write mode | Only most recent date's data in output table |
| TC-8 | BR-8 | Empty input returns empty DataFrame | No rows when credit_scores or customers empty |
| TC-9 | BR-1-8 | V2 output matches original for each date Oct 1-31 | EXCEPT query returns zero rows |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | No credit scores for date (weekend) | Empty DataFrame, table truncated |
| EC-2 | Customer with scores but no customer name record | Customer excluded from output |
| EC-3 | Customer with only one bureau score | avg_score = that score; other two bureaus = DBNull.Value |
| EC-4 | Duplicate bureau scores for same customer | Last encountered overwrites column; all in average |
