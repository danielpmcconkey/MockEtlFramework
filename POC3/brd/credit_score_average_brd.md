# CreditScoreAverage -- Business Requirements Document

## Overview
Produces a per-customer credit score summary that averages scores across all three bureaus (Equifax, TransUnion, Experian) and includes individual bureau scores alongside customer name and effective date. Output is a CSV file with a control trailer line.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/credit_score_average.csv`
- **includeHeader**: true
- **trailerFormat**: `CONTROL|{date}|{row_count}|{timestamp}`
- **writeMode**: Overwrite
- **lineEnding**: CRLF

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.credit_scores | credit_score_id, customer_id, bureau, score | Effective date range (injected by executor) | [credit_score_average.json:8-11] |
| datalake.customers | id, first_name, last_name | Effective date range (injected by executor) | [credit_score_average.json:14-17] |
| datalake.segments | segment_id, segment_name | Effective date range (injected by executor) | [credit_score_average.json:20-23] |

## Business Rules

BR-1: Credit scores are grouped by customer_id, and the average is computed across all bureau entries for that customer.
- Confidence: HIGH
- Evidence: [CreditScoreAverager.cs:26-37] -- `scoresByCustomer` dictionary groups scores by `customer_id`, line 61 computes `scores.Average(s => s.score)`.

BR-2: Individual bureau scores are extracted by case-insensitive bureau name matching ("equifax", "transunion", "experian"). If a customer has no entry for a given bureau, the value is `DBNull.Value`.
- Confidence: HIGH
- Evidence: [CreditScoreAverager.cs:64-82] -- switch on `bureau.ToLower()` with DBNull.Value defaults.

BR-3: Only customers who have at least one credit score record AND exist in the customers table are included in output. The join is performed via dictionary lookup on `customer_id`.
- Confidence: HIGH
- Evidence: [CreditScoreAverager.cs:56-57] -- `if (!customerNames.ContainsKey(customerId)) continue;`

BR-4: When either credit_scores or customers DataFrames are null or empty, the output is an empty DataFrame with the correct column schema.
- Confidence: HIGH
- Evidence: [CreditScoreAverager.cs:19-23] -- null/empty guard returns empty DataFrame.

BR-5: The `as_of` value for each output row is taken from the customers table row, not the credit_scores table.
- Confidence: HIGH
- Evidence: [CreditScoreAverager.cs:46,93] -- `asOf` extracted from `custRow["as_of"]` in the customer name lookup, used in output row.

BR-6: The segments table is sourced by DataSourcing but is NOT used by the External module. It is loaded into shared state but never accessed.
- Confidence: HIGH
- Evidence: [CreditScoreAverager.cs:16-17] -- only `credit_scores` and `customers` are retrieved from shared state. No reference to `segments`.

BR-7: The customer name lookup uses the last entry per customer_id encountered in the customers DataFrame (dictionary overwrite pattern).
- Confidence: HIGH
- Evidence: [CreditScoreAverager.cs:41-46] -- `customerNames[custId] = (firstName, lastName, custRow["as_of"])` overwrites without checking for existing key.

BR-8: The output traverses scoresByCustomer (credit_score-driven iteration), not customers. Customers with no credit scores are excluded from output.
- Confidence: HIGH
- Evidence: [CreditScoreAverager.cs:51] -- `foreach (var kvp in scoresByCustomer)`.

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | credit_scores.customer_id | Cast to int via Convert.ToInt32 | [CreditScoreAverager.cs:29,85] |
| first_name | customers.first_name | ToString with null coalesce to "" | [CreditScoreAverager.cs:44,87] |
| last_name | customers.last_name | ToString with null coalesce to "" | [CreditScoreAverager.cs:45,88] |
| avg_score | credit_scores.score | Average of all scores for this customer across bureaus | [CreditScoreAverager.cs:61,89] |
| equifax_score | credit_scores.score | Score where bureau = "equifax" (case-insensitive), else DBNull.Value | [CreditScoreAverager.cs:72-73,90] |
| transunion_score | credit_scores.score | Score where bureau = "transunion" (case-insensitive), else DBNull.Value | [CreditScoreAverager.cs:75-76,91] |
| experian_score | credit_scores.score | Score where bureau = "experian" (case-insensitive), else DBNull.Value | [CreditScoreAverager.cs:78-79,92] |
| as_of | customers.as_of | Pass-through from customer row | [CreditScoreAverager.cs:93] |

## Non-Deterministic Fields
- The trailer line contains `{timestamp}` which is UTC now at execution time (ISO 8601).

## Write Mode Implications
- **Overwrite**: Each execution replaces the entire output file. For multi-day auto-advance runs, only the last effective date's output survives on disk since each day's run overwrites the previous.

## Edge Cases
- **Missing bureau score**: If a customer has credit scores but not from all three bureaus, the missing bureau columns will contain DBNull.Value (written as empty in CSV).
- **Customer with no scores**: Excluded from output entirely (iteration is score-driven).
- **Scores with no matching customer**: Excluded via the `customerNames.ContainsKey` check.
- **Empty input**: Returns empty DataFrame with correct schema; CSV will have header + trailer only.
- **Segments table unused**: Loaded into shared state but never referenced by the External module.
- **Multiple scores per bureau per customer**: The switch/case takes the last value encountered for each bureau (overwrites). The average includes all entries regardless.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Average score computation | [CreditScoreAverager.cs:61] |
| Bureau-specific score extraction | [CreditScoreAverager.cs:64-82] |
| Customer filter (must exist in customers table) | [CreditScoreAverager.cs:56-57] |
| Empty input guard | [CreditScoreAverager.cs:19-23] |
| as_of from customers table | [CreditScoreAverager.cs:46,93] |
| Trailer format | [credit_score_average.json:36] |
| CRLF line endings | [credit_score_average.json:38] |
| Overwrite write mode | [credit_score_average.json:37] |
| Segments table unused | [CreditScoreAverager.cs:16-17] |

## Open Questions
- OQ-1: The segments table is sourced but unused. It is unclear whether this is intentional dead code or a missing feature. Confidence: MEDIUM -- segments are loaded by DataSourcing but never accessed in the External module.
- OQ-2: If a customer has multiple scores from the same bureau, the last one processed wins for the individual bureau column, but all entries contribute to the average. Whether this is intentional is unclear. Confidence: LOW -- single observation from dictionary overwrite pattern at line 71-81.
