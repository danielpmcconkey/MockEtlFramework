-- ==============================================================================
-- SeedExpansion_NewTables.sql
-- Seeds all 10 new datalake tables (from CreateExpansionTables.sql) for ALL
-- customers (1001-3230) across October 1 - December 31, 2024 (Q4).
--
-- Prerequisites:
--   1. CreateNewDataLakeTables.sql has been run (original 12 tables exist)
--   2. SeedDatalakeOctober2024.sql has been run (Oct data for customers 1001-1223)
--   3. CreateExpansionTables.sql has been run (10 new tables exist, empty)
--   4. Expansion seed for existing tables has been run (customers 1224-3230 exist
--      in datalake.customers, datalake.accounts, etc.)
--
-- This script populates:
--   1.  datalake.investments          (weekday snapshot, ~20% of customers)
--   2.  datalake.securities           (reference, daily — all 92 days)
--   3.  datalake.holdings             (weekday snapshot, per investment account)
--   4.  datalake.wire_transfers       (transactional, daily)
--   5.  datalake.cards                (weekday snapshot, 1 card per account)
--   6.  datalake.card_transactions    (transactional, daily)
--   7.  datalake.merchant_categories  (reference, daily — all 92 days)
--   8.  datalake.overdraft_events     (transactional, daily)
--   9.  datalake.compliance_events    (daily snapshot, ~5% of customers)
--  10.  datalake.customer_preferences (daily snapshot, all customers)
--
-- All values are deterministic via (hashtext(key::text || 'salt') & 2147483647) % N.
-- ==============================================================================

BEGIN;

-- ==============================================================================
-- STEP 0: Temporary reference tables reused throughout this script
-- ==============================================================================

-- All Q4 2024 weekdays (64 days)
CREATE TEMP TABLE tmp_q4_wd AS
SELECT d::date AS as_of
FROM generate_series('2024-10-01'::date, '2024-12-31'::date, '1 day'::interval) d
WHERE EXTRACT(DOW FROM d) NOT IN (0, 6);

-- All Q4 2024 calendar days (92 days)
CREATE TEMP TABLE tmp_q4_all AS
SELECT d::date AS as_of
FROM generate_series('2024-10-01'::date, '2024-12-31'::date, '1 day'::interval) d;

-- All customer IDs (1001-3230)
CREATE TEMP TABLE tmp_all_cust_ids AS
SELECT 1000 + gs AS cust_id
FROM generate_series(1, 2230) gs;

-- All accounts with their types and owning customer.
-- For customers 1001-1023: primary account_id = cust_id + 2000
-- For customers 1024-1223: primary account_id = cust_id + 2000
-- For customers 1224-3230: primary account_id = cust_id + 6000
-- Secondary accounts:
--   1001-1023: secondary = cust_id + 4200 (if eligible, ~30%)
--   1024-1223: secondary = cust_id + 4200 (if eligible, ~30%)
--   1224-3230: secondary = cust_id + 8200 (if eligible, ~30%)
--
-- We derive account_type from the same hash formula used in account generation.
CREATE TEMP TABLE tmp_all_accounts AS
-- Primary accounts for customers 1001-1223
SELECT
    c.cust_id,
    c.cust_id + 2000 AS account_id,
    (ARRAY['Checking','Savings','Credit'])[
        (hashtext(c.cust_id::text || 'at') & 2147483647) % 3 + 1
    ] AS account_type
FROM tmp_all_cust_ids c
WHERE c.cust_id <= 1223

UNION ALL

-- Secondary accounts for customers 1001-1223 (~30% eligible)
SELECT
    c.cust_id,
    c.cust_id + 4200 AS account_id,
    CASE (ARRAY['Checking','Savings','Credit'])[
            (hashtext(c.cust_id::text || 'at') & 2147483647) % 3 + 1
         ]
        WHEN 'Checking' THEN (ARRAY['Savings','Credit'])[
            (hashtext(c.cust_id::text || 'at2') & 2147483647) % 2 + 1]
        WHEN 'Savings'  THEN (ARRAY['Checking','Credit'])[
            (hashtext(c.cust_id::text || 'at2') & 2147483647) % 2 + 1]
        WHEN 'Credit'   THEN (ARRAY['Checking','Savings'])[
            (hashtext(c.cust_id::text || 'at2') & 2147483647) % 2 + 1]
    END AS account_type
FROM tmp_all_cust_ids c
WHERE c.cust_id <= 1223
  AND (hashtext(c.cust_id::text || 'has2') & 2147483647) % 10 < 3

UNION ALL

-- Primary accounts for customers 1224-3230
SELECT
    c.cust_id,
    c.cust_id + 6000 AS account_id,
    (ARRAY['Checking','Savings','Credit'])[
        (hashtext(c.cust_id::text || 'at') & 2147483647) % 3 + 1
    ] AS account_type
FROM tmp_all_cust_ids c
WHERE c.cust_id >= 1224

UNION ALL

-- Secondary accounts for customers 1224-3230 (~30% eligible)
SELECT
    c.cust_id,
    c.cust_id + 8200 AS account_id,
    CASE (ARRAY['Checking','Savings','Credit'])[
            (hashtext(c.cust_id::text || 'at') & 2147483647) % 3 + 1
         ]
        WHEN 'Checking' THEN (ARRAY['Savings','Credit'])[
            (hashtext(c.cust_id::text || 'at2') & 2147483647) % 2 + 1]
        WHEN 'Savings'  THEN (ARRAY['Checking','Credit'])[
            (hashtext(c.cust_id::text || 'at2') & 2147483647) % 2 + 1]
        WHEN 'Credit'   THEN (ARRAY['Checking','Savings'])[
            (hashtext(c.cust_id::text || 'at2') & 2147483647) % 2 + 1]
    END AS account_type
FROM tmp_all_cust_ids c
WHERE c.cust_id >= 1224
  AND (hashtext(c.cust_id::text || 'has2') & 2147483647) % 10 < 3;

-- Checking accounts only (needed for overdraft events)
CREATE TEMP TABLE tmp_checking_accounts AS
SELECT cust_id, account_id
FROM tmp_all_accounts
WHERE account_type = 'Checking';


-- ==============================================================================
-- SECTION 1: Securities (reference table — 50 securities, all 92 calendar days)
-- ==============================================================================
-- Hard-coded 50 securities cross-joined with all Q4 dates.
-- security_id is 1-50, stable across all dates.

INSERT INTO datalake.securities (security_id, ticker, security_name, security_type, sector, exchange, as_of)
SELECT
    s.security_id,
    s.ticker,
    s.security_name,
    s.security_type,
    s.sector,
    s.exchange,
    d.as_of
FROM (VALUES
    ( 1, 'ACME', 'Acme Corporation',             'Stock',       'Technology',   'NYSE'),
    ( 2, 'BOLT', 'Bolt Energy Inc',               'Stock',       'Energy',       'NYSE'),
    ( 3, 'CRST', 'Crest Healthcare Group',        'Stock',       'Healthcare',   'NASDAQ'),
    ( 4, 'DYNX', 'Dynex Financial Services',      'Stock',       'Finance',      'NYSE'),
    ( 5, 'EQTY', 'Equity Consumer Brands',        'Stock',       'Consumer',     'NASDAQ'),
    ( 6, 'FLUX', 'Flux Industrial Solutions',      'Stock',       'Industrial',   'NYSE'),
    ( 7, 'GRWN', 'Greenway Utilities Corp',       'Stock',       'Utilities',    'NYSE'),
    ( 8, 'HRZN', 'Horizon Real Estate Trust',     'ETF',         'Real Estate',  'NYSE'),
    ( 9, 'INTL', 'Intercontinental Tech Ltd',     'Stock',       'Technology',   'LSE'),
    (10, 'JOLT', 'Jolt Pharmaceuticals',          'Stock',       'Healthcare',   'NASDAQ'),
    (11, 'KNTC', 'Kinetic Energy Partners',       'ETF',         'Energy',       'NYSE'),
    (12, 'LMNT', 'Element Materials Science',     'Stock',       'Industrial',   'NASDAQ'),
    (13, 'MTRX', 'Matrix Financial Group',        'Mutual Fund', 'Finance',      'NYSE'),
    (14, 'NOVA', 'Nova Consumer Holdings',        'Stock',       'Consumer',     'NYSE'),
    (15, 'OPTX', 'Optix Technology Inc',          'Stock',       'Technology',   'NASDAQ'),
    (16, 'PRME', 'Prime Utilities Ltd',           'Bond',        'Utilities',    'LSE'),
    (17, 'QNTM', 'Quantum Computing Corp',        'Stock',       'Technology',   'NASDAQ'),
    (18, 'RSNG', 'Rising Healthcare Systems',     'Stock',       'Healthcare',   'NYSE'),
    (19, 'STRM', 'Storm Energy Solutions',        'ETF',         'Energy',       'TSX'),
    (20, 'TNDR', 'Thunder Industrial Inc',        'Stock',       'Industrial',   'NYSE'),
    (21, 'ULTM', 'Ultimate Finance Corp',         'Mutual Fund', 'Finance',      'NYSE'),
    (22, 'VRGE', 'Verge Consumer Tech',           'Stock',       'Consumer',     'NASDAQ'),
    (23, 'WNDX', 'Windexchange Utilities',        'Bond',        'Utilities',    'TSX'),
    (24, 'XPRT', 'Expert Real Estate Fund',       'ETF',         'Real Estate',  'NYSE'),
    (25, 'YLDX', 'Yield Max Income Fund',         'Mutual Fund', 'Finance',      'NYSE'),
    (26, 'ZETA', 'Zeta Biotech Inc',              'Stock',       'Healthcare',   'NASDAQ'),
    (27, 'APEX', 'Apex Technology Solutions',      'Stock',       'Technology',   'NYSE'),
    (28, 'BRDR', 'Broader Market ETF',            'ETF',         'Finance',      'NYSE'),
    (29, 'CLRX', 'Clearex Energy Corp',           'Stock',       'Energy',       'TSX'),
    (30, 'DPTH', 'Depth Mining Industries',       'Stock',       'Industrial',   'TSX'),
    (31, 'ELCN', 'Elcon Electronics Ltd',         'Stock',       'Technology',   'LSE'),
    (32, 'FRST', 'First Growth Capital',          'Mutual Fund', 'Finance',      'NYSE'),
    (33, 'GLBL', 'Global Healthcare ETF',         'ETF',         'Healthcare',   'NYSE'),
    (34, 'HLTX', 'Healthtex Systems Inc',         'Stock',       'Healthcare',   'NASDAQ'),
    (35, 'INSX', 'Insightex Consumer Group',      'Stock',       'Consumer',     'NYSE'),
    (36, 'JVLN', 'Javelin Industrial Tech',       'Stock',       'Industrial',   'NASDAQ'),
    (37, 'KNEX', 'Keynex Utilities Fund',         'Bond',        'Utilities',    'NYSE'),
    (38, 'LSTN', 'Listnet Real Estate Inc',       'Stock',       'Real Estate',  'NYSE'),
    (39, 'MGNX', 'Magnex Energy Corp',            'Stock',       'Energy',       'TSX'),
    (40, 'NXTG', 'Nextgen Technology ETF',        'ETF',         'Technology',   'NASDAQ'),
    (41, 'OMGX', 'Omega Financial Services',      'Stock',       'Finance',      'NYSE'),
    (42, 'PLSX', 'Pulse Healthcare Inc',          'Stock',       'Healthcare',   'NASDAQ'),
    (43, 'QRTZ', 'Quartz Consumer Products',      'Stock',       'Consumer',     'NYSE'),
    (44, 'RNTX', 'Rentex Real Estate Trust',      'ETF',         'Real Estate',  'NYSE'),
    (45, 'SPCX', 'Space Industrial Corp',         'Stock',       'Industrial',   'NYSE'),
    (46, 'TRUX', 'Truxion Utilities Group',       'Bond',        'Utilities',    'LSE'),
    (47, 'UMPX', 'Umpex Energy Holdings',         'Stock',       'Energy',       'NYSE'),
    (48, 'VLTX', 'Voltex Technology Inc',         'Stock',       'Technology',   'NASDAQ'),
    (49, 'WMRX', 'Westmark Finance Corp',         'Mutual Fund', 'Finance',      'NYSE'),
    (50, 'XION', 'Xion Consumer Electronics',     'Stock',       'Consumer',     'NASDAQ')
) AS s(security_id, ticker, security_name, security_type, sector, exchange)
CROSS JOIN tmp_q4_all d;


-- ==============================================================================
-- SECTION 2: Merchant Categories (reference table — 20 MCCs, all 92 calendar days)
-- ==============================================================================

INSERT INTO datalake.merchant_categories (mcc_code, mcc_description, risk_level, as_of)
SELECT
    m.mcc_code,
    m.mcc_description,
    m.risk_level,
    d.as_of
FROM (VALUES
    ('5411', 'Grocery Stores',     'Low'),
    ('5812', 'Restaurants',        'Low'),
    ('5541', 'Gas Stations',       'Low'),
    ('5311', 'Department Stores',  'Low'),
    ('4814', 'Telecom Services',   'Low'),
    ('5999', 'Misc Retail',        'Low'),
    ('7011', 'Hotels',             'Medium'),
    ('4511', 'Airlines',           'Medium'),
    ('5944', 'Jewelry',            'Medium'),
    ('5094', 'Precious Metals',    'High'),
    ('7995', 'Gambling',           'High'),
    ('5912', 'Pharmacies',         'Low'),
    ('5691', 'Clothing Stores',    'Low'),
    ('5732', 'Electronics',        'Low'),
    ('5942', 'Book Stores',        'Low'),
    ('5814', 'Fast Food',          'Low'),
    ('7832', 'Movie Theaters',     'Low'),
    ('8011', 'Medical Services',   'Low'),
    ('5200', 'Home Supply',        'Low'),
    ('6011', 'ATM/Cash',           'Medium')
) AS m(mcc_code, mcc_description, risk_level)
CROSS JOIN tmp_q4_all d;


-- ==============================================================================
-- SECTION 3: Investments (weekday snapshot, ~20% of customers)
-- ==============================================================================
-- Eligible customers: (hashtext(cust_id::text || 'inv') & 2147483647) % 10 < 2
-- investment_id: ROW_NUMBER over eligible customers (stable, date-independent)
-- account_type: hash into IRA/Brokerage/401k/529
-- current_value: base $5,000-$500,000, drifts +/- $500 per weekday from Oct 1
-- risk_profile: hash into Conservative/Moderate/Aggressive
-- advisor_id: hash % 50 + 1

-- Pre-compute eligible customers and their investment_ids (stable across dates)
CREATE TEMP TABLE tmp_investment_custs AS
SELECT
    ROW_NUMBER() OVER (ORDER BY c.cust_id) AS investment_id,
    c.cust_id,
    (ARRAY['IRA','Brokerage','401k','529'])[
        (hashtext(c.cust_id::text || 'inv_at') & 2147483647) % 4 + 1
    ] AS account_type,
    (5000 + (hashtext(c.cust_id::text || 'inv_val') & 2147483647) % 495001)::numeric(14,2)
        AS base_value,
    (ARRAY['Conservative','Moderate','Aggressive'])[
        (hashtext(c.cust_id::text || 'inv_rp') & 2147483647) % 3 + 1
    ] AS risk_profile,
    (hashtext(c.cust_id::text || 'inv_adv') & 2147483647) % 50 + 1 AS advisor_id
FROM tmp_all_cust_ids c
WHERE (hashtext(c.cust_id::text || 'inv') & 2147483647) % 10 < 2;

INSERT INTO datalake.investments (investment_id, customer_id, account_type, current_value,
                                  risk_profile, advisor_id, as_of)
SELECT
    ic.investment_id,
    ic.cust_id,
    ic.account_type,
    GREATEST(1000.00,
        ROUND(ic.base_value
            + (d.as_of - DATE '2024-10-01')
              * ((hashtext(ic.cust_id::text || d.as_of::text || 'inv_dr') & 2147483647) % 1001 - 500)::numeric
        , 2)
    ) AS current_value,
    ic.risk_profile,
    ic.advisor_id,
    d.as_of
FROM tmp_investment_custs ic
CROSS JOIN tmp_q4_wd d;


-- ==============================================================================
-- SECTION 4: Holdings (weekday snapshot, per investment account)
-- ==============================================================================
-- Per investment account, hash determines number of holdings (1-5).
-- holding_id: ROW_NUMBER across all holdings (stable, date-independent).
-- security_id: hash into 1-50.
-- quantity: 10-1000 shares, 4 decimal places.
-- cost_basis: $1,000-$50,000.
-- current_value: cost_basis * (0.8 to 1.3) +/- daily drift.

-- Pre-compute holdings per investment (stable across dates)
CREATE TEMP TABLE tmp_holding_base AS
WITH slots AS (
    SELECT generate_series(1, 5) AS slot
),
expanded AS (
    SELECT
        ic.investment_id,
        ic.cust_id,
        s.slot,
        -- Number of holdings this investment has (1-5)
        (hashtext(ic.cust_id::text || 'inv_nh') & 2147483647) % 5 + 1 AS num_holdings,
        -- Security assignment per slot
        (hashtext(ic.cust_id::text || s.slot::text || 'h_sec') & 2147483647) % 50 + 1
            AS security_id,
        -- Quantity: 10.0000 to 1000.0000
        ROUND((10 + (hashtext(ic.cust_id::text || s.slot::text || 'h_qty') & 2147483647) % 9901
            + (hashtext(ic.cust_id::text || s.slot::text || 'h_qtd') & 2147483647) % 10000 / 10000.0
        )::numeric, 4) AS quantity,
        -- Cost basis: $1,000-$50,000
        ROUND((1000 + (hashtext(ic.cust_id::text || s.slot::text || 'h_cb') & 2147483647) % 49001
        )::numeric, 2) AS cost_basis,
        -- Value factor: 0.80 to 1.30 (stored as integer 80-130, divided later)
        80 + (hashtext(ic.cust_id::text || s.slot::text || 'h_vf') & 2147483647) % 51
            AS value_factor_pct
    FROM tmp_investment_custs ic
    CROSS JOIN slots s
)
SELECT
    ROW_NUMBER() OVER (ORDER BY investment_id, slot) AS holding_id,
    investment_id,
    cust_id,
    slot,
    security_id,
    quantity,
    cost_basis,
    value_factor_pct
FROM expanded
WHERE slot <= num_holdings;

INSERT INTO datalake.holdings (holding_id, investment_id, security_id, customer_id,
                               quantity, cost_basis, current_value, as_of)
SELECT
    hb.holding_id,
    hb.investment_id,
    hb.security_id,
    hb.cust_id,
    hb.quantity,
    hb.cost_basis,
    GREATEST(100.00,
        ROUND(hb.cost_basis * hb.value_factor_pct / 100.0
            + (d.as_of - DATE '2024-10-01')
              * ((hashtext(hb.holding_id::text || d.as_of::text || 'h_dr') & 2147483647) % 201 - 100)::numeric
        , 2)
    ) AS current_value,
    d.as_of
FROM tmp_holding_base hb
CROSS JOIN tmp_q4_wd d;


-- ==============================================================================
-- SECTION 5: Wire Transfers (transactional, daily — all 92 calendar days)
-- ==============================================================================
-- ~2% of customers per day: hashtext(cust_id || as_of || 'wt') % 50 = 0
-- wire_id: ROW_NUMBER over all qualifying rows.
-- account_id: primary account (cust_id + 2000 for 1001-1223, cust_id + 6000 for 1224-3230).
-- direction: 60% Outbound, 40% Inbound.
-- amount: $1,000-$50,000.
-- counterparty_name: hash into 20 business names.
-- counterparty_bank: hash into 10 bank names.
-- status: 90% Completed, 8% Pending, 2% Rejected.
-- wire_timestamp: business hours 9am-5pm (32400-61200 seconds).

INSERT INTO datalake.wire_transfers (wire_id, customer_id, account_id, direction, amount,
                                     counterparty_name, counterparty_bank, status,
                                     wire_timestamp, as_of)
WITH candidate_wires AS (
    SELECT
        c.cust_id,
        CASE WHEN c.cust_id <= 1223 THEN c.cust_id + 2000 ELSE c.cust_id + 6000 END AS account_id,
        d.as_of
    FROM tmp_all_cust_ids c
    CROSS JOIN tmp_q4_all d
    WHERE (hashtext(c.cust_id::text || d.as_of::text || 'wt') & 2147483647) % 50 = 0
),
numbered AS (
    SELECT *, ROW_NUMBER() OVER (ORDER BY as_of, cust_id) AS rn
    FROM candidate_wires
)
SELECT
    rn AS wire_id,
    cust_id AS customer_id,
    account_id,
    -- Direction: 60% Outbound, 40% Inbound
    CASE WHEN (hashtext(cust_id::text || as_of::text || 'wt_dir') & 2147483647) % 10 < 6
         THEN 'Outbound' ELSE 'Inbound' END AS direction,
    -- Amount: $1,000-$50,000
    ROUND((1000 + (hashtext(cust_id::text || as_of::text || 'wt_amt') & 2147483647) % 49001
    )::numeric, 2) AS amount,
    -- Counterparty name: 20 business names
    (ARRAY[
        'Global Trading Corp','Pacific Holdings LLC','Eastern Supply Co',
        'Northern Industries Inc','Meridian Ventures','Atlas Capital Group',
        'Summit Financial Partners','Pinnacle Consulting','Bridgewater Associates',
        'Ironwood Manufacturing','Silver Creek Investments','Blue Harbor Trading',
        'Redstone Logistics','Greenfield Development','Westshore Enterprises',
        'Northstar Advisory','Suncoast Properties','Bayside Commercial',
        'Heritage Financial Group','Cornerstone Business Services'
    ])[(hashtext(cust_id::text || as_of::text || 'wt_cpn') & 2147483647) % 20 + 1]
        AS counterparty_name,
    -- Counterparty bank: 10 banks
    (ARRAY[
        'Chase','Wells Fargo','Bank of America','Citibank','HSBC',
        'TD Bank','RBC','BMO','Scotia','Barclays'
    ])[(hashtext(cust_id::text || as_of::text || 'wt_cpb') & 2147483647) % 10 + 1]
        AS counterparty_bank,
    -- Status: 90% Completed, 8% Pending, 2% Rejected
    CASE
        WHEN (hashtext(cust_id::text || as_of::text || 'wt_st') & 2147483647) % 100 < 90
            THEN 'Completed'
        WHEN (hashtext(cust_id::text || as_of::text || 'wt_st') & 2147483647) % 100 < 98
            THEN 'Pending'
        ELSE 'Rejected'
    END AS status,
    -- Timestamp: business hours 9am-5pm (32400s offset + up to 28800s = 8 hours)
    as_of::timestamp
        + ((32400 + (hashtext(cust_id::text || as_of::text || 'wt_ts') & 2147483647) % 28800)
           || ' seconds')::interval AS wire_timestamp,
    as_of
FROM numbered;


-- ==============================================================================
-- SECTION 6: Cards (weekday snapshot — one card per account)
-- ==============================================================================
-- card_id = account_id (1:1 mapping).
-- card_type: Debit for Checking/Savings, Credit for Credit accounts.
-- card_number_masked: '****-****-****-' || LPAD(hash % 10000, 4, '0').
-- expiration_date: hash -> date between 2025-01 and 2028-12.
-- card_status: 90% Active, 7% Blocked, 3% Expired.

INSERT INTO datalake.cards (card_id, customer_id, account_id, card_type, card_number_masked,
                            expiration_date, card_status, as_of)
SELECT
    a.account_id AS card_id,
    a.cust_id AS customer_id,
    a.account_id,
    CASE WHEN a.account_type = 'Credit' THEN 'Credit' ELSE 'Debit' END AS card_type,
    '****-****-****-'
        || LPAD(((hashtext(a.account_id::text || 'card_num') & 2147483647) % 10000)::text, 4, '0')
        AS card_number_masked,
    -- Expiration date: 2025-01-01 + hash days in range 0..1460 (~4 years)
    -- Then truncate to first of month for clean expiration dates
    DATE_TRUNC('month',
        DATE '2025-01-01'
        + ((hashtext(a.account_id::text || 'card_exp') & 2147483647) % 1461)
    )::date AS expiration_date,
    -- Card status: 90% Active, 7% Blocked, 3% Expired
    CASE
        WHEN (hashtext(a.account_id::text || 'card_st') & 2147483647) % 100 < 90 THEN 'Active'
        WHEN (hashtext(a.account_id::text || 'card_st') & 2147483647) % 100 < 97 THEN 'Blocked'
        ELSE 'Expired'
    END AS card_status,
    d.as_of
FROM tmp_all_accounts a
CROSS JOIN tmp_q4_wd d;


-- ==============================================================================
-- SECTION 7: Card Transactions (transactional, daily — all 92 calendar days)
-- ==============================================================================
-- ~30% of active (status='Active') card holders per day, 1-3 transactions each.
-- card_txn_id: ROW_NUMBER.
-- merchant_name: hash into 30 merchant names.
-- merchant_category_code: linked to the 20 MCCs from Section 2.
-- amount: $5-$500.
-- txn_timestamp: 6am-11pm (21600-82800 seconds).
-- authorization_status: 95% Approved, 5% Declined.

-- Build active cards temp table for efficient filtering
CREATE TEMP TABLE tmp_active_cards AS
SELECT account_id AS card_id, cust_id AS customer_id
FROM tmp_all_accounts a
WHERE (hashtext(a.account_id::text || 'card_st') & 2147483647) % 100 < 90;

-- Merchant names mapped to MCC codes (30 merchants across 20 categories)
-- We assign each merchant a category from the 20 MCCs defined in Section 2.
CREATE TEMP TABLE tmp_merchants (
    merchant_idx  integer,
    merchant_name varchar(200),
    mcc_code      varchar(10)
);

INSERT INTO tmp_merchants VALUES
( 1, 'Whole Foods Market',       '5411'),  -- Grocery
( 2, 'Trader Joes',             '5411'),  -- Grocery
( 3, 'Olive Garden',            '5812'),  -- Restaurants
( 4, 'Cheesecake Factory',      '5812'),  -- Restaurants
( 5, 'Shell Gas Station',       '5541'),  -- Gas Stations
( 6, 'Chevron Fuel Center',     '5541'),  -- Gas Stations
( 7, 'Nordstrom',               '5311'),  -- Department Stores
( 8, 'Target',                  '5311'),  -- Department Stores
( 9, 'Verizon Wireless',        '4814'),  -- Telecom
(10, 'AT&T Mobility',           '4814'),  -- Telecom
(11, 'Amazon.com',              '5999'),  -- Misc Retail
(12, 'Etsy Marketplace',        '5999'),  -- Misc Retail
(13, 'Marriott Hotels',         '7011'),  -- Hotels
(14, 'Hilton Garden Inn',       '7011'),  -- Hotels
(15, 'Delta Airlines',          '4511'),  -- Airlines
(16, 'United Airlines',         '4511'),  -- Airlines
(17, 'Kay Jewelers',            '5944'),  -- Jewelry
(18, 'CVS Pharmacy',            '5912'),  -- Pharmacies
(19, 'Walgreens',               '5912'),  -- Pharmacies
(20, 'Gap Clothing',            '5691'),  -- Clothing
(21, 'Old Navy',                '5691'),  -- Clothing
(22, 'Best Buy Electronics',    '5732'),  -- Electronics
(23, 'Apple Store',             '5732'),  -- Electronics
(24, 'Barnes & Noble',          '5942'),  -- Book Stores
(25, 'McDonalds',               '5814'),  -- Fast Food
(26, 'Starbucks',               '5814'),  -- Fast Food
(27, 'AMC Theaters',            '7832'),  -- Movie Theaters
(28, 'Kaiser Permanente',       '8011'),  -- Medical
(29, 'Home Depot',              '5200'),  -- Home Supply
(30, 'Lowes Hardware',          '5200');  -- Home Supply

INSERT INTO datalake.card_transactions (card_txn_id, card_id, customer_id, merchant_name,
                                        merchant_category_code, amount, txn_timestamp,
                                        authorization_status, as_of)
WITH candidate_txns AS (
    SELECT
        ac.card_id,
        ac.customer_id,
        d.as_of,
        t.slot
    FROM tmp_active_cards ac
    CROSS JOIN tmp_q4_all d
    CROSS JOIN (SELECT generate_series(1, 3) AS slot) t
    -- ~30% of card holders transact per day, with slot filtering for 1-3 txns
    WHERE (hashtext(ac.card_id::text || d.as_of::text || 'ct_elig') & 2147483647) % 10 < 3
      AND t.slot <= (hashtext(ac.card_id::text || d.as_of::text || 'ct_cnt') & 2147483647) % 3 + 1
),
numbered AS (
    SELECT *, ROW_NUMBER() OVER (ORDER BY as_of, card_id, slot) AS rn
    FROM candidate_txns
)
SELECT
    rn AS card_txn_id,
    n.card_id,
    n.customer_id,
    m.merchant_name,
    m.mcc_code AS merchant_category_code,
    -- Amount: $5-$500
    ROUND((5 + (hashtext(n.card_id::text || n.as_of::text || n.slot::text || 'ct_amt') & 2147483647) % 49601 / 100.0
    )::numeric, 2) AS amount,
    -- Timestamp: 6am-11pm (21600s + up to 61200s = 17 hours)
    n.as_of::timestamp
        + ((21600 + (hashtext(n.card_id::text || n.as_of::text || n.slot::text || 'ct_ts') & 2147483647) % 61200)
           || ' seconds')::interval AS txn_timestamp,
    -- Authorization: 95% Approved, 5% Declined
    CASE WHEN (hashtext(n.card_id::text || n.as_of::text || n.slot::text || 'ct_auth') & 2147483647) % 100 < 95
         THEN 'Approved' ELSE 'Declined' END AS authorization_status,
    n.as_of
FROM numbered n
JOIN tmp_merchants m ON m.merchant_idx =
    (hashtext(n.card_id::text || n.as_of::text || n.slot::text || 'ct_merch') & 2147483647) % 30 + 1;


-- ==============================================================================
-- SECTION 8: Overdraft Events (transactional, daily — all 92 calendar days)
-- ==============================================================================
-- Only checking accounts can overdraft.
-- ~5% of checking accounts per month -> ~0.17% per day -> hash % 600 = 0.
-- overdraft_id: ROW_NUMBER.
-- overdraft_amount: $10-$500.
-- fee_amount: $35.00 standard, or $0.00 if waived.
-- fee_waived: ~20% waived (hash % 5 = 0).
-- event_timestamp: business hours 9am-5pm.

INSERT INTO datalake.overdraft_events (overdraft_id, account_id, customer_id,
                                       overdraft_amount, fee_amount, fee_waived,
                                       event_timestamp, as_of)
WITH candidate_events AS (
    SELECT
        ca.account_id,
        ca.cust_id,
        d.as_of
    FROM tmp_checking_accounts ca
    CROSS JOIN tmp_q4_all d
    WHERE (hashtext(ca.account_id::text || d.as_of::text || 'od_ev') & 2147483647) % 600 = 0
),
numbered AS (
    SELECT *, ROW_NUMBER() OVER (ORDER BY as_of, account_id) AS rn
    FROM candidate_events
)
SELECT
    rn AS overdraft_id,
    account_id,
    cust_id AS customer_id,
    -- Overdraft amount: $10-$500
    ROUND((10 + (hashtext(account_id::text || as_of::text || 'od_amt') & 2147483647) % 49100 / 100.0
    )::numeric, 2) AS overdraft_amount,
    -- Fee: $35.00 unless waived
    CASE WHEN (hashtext(account_id::text || as_of::text || 'od_fw') & 2147483647) % 5 = 0
         THEN 0.00 ELSE 35.00 END AS fee_amount,
    -- Fee waived flag
    (hashtext(account_id::text || as_of::text || 'od_fw') & 2147483647) % 5 = 0 AS fee_waived,
    -- Timestamp: business hours 9am-5pm
    as_of::timestamp
        + ((32400 + (hashtext(account_id::text || as_of::text || 'od_ts') & 2147483647) % 28800)
           || ' seconds')::interval AS event_timestamp,
    as_of
FROM numbered;


-- ==============================================================================
-- SECTION 9: Compliance Events (daily snapshot — all 92 calendar days)
-- ==============================================================================
-- ~5% of customers: hashtext(cust_id || 'comp') % 20 = 0.
-- event_id: ROW_NUMBER over eligible customers (stable, date-independent).
-- event_type: hash into 5 types (KYC_REVIEW, AML_FLAG, PEP_CHECK,
--             SANCTIONS_SCREEN, ID_VERIFICATION).
-- event_date: hash -> specific date within Q4 2024.
-- status: 60% Cleared, 30% Open, 10% Escalated.
-- review_date: NULL if Open; event_date + 1-30 days if Cleared/Escalated.
-- This is a snapshot table — same events appear every day (static for POC).

CREATE TEMP TABLE tmp_compliance_custs AS
SELECT
    ROW_NUMBER() OVER (ORDER BY c.cust_id) AS event_id,
    c.cust_id,
    (ARRAY['KYC_REVIEW','AML_FLAG','PEP_CHECK','SANCTIONS_SCREEN','ID_VERIFICATION'])[
        (hashtext(c.cust_id::text || 'comp_et') & 2147483647) % 5 + 1
    ] AS event_type,
    -- Event date: within Q4 2024 (0-91 days from Oct 1)
    DATE '2024-10-01'
        + ((hashtext(c.cust_id::text || 'comp_ed') & 2147483647) % 92) AS event_date,
    -- Status raw hash for reuse
    (hashtext(c.cust_id::text || 'comp_st') & 2147483647) % 100 AS status_pct
FROM tmp_all_cust_ids c
WHERE (hashtext(c.cust_id::text || 'comp') & 2147483647) % 20 = 0;

INSERT INTO datalake.compliance_events (event_id, customer_id, event_type, event_date,
                                        status, review_date, as_of)
SELECT
    cc.event_id,
    cc.cust_id AS customer_id,
    cc.event_type,
    cc.event_date,
    -- Status: 60% Cleared, 30% Open, 10% Escalated
    CASE
        WHEN cc.status_pct < 60 THEN 'Cleared'
        WHEN cc.status_pct < 90 THEN 'Open'
        ELSE 'Escalated'
    END AS status,
    -- Review date: NULL if Open; event_date + 1-30 days otherwise
    CASE
        WHEN cc.status_pct >= 60 AND cc.status_pct < 90 THEN NULL
        ELSE cc.event_date
            + ((hashtext(cc.cust_id::text || 'comp_rd') & 2147483647) % 30 + 1)
    END AS review_date,
    d.as_of
FROM tmp_compliance_custs cc
CROSS JOIN tmp_q4_all d;


-- ==============================================================================
-- SECTION 10: Customer Preferences (daily snapshot — all 92 calendar days)
-- ==============================================================================
-- All customers get 5 preference records (one per type).
-- preference_id: (cust_id - 1001) * 5 + slot (1-5).
-- Preference types (in slot order):
--   1: PAPER_STATEMENTS   — 30% opted in
--   2: E_STATEMENTS       — 80% opted in
--   3: MARKETING_EMAIL    — 50% opted in
--   4: MARKETING_SMS      — 25% opted in
--   5: PUSH_NOTIFICATIONS — 60% opted in
-- updated_date: hash -> date in 2023-2024.

INSERT INTO datalake.customer_preferences (preference_id, customer_id, preference_type,
                                           opted_in, updated_date, as_of)
SELECT
    (c.cust_id - 1001) * 5 + p.slot AS preference_id,
    c.cust_id AS customer_id,
    p.pref_type AS preference_type,
    -- Opt-in rates vary by preference type
    CASE p.slot
        WHEN 1 THEN (hashtext(c.cust_id::text || 'pref_ps') & 2147483647) % 100 < 30   -- PAPER: 30%
        WHEN 2 THEN (hashtext(c.cust_id::text || 'pref_es') & 2147483647) % 100 < 80   -- E_STMT: 80%
        WHEN 3 THEN (hashtext(c.cust_id::text || 'pref_me') & 2147483647) % 100 < 50   -- EMAIL: 50%
        WHEN 4 THEN (hashtext(c.cust_id::text || 'pref_ms') & 2147483647) % 100 < 25   -- SMS: 25%
        WHEN 5 THEN (hashtext(c.cust_id::text || 'pref_pn') & 2147483647) % 100 < 60   -- PUSH: 60%
    END AS opted_in,
    -- Updated date: random date in 2023-2024 range (Jan 1, 2023 + up to 730 days)
    DATE '2023-01-01'
        + ((hashtext(c.cust_id::text || p.slot::text || 'pref_ud') & 2147483647) % 731)
        AS updated_date,
    d.as_of
FROM tmp_all_cust_ids c
CROSS JOIN (VALUES
    (1, 'PAPER_STATEMENTS'),
    (2, 'E_STATEMENTS'),
    (3, 'MARKETING_EMAIL'),
    (4, 'MARKETING_SMS'),
    (5, 'PUSH_NOTIFICATIONS')
) AS p(slot, pref_type)
CROSS JOIN tmp_q4_all d;


COMMIT;
