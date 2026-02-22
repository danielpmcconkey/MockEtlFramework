# Phase 2: Intentionally Bad ETL Jobs

## Overview

Phase 2 implements 30 ETL jobs that produce **correct curated output** using **intentionally bad code practices**. These jobs will be analyzed by Phase 3's AI agent swarm, which must reverse-engineer business requirements and rewrite the jobs without access to this documentation.

## Job Catalog

| # | Job Name | Curated Table | Mode | Module Type | Anti-Patterns |
|---|----------|---------------|------|-------------|---------------|
| J01 | DailyTransactionSummary | daily_transaction_summary | Append | SQL | [1][4][5][8] |
| J02 | TransactionCategorySummary | transaction_category_summary | Append | SQL | [1][5][8] |
| J03 | LargeTransactionLog | large_transaction_log | Append | External | [1][3][6][7] |
| J04 | DailyTransactionVolume | daily_transaction_volume | Append | SQL | [2][8] |
| J05 | MonthlyTransactionTrend | monthly_transaction_trend | Append | SQL | [5][7][8][9] |
| J06 | CustomerDemographics | customer_demographics | Overwrite | External | [1][3][5][6][7] |
| J07 | CustomerContactInfo | customer_contact_info | Append | SQL | [1][5][8] |
| J08 | CustomerSegmentMap | customer_segment_map | Append | SQL | [1][4][5] |
| J09 | CustomerAddressHistory | customer_address_history | Append | SQL | [1][4][5][8] |
| J10 | CustomerFullProfile | customer_full_profile | Overwrite | External | [3][6][9][10] |
| J11 | AccountBalanceSnapshot | account_balance_snapshot | Append | External | [1][3][4][5] |
| J12 | AccountStatusSummary | account_status_summary | Overwrite | External | [1][3][5][6] |
| J13 | AccountTypeDistribution | account_type_distribution | Overwrite | External | [1][3][5][7] |
| J14 | HighBalanceAccounts | high_balance_accounts | Overwrite | External | [3][6][7][9] |
| J15 | AccountCustomerJoin | account_customer_join | Overwrite | External | [1][3][4][5][9] |
| J16 | CreditScoreSnapshot | credit_score_snapshot | Overwrite | External | [1][3][4] |
| J17 | CreditScoreAverage | credit_score_average | Overwrite | External | [1][3][5][6][9][10] |
| J18 | LoanPortfolioSnapshot | loan_portfolio_snapshot | Overwrite | External | [1][3][4][5] |
| J19 | LoanRiskAssessment | loan_risk_assessment | Overwrite | External | [1][3][5][6][7][9][10] |
| J20 | CustomerCreditSummary | customer_credit_summary | Overwrite | External | [1][3][5][6][9][10] |
| J21 | BranchDirectory | branch_directory | Overwrite | SQL | [8] |
| J22 | BranchVisitLog | branch_visit_log | Append | External | [1][3][4][6] |
| J23 | BranchVisitSummary | branch_visit_summary | Append | SQL | [2][8] |
| J24 | BranchVisitPurposeBreakdown | branch_visit_purpose_breakdown | Append | SQL | [1][2][5][8] |
| J25 | TopBranches | top_branches | Overwrite | SQL | [7][8][9] |
| J26 | CustomerAccountSummaryV2 | customer_account_summary_v2 | Overwrite | External | [1][3][4][5] |
| J27 | CustomerTransactionActivity | customer_transaction_activity | Append | External | [3][6][9][10] |
| J28 | CustomerBranchActivity | customer_branch_activity | Append | External | [1][3][5][6][9] |
| J29 | CustomerValueScore | customer_value_score | Overwrite | External | [3][6][7][9][10] |
| J30 | ExecutiveDashboard | executive_dashboard | Overwrite | External | [1][3][4][5][6][7][9][10] |

## Job Descriptions

### J01 — DailyTransactionSummary
**Business purpose:** Computes per-account daily transaction totals including total amount, transaction count, debit total, and credit total. Enables account-level transaction monitoring and reconciliation.

**Anti-patterns applied:**
- [1] Sources `branches` table — never referenced in the SQL
- [4] Sources unused columns from `transactions` (e.g., `description`)
- [5] Loads `branches` into shared state as a dead-end DataFrame
- [8] Uses unnecessary CTEs and window functions for a straightforward GROUP BY aggregation

---

### J02 — TransactionCategorySummary
**Business purpose:** Summarizes daily transaction activity by type (Debit/Credit), producing total amount, transaction count, and average amount per type per day. Supports category-level trend analysis.

**Anti-patterns applied:**
- [1] Sources `segments` table — irrelevant to transaction type analysis
- [5] Loads `segments` into shared state as a dead-end DataFrame
- [8] Overly complex SQL with unnecessary subqueries for a simple GROUP BY

---

### J03 — LargeTransactionLog
**Business purpose:** Captures all transactions exceeding $500, enriched with customer name and account details. Serves as an audit log for compliance monitoring of high-value transactions.

**Anti-patterns applied:**
- [1] Sources `addresses` table — never used in output
- [3] Uses C# External module (LargeTransactionProcessor) when a SQL JOIN + WHERE would suffice
- [6] Iterates row-by-row to filter and enrich transactions instead of set-based operations
- [7] Hardcodes the $500 threshold as a magic number

---

### J04 — DailyTransactionVolume
**Business purpose:** Produces a single row per day with total transaction count, total dollar amount, and average amount. Provides a high-level daily pulse of transaction velocity.

**Anti-patterns applied:**
- [2] Re-aggregates transactions from raw data when J01 already computed per-account totals that could be summed
- [8] Uses unnecessary CTEs for a single-table GROUP BY

---

### J05 — MonthlyTransactionTrend
**Business purpose:** Records daily transaction count, daily amount, and average transaction amount per day. Supports month-over-month trend reporting and forecasting.

**Anti-patterns applied:**
- [5] Sources `branches` and loads it as a dead-end DataFrame
- [7] Hardcodes `'2024-10-01'` as a filter date instead of deriving it dynamically
- [8] Uses window functions unnecessarily for what is a simple daily aggregation
- [9] Re-derives daily totals from raw transactions instead of reading from J04's curated output

---

### J06 — CustomerDemographics
**Business purpose:** Builds a customer demographic profile with computed age, age bracket (18-25, 26-35, 36-45, 46-55, 56-65, 65+), and primary phone/email. Supports segmentation and marketing outreach.

**Anti-patterns applied:**
- [1] Sources `segments` table — never used in output
- [3] Uses C# External module (CustomerDemographicsBuilder) when SQL could compute age and join contacts
- [5] Loads `segments` into shared state as a dead-end DataFrame
- [6] Iterates row-by-row over customers, phones, and emails to build lookups
- [7] Hardcodes age bracket boundaries as magic numbers in a switch expression

---

### J07 — CustomerContactInfo
**Business purpose:** Produces a unified contact directory combining all phone numbers and email addresses per customer, classified by contact type (Phone/Email) and subtype (Mobile, Home, Work, Personal, etc.).

**Anti-patterns applied:**
- [1] Sources `segments` table — irrelevant to contact info
- [5] Loads `segments` into shared state as a dead-end DataFrame
- [8] Uses UNION with unnecessary CTEs when a simpler UNION ALL would suffice

---

### J08 — CustomerSegmentMap
**Business purpose:** Maps each customer to their assigned marketing segments, including segment name and segment code. Enables segment-based filtering and campaign targeting.

**Anti-patterns applied:**
- [1] Sources `branches` table — never referenced
- [4] Sources unused columns from the join tables
- [5] Loads `branches` into shared state as a dead-end DataFrame

---

### J09 — CustomerAddressHistory
**Business purpose:** Records the full mailing address (line 1, city, state/province, postal code, country) for each customer as of each date. Tracks address changes over time.

**Anti-patterns applied:**
- [1] Sources `branches` table — never referenced
- [4] Sources unused columns from `addresses`
- [5] Loads `branches` into shared state as a dead-end DataFrame
- [8] Uses unnecessary CTEs for a simple SELECT with WHERE filters

---

### J10 — CustomerFullProfile
**Business purpose:** Assembles a 360-degree customer view combining demographics (age, age bracket), primary contact info (phone, email), and comma-separated segment names. Serves as the single-source CRM profile.

**Anti-patterns applied:**
- [3] Uses C# External module (FullProfileAssembler) when it could read from J06/J07/J08 curated tables
- [6] Iterates row-by-row to build phone, email, and segment lookup dictionaries
- [9] Re-derives demographics, contact info, and segment mappings from raw datalake tables instead of reading from curated J06, J07, and J08 outputs
- [10] Does not declare dependencies on J06, J07, or J08

---

### J11 — AccountBalanceSnapshot
**Business purpose:** Captures a daily snapshot of every account's balance, type, status, and owning customer. Provides the foundation for balance trend analysis and portfolio monitoring.

**Anti-patterns applied:**
- [1] Sources `branches` table — never referenced
- [3] Uses C# External module (AccountSnapshotBuilder) for what is essentially a pass-through SELECT
- [4] Sources unused columns from `accounts` (e.g., interest_rate, credit_limit)
- [5] Loads `branches` into shared state as a dead-end DataFrame

---

### J12 — AccountStatusSummary
**Business purpose:** Counts accounts by (account_type, account_status) combination. Shows how many Checking/Savings/etc. accounts are Active, Inactive, Closed, etc.

**Anti-patterns applied:**
- [1] Sources `segments` table — irrelevant to account status
- [3] Uses C# External module (AccountStatusCounter) when SQL GROUP BY + COUNT would suffice
- [5] Loads `segments` into shared state as a dead-end DataFrame
- [6] Iterates row-by-row to build grouping dictionaries instead of using set-based aggregation

---

### J13 — AccountTypeDistribution
**Business purpose:** Shows the portfolio composition by account type — count per type, total accounts, and percentage of each type. Supports product mix analysis.

**Anti-patterns applied:**
- [1] Sources `branches` table — never referenced
- [3] Uses C# External module (AccountDistributionCalculator) when SQL with COUNT + window function would suffice
- [5] Loads `branches` into shared state as a dead-end DataFrame
- [7] Hardcodes `100.0` as the percentage multiplier (cosmetic, but still a magic number pattern)

---

### J14 — HighBalanceAccounts
**Business purpose:** Identifies accounts with balances exceeding $10,000, enriched with the customer's name. Supports wealth management targeting and VIP identification.

**Anti-patterns applied:**
- [3] Uses C# External module (HighBalanceFilter) when SQL WHERE + JOIN would suffice
- [6] Iterates row-by-row to filter and enrich accounts
- [7] Hardcodes the $10,000 threshold as a magic number
- [9] Re-derives account balances from raw `accounts` table instead of reading from J11's curated snapshot

---

### J15 — AccountCustomerJoin
**Business purpose:** Produces a denormalized view joining each account with its owner's name, account type, status, and current balance. Enables single-table queries combining account and customer dimensions.

**Anti-patterns applied:**
- [1] Sources `addresses` table — never used in output
- [3] Uses C# External module (AccountCustomerDenormalizer) when SQL JOIN would suffice
- [4] Sources unused columns from `addresses`
- [5] Loads `addresses` into shared state as a dead-end DataFrame
- [9] Re-derives the account-customer relationship from raw tables instead of reading from J11 and J26

---

### J16 — CreditScoreSnapshot
**Business purpose:** Captures each customer's credit score per bureau (Equifax, TransUnion, Experian) as of a given date. Foundation for all credit-related analytics.

**Anti-patterns applied:**
- [1] Sources `branches` table — never referenced
- [3] Uses C# External module (CreditScoreProcessor) for what is essentially a pass-through SELECT
- [4] Sources unused columns from `credit_scores`

---

### J17 — CreditScoreAverage
**Business purpose:** Computes the average credit score across all three bureaus per customer, plus individual bureau scores pivoted into columns. Supports lending decisions and credit risk assessment.

**Anti-patterns applied:**
- [1] Sources `segments` table — irrelevant to credit scores
- [3] Uses C# External module (CreditScoreAverager) when SQL AVG + FILTER/CASE would suffice
- [5] Loads `segments` into shared state as a dead-end DataFrame
- [6] Iterates row-by-row to compute averages and pivot bureau scores
- [9] Re-derives credit score data from raw `credit_scores` table instead of reading from J16's curated snapshot
- [10] Does not declare a dependency on J16

---

### J18 — LoanPortfolioSnapshot
**Business purpose:** Captures a point-in-time snapshot of every loan — type, original amount, current balance, interest rate, and status. Foundation for loan portfolio management.

**Anti-patterns applied:**
- [1] Sources `branches` table — never referenced
- [3] Uses C# External module (LoanSnapshotBuilder) for what is essentially a pass-through SELECT
- [4] Sources unused columns from `loan_accounts` (e.g., origination_date, maturity_date)
- [5] Loads `branches` into shared state as a dead-end DataFrame

---

### J19 — LoanRiskAssessment
**Business purpose:** Joins each loan with the customer's average credit score and assigns a risk tier: Low (750+), Medium (650-749), High (550-649), or Very High (<550). Supports loan provisioning and pricing decisions.

**Anti-patterns applied:**
- [1] Sources `segments` table — irrelevant to loan risk
- [3] Uses C# External module (LoanRiskCalculator) when SQL JOIN + CASE would suffice
- [5] Loads `segments` into shared state as a dead-end DataFrame
- [6] Iterates row-by-row to join loans with credit scores and assign risk tiers
- [7] Hardcodes risk tier thresholds (750, 650, 550) as magic numbers
- [9] Re-derives credit averages and loan data from raw tables instead of reading from J17 and J18
- [10] Does not declare dependencies on J17 or J18

---

### J20 — CustomerCreditSummary
**Business purpose:** Assembles a holistic credit profile per customer: average credit score, total loan balance, total account balance, loan count, and account count. Single view of a customer's financial position.

**Anti-patterns applied:**
- [1] Sources `segments` table — irrelevant to credit summary
- [3] Uses C# External module (CustomerCreditSummaryBuilder) when SQL aggregation + joins would suffice
- [5] Loads `segments` into shared state as a dead-end DataFrame
- [6] Iterates row-by-row across customers, accounts, credit scores, and loans
- [9] Re-derives account balances, credit scores, and loan data from raw tables instead of reading from J11, J16, J17, and J18
- [10] Does not declare dependencies on J11, J16, J17, or J18

---

### J21 — BranchDirectory
**Business purpose:** Produces a deduplicated master listing of all branches with name, address, city, state, postal code, and country. Serves as the reference dimension for branch analytics.

**Anti-patterns applied:**
- [8] Uses ROW_NUMBER() window function to deduplicate branches when a simple SELECT DISTINCT or GROUP BY would suffice

---

### J22 — BranchVisitLog
**Business purpose:** Enriches each branch visit record with the branch name and customer name. Creates a detailed visit audit trail with timestamp and visit purpose.

**Anti-patterns applied:**
- [1] Sources `addresses` table — never used in output
- [3] Uses C# External module (BranchVisitEnricher) when SQL JOINs would suffice
- [4] Sources unused columns from `addresses`
- [6] Iterates row-by-row to join visits with branches and customers

---

### J23 — BranchVisitSummary
**Business purpose:** Counts total visits per branch per day, joined with the branch name. Enables daily traffic monitoring and branch performance comparison.

**Anti-patterns applied:**
- [2] Re-computes visit counts from raw `branch_visits` when a simple COUNT over J22's enriched log would work
- [8] Uses unnecessary CTEs for a straightforward GROUP BY with JOIN

---

### J24 — BranchVisitPurposeBreakdown
**Business purpose:** Breaks down daily visit counts per branch by visit purpose (e.g., Deposit, Withdrawal, Account Inquiry, Loan Payment). Reveals service demand patterns per branch.

**Anti-patterns applied:**
- [1] Sources `segments` table — irrelevant to visit purposes
- [2] Re-aggregates raw visit data when upstream jobs already produced visit counts
- [5] Loads `segments` into shared state as a dead-end DataFrame
- [8] Uses unnecessary CTEs and subqueries for a GROUP BY with one additional dimension

---

### J25 — TopBranches
**Business purpose:** Ranks all branches by total visit count (descending) with a numeric rank column. Supports location strategy, resource allocation, and performance benchmarking.

**Anti-patterns applied:**
- [7] Hardcodes `'2024-10-01'` as the start date filter
- [8] Uses DENSE_RANK() window function when a simple ORDER BY with ROW_NUMBER would be more straightforward
- [9] Re-derives visit totals from raw `branch_visits` instead of summing from J23's curated daily counts

---

### J26 — CustomerAccountSummaryV2
**Business purpose:** Summarizes each customer's account portfolio: total account count, total balance across all accounts, and active-only balance. Distinguishes between total and active holdings.

**Anti-patterns applied:**
- [1] Sources `branches` table — never referenced
- [3] Uses C# External module (CustomerAccountSummaryBuilder) when SQL GROUP BY + CASE would suffice
- [4] Sources unused columns from `accounts`
- [5] Loads `branches` into shared state as a dead-end DataFrame

---

### J27 — CustomerTransactionActivity
**Business purpose:** Aggregates daily transaction activity per customer: total transaction count, total amount, debit count, and credit count. Routes through account-to-customer mapping since transactions lack a direct customer_id.

**Anti-patterns applied:**
- [3] Uses C# External module (CustomerTxnActivityBuilder) when SQL with JOIN + GROUP BY would suffice
- [6] Iterates row-by-row to build account-to-customer lookup and aggregate
- [9] Re-derives per-account transaction totals from raw data instead of reading from J01, J11, and J15
- [10] Does not declare dependencies on J01, J11, or J15

---

### J28 — CustomerBranchActivity
**Business purpose:** Counts total branch visits per customer per day, enriched with the customer's name. Measures in-branch engagement frequency.

**Anti-patterns applied:**
- [1] Sources `branches` table — never used in output aggregation
- [3] Uses C# External module (CustomerBranchActivityBuilder) when SQL GROUP BY + JOIN would suffice
- [5] Loads `branches` into shared state as a dead-end DataFrame
- [6] Iterates row-by-row to count visits per customer
- [9] Re-derives visit data from raw tables instead of reading from J22 and J23

---

### J29 — CustomerValueScore
**Business purpose:** Computes a composite customer value score combining three dimensions: transaction activity (40% weight), account balance (35% weight), and branch visit engagement (25% weight). Each dimension is scored 0–1000, producing a composite score 0–1000.

**Scoring formulas:**
- `transaction_score = min(transaction_count × 10, 1000)`
- `balance_score = min(total_balance / 1000, 1000)`
- `visit_score = min(visit_count × 50, 1000)`
- `composite_score = 0.4 × transaction_score + 0.35 × balance_score + 0.25 × visit_score`

**Anti-patterns applied:**
- [3] Uses C# External module (CustomerValueCalculator) when SQL could compute the same with scalar expressions
- [6] Iterates row-by-row across customers, transactions, accounts, and visits
- [7] Hardcodes scoring multipliers (10, 1000, 50), caps (1000), and weights (0.4, 0.35, 0.25) as magic numbers
- [9] Re-derives all activity data from raw tables instead of reading from J01, J11, J22, J27, and J28
- [10] Does not declare dependencies on upstream curated jobs

---

### J30 — ExecutiveDashboard
**Business purpose:** Produces 9 high-level KPI metrics for executive reporting: total_customers, total_accounts, total_balance, total_transactions, total_txn_amount, avg_txn_amount, total_loans, total_loan_balance, and total_branch_visits. Each metric is a single row with metric_name and metric_value.

**Anti-patterns applied:**
- [1] Sources `segments` and `branches` tables — irrelevant to KPI computation
- [3] Uses C# External module (ExecutiveDashboardBuilder) when SQL COUNT/SUM/AVG would suffice
- [4] Sources unused columns from multiple tables
- [5] Loads `segments` and `branches` into shared state as dead-end DataFrames
- [6] Iterates row-by-row across all source tables to compute counts and sums
- [7] Hardcodes metric names as string literals
- [9] Re-derives all metrics from raw datalake tables instead of reading from curated outputs (J01, J04, J11, J18, J22, J23)
- [10] Does not declare dependencies on any upstream curated jobs

---

## Anti-Pattern Legend

| Code | Anti-Pattern | Description |
|------|-------------|-------------|
| [1] | Redundant data sourcing | Sources tables not needed by the job (e.g., branches table loaded but never referenced) |
| [2] | Duplicated transformation logic | Re-computes aggregations that upstream jobs already produced |
| [3] | Unnecessary External module | Uses C# External module when SQL Transformation would suffice or could be restructured |
| [4] | Unused columns sourced | Requests columns from datalake that are never used in output |
| [5] | Dead-end DataFrames | DataFrames loaded into shared state but never consumed by any module |
| [6] | Row-by-row iteration | Processes data via C# foreach loops instead of set-based SQL operations |
| [7] | Hardcoded magic values | Uses hardcoded thresholds, dates, or weights without configuration |
| [8] | Overly complex SQL | Uses unnecessary CTEs, subqueries, or window functions |
| [9] | Re-derives curated output | Recomputes data from raw sources instead of reading from curated tables that already contain it |
| [10] | Missing dependency declaration | Should depend on upstream curated jobs but doesn't declare the dependency |

## Declared Dependencies (5 edges)

```
J04 (DailyTransactionVolume) --> J01 (DailyTransactionSummary) [SameDay]
J05 (MonthlyTransactionTrend) --> J04 (DailyTransactionVolume) [SameDay]
J23 (BranchVisitSummary) --> J21 (BranchDirectory) [SameDay]
J24 (BranchVisitPurposeBreakdown) --> J21 (BranchDirectory) [SameDay]
J25 (TopBranches) --> J23 (BranchVisitSummary) [SameDay]
```

## Missing Dependencies (Phase 3 should discover these)

```
J10 --> J06, J07, J08
J14 --> J11
J15 --> J11, J26
J17 --> J16
J19 --> J17, J18
J20 --> J11, J16, J17, J18
J27 --> J01, J11, J15
J28 --> J22, J23
J29 --> J01, J11, J22, J27, J28
J30 --> J01, J04, J11, J18, J22, J23
```

## Weekend Safety

Weekday-only datalake tables: `customers`, `accounts`, `credit_scores`, `loan_accounts` (return 0 rows on weekends).

External module classes use a weekend guard pattern:
```csharp
if (weekdayDf == null || weekdayDf.Count == 0)
{
    sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
    return sharedState;
}
```

SQL-only jobs use only daily tables (transactions, branches, branch_visits, addresses, phone_numbers, email_addresses, segments, customers_segments) which always have data.

## Verification Results

- `dotnet build`: 0 warnings, 0 errors
- `dotnet test`: 33 passed, 0 failed
- Oct 1-31 execution: 930 Succeeded, 0 Failed, 0 Skipped
- Overwrite tables contain Oct 31 snapshot
- Append tables contain multi-date data across October
