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
