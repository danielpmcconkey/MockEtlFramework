# CreditScoreAverage — Business Requirements Document

## Overview

Produces a per-customer summary of credit scores with the average across all bureaus and individual bureau-level scores (Equifax, TransUnion, Experian), enriched with customer name. Output uses Overwrite mode, replacing all data each run.

## Source Tables

| Table | Schema | Columns Used | Purpose |
|-------|--------|-------------|---------|
| `datalake.credit_scores` | datalake | credit_score_id, customer_id, bureau, score | Credit scores per customer per bureau |
| `datalake.customers` | datalake | id, first_name, last_name | Customer name lookup |
| `datalake.segments` | datalake | segment_id, segment_name | **SOURCED BUT NEVER USED** — not referenced by the External module |

## Business Rules

BR-1: One output row is produced per customer who has credit scores AND exists in the customers table.
- Confidence: HIGH
- Evidence: [ExternalModules/CreditScoreAverager.cs:56-57] `if (!customerNames.ContainsKey(customerId)) continue;` — customers without a name lookup are skipped
- Evidence: [ExternalModules/CreditScoreAverager.cs:51] Iteration is over `scoresByCustomer` — only customers with scores

BR-2: The average score (`avg_score`) is the arithmetic mean of all bureau scores for that customer.
- Confidence: HIGH
- Evidence: [ExternalModules/CreditScoreAverager.cs:61] `var avgScore = scores.Average(s => s.score);`
- Evidence: [curated.credit_score_average] Sample: customer 1001 has scores 850, 836, 850 -> avg_score = 845.33

BR-3: Individual bureau scores are pivoted into separate columns: equifax_score, transunion_score, experian_score.
- Confidence: HIGH
- Evidence: [ExternalModules/CreditScoreAverager.cs:68-82] Switch on `bureau.ToLower()` mapping to equifax/transunion/experian columns

BR-4: When a customer has no score for a particular bureau, that column is DBNull.Value (NULL).
- Confidence: HIGH
- Evidence: [ExternalModules/CreditScoreAverager.cs:64-66] Default values are `DBNull.Value`; only overwritten if bureau match found

BR-5: The as_of value comes from the customers table (last entry for each customer in the DataFrame).
- Confidence: HIGH
- Evidence: [ExternalModules/CreditScoreAverager.cs:46] `customerNames[custId] = (firstName, lastName, custRow["as_of"]);` — last row per custId wins in iteration
- Evidence: [ExternalModules/CreditScoreAverager.cs:93] `["as_of"] = asOf` from customerNames lookup

BR-6: When either credit_scores or customers DataFrames are empty, an empty output is produced.
- Confidence: HIGH
- Evidence: [ExternalModules/CreditScoreAverager.cs:19-23] Null/empty check returns empty DataFrame

BR-7: Bureau name matching is case-insensitive.
- Confidence: HIGH
- Evidence: [ExternalModules/CreditScoreAverager.cs:70] `bureau.ToLower()` — converts to lowercase before switch

BR-8: Output uses Overwrite mode — all data is replaced on each run.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/credit_score_average.json:35] `"writeMode": "Overwrite"`

## Output Schema

| Column | Source | Transformation |
|--------|--------|----------------|
| customer_id | credit_scores.customer_id | Direct (integer) |
| first_name | customers.first_name | Direct (string, default empty) |
| last_name | customers.last_name | Direct (string, default empty) |
| avg_score | Computed | Average of all bureau scores for the customer |
| equifax_score | credit_scores.score | Where bureau = 'Equifax'; NULL if absent |
| transunion_score | credit_scores.score | Where bureau = 'TransUnion'; NULL if absent |
| experian_score | credit_scores.score | Where bureau = 'Experian'; NULL if absent |
| as_of | customers.as_of | From the customer record |

## Edge Cases

- **No credit scores for effective date**: Empty output (BR-6).
- **No customers for effective date**: Empty output (BR-6). This happens on weekends when datalake.customers has no data but credit_scores may.
- **Customer with fewer than 3 bureaus**: Missing bureau columns are NULL (BR-4). In current data all customers have exactly 3 bureaus.
- **Multiple scores per customer per bureau**: The last score encountered in iteration overwrites earlier ones. In current data each customer has exactly one score per bureau.
- **Customer in credit_scores but not in customers table**: Row is skipped (BR-1).

## Anti-Patterns Identified

- **AP-1: Redundant Data Sourcing** — The `segments` table is sourced via DataSourcing but never referenced by the External module `CreditScoreAverager`. The module only uses `credit_scores` and `customers` from shared state. Evidence: [JobExecutor/Jobs/credit_score_average.json:20-25] segments sourced; [ExternalModules/CreditScoreAverager.cs] grep for "segments" — not found. V2 approach: Remove the segments DataSourcing module entirely.

- **AP-3: Unnecessary External Module** — The logic is: group credit scores by customer, compute average, pivot bureau scores into columns, join with customer names. This can be expressed entirely in SQL using GROUP BY, AVG, and conditional aggregation (CASE WHEN bureau = 'Equifax' THEN score END). V2 approach: Replace with a SQL Transformation.

- **AP-4: Unused Columns Sourced** — The `credit_score_id` column is sourced from credit_scores but never appears in the output. Evidence: [JobExecutor/Jobs/credit_score_average.json:11] includes `credit_score_id`; [ExternalModules/CreditScoreAverager.cs] output columns do not include credit_score_id. V2 approach: Remove credit_score_id from DataSourcing columns.

- **AP-5: Asymmetric NULL/Default Handling** — String fields (first_name, last_name) are coalesced to empty string when null, but missing bureau scores are left as DBNull.Value (NULL). This is inconsistent handling of absent data across column types. Evidence: [ExternalModules/CreditScoreAverager.cs:44-45] `?? ""` for name fields vs [ExternalModules/CreditScoreAverager.cs:64-66] `DBNull.Value` for bureau scores. V2 approach: Reproduce the same asymmetric behavior for output equivalence, but document it as a known inconsistency.

- **AP-6: Row-by-Row Iteration in External Module** — The External module iterates over credit score rows one by one to build a dictionary, then iterates over customers one by one. This is a set-based operation (GROUP BY + JOIN) that SQL handles natively. Evidence: [ExternalModules/CreditScoreAverager.cs:27-37,50-95] foreach loops over rows. V2 approach: Replace with SQL.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [ExternalModules/CreditScoreAverager.cs:51,56-57] |
| BR-2 | [ExternalModules/CreditScoreAverager.cs:61] |
| BR-3 | [ExternalModules/CreditScoreAverager.cs:68-82] |
| BR-4 | [ExternalModules/CreditScoreAverager.cs:64-66] |
| BR-5 | [ExternalModules/CreditScoreAverager.cs:46,93] |
| BR-6 | [ExternalModules/CreditScoreAverager.cs:19-23] |
| BR-7 | [ExternalModules/CreditScoreAverager.cs:70] |
| BR-8 | [JobExecutor/Jobs/credit_score_average.json:35] |

## Open Questions

- **Ordering of output rows**: The External module does not sort the output. Row order depends on dictionary iteration order of `scoresByCustomer`. In practice, this means ordering is not guaranteed. Confidence: MEDIUM — V2 should replicate the same non-deterministic order or explicitly ORDER BY customer_id for consistency.
- **Multiple scores per bureau per customer**: If a customer had duplicate bureau entries, the last one encountered would win. Current data shows exactly one score per bureau per customer. Confidence: LOW that duplicates can occur based on data constraints.
