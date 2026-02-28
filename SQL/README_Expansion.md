# Data Lake Expansion: Q4 2024

## Overview

This expansion extends the MockEtlFramework synthetic data lake in two dimensions:

1. **New tables**: 10 new datalake tables covering investments, cards, wire transfers, compliance, and customer preferences.
2. **More customers**: ~2,007 new customers (IDs 1224-3230), bringing the total from 223 to ~2,230.
3. **Extended date range**: From October only (31 days) to October 1 through December 31, 2024 (92 calendar days, ~64 weekdays).

This expansion **extends** the existing data lake. It does not replace or modify any existing tables or data. The original 12 tables remain unchanged and will receive additional rows for the new customers and dates via separate seed scripts.

## Run Order

1. **CreateExpansionTables.sql** -- Creates the 10 new datalake tables (this DDL). Run this first.
2. **Seed scripts** (being created separately) -- Populate both the new tables and extend existing tables with new customers/dates.

The DDL uses `CREATE TABLE IF NOT EXISTS`, so it is safe to run multiple times.

## New Tables

### Reference Tables (full-load daily, static reference data)

| Table | Description | Load Pattern | Est. Rows |
|-------|-------------|--------------|-----------|
| `datalake.securities` | Universe of ~50 tradeable securities (stocks, bonds, ETFs, mutual funds) | Daily, all 92 days | ~4,600 |
| `datalake.merchant_categories` | ~20 merchant category codes for card transaction classification | Daily, all 92 days | ~1,840 |

### Snapshot Tables (full-load weekdays, state drifts over time)

| Table | Description | Load Pattern | Est. Rows |
|-------|-------------|--------------|-----------|
| `datalake.investments` | Investment accounts (~20% of customers); value drifts with market simulation | Weekdays only (~64 days) | ~28,500 |
| `datalake.holdings` | Per-investment security holdings (1-5 per account); quantity and value drift | Weekdays only (~64 days) | ~85,600 |
| `datalake.cards` | Debit/credit cards linked to accounts; status may change over time | Weekdays only (~64 days) | ~285,000 |
| `datalake.compliance_events` | KYC, AML, sanctions, and identity verification records (~5% of customers) | Daily, all 92 days | ~10,300 |
| `datalake.customer_preferences` | Communication preferences for all customers (5 preference types each) | Daily, all 92 days | ~1,026,000 |

### Transactional Tables (event-driven, rows accumulate daily)

| Table | Description | Load Pattern | Est. Rows |
|-------|-------------|--------------|-----------|
| `datalake.wire_transfers` | Inbound/outbound wire transfers (~2% of customers per day) | Daily, all 92 days | ~4,100 |
| `datalake.card_transactions` | Card purchase/payment transactions (~30% of card holders, 1-3 txns/day) | Daily, all 92 days | ~246,000 |
| `datalake.overdraft_events` | Checking account overdrafts (~5% of checking accounts per month) | Daily, all 92 days | ~170 |

## Customer ID Ranges

| Range | Count | Source |
|-------|-------|--------|
| 1001-1223 | 223 | Original data lake (existing) |
| 1224-3230 | ~2,007 | Expansion (new) |
| **Total** | **~2,230** | |

## Date Range

| Period | Calendar Days | Weekdays | Status |
|--------|---------------|----------|--------|
| October 2024 | 31 | 23 | Existing (extended to new customers + tables) |
| November 2024 | 30 | 21 | New |
| December 2024 | 31 | 20 | New |
| **Total** | **92** | **64** | |

## Relationship to Existing Data

- The 10 new tables are **additive** -- they introduce new entity types (investments, cards, wire transfers, etc.) that did not exist before.
- The 12 original tables (`customers`, `accounts`, `addresses`, `segments`, `customers_segments`, `transactions`, `branches`, `branch_visits`, `credit_scores`, `loan_accounts`, `phone_numbers`, `email_addresses`) are **not modified** by this DDL.
- Seed scripts (created separately) will populate the new tables AND extend the existing tables with rows for the ~2,007 new customers and the November-December date range.
- Foreign key relationships (e.g., `investments.customer_id` referencing customers, `holdings.security_id` referencing securities, `cards.account_id` referencing accounts) are enforced at the application/seed level, not via DDL constraints, consistent with the existing datalake convention.

## Seed Scripts

Seed scripts for this expansion are being created separately. They will:

1. Add ~2,007 new customers (and their associated accounts, addresses, segments, etc.) to existing tables.
2. Extend all 22 tables (12 original + 10 new) through December 31, 2024.
3. Follow the same deterministic seeding patterns as `SeedDatalakeOctober2024.sql`.

## Estimated Total Row Counts (All Tables Combined)

With 2,230 customers across 92 days, the expanded data lake will contain approximately **1.7 million rows** across the 10 new tables alone, plus significant growth in the 12 existing tables from the new customers and extended date range.
