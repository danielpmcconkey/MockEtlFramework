# LoanRiskAssessment -- Functional Specification Document

## Design Approach

SQL-first. The original External module (LoanRiskCalculator) groups credit scores by customer, averages them, joins to loan accounts, and assigns risk tiers via a switch expression. All of this is expressible as SQL using AVG + GROUP BY + LEFT JOIN + CASE WHEN. The V2 replaces the External module with a Transformation step and removes unused DataSourcing modules.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed `customers` and `segments` DataSourcing modules (never referenced by External module) |
| AP-2    | N                   | N/A                | No duplicated upstream logic |
| AP-3    | Y                   | Y                  | Replaced External module with SQL Transformation (AVG + JOIN + CASE) |
| AP-4    | Y                   | Y                  | Removed `credit_score_id` and `bureau` from credit_scores DataSourcing (only customer_id and score needed) |
| AP-5    | N                   | N/A                | NULL handling is consistent (NULL avg_credit_score -> "Unknown" risk_tier) |
| AP-6    | Y                   | Y                  | Three foreach loops replaced by set-based SQL |
| AP-7    | Y                   | Y (documented)     | Risk tier thresholds (750, 650, 550) documented in SQL comments |
| AP-8    | N                   | N/A                | No overly complex SQL in original |
| AP-9    | N                   | N/A                | Job name accurately describes what it does |
| AP-10   | N                   | N/A                | No undeclared dependencies |

## V2 Pipeline Design

1. **DataSourcing** `loan_accounts`: Read `loan_id`, `customer_id`, `loan_type`, `current_balance`, `interest_rate`, `loan_status` from `datalake.loan_accounts`
2. **DataSourcing** `credit_scores`: Read `customer_id`, `score` from `datalake.credit_scores`
3. **Transformation** `loan_risk_result`: SQL query that computes average credit score per customer, joins to loans, and assigns risk tier
4. **DataFrameWriter**: Write `loan_risk_result` to `double_secret_curated.loan_risk_assessment` in Overwrite mode

## SQL Transformation Logic

```sql
SELECT
    la.loan_id,
    la.customer_id,
    la.loan_type,
    la.current_balance,
    la.interest_rate,
    la.loan_status,
    -- Average credit score across all bureaus for the customer (BR-1)
    ROUND(avg_scores.avg_score, 2) AS avg_credit_score,
    -- Risk tier based on average credit score thresholds (BR-2)
    CASE
        WHEN avg_scores.avg_score IS NULL THEN 'Unknown'       -- No credit scores on file (BR-3)
        WHEN avg_scores.avg_score >= 750 THEN 'Low Risk'       -- Excellent credit
        WHEN avg_scores.avg_score >= 650 THEN 'Medium Risk'    -- Good credit
        WHEN avg_scores.avg_score >= 550 THEN 'High Risk'      -- Fair credit
        ELSE 'Very High Risk'                                   -- Poor credit
    END AS risk_tier,
    la.as_of
FROM loan_accounts la
LEFT JOIN (
    SELECT customer_id, as_of, AVG(score) AS avg_score
    FROM credit_scores
    GROUP BY customer_id, as_of
) avg_scores
    ON la.customer_id = avg_scores.customer_id
    AND la.as_of = avg_scores.as_of
```

**Key design notes:**
- The subquery computes AVG(score) per customer_id per as_of, matching the original's grouping logic
- LEFT JOIN ensures loans with no credit scores still appear (avg_score would be NULL)
- CASE evaluates NULL first (BR-3: Unknown), then checks thresholds in descending order (BR-2)
- ROUND(avg_score, 2) matches the C# decimal precision observed in curated output
- No filtering: all loan accounts included regardless of status (BR-4)

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1 | Subquery: AVG(score) GROUP BY customer_id, as_of |
| BR-2 | CASE WHEN with thresholds: >= 750 Low Risk, >= 650 Medium Risk, >= 550 High Risk, else Very High Risk |
| BR-3 | CASE WHEN avg_scores.avg_score IS NULL THEN 'Unknown'; ROUND produces NULL from NULL input |
| BR-4 | No WHERE clause on loan_accounts; all loans included |
| BR-5 | DataFrameWriter writeMode: "Overwrite" |
| BR-6 | Framework handles empty DataFrames natively; SQL produces empty result if no rows |
