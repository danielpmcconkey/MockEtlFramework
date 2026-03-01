# ProductPenetration — Business Requirements Document

## Overview
Calculates product penetration rates across three product types (accounts, cards, investments) as a percentage of total customers. Uses SQL Transformation (no External module) to compute distinct holder counts and penetration ratios. Output is written to CSV with Overwrite mode.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/product_penetration.csv`
- **includeHeader**: true
- **writeMode**: Overwrite
- **lineEnding**: LF
- **trailerFormat**: Not specified (no trailer)

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.customers | id, first_name, last_name | Effective date range (injected) | [product_penetration.json:8-10] |
| datalake.accounts | account_id, customer_id, account_type | Effective date range (injected) | [product_penetration.json:13-15] |
| datalake.cards | card_id, customer_id | Effective date range (injected) | [product_penetration.json:18-20] |
| datalake.investments | investment_id, customer_id | Effective date range (injected) | [product_penetration.json:23-25] |

## Business Rules

BR-1: Product penetration is computed for 3 product types: accounts, cards, and investments. Each is calculated as `COUNT(DISTINCT customer_id) / COUNT(DISTINCT customers.id)`.
- Confidence: HIGH
- Evidence: [product_penetration.json:36] — SQL with UNION ALL of 3 product types

BR-2: **KNOWN BUG — Integer division**: The penetration_rate is computed using `CAST(cnt AS INTEGER) / CAST(total_customers AS INTEGER)`, which performs INTEGER division in SQLite. This means penetration rates will be 0 for any value less than 100% and 1 for exactly 100%.
- Confidence: HIGH
- Evidence: [product_penetration.json:36] — `CAST(ah.cnt AS INTEGER) / CAST(cc.total_customers AS INTEGER) AS penetration_rate`

BR-3: The SQL uses CTEs: customer_counts (total distinct customers), account_holders (distinct account holders), card_holders (distinct card holders), investment_holders (distinct investment holders), then product_stats (UNION ALL of all three).
- Confidence: HIGH
- Evidence: [product_penetration.json:36] — full SQL with WITH clause

BR-4: The output is limited to 3 rows via `LIMIT 3`, matching the 3 UNION ALL segments. The LIMIT is likely a safety guard but also prevents additional rows from the cross-join.
- Confidence: HIGH
- Evidence: [product_penetration.json:36] — `LIMIT 3` at end of SQL

BR-5: The as_of column is obtained by joining product_stats with the customers table (`JOIN customers ON 1=1`) and selecting `customers.as_of`. This is a cross-join, limited by the LIMIT 3 clause.
- Confidence: HIGH
- Evidence: [product_penetration.json:36] — `JOIN customers ON 1=1 LIMIT 3`

BR-6: first_name and last_name are sourced from customers but are NOT included in the output — they serve no purpose.
- Confidence: HIGH
- Evidence: [product_penetration.json:36] — SQL SELECT only includes product_type, customer_count, product_count, penetration_rate, customers.as_of

BR-7: This job uses a Transformation module with SQL rather than an External module. No custom C# code is involved.
- Confidence: HIGH
- Evidence: [product_penetration.json:34-37] — module type is "Transformation", not "External"

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| product_type | Fixed strings | "accounts", "cards", "investments" | [product_penetration.json:36 SQL] |
| customer_count | Computed | COUNT(DISTINCT customers.id) — same for all rows | [product_penetration.json:36 SQL] |
| product_count | Computed | COUNT(DISTINCT customer_id) per product table | [product_penetration.json:36 SQL] |
| penetration_rate | Computed | INTEGER division: product_count / customer_count (always 0 or 1) | [product_penetration.json:36 SQL] |
| as_of | customers.as_of | From cross-join with customers | [product_penetration.json:36 SQL] |

## Non-Deterministic Fields
- **as_of**: The `JOIN customers ON 1=1 LIMIT 3` cross-join means the as_of value depends on which customer row SQLite picks first. In practice this is likely deterministic within a single run but not formally guaranteed.
- Confidence: MEDIUM

## Write Mode Implications
- **Overwrite mode**: Each run replaces the CSV. Only the latest effective date's data persists.

## Edge Cases
- **Integer division bug**: All penetration_rate values will be 0 (if product holders < total customers, which is the normal case) or 1 (if equal). Fractional penetration rates are never produced. This is almost certainly a bug.
- **Multi-day effective date range**: With multi-day data, COUNT(DISTINCT id) counts unique customer IDs across all dates (correct since it's the same set each day), but the cross-join with customers multiplied by LIMIT 3 could behave unexpectedly.
- **Empty tables**: If any product table has 0 holders, its penetration_rate is 0 (0 / N = 0 in integer division).
- **Weekend dates**: Customers and accounts are weekday-only. Cards are weekday-only. Investments are weekday-only. Weekend dates produce empty DataFrames, leading to division by zero potential if customer_count is 0.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| 3 product types (accounts, cards, investments) | product_penetration.json:36 SQL UNION ALL |
| Integer division bug | product_penetration.json:36 CAST expressions |
| LIMIT 3 output | product_penetration.json:36 SQL |
| Cross-join for as_of | product_penetration.json:36 JOIN ON 1=1 |
| Uses Transformation not External | product_penetration.json:34 type="Transformation" |
| Overwrite write mode | product_penetration.json:43 |
| First effective date 2024-10-01 | product_penetration.json:3 |

## Open Questions
1. The integer division produces penetration_rate of 0 or 1 only. This is almost certainly a bug — should it be `CAST(cnt AS REAL) / CAST(total AS REAL)` for a proper ratio? (Confidence: HIGH — likely bug)
2. The cross-join with customers for as_of is fragile. Could the LIMIT 3 produce unexpected results if the cross-join reorders rows? (Confidence: MEDIUM)
3. Why are first_name and last_name sourced from customers if they're not in the output? (Confidence: LOW)
