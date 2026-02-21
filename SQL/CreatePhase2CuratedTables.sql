-- ==============================================================================
-- CreatePhase2CuratedTables.sql
-- DDL for all 30 Phase 2 curated tables.
-- Run this before registering or executing Phase 2 jobs.
-- ==============================================================================

-- J01: DailyTransactionSummary (Append)
CREATE TABLE IF NOT EXISTS curated.daily_transaction_summary (
    account_id        integer       NOT NULL,
    as_of             date          NOT NULL,
    total_amount      numeric(14,2) NOT NULL,
    transaction_count integer       NOT NULL,
    debit_total       numeric(14,2) NOT NULL,
    credit_total      numeric(14,2) NOT NULL
);

-- J02: TransactionCategorySummary (Append)
CREATE TABLE IF NOT EXISTS curated.transaction_category_summary (
    txn_type          varchar(6)    NOT NULL,
    as_of             date          NOT NULL,
    total_amount      numeric(14,2) NOT NULL,
    transaction_count integer       NOT NULL,
    avg_amount        numeric(14,2) NOT NULL
);

-- J03: LargeTransactionLog (Append)
CREATE TABLE IF NOT EXISTS curated.large_transaction_log (
    transaction_id    integer        NOT NULL,
    account_id        integer        NOT NULL,
    customer_id       integer        NOT NULL,
    first_name        varchar(100)   NOT NULL,
    last_name         varchar(100)   NOT NULL,
    txn_type          varchar(6)     NOT NULL,
    amount            numeric(12,2)  NOT NULL,
    description       varchar(255),
    txn_timestamp     timestamp      NOT NULL,
    as_of             date           NOT NULL
);

-- J04: DailyTransactionVolume (Append)
CREATE TABLE IF NOT EXISTS curated.daily_transaction_volume (
    as_of             date          NOT NULL,
    total_transactions integer      NOT NULL,
    total_amount      numeric(14,2) NOT NULL,
    avg_amount        numeric(14,2) NOT NULL
);

-- J05: MonthlyTransactionTrend (Append)
CREATE TABLE IF NOT EXISTS curated.monthly_transaction_trend (
    as_of                    date          NOT NULL,
    daily_transactions       integer       NOT NULL,
    daily_amount             numeric(14,2) NOT NULL,
    avg_transaction_amount   numeric(14,2) NOT NULL
);

-- J06: CustomerDemographics (Overwrite)
CREATE TABLE IF NOT EXISTS curated.customer_demographics (
    customer_id   integer       NOT NULL,
    first_name    varchar(100)  NOT NULL,
    last_name     varchar(100)  NOT NULL,
    birthdate     date          NOT NULL,
    age           integer       NOT NULL,
    age_bracket   varchar(10)   NOT NULL,
    primary_phone varchar(20),
    primary_email varchar(255),
    as_of         date          NOT NULL
);

-- J07: CustomerContactInfo (Append)
CREATE TABLE IF NOT EXISTS curated.customer_contact_info (
    customer_id      integer      NOT NULL,
    contact_type     varchar(10)  NOT NULL,
    contact_subtype  varchar(10)  NOT NULL,
    contact_value    varchar(255) NOT NULL,
    as_of            date         NOT NULL
);

-- J08: CustomerSegmentMap (Append)
CREATE TABLE IF NOT EXISTS curated.customer_segment_map (
    customer_id   integer      NOT NULL,
    segment_id    integer      NOT NULL,
    segment_name  varchar(100) NOT NULL,
    segment_code  varchar(10)  NOT NULL,
    as_of         date         NOT NULL
);

-- J09: CustomerAddressHistory (Append)
CREATE TABLE IF NOT EXISTS curated.customer_address_history (
    customer_id    integer      NOT NULL,
    address_line1  varchar(200) NOT NULL,
    city           varchar(100) NOT NULL,
    state_province varchar(50)  NOT NULL,
    postal_code    varchar(20)  NOT NULL,
    country        char(2)      NOT NULL,
    as_of          date         NOT NULL
);

-- J10: CustomerFullProfile (Overwrite)
CREATE TABLE IF NOT EXISTS curated.customer_full_profile (
    customer_id   integer      NOT NULL,
    first_name    varchar(100) NOT NULL,
    last_name     varchar(100) NOT NULL,
    age           integer      NOT NULL,
    age_bracket   varchar(10)  NOT NULL,
    primary_phone varchar(20),
    primary_email varchar(255),
    segments      text,
    as_of         date         NOT NULL
);

-- J11: AccountBalanceSnapshot (Append)
CREATE TABLE IF NOT EXISTS curated.account_balance_snapshot (
    account_id      integer       NOT NULL,
    customer_id     integer       NOT NULL,
    account_type    varchar(20)   NOT NULL,
    account_status  varchar(20)   NOT NULL,
    current_balance numeric(12,2) NOT NULL,
    as_of           date          NOT NULL
);

-- J12: AccountStatusSummary (Overwrite)
CREATE TABLE IF NOT EXISTS curated.account_status_summary (
    account_type   varchar(20) NOT NULL,
    account_status varchar(20) NOT NULL,
    account_count  integer     NOT NULL,
    as_of          date        NOT NULL
);

-- J13: AccountTypeDistribution (Overwrite)
CREATE TABLE IF NOT EXISTS curated.account_type_distribution (
    account_type    varchar(20)   NOT NULL,
    account_count   integer       NOT NULL,
    total_accounts  integer       NOT NULL,
    percentage      numeric(5,2)  NOT NULL,
    as_of           date          NOT NULL
);

-- J14: HighBalanceAccounts (Overwrite)
CREATE TABLE IF NOT EXISTS curated.high_balance_accounts (
    account_id      integer       NOT NULL,
    customer_id     integer       NOT NULL,
    account_type    varchar(20)   NOT NULL,
    current_balance numeric(12,2) NOT NULL,
    first_name      varchar(100)  NOT NULL,
    last_name       varchar(100)  NOT NULL,
    as_of           date          NOT NULL
);

-- J15: AccountCustomerJoin (Overwrite)
CREATE TABLE IF NOT EXISTS curated.account_customer_join (
    account_id      integer       NOT NULL,
    customer_id     integer       NOT NULL,
    first_name      varchar(100)  NOT NULL,
    last_name       varchar(100)  NOT NULL,
    account_type    varchar(20)   NOT NULL,
    account_status  varchar(20)   NOT NULL,
    current_balance numeric(12,2) NOT NULL,
    as_of           date          NOT NULL
);

-- J16: CreditScoreSnapshot (Overwrite)
CREATE TABLE IF NOT EXISTS curated.credit_score_snapshot (
    credit_score_id integer     NOT NULL,
    customer_id     integer     NOT NULL,
    bureau          varchar(20) NOT NULL,
    score           integer     NOT NULL,
    as_of           date        NOT NULL
);

-- J17: CreditScoreAverage (Overwrite)
CREATE TABLE IF NOT EXISTS curated.credit_score_average (
    customer_id      integer       NOT NULL,
    first_name       varchar(100)  NOT NULL,
    last_name        varchar(100)  NOT NULL,
    avg_score        numeric(6,2)  NOT NULL,
    equifax_score    integer,
    transunion_score integer,
    experian_score   integer,
    as_of            date          NOT NULL
);

-- J18: LoanPortfolioSnapshot (Overwrite)
CREATE TABLE IF NOT EXISTS curated.loan_portfolio_snapshot (
    loan_id          integer       NOT NULL,
    customer_id      integer       NOT NULL,
    loan_type        varchar(20)   NOT NULL,
    original_amount  numeric(12,2) NOT NULL,
    current_balance  numeric(12,2) NOT NULL,
    interest_rate    numeric(5,2)  NOT NULL,
    loan_status      varchar(15)   NOT NULL,
    as_of            date          NOT NULL
);

-- J19: LoanRiskAssessment (Overwrite)
CREATE TABLE IF NOT EXISTS curated.loan_risk_assessment (
    loan_id          integer       NOT NULL,
    customer_id      integer       NOT NULL,
    loan_type        varchar(20)   NOT NULL,
    current_balance  numeric(12,2) NOT NULL,
    interest_rate    numeric(5,2)  NOT NULL,
    loan_status      varchar(15)   NOT NULL,
    avg_credit_score numeric(6,2),
    risk_tier        varchar(15)   NOT NULL,
    as_of            date          NOT NULL
);

-- J20: CustomerCreditSummary (Overwrite)
CREATE TABLE IF NOT EXISTS curated.customer_credit_summary (
    customer_id          integer       NOT NULL,
    first_name           varchar(100)  NOT NULL,
    last_name            varchar(100)  NOT NULL,
    avg_credit_score     numeric(6,2),
    total_loan_balance   numeric(14,2) NOT NULL,
    total_account_balance numeric(14,2) NOT NULL,
    loan_count           integer       NOT NULL,
    account_count        integer       NOT NULL,
    as_of                date          NOT NULL
);

-- J21: BranchDirectory (Overwrite)
CREATE TABLE IF NOT EXISTS curated.branch_directory (
    branch_id      integer      NOT NULL,
    branch_name    varchar(200) NOT NULL,
    address_line1  varchar(200) NOT NULL,
    city           varchar(100) NOT NULL,
    state_province varchar(50)  NOT NULL,
    postal_code    varchar(20)  NOT NULL,
    country        char(2)      NOT NULL,
    as_of          date         NOT NULL
);

-- J22: BranchVisitLog (Append)
CREATE TABLE IF NOT EXISTS curated.branch_visit_log (
    visit_id        integer      NOT NULL,
    customer_id     integer      NOT NULL,
    first_name      varchar(100),
    last_name       varchar(100),
    branch_id       integer      NOT NULL,
    branch_name     varchar(200) NOT NULL,
    visit_timestamp timestamp    NOT NULL,
    visit_purpose   varchar(30)  NOT NULL,
    as_of           date         NOT NULL
);

-- J23: BranchVisitSummary (Append)
CREATE TABLE IF NOT EXISTS curated.branch_visit_summary (
    branch_id   integer      NOT NULL,
    branch_name varchar(200) NOT NULL,
    as_of       date         NOT NULL,
    visit_count integer      NOT NULL
);

-- J24: BranchVisitPurposeBreakdown (Append)
CREATE TABLE IF NOT EXISTS curated.branch_visit_purpose_breakdown (
    branch_id     integer      NOT NULL,
    branch_name   varchar(200) NOT NULL,
    visit_purpose varchar(30)  NOT NULL,
    as_of         date         NOT NULL,
    visit_count   integer      NOT NULL
);

-- J25: TopBranches (Overwrite)
CREATE TABLE IF NOT EXISTS curated.top_branches (
    branch_id    integer      NOT NULL,
    branch_name  varchar(200) NOT NULL,
    total_visits integer      NOT NULL,
    rank         integer      NOT NULL,
    as_of        date         NOT NULL
);

-- J26: CustomerAccountSummary_v2 (Overwrite)
CREATE TABLE IF NOT EXISTS curated.customer_account_summary_v2 (
    customer_id    integer       NOT NULL,
    first_name     varchar(100)  NOT NULL,
    last_name      varchar(100)  NOT NULL,
    account_count  integer       NOT NULL,
    total_balance  numeric(14,2) NOT NULL,
    active_balance numeric(14,2) NOT NULL,
    as_of          date          NOT NULL
);

-- J27: CustomerTransactionActivity (Append)
CREATE TABLE IF NOT EXISTS curated.customer_transaction_activity (
    customer_id       integer       NOT NULL,
    as_of             date          NOT NULL,
    transaction_count integer       NOT NULL,
    total_amount      numeric(14,2) NOT NULL,
    debit_count       integer       NOT NULL,
    credit_count      integer       NOT NULL
);

-- J28: CustomerBranchActivity (Append)
CREATE TABLE IF NOT EXISTS curated.customer_branch_activity (
    customer_id integer      NOT NULL,
    first_name  varchar(100),
    last_name   varchar(100),
    as_of       date         NOT NULL,
    visit_count integer      NOT NULL
);

-- J29: CustomerValueScore (Overwrite)
CREATE TABLE IF NOT EXISTS curated.customer_value_score (
    customer_id       integer       NOT NULL,
    first_name        varchar(100)  NOT NULL,
    last_name         varchar(100)  NOT NULL,
    transaction_score numeric(8,2)  NOT NULL,
    balance_score     numeric(8,2)  NOT NULL,
    visit_score       numeric(8,2)  NOT NULL,
    composite_score   numeric(8,2)  NOT NULL,
    as_of             date          NOT NULL
);

-- J30: ExecutiveDashboard (Overwrite)
CREATE TABLE IF NOT EXISTS curated.executive_dashboard (
    metric_name  varchar(100)  NOT NULL,
    metric_value numeric(14,2) NOT NULL,
    as_of        date          NOT NULL
);
