-- Phase 3: Create double_secret_curated schema
-- Mirror of curated schema for parallel comparison testing
-- Generated with exact column types matching curated schema

CREATE SCHEMA IF NOT EXISTS double_secret_curated;

CREATE TABLE IF NOT EXISTS double_secret_curated.account_balance_snapshot (
    account_id INTEGER NOT NULL,
    customer_id INTEGER NOT NULL,
    account_type VARCHAR(20) NOT NULL,
    account_status VARCHAR(20) NOT NULL,
    current_balance NUMERIC(12,2) NOT NULL,
    as_of DATE NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.account_customer_join (
    account_id INTEGER NOT NULL,
    customer_id INTEGER NOT NULL,
    first_name VARCHAR(100) NOT NULL,
    last_name VARCHAR(100) NOT NULL,
    account_type VARCHAR(20) NOT NULL,
    account_status VARCHAR(20) NOT NULL,
    current_balance NUMERIC(12,2) NOT NULL,
    as_of DATE NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.account_status_summary (
    account_type VARCHAR(20) NOT NULL,
    account_status VARCHAR(20) NOT NULL,
    account_count INTEGER NOT NULL,
    as_of DATE NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.account_type_distribution (
    account_type VARCHAR(20) NOT NULL,
    account_count INTEGER NOT NULL,
    total_accounts INTEGER NOT NULL,
    percentage NUMERIC(5,2) NOT NULL,
    as_of DATE NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.branch_directory (
    branch_id INTEGER NOT NULL,
    branch_name VARCHAR(200) NOT NULL,
    address_line1 VARCHAR(200) NOT NULL,
    city VARCHAR(100) NOT NULL,
    state_province VARCHAR(50) NOT NULL,
    postal_code VARCHAR(20) NOT NULL,
    country CHAR(2) NOT NULL,
    as_of DATE NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.branch_visit_log (
    visit_id INTEGER NOT NULL,
    customer_id INTEGER NOT NULL,
    first_name VARCHAR(100),
    last_name VARCHAR(100),
    branch_id INTEGER NOT NULL,
    branch_name VARCHAR(200) NOT NULL,
    visit_timestamp TIMESTAMP NOT NULL,
    visit_purpose VARCHAR(30) NOT NULL,
    as_of DATE NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.branch_visit_purpose_breakdown (
    branch_id INTEGER NOT NULL,
    branch_name VARCHAR(200) NOT NULL,
    visit_purpose VARCHAR(30) NOT NULL,
    as_of DATE NOT NULL,
    visit_count INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.branch_visit_summary (
    branch_id INTEGER NOT NULL,
    branch_name VARCHAR(200) NOT NULL,
    as_of DATE NOT NULL,
    visit_count INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.covered_transactions (
    transaction_id INTEGER,
    txn_timestamp TEXT,
    txn_type TEXT,
    amount NUMERIC,
    description TEXT,
    customer_id INTEGER,
    name_prefix TEXT,
    first_name TEXT,
    last_name TEXT,
    sort_name TEXT,
    name_suffix TEXT,
    customer_segment TEXT,
    address_id INTEGER,
    address_line1 TEXT,
    city TEXT,
    state_province TEXT,
    postal_code TEXT,
    country TEXT,
    account_id INTEGER,
    account_type TEXT,
    account_status TEXT,
    account_opened TEXT,
    as_of TEXT,
    record_count INTEGER
);

CREATE TABLE IF NOT EXISTS double_secret_curated.credit_score_average (
    customer_id INTEGER NOT NULL,
    first_name VARCHAR(100) NOT NULL,
    last_name VARCHAR(100) NOT NULL,
    avg_score NUMERIC(6,2) NOT NULL,
    equifax_score INTEGER,
    transunion_score INTEGER,
    experian_score INTEGER,
    as_of DATE NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.credit_score_snapshot (
    credit_score_id INTEGER NOT NULL,
    customer_id INTEGER NOT NULL,
    bureau VARCHAR(20) NOT NULL,
    score INTEGER NOT NULL,
    as_of DATE NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.customer_address_deltas (
    change_type TEXT,
    address_id TEXT,
    customer_id TEXT,
    customer_name TEXT,
    address_line1 TEXT,
    city TEXT,
    state_province TEXT,
    postal_code TEXT,
    country TEXT,
    start_date TEXT,
    end_date TEXT,
    as_of TEXT,
    record_count INTEGER
);

CREATE TABLE IF NOT EXISTS double_secret_curated.customer_address_history (
    customer_id INTEGER NOT NULL,
    address_line1 VARCHAR(200) NOT NULL,
    city VARCHAR(100) NOT NULL,
    state_province VARCHAR(50) NOT NULL,
    postal_code VARCHAR(20) NOT NULL,
    country CHAR(2) NOT NULL,
    as_of DATE NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.customer_branch_activity (
    customer_id INTEGER NOT NULL,
    first_name VARCHAR(100),
    last_name VARCHAR(100),
    as_of DATE NOT NULL,
    visit_count INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.customer_contact_info (
    customer_id INTEGER NOT NULL,
    contact_type VARCHAR(10) NOT NULL,
    contact_subtype VARCHAR(10) NOT NULL,
    contact_value VARCHAR(255) NOT NULL,
    as_of DATE NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.customer_credit_summary (
    customer_id INTEGER NOT NULL,
    first_name VARCHAR(100) NOT NULL,
    last_name VARCHAR(100) NOT NULL,
    avg_credit_score NUMERIC(6,2),
    total_loan_balance NUMERIC(14,2) NOT NULL,
    total_account_balance NUMERIC(14,2) NOT NULL,
    loan_count INTEGER NOT NULL,
    account_count INTEGER NOT NULL,
    as_of DATE NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.customer_demographics (
    customer_id INTEGER NOT NULL,
    first_name VARCHAR(100) NOT NULL,
    last_name VARCHAR(100) NOT NULL,
    birthdate DATE NOT NULL,
    age INTEGER NOT NULL,
    age_bracket VARCHAR(10) NOT NULL,
    primary_phone VARCHAR(20),
    primary_email VARCHAR(255),
    as_of DATE NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.customer_full_profile (
    customer_id INTEGER NOT NULL,
    first_name VARCHAR(100) NOT NULL,
    last_name VARCHAR(100) NOT NULL,
    age INTEGER NOT NULL,
    age_bracket VARCHAR(10) NOT NULL,
    primary_phone VARCHAR(20),
    primary_email VARCHAR(255),
    segments TEXT,
    as_of DATE NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.customer_segment_map (
    customer_id INTEGER NOT NULL,
    segment_id INTEGER NOT NULL,
    segment_name VARCHAR(100) NOT NULL,
    segment_code VARCHAR(10) NOT NULL,
    as_of DATE NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.customer_transaction_activity (
    customer_id INTEGER NOT NULL,
    as_of DATE NOT NULL,
    transaction_count INTEGER NOT NULL,
    total_amount NUMERIC(14,2) NOT NULL,
    debit_count INTEGER NOT NULL,
    credit_count INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.customer_value_score (
    customer_id INTEGER NOT NULL,
    first_name VARCHAR(100) NOT NULL,
    last_name VARCHAR(100) NOT NULL,
    transaction_score NUMERIC(8,2) NOT NULL,
    balance_score NUMERIC(8,2) NOT NULL,
    visit_score NUMERIC(8,2) NOT NULL,
    composite_score NUMERIC(8,2) NOT NULL,
    as_of DATE NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.daily_transaction_summary (
    account_id INTEGER NOT NULL,
    as_of DATE NOT NULL,
    total_amount NUMERIC(14,2) NOT NULL,
    transaction_count INTEGER NOT NULL,
    debit_total NUMERIC(14,2) NOT NULL,
    credit_total NUMERIC(14,2) NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.daily_transaction_volume (
    as_of DATE NOT NULL,
    total_transactions INTEGER NOT NULL,
    total_amount NUMERIC(14,2) NOT NULL,
    avg_amount NUMERIC(14,2) NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.executive_dashboard (
    metric_name VARCHAR(100) NOT NULL,
    metric_value NUMERIC(14,2) NOT NULL,
    as_of DATE NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.high_balance_accounts (
    account_id INTEGER NOT NULL,
    customer_id INTEGER NOT NULL,
    account_type VARCHAR(20) NOT NULL,
    current_balance NUMERIC(12,2) NOT NULL,
    first_name VARCHAR(100) NOT NULL,
    last_name VARCHAR(100) NOT NULL,
    as_of DATE NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.large_transaction_log (
    transaction_id INTEGER NOT NULL,
    account_id INTEGER NOT NULL,
    customer_id INTEGER NOT NULL,
    first_name VARCHAR(100) NOT NULL,
    last_name VARCHAR(100) NOT NULL,
    txn_type VARCHAR(6) NOT NULL,
    amount NUMERIC(12,2) NOT NULL,
    description VARCHAR(255),
    txn_timestamp TIMESTAMP NOT NULL,
    as_of DATE NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.loan_portfolio_snapshot (
    loan_id INTEGER NOT NULL,
    customer_id INTEGER NOT NULL,
    loan_type VARCHAR(20) NOT NULL,
    original_amount NUMERIC(12,2) NOT NULL,
    current_balance NUMERIC(12,2) NOT NULL,
    interest_rate NUMERIC(5,2) NOT NULL,
    loan_status VARCHAR(15) NOT NULL,
    as_of DATE NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.loan_risk_assessment (
    loan_id INTEGER NOT NULL,
    customer_id INTEGER NOT NULL,
    loan_type VARCHAR(20) NOT NULL,
    current_balance NUMERIC(12,2) NOT NULL,
    interest_rate NUMERIC(5,2) NOT NULL,
    loan_status VARCHAR(15) NOT NULL,
    avg_credit_score NUMERIC(6,2),
    risk_tier VARCHAR(15) NOT NULL,
    as_of DATE NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.monthly_transaction_trend (
    as_of DATE NOT NULL,
    daily_transactions INTEGER NOT NULL,
    daily_amount NUMERIC(14,2) NOT NULL,
    avg_transaction_amount NUMERIC(14,2) NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.top_branches (
    branch_id INTEGER NOT NULL,
    branch_name VARCHAR(200) NOT NULL,
    total_visits INTEGER NOT NULL,
    rank INTEGER NOT NULL,
    as_of DATE NOT NULL
);

CREATE TABLE IF NOT EXISTS double_secret_curated.transaction_category_summary (
    txn_type VARCHAR(6) NOT NULL,
    as_of DATE NOT NULL,
    total_amount NUMERIC(14,2) NOT NULL,
    transaction_count INTEGER NOT NULL,
    avg_amount NUMERIC(14,2) NOT NULL
);
