-- ==============================================================================
-- RegisterPhase2Jobs.sql
-- Inserts Phase 2 job registrations into control.jobs.
-- Run this after CreateControlSchema.sql and CreatePhase2CuratedTables.sql.
-- Update job_conf_path values if the repository is checked out at a
-- different location on your machine.
-- ==============================================================================

-- Group A: Transaction Analytics
INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('DailyTransactionSummary',
 'Per-account daily transaction totals including debit/credit breakdown.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/daily_transaction_summary.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('TransactionCategorySummary',
 'Per-type (Debit/Credit) daily transaction totals with averages.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/transaction_category_summary.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('LargeTransactionLog',
 'Transactions over $500 enriched with customer name and account info.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/large_transaction_log.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('DailyTransactionVolume',
 'Daily aggregate count and dollar volume of all transactions.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/daily_transaction_volume.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('MonthlyTransactionTrend',
 'Running daily transaction statistics for monthly trend analysis.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/monthly_transaction_trend.json')
ON CONFLICT (job_name) DO NOTHING;

-- Group B: Customer Profile
INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('CustomerDemographics',
 'Customer name, age, age bracket, and primary contact information.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/customer_demographics.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('CustomerContactInfo',
 'All phone numbers and email addresses per customer, unified contact list.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/customer_contact_info.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('CustomerSegmentMap',
 'Customer-to-segment mapping with segment name and type.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/customer_segment_map.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('CustomerAddressHistory',
 'Full address records per customer per day.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/customer_address_history.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('CustomerFullProfile',
 'Merged customer profile combining demographics, contacts, and segments.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/customer_full_profile.json')
ON CONFLICT (job_name) DO NOTHING;

-- Group C: Account Analytics
INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('AccountBalanceSnapshot',
 'Daily snapshot of account balances with type and status.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/account_balance_snapshot.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('AccountStatusSummary',
 'Count of accounts by type and status (Active/Inactive).',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/account_status_summary.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('AccountTypeDistribution',
 'Account type distribution with count and percentage breakdown.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/account_type_distribution.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('HighBalanceAccounts',
 'Accounts with balance exceeding $10,000 with customer details.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/high_balance_accounts.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('AccountCustomerJoin',
 'Denormalized view of accounts joined with customer name.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/account_customer_join.json')
ON CONFLICT (job_name) DO NOTHING;

-- Group D: Credit & Lending
INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('CreditScoreSnapshot',
 'Credit score per customer per bureau snapshot.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/credit_score_snapshot.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('CreditScoreAverage',
 'Average credit score across all three bureaus per customer.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/credit_score_average.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('LoanPortfolioSnapshot',
 'All loans with current balances and status.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/loan_portfolio_snapshot.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('LoanRiskAssessment',
 'Loan risk assessment combining loan data with credit score tiers.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/loan_risk_assessment.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('CustomerCreditSummary',
 'Per-customer summary of credit scores, loans, and account balances.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/customer_credit_summary.json')
ON CONFLICT (job_name) DO NOTHING;

-- Group E: Branch Analytics
INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('BranchDirectory',
 'Complete branch listing with addresses.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/branch_directory.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('BranchVisitLog',
 'Branch visits enriched with customer name and branch details.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/branch_visit_log.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('BranchVisitSummary',
 'Daily visit count per branch.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/branch_visit_summary.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('BranchVisitPurposeBreakdown',
 'Visit count broken down by purpose for each branch per day.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/branch_visit_purpose_breakdown.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('TopBranches',
 'Branches ranked by total visits.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/top_branches.json')
ON CONFLICT (job_name) DO NOTHING;

-- Group F: Cross-Domain Reports
INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('CustomerAccountSummaryV2',
 'Per-customer account count and balance totals (v2 with active balance).',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/customer_account_summary_v2.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('CustomerTransactionActivity',
 'Per-customer daily transaction totals with debit/credit counts.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/customer_transaction_activity.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('CustomerBranchActivity',
 'Per-customer branch visit frequency per day.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/customer_branch_activity.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('CustomerValueScore',
 'Composite customer value score based on transactions, balances, and visits.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/customer_value_score.json')
ON CONFLICT (job_name) DO NOTHING;

INSERT INTO control.jobs (job_name, description, job_conf_path) VALUES
('ExecutiveDashboard',
 'High-level KPIs: customer count, account totals, transaction volume, loan portfolio, branch activity.',
 '/media/dan/fdrive/codeprojects/MockEtlFramework/JobExecutor/Jobs/executive_dashboard.json')
ON CONFLICT (job_name) DO NOTHING;
