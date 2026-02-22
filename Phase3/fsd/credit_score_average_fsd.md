# CreditScoreAverage -- Functional Specification Document

## Design Approach

**SQL-equivalent logic in External module with empty-DataFrame guard.** The original External module groups credit scores by customer, computes averages, pivots bureau scores, and joins with customer names. This logic IS expressible in SQL (GROUP BY + conditional aggregation + JOIN), but the framework's Transformation module does not register empty DataFrames as SQLite tables. On weekends, both `credit_scores` and `customers` have no data, so pure SQL would crash.

The V2 uses a clean External module that:
1. Checks if input DataFrames are empty (returning empty output if so)
2. Uses SQLite in-memory to run the equivalent SQL (eliminating row-by-row iteration)
3. Rounds AVG to 2 decimal places to match the curated table's NUMERIC(6,2) precision

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed unused `segments` DataSourcing module |
| AP-2    | N                   | N/A                | No duplicated logic |
| AP-3    | Y                   | Partial            | External module retained for empty-DataFrame guard; internal logic uses set-based SQL instead of row-by-row iteration |
| AP-4    | Y                   | Y                  | Removed `credit_score_id` from DataSourcing columns |
| AP-5    | Y                   | N (documented)     | Asymmetric NULL handling reproduced for output equivalence: names coalesced to empty string, missing bureau scores remain NULL |
| AP-6    | Y                   | Y                  | Row-by-row foreach loops replaced with SQLite-based set operations |
| AP-7    | N                   | N/A                | No magic values |
| AP-8    | N                   | N/A                | No overly complex SQL |
| AP-9    | N                   | N/A                | Name accurately describes the job |
| AP-10   | N                   | N/A                | No inter-job dependencies needed |

## V2 Pipeline Design

1. **DataSourcing** - `credit_scores` from `datalake.credit_scores` with columns: `customer_id`, `bureau`, `score`
2. **DataSourcing** - `customers` from `datalake.customers` with columns: `id`, `first_name`, `last_name`
3. **External** - `CreditScoreAveragerV2`: empty-check guard + SQLite-based aggregation
4. **DataFrameWriter** - Write to `credit_score_average` in `double_secret_curated` schema, Overwrite mode

## External Module Design

```
IF credit_scores is empty OR customers is empty:
    Return empty DataFrame
ELSE:
    Run SQL in SQLite:
    SELECT
        cs.customer_id,
        COALESCE(c.first_name, '') AS first_name,
        COALESCE(c.last_name, '') AS last_name,
        ROUND(AVG(CAST(cs.score AS REAL)), 2) AS avg_score,
        MAX(CASE WHEN LOWER(cs.bureau) = 'equifax' THEN cs.score END) AS equifax_score,
        MAX(CASE WHEN LOWER(cs.bureau) = 'transunion' THEN cs.score END) AS transunion_score,
        MAX(CASE WHEN LOWER(cs.bureau) = 'experian' THEN cs.score END) AS experian_score,
        c.as_of
    FROM credit_scores cs
    INNER JOIN customers c ON cs.customer_id = c.id
    GROUP BY cs.customer_id, c.first_name, c.last_name, c.as_of
```

Key design decisions:
- INNER JOIN ensures only customers with both scores and customer records are output (BR-1)
- COALESCE to empty string for names matches the original ?? "" pattern (BR-5/AP-5)
- ROUND(AVG, 2) matches the curated table's NUMERIC(6,2) precision
- CASE WHEN with LOWER() for case-insensitive bureau matching (BR-7)
- Missing bureau columns naturally produce NULL via MAX on no matching rows (BR-4)

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1 | INNER JOIN between credit_scores and customers |
| BR-2 | AVG() aggregate function with ROUND to 2 decimal places |
| BR-3 | CASE WHEN LOWER(bureau) = 'equifax'/'transunion'/'experian' THEN score END |
| BR-4 | MAX(CASE WHEN ...) returns NULL when no matching bureau exists |
| BR-5 | as_of sourced from customers table via JOIN |
| BR-6 | Empty-check guard at the top of Execute method |
| BR-7 | LOWER(cs.bureau) for case-insensitive comparison |
| BR-8 | DataFrameWriter configured with writeMode: Overwrite |
