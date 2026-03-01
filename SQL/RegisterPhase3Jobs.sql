-- ==============================================================================
-- RegisterPhase3Jobs.sql
-- Inserts 70 Phase 3 job registrations into control.jobs.
-- Run this after CreateControlSchema.sql and RegisterSampleJobs.sql.
-- These are the file-output challenge jobs for the Phase 3 agent swarm run.
-- ==============================================================================

-- Domain A: Card Analytics (10 jobs)

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('CardTransactionDaily', 'Daily card transaction totals by card type', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/card_transaction_daily.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('CardSpendingByMerchant', 'Spending totals grouped by MCC category', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/card_spending_by_merchant.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('CardFraudFlags', 'Transactions at high-risk merchants above threshold', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/card_fraud_flags.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('CardAuthorizationSummary', 'Auth success/failure rates by card type', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/card_authorization_summary.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('CardCustomerSpending', 'Per-customer card spending summary', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/card_customer_spending.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('HighRiskMerchantActivity', 'Transactions at merchants with risk_level High', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/high_risk_merchant_activity.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('CardStatusSnapshot', 'Card counts by status (Active/Blocked/Expired)', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/card_status_snapshot.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('CardTypeDistribution', 'Percentage distribution of card types', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/card_type_distribution.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('MerchantCategoryDirectory', 'Reference list of MCC codes', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/merchant_category_directory.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('CardExpirationWatch', 'Cards expiring within 90 days', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/card_expiration_watch.json', true)
ON CONFLICT (job_name) DO NOTHING;

-- Domain B: Investment & Securities (10 jobs)

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('PortfolioValueSummary', 'Total portfolio value per customer', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/portfolio_value_summary.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('HoldingsBySector', 'Holdings grouped by security sector', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/holdings_by_sector.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('InvestmentRiskProfile', 'Risk classification per investment account', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/investment_risk_profile.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('SecuritiesDirectory', 'Reference list of traded securities', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/securities_directory.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('TopHoldingsByValue', 'Top 20 most-held securities by total value', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/top_holdings_by_value.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('InvestmentAccountOverview', 'Investment accounts with current values', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/investment_account_overview.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('PortfolioConcentration', 'Sector percentage of total portfolio per customer', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/portfolio_concentration.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('CustomerInvestmentSummary', 'Investment count and total value per customer', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/customer_investment_summary.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('BondMaturitySchedule', 'Bonds with holdings data', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/bond_maturity_schedule.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('FundAllocationBreakdown', 'Fund-type holdings distribution', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/fund_allocation_breakdown.json', true)
ON CONFLICT (job_name) DO NOTHING;

-- Domain C: Compliance & Regulatory (10 jobs)

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('ComplianceEventSummary', 'Event counts by type and status', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/compliance_event_summary.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('WireTransferDaily', 'Daily wire transfer volume and amounts', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/wire_transfer_daily.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('LargeWireReport', 'Wire transfers above $10K threshold', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/large_wire_report.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('WireDirectionSummary', 'Inbound vs outbound wire totals', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/wire_direction_summary.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('ComplianceOpenItems', 'Unresolved compliance events', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/compliance_open_items.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('CustomerComplianceRisk', 'Composite risk score from events, wires, and transactions', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/customer_compliance_risk.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('SuspiciousWireFlags', 'Wires to/from flagged counterparties', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/suspicious_wire_flags.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('ComplianceResolutionTime', 'Avg days to resolve by event type', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/compliance_resolution_time.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('DailyWireVolume', 'Wire count and amount by day', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/daily_wire_volume.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('RegulatoryExposureSummary', 'Customer exposure by account, wire, and compliance', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/regulatory_exposure_summary.json', true)
ON CONFLICT (job_name) DO NOTHING;

-- Domain D: Overdraft & Fee Analysis (10 jobs)

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('OverdraftDailySummary', 'Daily overdraft count and total amount', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/overdraft_daily_summary.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('OverdraftByAccountType', 'Overdraft rates by account type', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/overdraft_by_account_type.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('FeeWaiverAnalysis', 'Waived vs charged fee breakdown', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/fee_waiver_analysis.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('RepeatOverdraftCustomers', 'Customers with 2+ overdrafts', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/repeat_overdraft_customers.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('OverdraftAmountDistribution', 'Amount bucketed distribution', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/overdraft_amount_distribution.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('FeeRevenueDaily', 'Daily fee revenue (charged minus waived)', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/fee_revenue_daily.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('OverdraftCustomerProfile', 'Demographics of overdraft customers', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/overdraft_customer_profile.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('OverdraftRecoveryRate', 'Percentage of overdrafts recovered', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/overdraft_recovery_rate.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('AccountOverdraftHistory', 'Full overdraft event history per account', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/account_overdraft_history.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('OverdraftFeeSummary', 'Total fees by waived/charged status', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/overdraft_fee_summary.json', true)
ON CONFLICT (job_name) DO NOTHING;

-- Domain E: Customer Preferences & Communication (10 jobs)

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('PreferenceSummary', 'Opt-in/opt-out counts by preference type', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/preference_summary.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('EmailOptInRate', 'Email opt-in percentage by segment', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/email_opt_in_rate.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('SmsOptInRate', 'SMS opt-in percentage by segment', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/sms_opt_in_rate.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('MarketingEligibleCustomers', 'Customers opted in to all marketing channels', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/marketing_eligible_customers.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('DoNotContactList', 'Customers opted out of everything', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/do_not_contact_list.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('PreferenceChangeCount', 'Count of preference changes per customer', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/preference_change_count.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('CommunicationChannelMap', 'Preferred contact method per customer', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/communication_channel_map.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('PreferenceBySegment', 'Opt-in rates grouped by segment', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/preference_by_segment.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('CustomerContactability', 'Customers with valid email, phone, and opt-in', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/customer_contactability.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('PreferenceTrend', 'Daily opt-in counts (misleadingly named trend)', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/preference_trend.json', true)
ON CONFLICT (job_name) DO NOTHING;

-- Domain F: Cross-Domain Analytics (10 jobs)

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('Customer360Snapshot', 'Full customer view: accounts, cards, investments', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/customer_360_snapshot.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('WealthTierAnalysis', 'Customers bucketed by total wealth', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/wealth_tier_analysis.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('PaymentChannelMix', 'Transaction vs card vs wire payment volumes', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/payment_channel_mix.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('CrossSellCandidates', 'Customers missing product categories', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/cross_sell_candidates.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('CustomerAttritionSignals', 'Risk score from dormant accounts and declining transactions', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/customer_attrition_signals.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('MonthlyRevenueBreakdown', 'Revenue by product line (fees and interest)', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/monthly_revenue_breakdown.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('BranchCardActivity', 'Card transactions near branch locations', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/branch_card_activity.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('ProductPenetration', 'Product usage rates across customer base', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/product_penetration.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('ComplianceTransactionRatio', 'Compliance events per 1000 transactions', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/compliance_transaction_ratio.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('QuarterlyExecutiveKpis', 'High-level KPIs across all domains', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/quarterly_executive_kpis.json', true)
ON CONFLICT (job_name) DO NOTHING;

-- Domain G: Extended Transaction Analytics (10 jobs)

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('WeekendTransactionPattern', 'Weekend vs weekday transaction comparison', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/weekend_transaction_pattern.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('DebitCreditRatio', 'Debit/credit ratio per account', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/debit_credit_ratio.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('TransactionSizeBuckets', 'Transaction amounts bucketed into ranges', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/transaction_size_buckets.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('DormantAccountDetection', 'Accounts with 0 transactions in date range', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/dormant_account_detection.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('AccountVelocityTracking', 'Transaction frequency per account per day', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/account_velocity_tracking.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('BranchTransactionVolume', 'Transaction volumes attributed to branches', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/branch_transaction_volume.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('TransactionAnomalyFlags', 'Transactions more than 3 std devs from account mean', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/transaction_anomaly_flags.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('InterAccountTransfers', 'Paired debit/credit transactions between accounts', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/inter_account_transfers.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('PeakTransactionTimes', 'Hourly transaction distribution', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/peak_transaction_times.json', true)
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('DailyBalanceMovement', 'Net balance change per account per day', '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/daily_balance_movement.json', true)
ON CONFLICT (job_name) DO NOTHING;
