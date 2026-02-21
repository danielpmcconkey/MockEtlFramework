-- ==============================================================================
-- CreateNewDataLakeTables.sql
-- DDL for new datalake tables added as part of the October 2024 data expansion.
-- Run this before SeedDatalakeOctober2024.sql.
-- ==============================================================================

-- Branches: one per postal code, full-load daily
CREATE TABLE IF NOT EXISTS datalake.branches (
    branch_id      integer       NOT NULL,
    branch_name    varchar(200)  NOT NULL,
    address_line1  varchar(200)  NOT NULL,
    city           varchar(100)  NOT NULL,
    state_province varchar(50)   NOT NULL,
    postal_code    varchar(20)   NOT NULL,
    country        char(2)       NOT NULL,
    as_of          date          NOT NULL DEFAULT '2000-01-01',
    CONSTRAINT branches_country_check CHECK (country IN ('US', 'CA'))
);

-- Phone Numbers: full-load daily; phone_id identifies the (customer, phone_type) relationship
CREATE TABLE IF NOT EXISTS datalake.phone_numbers (
    phone_id     integer     NOT NULL,
    customer_id  integer     NOT NULL,
    phone_type   varchar(10) NOT NULL,
    phone_number varchar(20) NOT NULL,
    as_of        date        NOT NULL DEFAULT '2000-01-01',
    CONSTRAINT phone_numbers_type_check CHECK (phone_type IN ('Mobile', 'Home', 'Work'))
);

-- Email Addresses: full-load daily; email_id identifies the (customer, email_type) relationship
CREATE TABLE IF NOT EXISTS datalake.email_addresses (
    email_id      integer      NOT NULL,
    customer_id   integer      NOT NULL,
    email_address varchar(255) NOT NULL,
    email_type    varchar(10)  NOT NULL,
    as_of         date         NOT NULL DEFAULT '2000-01-01',
    CONSTRAINT email_type_check CHECK (email_type IN ('Personal', 'Work'))
);

-- Credit Scores: full-load weekdays; credit_score_id identifies the (customer, bureau) relationship;
-- scores drift slightly each weekday to simulate real bureau reporting
CREATE TABLE IF NOT EXISTS datalake.credit_scores (
    credit_score_id integer     NOT NULL,
    customer_id     integer     NOT NULL,
    bureau          varchar(20) NOT NULL,
    score           integer     NOT NULL,
    as_of           date        NOT NULL DEFAULT '2000-01-01',
    CONSTRAINT credit_scores_bureau_check CHECK (bureau IN ('Equifax', 'TransUnion', 'Experian')),
    CONSTRAINT credit_scores_range_check  CHECK (score BETWEEN 300 AND 850)
);

-- Loan Accounts: full-load weekdays; ~40% of customers have at least one loan;
-- current_balance decreases slightly each weekday as principal is paid down
CREATE TABLE IF NOT EXISTS datalake.loan_accounts (
    loan_id          integer       NOT NULL,
    customer_id      integer       NOT NULL,
    loan_type        varchar(20)   NOT NULL,
    original_amount  numeric(12,2) NOT NULL,
    current_balance  numeric(12,2) NOT NULL,
    interest_rate    numeric(5,2)  NOT NULL,
    origination_date date          NOT NULL,
    maturity_date    date          NOT NULL,
    loan_status      varchar(15)   NOT NULL,
    as_of            date          NOT NULL DEFAULT '2000-01-01',
    CONSTRAINT loan_type_check   CHECK (loan_type   IN ('Mortgage', 'Auto', 'Personal', 'Student')),
    CONSTRAINT loan_status_check CHECK (loan_status IN ('Active', 'Paid Off', 'Delinquent'))
);

-- Branch Visits: transactional daily; ~10% of customers visit a branch on any given day
CREATE TABLE IF NOT EXISTS datalake.branch_visits (
    visit_id        integer     NOT NULL,
    customer_id     integer     NOT NULL,
    branch_id       integer     NOT NULL,
    visit_timestamp timestamp   NOT NULL,
    visit_purpose   varchar(30) NOT NULL,
    as_of           date        NOT NULL DEFAULT '2000-01-01',
    CONSTRAINT visit_purpose_check CHECK (visit_purpose IN (
        'Deposit', 'Withdrawal', 'Account Opening', 'Inquiry', 'Loan Application'
    ))
);
