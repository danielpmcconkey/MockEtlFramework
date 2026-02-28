-- ==============================================================================
-- CreateExpansionTables.sql
-- DDL for 10 new datalake tables added as part of the Q4 2024 data expansion.
-- Run this before any expansion seed scripts.
-- ==============================================================================

-- Investments: full-load weekdays; ~20% of customers have investment accounts;
-- current_value drifts slightly each weekday to simulate market movement
CREATE TABLE IF NOT EXISTS datalake.investments (
    investment_id  integer       NOT NULL,
    customer_id    integer       NOT NULL,
    account_type   varchar(20)   NOT NULL,
    current_value  numeric(14,2) NOT NULL,
    risk_profile   varchar(15)   NOT NULL,
    advisor_id     integer       NOT NULL,
    as_of          date          NOT NULL DEFAULT '2000-01-01',
    CONSTRAINT investments_account_type_check CHECK (account_type IN ('IRA', 'Brokerage', '401k', '529')),
    CONSTRAINT investments_risk_profile_check CHECK (risk_profile IN ('Conservative', 'Moderate', 'Aggressive'))
);

-- Securities: reference table, full-load daily; ~50 securities in the universe
-- (stocks, bonds, ETFs, mutual funds across major exchanges)
CREATE TABLE IF NOT EXISTS datalake.securities (
    security_id    integer      NOT NULL,
    ticker         varchar(10)  NOT NULL,
    security_name  varchar(200) NOT NULL,
    security_type  varchar(15)  NOT NULL,
    sector         varchar(50)  NOT NULL,
    exchange       varchar(10)  NOT NULL,
    as_of          date         NOT NULL DEFAULT '2000-01-01',
    CONSTRAINT securities_type_check     CHECK (security_type IN ('Stock', 'Bond', 'ETF', 'Mutual Fund')),
    CONSTRAINT securities_exchange_check CHECK (exchange IN ('NYSE', 'NASDAQ', 'TSX', 'LSE'))
);

-- Holdings: full-load weekdays; per investment account, 1-5 securities held;
-- quantity and current_value drift each weekday to simulate portfolio changes
CREATE TABLE IF NOT EXISTS datalake.holdings (
    holding_id     integer       NOT NULL,
    investment_id  integer       NOT NULL,
    security_id    integer       NOT NULL,
    customer_id    integer       NOT NULL,
    quantity       numeric(12,4) NOT NULL,
    cost_basis     numeric(14,2) NOT NULL,
    current_value  numeric(14,2) NOT NULL,
    as_of          date          NOT NULL DEFAULT '2000-01-01'
);

-- Wire Transfers: transactional daily; ~2% of customers per day initiate or
-- receive wire transfers; each wire has a direction, counterparty, and status
CREATE TABLE IF NOT EXISTS datalake.wire_transfers (
    wire_id            integer       NOT NULL,
    customer_id        integer       NOT NULL,
    account_id         integer       NOT NULL,
    direction          varchar(10)   NOT NULL,
    amount             numeric(14,2) NOT NULL,
    counterparty_name  varchar(200)  NOT NULL,
    counterparty_bank  varchar(200)  NOT NULL,
    status             varchar(10)   NOT NULL,
    wire_timestamp     timestamp     NOT NULL,
    as_of              date          NOT NULL DEFAULT '2000-01-01',
    CONSTRAINT wire_transfers_direction_check CHECK (direction IN ('Inbound', 'Outbound')),
    CONSTRAINT wire_transfers_status_check    CHECK (status IN ('Completed', 'Pending', 'Rejected'))
);

-- Cards: full-load weekdays; each account gets a card (debit for checking/savings,
-- credit for credit accounts); card_number_masked shows last-4 only
CREATE TABLE IF NOT EXISTS datalake.cards (
    card_id             integer     NOT NULL,
    customer_id         integer     NOT NULL,
    account_id          integer     NOT NULL,
    card_type           varchar(10) NOT NULL,
    card_number_masked  varchar(20) NOT NULL,
    expiration_date     date        NOT NULL,
    card_status         varchar(10) NOT NULL,
    as_of               date        NOT NULL DEFAULT '2000-01-01',
    CONSTRAINT cards_type_check   CHECK (card_type IN ('Debit', 'Credit')),
    CONSTRAINT cards_status_check CHECK (card_status IN ('Active', 'Blocked', 'Expired'))
);

-- Card Transactions: transactional daily; ~30% of card holders per day generate
-- 1-3 transactions each; merchant category codes reference the merchant_categories table
CREATE TABLE IF NOT EXISTS datalake.card_transactions (
    card_txn_id           integer       NOT NULL,
    card_id               integer       NOT NULL,
    customer_id           integer       NOT NULL,
    merchant_name         varchar(200)  NOT NULL,
    merchant_category_code varchar(10)  NOT NULL,
    amount                numeric(12,2) NOT NULL,
    txn_timestamp         timestamp     NOT NULL,
    authorization_status  varchar(10)   NOT NULL,
    as_of                 date          NOT NULL DEFAULT '2000-01-01',
    CONSTRAINT card_txn_auth_status_check CHECK (authorization_status IN ('Approved', 'Declined'))
);

-- Merchant Categories: reference table, full-load daily; ~20 MCC categories
-- used to classify card transactions by merchant type and risk level
CREATE TABLE IF NOT EXISTS datalake.merchant_categories (
    mcc_code        varchar(10)  NOT NULL,
    mcc_description varchar(200) NOT NULL,
    risk_level      varchar(10)  NOT NULL,
    as_of           date         NOT NULL DEFAULT '2000-01-01',
    CONSTRAINT merchant_categories_risk_check CHECK (risk_level IN ('Low', 'Medium', 'High'))
);

-- Overdraft Events: transactional daily; ~5% of checking accounts per month
-- trigger overdraft events; fees may be waived at branch discretion
CREATE TABLE IF NOT EXISTS datalake.overdraft_events (
    overdraft_id     integer       NOT NULL,
    account_id       integer       NOT NULL,
    customer_id      integer       NOT NULL,
    overdraft_amount numeric(12,2) NOT NULL,
    fee_amount       numeric(8,2)  NOT NULL,
    fee_waived       boolean       NOT NULL,
    event_timestamp  timestamp     NOT NULL,
    as_of            date          NOT NULL DEFAULT '2000-01-01'
);

-- Compliance Events: full-load daily; ~5% of customers have compliance records;
-- events track KYC reviews, AML flags, sanctions screens, etc.
CREATE TABLE IF NOT EXISTS datalake.compliance_events (
    event_id    integer     NOT NULL,
    customer_id integer     NOT NULL,
    event_type  varchar(25) NOT NULL,
    event_date  date        NOT NULL,
    status      varchar(15) NOT NULL,
    review_date date,
    as_of       date        NOT NULL DEFAULT '2000-01-01',
    CONSTRAINT compliance_event_type_check   CHECK (event_type IN (
        'KYC_REVIEW', 'AML_FLAG', 'PEP_CHECK', 'SANCTIONS_SCREEN', 'ID_VERIFICATION'
    )),
    CONSTRAINT compliance_event_status_check CHECK (status IN ('Open', 'Cleared', 'Escalated'))
);

-- Customer Preferences: full-load daily; all customers have communication
-- preferences controlling how the bank contacts them
CREATE TABLE IF NOT EXISTS datalake.customer_preferences (
    preference_id   integer     NOT NULL,
    customer_id     integer     NOT NULL,
    preference_type varchar(25) NOT NULL,
    opted_in        boolean     NOT NULL,
    updated_date    date        NOT NULL,
    as_of           date        NOT NULL DEFAULT '2000-01-01',
    CONSTRAINT customer_pref_type_check CHECK (preference_type IN (
        'PAPER_STATEMENTS', 'E_STATEMENTS', 'MARKETING_EMAIL', 'MARKETING_SMS', 'PUSH_NOTIFICATIONS'
    ))
);
