# BRD: CreditScoreAverage

## Overview
This job computes the average credit score across all bureaus for each customer, along with individual bureau scores (Equifax, TransUnion, Experian), and joins with customer name information. It produces a per-customer credit score summary for the current effective date.

## Source Tables

| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| credit_scores | datalake | credit_score_id, customer_id, bureau, score | Sourced via DataSourcing for effective date range (single date in practice) | [credit_score_average.json:7-11] |
| customers | datalake | id, first_name, last_name | Sourced via DataSourcing for effective date range | [credit_score_average.json:13-18] |
| segments | datalake | segment_id, segment_name | Sourced via DataSourcing but NOT USED in the External module | [credit_score_average.json:20-24] |

## Business Rules

BR-1: For each customer with credit scores, the average score across all bureaus is computed.
- Confidence: HIGH
- Evidence: [ExternalModules/CreditScoreAverager.cs:61] `var avgScore = scores.Average(s => s.score);`

BR-2: Individual bureau scores are pivoted into separate columns: equifax_score, transunion_score, experian_score.
- Confidence: HIGH
- Evidence: [ExternalModules/CreditScoreAverager.cs:68-82] Switch on `bureau.ToLower()` assigns to equifaxScore, transunionScore, experianScore

BR-3: Bureau matching is case-insensitive.
- Confidence: HIGH
- Evidence: [ExternalModules/CreditScoreAverager.cs:70] `switch (bureau.ToLower())`

BR-4: Only customers who exist in BOTH the credit_scores and customers DataFrames are included in output.
- Confidence: HIGH
- Evidence: [ExternalModules/CreditScoreAverager.cs:56-57] `if (!customerNames.ContainsKey(customerId)) continue;`
- Evidence: The loop iterates scoresByCustomer (customers with scores), and skips those not in customerNames

BR-5: If a customer has no score for a particular bureau, that bureau column is set to DBNull.Value.
- Confidence: HIGH
- Evidence: [ExternalModules/CreditScoreAverager.cs:64-66] Initial values are `DBNull.Value`; only overwritten if bureau match found

BR-6: The as_of value comes from the customers DataFrame row.
- Confidence: HIGH
- Evidence: [ExternalModules/CreditScoreAverager.cs:46] `customerNames[custId] = (firstName, lastName, custRow["as_of"]);`
- Evidence: [ExternalModules/CreditScoreAverager.cs:93] `["as_of"] = asOf`

BR-7: Data is written in Overwrite mode -- only the most recent effective date's data persists.
- Confidence: HIGH
- Evidence: [credit_score_average.json:35] `"writeMode": "Overwrite"`
- Evidence: [curated.credit_score_average] Only 1 as_of value (2024-10-31) with 223 rows

BR-8: If credit_scores or customers DataFrame is empty (no data for the effective date), an empty DataFrame is returned.
- Confidence: HIGH
- Evidence: [ExternalModules/CreditScoreAverager.cs:19-23] `if (creditScores == null || creditScores.Count == 0 || customers == null || customers.Count == 0)` returns empty DataFrame

BR-9: The segments DataSourcing module is declared in the job config but is NOT used by the CreditScoreAverager External module.
- Confidence: HIGH
- Evidence: [credit_score_average.json:20-24] segments is sourced but [CreditScoreAverager.cs] never references `sharedState["segments"]`

BR-10: If a customer has multiple scores for the same bureau in the same snapshot, the last one encountered in iteration order overwrites the previous one for individual bureau columns, but all scores contribute to the average.
- Confidence: MEDIUM
- Evidence: [ExternalModules/CreditScoreAverager.cs:68-82] The switch block overwrites without checking for duplicates; [CreditScoreAverager.cs:36] all scores are added to the list, so Average uses all.

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | credit_scores.customer_id (loop key) | Cast to int | [CreditScoreAverager.cs:53,85] |
| first_name | customers.first_name | ToString with null coalesce to "" | [CreditScoreAverager.cs:44,87] |
| last_name | customers.last_name | ToString with null coalesce to "" | [CreditScoreAverager.cs:45,88] |
| avg_score | Computed | Average of all bureau scores for customer (decimal) | [CreditScoreAverager.cs:61,89] |
| equifax_score | credit_scores.score where bureau=Equifax | Direct or DBNull | [CreditScoreAverager.cs:72-73,90] |
| transunion_score | credit_scores.score where bureau=TransUnion | Direct or DBNull | [CreditScoreAverager.cs:75-76,91] |
| experian_score | credit_scores.score where bureau=Experian | Direct or DBNull | [CreditScoreAverager.cs:78-79,92] |
| as_of | customers.as_of | Pass-through | [CreditScoreAverager.cs:93] |

## Edge Cases

- **NULL handling**: customer first/last name default to empty string if null. Bureau scores default to DBNull.Value if no matching bureau found. avg_score is computed from all available scores (never null if customer has any scores).
  - Evidence: [CreditScoreAverager.cs:44-45] `?? ""` for names; [CreditScoreAverager.cs:64-66] `DBNull.Value` defaults for bureau scores
- **Weekend/date fallback**: Since DataSourcing sources credit_scores and customers with the framework's effective date mechanism, and credit_scores/customers only have weekday data (23 dates), weekend runs will produce empty DataFrames (no matching as_of), resulting in an empty output.
  - Evidence: [datalake.credit_scores] 23 distinct as_of dates (weekdays); Overwrite mode means the table becomes empty on weekends
- **Zero-row behavior**: Empty input (no credit scores or no customers) produces an empty DataFrame, which means the table is truncated (Overwrite mode) and no rows are inserted.
  - Evidence: [CreditScoreAverager.cs:19-23]

## Traceability Matrix

| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [CreditScoreAverager.cs:61] |
| BR-2 | [CreditScoreAverager.cs:68-82] |
| BR-3 | [CreditScoreAverager.cs:70] |
| BR-4 | [CreditScoreAverager.cs:56-57] |
| BR-5 | [CreditScoreAverager.cs:64-66] |
| BR-6 | [CreditScoreAverager.cs:46,93] |
| BR-7 | [credit_score_average.json:35], [curated.credit_score_average row counts] |
| BR-8 | [CreditScoreAverager.cs:19-23] |
| BR-9 | [credit_score_average.json:20-24], [CreditScoreAverager.cs full source] |
| BR-10 | [CreditScoreAverager.cs:68-82,36] |

## Open Questions

- **Segments sourced but unused**: The job config sources the segments table, but the CreditScoreAverager never uses it. This may be dead configuration left from a prior version or intended for future use. Confidence: HIGH that it is unused; business intent is MEDIUM.
- **Weekend Overwrite behavior**: On weekends, the credit_scores DataSourcing returns no rows (weekday-only source). With Overwrite mode, this means the table is truncated and left empty until the next weekday. This may be intentional or an oversight. Confidence: MEDIUM.
- **Duplicate bureau scores**: If a customer has two Equifax scores in the same snapshot, the last one in iteration order is used for the column but both contribute to the average. This edge case is unlikely but the behavior is clear from code. Confidence: HIGH on behavior, LOW on whether this scenario occurs.
