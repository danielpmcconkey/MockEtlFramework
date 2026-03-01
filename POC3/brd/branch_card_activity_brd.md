# BranchCardActivity — Business Requirements Document

## Overview
Produces a per-branch, per-date summary of card transaction activity by assigning customers to branches via a modulo-based mapping (`customer_id % MAX(branch_id) + 1`) and aggregating card transaction counts and amounts.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `output`
- **outputDirectory**: `Output/curated/branch_card_activity/`
- **numParts**: 50
- **writeMode**: Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.card_transactions | card_txn_id, card_id, customer_id, amount, authorization_status | Effective date range (injected by executor) | [branch_card_activity.json:8-10] |
| datalake.branches | branch_id, branch_name, country | Effective date range (injected by executor) | [branch_card_activity.json:14-16] |
| datalake.customers | id, first_name, last_name | Effective date range (injected by executor) | [branch_card_activity.json:20-22] |
| datalake.segments | segment_id, segment_name | Effective date range (injected by executor) | [branch_card_activity.json:26-28] |

### Schema Details

**card_transactions**: card_txn_id (integer), card_id (integer), customer_id (integer), merchant_name (varchar), merchant_category_code (varchar), amount (numeric), txn_timestamp (timestamp), authorization_status (varchar), as_of (date)

**branches**: branch_id (integer), branch_name (varchar), address_line1 (varchar), city (varchar), state_province (varchar), postal_code (varchar), country (char), as_of (date)

**customers**: id (integer), prefix (varchar), first_name (varchar), last_name (varchar), sort_name (varchar), suffix (varchar), birthdate (date), as_of (date)

**segments**: segment_id (integer), segment_name (varchar), segment_code (varchar), as_of (date)

## Business Rules

BR-1: Branch assignment is synthetic — customers are mapped to branches via `(customer_id % MAX(branch_id)) + 1`, not through any actual customer-branch relationship table.
- Confidence: HIGH
- Evidence: [branch_card_activity.json:36] SQL: `b.branch_id = (ct.customer_id % (SELECT MAX(branch_id) FROM branches)) + 1`

BR-2: All card transactions are included regardless of authorization_status (Approved or Declined). The `authorization_status` column is sourced but not filtered on.
- Confidence: HIGH
- Evidence: [branch_card_activity.json:10] column sourced; [branch_card_activity.json:36] SQL has no WHERE clause filtering authorization_status

BR-3: The `card_id` column is sourced from card_transactions but not used in the transformation SQL.
- Confidence: HIGH
- Evidence: [branch_card_activity.json:10] sourced; [branch_card_activity.json:36] not referenced in SQL

BR-4: The `customers` table is JOINed on `ct.customer_id = c.id` but no customer columns appear in the output SELECT — it only serves as a filter (inner join eliminates card transactions without a matching customer).
- Confidence: HIGH
- Evidence: [branch_card_activity.json:36] `JOIN customers c ON ct.customer_id = c.id` but SELECT only uses b.* and ct.* columns

BR-5: The `segments` table is sourced but never referenced in the transformation SQL.
- Confidence: HIGH
- Evidence: [branch_card_activity.json:26-28] segments DataSourcing; [branch_card_activity.json:36] SQL does not mention segments

BR-6: The MAX(branch_id) subquery runs against the full `branches` DataFrame across all as_of dates in the effective range, yielding MAX across all snapshots (which is 40 based on observed data).
- Confidence: HIGH
- Evidence: [branch_card_activity.json:36] `SELECT MAX(branch_id) FROM branches` — no as_of filter; DB query confirms max is 40

BR-7: Output is grouped by branch_id, branch_name, and as_of — producing one row per branch per date.
- Confidence: HIGH
- Evidence: [branch_card_activity.json:36] `GROUP BY b.branch_id, b.branch_name, ct.as_of`

BR-8: Card amounts are rounded to 2 decimal places in the total.
- Confidence: HIGH
- Evidence: [branch_card_activity.json:36] `ROUND(SUM(ct.amount), 2)`

BR-9: The `country` column is sourced from branches but not used in the transformation SQL.
- Confidence: HIGH
- Evidence: [branch_card_activity.json:16] sourced; [branch_card_activity.json:36] not in SQL

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| branch_id | branches.branch_id | Derived from modulo mapping of customer_id | [branch_card_activity.json:36] |
| branch_name | branches.branch_name | Direct lookup via branch_id | [branch_card_activity.json:36] |
| card_txn_count | card_transactions.card_txn_id | COUNT(ct.card_txn_id) per group | [branch_card_activity.json:36] |
| total_card_amount | card_transactions.amount | ROUND(SUM(ct.amount), 2) per group | [branch_card_activity.json:36] |
| as_of | card_transactions.as_of | Passthrough from card_transactions | [branch_card_activity.json:36] |

## Non-Deterministic Fields
None identified.

## Write Mode Implications
Overwrite mode: Each execution replaces the entire `Output/curated/branch_card_activity/` directory. Multi-day effective date ranges produce multiple rows (one per branch per as_of date), all written in a single overwrite pass per execution cycle.

## Edge Cases

- **Modulo branch assignment**: If MAX(branch_id) changes across as_of dates (branches added/removed), the modulo mapping would shift. Current data shows a stable 40 branches.
- **No customer match**: Inner join on customers filters out card_transactions where customer_id has no match in the customers table.
- **Zero transactions for a branch**: Branches with no card transactions assigned via modulo will not appear in output (no outer join).
- **Weekend data**: branch_visits and transactions exist on all dates including weekends per data observation. No weekend guard logic in the SQL transformation.
- **50 numParts**: With potentially few output rows (max 40 branches per date), some part files may be empty.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Modulo branch assignment | [branch_card_activity.json:36] |
| BR-2: No authorization_status filter | [branch_card_activity.json:10, 36] |
| BR-3: card_id unused | [branch_card_activity.json:10, 36] |
| BR-4: customers JOIN as filter | [branch_card_activity.json:36] |
| BR-5: segments unused | [branch_card_activity.json:26-28, 36] |
| BR-6: MAX across all dates | [branch_card_activity.json:36], DB query |
| BR-7: Group by branch+date | [branch_card_activity.json:36] |
| BR-8: ROUND to 2 decimals | [branch_card_activity.json:36] |
| BR-9: country unused | [branch_card_activity.json:16, 36] |

## Open Questions

OQ-1: Why are `segments`, `card_id`, `authorization_status`, and `country` sourced but never used? Possible vestigial columns from a broader design or copy-paste from another job config.
- Confidence: MEDIUM — columns clearly sourced but absent from SQL
