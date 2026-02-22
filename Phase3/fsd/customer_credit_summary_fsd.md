# CustomerCreditSummary -- Functional Specification Document

## Design Approach

**SQL-equivalent logic in External module with empty-DataFrame guard.** The original External module computes per-customer financial aggregates: average credit score, total loan balance, total account balance, and counts. This is expressible in SQL (LEFT JOINs + aggregate subqueries), but the framework's Transformation module does not register empty DataFrames as SQLite tables. On weekends, customers, accounts, credit_scores, and loan_accounts all have no data. The original returns empty output when ANY of the four is empty.

The V2 uses a clean External module that:
1. Checks all four input DataFrames for emptiness
2. Uses LINQ-based aggregation for clean, set-oriented computation
3. Preserves the asymmetric NULL/default handling: avg_credit_score is NULL when no scores, but loan/account totals default to 0

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed unused `segments` DataSourcing module |
| AP-2    | N                   | N/A                | No duplicated logic |
| AP-3    | Y                   | Partial            | External module retained for empty-DataFrame guard; internal logic uses LINQ instead of manual dictionary loops |
| AP-4    | Y                   | Y                  | Removed unused columns: account_id/account_type/account_status from accounts, credit_score_id/bureau from credit_scores, loan_id/loan_type from loan_accounts |
| AP-5    | Y                   | N (documented)     | Asymmetric handling reproduced: avg_credit_score = NULL when no scores vs loan/account totals = 0 when none. Documented as semantically appropriate. |
| AP-6    | Y                   | Y                  | Four manual foreach loops replaced with LINQ GroupBy + ToDictionary |
| AP-7    | N                   | N/A                | No magic values |
| AP-8    | N                   | N/A                | No overly complex SQL |
| AP-9    | N                   | N/A                | Name accurately describes the job |
| AP-10   | N                   | N/A                | No inter-job dependencies needed |

## V2 Pipeline Design

1. **DataSourcing** - `customers` from `datalake.customers` with columns: `id`, `first_name`, `last_name`
2. **DataSourcing** - `accounts` from `datalake.accounts` with columns: `customer_id`, `current_balance`
3. **DataSourcing** - `credit_scores` from `datalake.credit_scores` with columns: `customer_id`, `score`
4. **DataSourcing** - `loan_accounts` from `datalake.loan_accounts` with columns: `customer_id`, `current_balance`
5. **External** - `CustomerCreditSummaryBuilderV2`: empty-check guard + LINQ aggregation
6. **DataFrameWriter** - Write to `customer_credit_summary` in `double_secret_curated` schema, Overwrite mode

## External Module Design

```
IF any of customers/accounts/credit_scores/loan_accounts is empty:
    Return empty DataFrame
ELSE:
    1. Group credit_scores by customer_id -> average score per customer (LINQ)
    2. Group loan_accounts by customer_id -> sum balance, count per customer (LINQ)
    3. Group accounts by customer_id -> sum balance, count per customer (LINQ)
    4. For each customer row:
       - avg_credit_score: from score lookup, or NULL if not found
       - total_loan_balance/loan_count: from loan lookup, or 0 if not found
       - total_account_balance/account_count: from account lookup, or 0 if not found
       - first_name/last_name: coalesce to empty string
       - as_of: from customer row
```

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1 | Iterate over all customers rows (one output per customer) |
| BR-2 | LINQ Average on grouped credit scores; NULL if customer has no scores |
| BR-3 | LINQ Sum on grouped loan balances; default 0 |
| BR-4 | LINQ Count on grouped loans; default 0 |
| BR-5 | LINQ Sum on grouped account balances; default 0 |
| BR-6 | LINQ Count on grouped accounts; default 0 |
| BR-7 | Empty-check guard on all four DataFrames |
| BR-8 | as_of from customer row |
| BR-9 | DataFrameWriter configured with writeMode: Overwrite |
| BR-10 | No filtering on accounts (all included) |
| BR-11 | No filtering on loans (all included) |
