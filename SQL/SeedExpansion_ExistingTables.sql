-- ==============================================================================
-- SeedExpansion_ExistingTables.sql
-- Expands the 12 existing datalake tables along two axes:
--
-- Axis 1: 2,007 new customers (IDs 1224-3230) for Oct 1 - Dec 31, 2024
-- Axis 2: Extended dates (Nov 1 - Dec 31, 2024) for ALL customers (1001-3230)
--
-- Prerequisites:
--   - CreateNewDataLakeTables.sql has been run (branches, phone_numbers, etc.)
--   - CreateExpansionTables.sql has been run (new table DDL exists)
--   - SeedDatalakeOctober2024.sql has been run (existing Oct data for 1001-1223)
--   - This script is run ONCE after existing data is in place
--
-- New customer range: 1224-3230 (2,007 customers)
-- Date extension: Nov 1 - Dec 31, 2024 (for all customers 1001-3230)
--
-- ID collision prevention:
--   - Primary accounts:   cust_id + 6000 (7224-9230) — no overlap with 3001-5423
--   - Secondary accounts: cust_id + 8200 (9424-11430) — no overlap
--   - Phone IDs:          (cust_id - 1001) * 3 + slot — continues naturally
--   - Email IDs:          (cust_id - 1001) * 2 + slot — continues naturally
--   - Credit score IDs:   (cust_id - 1001) * 3 + slot — continues naturally
--   - Address IDs:        3000 + (cust_id - 1223) = 3001-5007
--   - Segment assign IDs: 1000 + ROW_NUMBER for secondary; cust_id + 2000 for primary
--   - Loan IDs:           continue after existing MAX(loan_id)
--   - Transaction IDs:    continue after existing MAX(transaction_id)
--   - Visit IDs:          continue after existing MAX(visit_id)
-- ==============================================================================

BEGIN;

-- ==============================================================================
-- STEP 0: Temporary reference tables reused throughout this script
-- ==============================================================================

-- All October 2024 weekdays (for new customers who need Oct data)
CREATE TEMP TABLE tmp_oct_wd AS
SELECT d::date AS as_of
FROM generate_series('2024-10-01'::date, '2024-10-31'::date, '1 day'::interval) d
WHERE EXTRACT(DOW FROM d) NOT IN (0, 6);

-- All October 2024 calendar days (for new customers who need Oct data)
CREATE TEMP TABLE tmp_oct_all AS
SELECT d::date AS as_of
FROM generate_series('2024-10-01'::date, '2024-10-31'::date, '1 day'::interval) d;

-- Nov-Dec 2024 weekdays (for extending ALL customers)
CREATE TEMP TABLE tmp_novdec_wd AS
SELECT d::date AS as_of
FROM generate_series('2024-11-01'::date, '2024-12-31'::date, '1 day'::interval) d
WHERE EXTRACT(DOW FROM d) NOT IN (0, 6);

-- Nov-Dec 2024 calendar days (for extending ALL customers)
CREATE TEMP TABLE tmp_novdec_all AS
SELECT d::date AS as_of
FROM generate_series('2024-11-01'::date, '2024-12-31'::date, '1 day'::interval) d;

-- All Q4 2024 weekdays (Oct-Dec, for new customer tables that are weekday-only)
CREATE TEMP TABLE tmp_q4_wd AS
SELECT d::date AS as_of
FROM generate_series('2024-10-01'::date, '2024-12-31'::date, '1 day'::interval) d
WHERE EXTRACT(DOW FROM d) NOT IN (0, 6);

-- All Q4 2024 calendar days (Oct-Dec, for new customer tables that are daily)
CREATE TEMP TABLE tmp_q4_all AS
SELECT d::date AS as_of
FROM generate_series('2024-10-01'::date, '2024-12-31'::date, '1 day'::interval) d;

-- All 40 branches (reused for branch visits and branch extension)
CREATE TEMP TABLE tmp_branches (
    branch_id      integer,
    branch_name    varchar(200),
    address_line1  varchar(200),
    city           varchar(100),
    state_province varchar(50),
    postal_code    varchar(20),
    country        char(2)
);

INSERT INTO tmp_branches VALUES
-- US locations
( 1, 'Columbus OH Branch',       '100 E Broad St',         'Columbus',      'OH', '43215',   'US'),
( 2, 'Chicago IL Branch',        '200 N Michigan Ave',     'Chicago',       'IL', '60601',   'US'),
( 3, 'Springfield IL Branch',    '100 E Capitol Ave',      'Springfield',   'IL', '62704',   'US'),
( 4, 'San Francisco CA Branch',  '1 Market St',            'San Francisco', 'CA', '94105',   'US'),
( 5, 'Seattle WA Branch',        '1 Westlake Ave N',       'Seattle',       'WA', '98101',   'US'),
( 6, 'Austin TX Branch',         '1 Congress Ave',         'Austin',        'TX', '73301',   'US'),
( 7, 'Denver CO Branch',         '1 Colfax Ave',           'Denver',        'CO', '80202',   'US'),
( 8, 'New York NY Branch',       '1 Park Ave S',           'New York',      'NY', '10003',   'US'),
( 9, 'Phoenix AZ Branch',        '1 N Central Ave',        'Phoenix',       'AZ', '85004',   'US'),
(10, 'San Diego CA Branch',      '1 Harbor Dr',            'San Diego',     'CA', '92101',   'US'),
(11, 'Atlanta GA Branch',        '1 Peachtree St NE',      'Atlanta',       'GA', '30303',   'US'),
(12, 'Kansas City MO Branch',    '1 Main St',              'Kansas City',   'MO', '64105',   'US'),
(13, 'Boston MA Branch',         '1 State St',             'Boston',        'MA', '02101',   'US'),
(14, 'Miami FL Branch',          '1 Biscayne Blvd',        'Miami',         'FL', '33101',   'US'),
(15, 'Minneapolis MN Branch',    '1 Nicollet Mall',        'Minneapolis',   'MN', '55401',   'US'),
(16, 'Portland OR Branch',       '1 SW Broadway',          'Portland',      'OR', '97201',   'US'),
(17, 'Las Vegas NV Branch',      '1 Las Vegas Blvd S',     'Las Vegas',     'NV', '89101',   'US'),
(18, 'Nashville TN Branch',      '1 Broadway',             'Nashville',     'TN', '37201',   'US'),
(19, 'Charlotte NC Branch',      '1 Trade St',             'Charlotte',     'NC', '28201',   'US'),
(20, 'Detroit MI Branch',        '1 Woodward Ave',         'Detroit',       'MI', '48201',   'US'),
(21, 'Indianapolis IN Branch',   '1 Monument Circle',      'Indianapolis',  'IN', '46201',   'US'),
(22, 'Memphis TN Branch',        '1 Beale St',             'Memphis',       'TN', '38101',   'US'),
(23, 'Baltimore MD Branch',      '1 Charles St',           'Baltimore',     'MD', '21201',   'US'),
(24, 'Oklahoma City OK Branch',  '1 N Broadway Ave',       'Oklahoma City', 'OK', '73101',   'US'),
-- Canadian locations
(25, 'Ottawa ON Branch',         '250 Rideau St',          'Ottawa',        'ON', 'K1N 5Y1', 'CA'),
(26, 'Montreal QC Branch',       '1200 Rue McGill',        'Montreal',      'QC', 'H3B 1K9', 'CA'),
(27, 'Toronto ON (M5H) Branch',  '100 King St W',          'Toronto',       'ON', 'M5H 1J9', 'CA'),
(28, 'Toronto ON (M5G) Branch',  '200 College St',         'Toronto',       'ON', 'M5G 1L4', 'CA'),
(29, 'Edmonton AB Branch',       '10055 Jasper Ave',       'Edmonton',      'AB', 'T5J 3R7', 'CA'),
(30, 'Vancouver BC Branch',      '200 Granville St',       'Vancouver',     'BC', 'V6C 1S4', 'CA'),
(31, 'Winnipeg MB Branch',       '18 Portage Ave',         'Winnipeg',      'MB', 'R3C 0B1', 'CA'),
(32, 'Halifax NS Branch',        '1505 Barrington St',     'Halifax',       'NS', 'B3J 1Z4', 'CA'),
(33, 'Fredericton NB Branch',    '10 Queen Square',        'Fredericton',   'NB', 'E3B 1B2', 'CA'),
(34, 'St Johns NL Branch',       '55 Water St',            'St. Johns',     'NL', 'A1C 1A1', 'CA'),
(35, 'Regina SK Branch',         '300 Victoria Ave',       'Regina',        'SK', 'S4P 0S4', 'CA'),
(36, 'Whitehorse YT Branch',     '15 2nd Ave',             'Whitehorse',    'YT', 'Y1A 1B2', 'CA'),
(37, 'Calgary AB Branch',        '400 3rd Ave SW',         'Calgary',       'AB', 'T2P 1J9', 'CA'),
(38, 'Quebec City QC Branch',    '900 Rene-Levesque Blvd', 'Quebec City',   'QC', 'G1R 1Z3', 'CA'),
(39, 'Victoria BC Branch',       '1 Government St',        'Victoria',      'BC', 'V8W 1M1', 'CA'),
(40, 'Saskatoon SK Branch',      '244 1st Ave N',          'Saskatoon',     'SK', 'S7K 1J5', 'CA');

-- ---------------------------------------------------------------------------
-- New customer base data: one row per new customer (1224-3230).
-- Uses same deterministic hashtext() pattern as the existing seed script.
-- ---------------------------------------------------------------------------
CREATE TEMP TABLE tmp_exp_cust AS
WITH
female_first AS (SELECT string_to_array(
    'Emma,Olivia,Ava,Isabella,Sophia,Charlotte,Mia,Amelia,Harper,Evelyn,'
    'Abigail,Emily,Elizabeth,Mila,Ella,Avery,Sofia,Camila,Aria,Scarlett,'
    'Victoria,Madison,Luna,Grace,Chloe,Penelope,Layla,Riley,Zoey,Nora,'
    'Lily,Eleanor,Hannah,Lillian,Addison,Aubrey,Ellie,Stella,Natalie,Zoe,'
    'Leah,Hazel,Violet,Aurora,Savannah,Audrey,Brooklyn,Bella,Claire,Skylar',
    ',') AS arr),
male_first AS (SELECT string_to_array(
    'Liam,Noah,Oliver,Elijah,William,James,Benjamin,Lucas,Henry,Alexander,'
    'Mason,Ethan,Daniel,Jacob,Logan,Jackson,Sebastian,Jack,Aiden,Owen,'
    'Samuel,Ryan,Nathan,Caleb,Isaiah,Christian,Hunter,Andrew,Dylan,Evan,'
    'Christopher,Josiah,Xavier,Julian,Carter,Luke,Jayden,Gabriel,Isaac,Leo,'
    'Lincoln,Jaxon,Levi,Anthony,Joshua,Kevin,Connor,Eli,Adrian,Wyatt',
    ',') AS arr),
last_names AS (SELECT string_to_array(
    'Smith,Johnson,Williams,Brown,Jones,Garcia,Miller,Davis,Rodriguez,Martinez,'
    'Hernandez,Lopez,Gonzalez,Wilson,Anderson,Thomas,Taylor,Moore,Jackson,Martin,'
    'Lee,Perez,Thompson,White,Harris,Sanchez,Clark,Ramirez,Lewis,Robinson,'
    'Walker,Young,Allen,King,Wright,Scott,Torres,Nguyen,Hill,Flores,'
    'Green,Adams,Nelson,Baker,Hall,Rivera,Campbell,Mitchell,Carter,Roberts,'
    'Turner,Collins,Stewart,Phillips,Evans,Morris,Murphy,Cook,Rogers,Peterson,'
    'Cooper,Reed,Bailey,Bell,Gomez,Kelly,Howard,Ward,Cox,Diaz,'
    'Richardson,Wood,Watson,Brooks,Bennett,Gray,James,Reyes,Cruz,Hughes,'
    'Price,Myers,Long,Foster,Sanders,Ross,Morales,Powell,Sullivan,Russell,'
    'Ortiz,Jenkins,Gutierrez,Perry,Butler,Barnes,Fisher,Henderson,Coleman,Simmons',
    ',') AS arr),
-- 7 options: index 6 (value = empty string) becomes NULL via NULLIF
prefixes AS (SELECT string_to_array('Mr.,Mrs.,Ms.,Dr.,Prof.,Mx.,', ',') AS arr),
street_names AS (SELECT string_to_array(
    'Oak,Maple,Cedar,Pine,Elm,Washington,Lincoln,Madison,Jefferson,Main,'
    'Lake,River,Hill,Valley,Park,Spring,Forest,Highland,Sunset,Broadway',
    ',') AS arr),
street_types AS (SELECT string_to_array('St,Ave,Blvd,Dr,Rd,Ln,Way,Ct,Pl,Ter', ',') AS arr),
us_locs(idx, state_prov, city, postal_code, branch_id) AS (VALUES
    ( 1,'OH','Columbus','43215',1),    ( 2,'IL','Chicago','60601',2),
    ( 3,'IL','Springfield','62704',3), ( 4,'CA','San Francisco','94105',4),
    ( 5,'WA','Seattle','98101',5),     ( 6,'TX','Austin','73301',6),
    ( 7,'CO','Denver','80202',7),      ( 8,'NY','New York','10003',8),
    ( 9,'AZ','Phoenix','85004',9),     (10,'CA','San Diego','92101',10),
    (11,'GA','Atlanta','30303',11),    (12,'MO','Kansas City','64105',12),
    (13,'MA','Boston','02101',13),     (14,'FL','Miami','33101',14),
    (15,'MN','Minneapolis','55401',15),(16,'OR','Portland','97201',16),
    (17,'NV','Las Vegas','89101',17),  (18,'TN','Nashville','37201',18),
    (19,'NC','Charlotte','28201',19),  (20,'MI','Detroit','48201',20),
    (21,'IN','Indianapolis','46201',21),(22,'TN','Memphis','38101',22),
    (23,'MD','Baltimore','21201',23),  (24,'OK','Oklahoma City','73101',24)),
ca_locs(idx, state_prov, city, postal_code, branch_id) AS (VALUES
    ( 1,'ON','Ottawa','K1N 5Y1',25),   ( 2,'QC','Montreal','H3B 1K9',26),
    ( 3,'ON','Toronto','M5H 1J9',27),  ( 4,'ON','Toronto','M5G 1L4',28),
    ( 5,'AB','Edmonton','T5J 3R7',29), ( 6,'BC','Vancouver','V6C 1S4',30),
    ( 7,'MB','Winnipeg','R3C 0B1',31), ( 8,'NS','Halifax','B3J 1Z4',32),
    ( 9,'NB','Fredericton','E3B 1B2',33),(10,'NL','St. Johns','A1C 1A1',34),
    (11,'SK','Regina','S4P 0S4',35),   (12,'YT','Whitehorse','Y1A 1B2',36),
    (13,'AB','Calgary','T2P 1J9',37),  (14,'QC','Quebec City','G1R 1Z3',38),
    (15,'BC','Victoria','V8W 1M1',39), (16,'SK','Saskatoon','S7K 1J5',40)),
base AS (
    SELECT
        1223 + gs                                                                      AS cust_id,
        CASE WHEN (hashtext((1223+gs)::text || 'g') & 2147483647) % 2 = 0 THEN 'F' ELSE 'M' END AS gender,
        (hashtext((1223+gs)::text || 'fn') & 2147483647) % 50 + 1                     AS fn_idx,
        (hashtext((1223+gs)::text || 'ln') & 2147483647) % 100 + 1                    AS ln_idx,
        (hashtext((1223+gs)::text || 'px') & 2147483647) % 7                           AS px_raw,
        DATE '1950-01-01' + ((hashtext((1223+gs)::text || 'bd') & 2147483647) % 18263) AS birthdate,
        CASE WHEN (hashtext((1223+gs)::text || 'co') & 2147483647) % 5 < 3 THEN 'US' ELSE 'CA' END AS country,
        (hashtext((1223+gs)::text || 'ul') & 2147483647) % 24 + 1                      AS us_idx,
        (hashtext((1223+gs)::text || 'cl') & 2147483647) % 16 + 1                      AS ca_idx,
        (hashtext((1223+gs)::text || 'sn') & 2147483647) % 20 + 1                      AS street_name_idx,
        (hashtext((1223+gs)::text || 'st') & 2147483647) % 10 + 1                      AS street_type_idx,
        ((hashtext((1223+gs)::text || 'hn') & 2147483647) % 9000) + 100                AS house_num
    FROM generate_series(1, 2007) gs
)
SELECT
    b.cust_id,
    b.birthdate,
    b.country,
    CASE WHEN b.gender = 'F' THEN (ff.arr)[b.fn_idx] ELSE (mf.arr)[b.fn_idx] END  AS first_name,
    (ln.arr)[b.ln_idx]                                                              AS last_name,
    NULLIF((px.arr)[b.px_raw + 1], '')                                              AS prefix,
    CASE WHEN b.country = 'US' THEN ul.state_prov ELSE cl.state_prov END           AS state_prov,
    CASE WHEN b.country = 'US' THEN ul.city       ELSE cl.city       END           AS city,
    CASE WHEN b.country = 'US' THEN ul.postal_code ELSE cl.postal_code END         AS postal_code,
    CASE WHEN b.country = 'US' THEN ul.branch_id   ELSE cl.branch_id  END          AS home_branch_id,
    b.house_num || ' ' || (sn.arr)[b.street_name_idx] || ' ' || (st.arr)[b.street_type_idx] AS address_line1
FROM base b
CROSS JOIN female_first ff
CROSS JOIN male_first   mf
CROSS JOIN last_names   ln
CROSS JOIN prefixes     px
CROSS JOIN street_names sn
CROSS JOIN street_types st
JOIN us_locs ul ON ul.idx = b.us_idx
JOIN ca_locs cl ON cl.idx = b.ca_idx;

-- Primary accounts for new customers (account_id = cust_id + 6000)
CREATE TEMP TABLE tmp_exp_accts_primary AS
WITH acct_type AS (
    SELECT
        nc.cust_id,
        nc.cust_id + 6000                                                               AS account_id,
        (ARRAY['Checking','Savings','Credit'])[(hashtext(nc.cust_id::text || 'at') & 2147483647) % 3 + 1] AS account_type,
        'Active'                                                                         AS account_status,
        DATE '2018-01-01' + ((hashtext(nc.cust_id::text || 'od') & 2147483647) % 2190)  AS open_date
    FROM tmp_exp_cust nc
)
SELECT
    cust_id,
    account_id,
    account_type,
    account_status,
    open_date,
    CASE account_type
        WHEN 'Checking' THEN ROUND((200  + (hashtext(cust_id::text || 'bal') & 2147483647) % 4800)::numeric, 2)
        WHEN 'Savings'  THEN ROUND((1000 + (hashtext(cust_id::text || 'bal') & 2147483647) % 19000)::numeric, 2)
        WHEN 'Credit'   THEN ROUND((-1   * ((hashtext(cust_id::text || 'bal') & 2147483647) % 5001))::numeric, 2)
    END  AS base_balance,
    CASE account_type
        WHEN 'Checking' THEN ROUND((0.01 + (hashtext(cust_id::text || 'ir') & 2147483647) % 15 / 100.0)::numeric, 2)
        WHEN 'Savings'  THEN ROUND((0.50 + (hashtext(cust_id::text || 'ir') & 2147483647) % 400 / 100.0)::numeric, 2)
        ELSE NULL
    END  AS interest_rate,
    CASE account_type
        WHEN 'Credit' THEN ROUND((1000 + (hashtext(cust_id::text || 'cl') & 2147483647) % 14001)::numeric, 2)
        ELSE NULL
    END  AS credit_limit,
    CASE account_type
        WHEN 'Credit' THEN ROUND((12.00 + (hashtext(cust_id::text || 'apr') & 2147483647) % 1800 / 100.0)::numeric, 2)
        ELSE NULL
    END  AS apr
FROM acct_type;

-- Secondary accounts (~30% of new customers; account_id = cust_id + 8200)
CREATE TEMP TABLE tmp_exp_accts_secondary AS
WITH typed AS (
    SELECT
        nc.cust_id,
        nc.cust_id + 8200  AS account_id,
        CASE a.account_type
            WHEN 'Checking' THEN (ARRAY['Savings','Credit'])[(hashtext(nc.cust_id::text || 'at2') & 2147483647) % 2 + 1]
            WHEN 'Savings'  THEN (ARRAY['Checking','Credit'])[(hashtext(nc.cust_id::text || 'at2') & 2147483647) % 2 + 1]
            WHEN 'Credit'   THEN (ARRAY['Checking','Savings'])[(hashtext(nc.cust_id::text || 'at2') & 2147483647) % 2 + 1]
        END  AS account_type,
        'Active'  AS account_status,
        DATE '2020-01-01' + ((hashtext(nc.cust_id::text || 'od2') & 2147483647) % 1461)  AS open_date
    FROM tmp_exp_cust nc
    JOIN tmp_exp_accts_primary a ON a.cust_id = nc.cust_id
    WHERE (hashtext(nc.cust_id::text || 'has2') & 2147483647) % 10 < 3
)
SELECT
    cust_id,
    account_id,
    account_type,
    account_status,
    open_date,
    CASE account_type
        WHEN 'Checking' THEN ROUND((200  + (hashtext(cust_id::text || 'b2') & 2147483647) % 4800)::numeric, 2)
        WHEN 'Savings'  THEN ROUND((1000 + (hashtext(cust_id::text || 'b2') & 2147483647) % 19000)::numeric, 2)
        WHEN 'Credit'   THEN ROUND((-1   * ((hashtext(cust_id::text || 'b2') & 2147483647) % 5001))::numeric, 2)
    END  AS base_balance,
    CASE account_type
        WHEN 'Checking' THEN ROUND((0.01 + (hashtext(cust_id::text || 'ir2') & 2147483647) % 15 / 100.0)::numeric, 2)
        WHEN 'Savings'  THEN ROUND((0.50 + (hashtext(cust_id::text || 'ir2') & 2147483647) % 400 / 100.0)::numeric, 2)
        ELSE NULL
    END  AS interest_rate,
    CASE account_type
        WHEN 'Credit' THEN ROUND((1000 + (hashtext(cust_id::text || 'cl2') & 2147483647) % 14001)::numeric, 2)
        ELSE NULL
    END  AS credit_limit,
    CASE account_type
        WHEN 'Credit' THEN ROUND((12.00 + (hashtext(cust_id::text || 'apr2') & 2147483647) % 1800 / 100.0)::numeric, 2)
        ELSE NULL
    END  AS apr
FROM typed;

-- All new expansion accounts combined
CREATE TEMP TABLE tmp_exp_accts AS
SELECT * FROM tmp_exp_accts_primary
UNION ALL
SELECT * FROM tmp_exp_accts_secondary;

-- Loan-eligible customers among the expansion set (~40%)
-- Loan IDs must continue after the existing max
CREATE TEMP TABLE tmp_exp_loan_customers AS
WITH eligible AS (
    SELECT cust_id, birthdate
    FROM tmp_exp_cust
    WHERE (hashtext(cust_id::text || 'loan') & 2147483647) % 10 < 4
),
existing_max AS (
    SELECT COALESCE(MAX(loan_id), 0) AS max_id FROM datalake.loan_accounts
)
SELECT
    (SELECT max_id FROM existing_max) + ROW_NUMBER() OVER (ORDER BY cust_id) AS loan_id,
    cust_id,
    (ARRAY['Mortgage','Auto','Personal','Student'])[(hashtext(cust_id::text || 'lt') & 2147483647) % 4 + 1] AS loan_type,
    ROUND((5000  + (hashtext(cust_id::text || 'lo') & 2147483647) % 295001)::numeric, 2) AS original_amount,
    ROUND((5000  + (hashtext(cust_id::text || 'lo') & 2147483647) % 295001)::numeric * 0.85, 2) AS current_balance,
    ROUND((2.50  + (hashtext(cust_id::text || 'lr') & 2147483647) % 1250 / 100.0)::numeric, 2)  AS interest_rate,
    DATE '2018-01-01' + ((hashtext(cust_id::text || 'lod') & 2147483647) % 2190)    AS origination_date,
    DATE '2025-01-01' + ((hashtext(cust_id::text || 'lmd') & 2147483647) % 3650)    AS maturity_date,
    CASE WHEN (hashtext(cust_id::text || 'ls') & 2147483647) % 20 = 0 THEN 'Delinquent' ELSE 'Active' END AS loan_status
FROM eligible;

-- Secondary segment assignments for new expansion customers
-- IDs start at 1000 + ROW_NUMBER to avoid collision with existing (max ~300)
CREATE TEMP TABLE tmp_exp_cust_secondary_segs AS
WITH secondary_eligible AS (
    SELECT
        nc.cust_id,
        CASE
            WHEN (hashtext(nc.cust_id::text || 'sr') & 2147483647) % 10 = 0 THEN 3  -- 10% RICH
            WHEN (hashtext(nc.cust_id::text || 'sp') & 2147483647) % 7  = 0 THEN 7  -- ~14% PREM
            WHEN nc.birthdate < DATE '1960-01-01'                          THEN 6  -- seniors -> SENR
            WHEN (hashtext(nc.cust_id::text || 'ss') & 2147483647) % 8  = 0 THEN 4  -- ~12% SMBIZ
            ELSE 8                                                                   -- rest -> INTL
        END  AS segment_id
    FROM tmp_exp_cust nc
    WHERE (hashtext(nc.cust_id::text || 'has_sec') & 2147483647) % 3 = 0
)
SELECT
    1000 + ROW_NUMBER() OVER (ORDER BY cust_id)  AS cs_id,
    cust_id,
    segment_id
FROM secondary_eligible;


-- ==============================================================================
-- SECTION 1: Segments — extend to Nov-Dec for all 8 segments
-- Source: Oct 31 snapshot (last day of existing data)
-- ==============================================================================

INSERT INTO datalake.segments (segment_id, segment_name, segment_code, as_of)
SELECT s.segment_id, s.segment_name, s.segment_code, d.as_of
FROM datalake.segments s
CROSS JOIN tmp_novdec_all d
WHERE s.as_of = '2024-10-31';

-- ==============================================================================
-- SECTION 2: Branches — extend to Nov-Dec
-- Source: Oct 31 snapshot (all 40 branches, same data each day)
-- ==============================================================================

INSERT INTO datalake.branches (branch_id, branch_name, address_line1, city, state_province, postal_code, country, as_of)
SELECT b.branch_id, b.branch_name, b.address_line1, b.city, b.state_province, b.postal_code, b.country, d.as_of
FROM datalake.branches b
CROSS JOIN tmp_novdec_all d
WHERE b.as_of = '2024-10-31';

-- ==============================================================================
-- SECTION 3: New customers (1224-3230) — all Q4 weekdays (Oct-Dec)
-- customers is a weekday full-load table
-- ==============================================================================

INSERT INTO datalake.customers (id, prefix, first_name, last_name, sort_name, birthdate, as_of)
SELECT
    nc.cust_id,
    nc.prefix,
    nc.first_name,
    nc.last_name,
    nc.last_name || ' ' || nc.first_name  AS sort_name,
    nc.birthdate,
    d.as_of
FROM tmp_exp_cust nc
CROSS JOIN tmp_q4_wd d;

-- ==============================================================================
-- SECTION 4: Extend existing customers (1001-1223) to Nov-Dec weekdays
-- Source: Oct 31 snapshot (Thursday = weekday, so it exists)
-- Guard: id <= 1223 ensures we do not re-extend new customers from Section 3
-- ==============================================================================

INSERT INTO datalake.customers (id, prefix, first_name, last_name, sort_name, suffix, birthdate, as_of)
SELECT c.id, c.prefix, c.first_name, c.last_name, c.sort_name, c.suffix, c.birthdate, d.as_of
FROM datalake.customers c
CROSS JOIN tmp_novdec_wd d
WHERE c.as_of = '2024-10-31'
  AND c.id <= 1223;

-- ==============================================================================
-- SECTION 5: New customer accounts (1224-3230) — all Q4 weekdays
-- Primary: account_id = cust_id + 6000; Secondary: account_id = cust_id + 8200
-- Balance drifts per (account_id, as_of) hash
-- ==============================================================================

INSERT INTO datalake.accounts (account_id, customer_id, account_type, account_status, open_date,
                               current_balance, interest_rate, credit_limit, apr, as_of)
SELECT
    a.account_id,
    a.cust_id,
    a.account_type,
    a.account_status,
    a.open_date,
    GREATEST(
        CASE WHEN a.account_type = 'Credit' THEN -50000.00 ELSE 0.01 END,
        ROUND(a.base_balance
            + ((hashtext(a.account_id::text || d.as_of::text || 'b') & 2147483647) % 201 - 100)::numeric,
            2)
    )  AS current_balance,
    a.interest_rate,
    a.credit_limit,
    a.apr,
    d.as_of
FROM tmp_exp_accts a
CROSS JOIN tmp_q4_wd d;

-- ==============================================================================
-- SECTION 6: Extend existing accounts (1001-1223 customers) to Nov-Dec weekdays
-- This covers account_ids in ranges: 3001-3223 (primary) and 5224-5423 (secondary)
-- from the October seed, plus the original 23 customer accounts.
-- Guard: account_id <= 5423 ensures we do not re-extend new expansion accounts (7224+)
-- Source: Oct 31 snapshot
-- ==============================================================================

INSERT INTO datalake.accounts (account_id, customer_id, account_type, account_status, open_date,
                               current_balance, interest_rate, credit_limit, apr, as_of)
SELECT
    a.account_id,
    a.customer_id,
    a.account_type,
    a.account_status,
    a.open_date,
    GREATEST(
        CASE WHEN a.account_type = 'Credit' THEN -50000.00 ELSE 0.01 END,
        ROUND(a.current_balance
            + ((hashtext(a.account_id::text || d.as_of::text || 'b') & 2147483647) % 201 - 100)::numeric,
            2)
    )  AS current_balance,
    a.interest_rate,
    a.credit_limit,
    a.apr,
    d.as_of
FROM datalake.accounts a
CROSS JOIN tmp_novdec_wd d
WHERE a.as_of = '2024-10-31'
  AND a.account_id <= 5423;

-- ==============================================================================
-- SECTION 7: New customer addresses (1224-3230) — all Q4 calendar days
-- address_id = 3000 + (cust_id - 1223) -> 3001 for customer 1224, 5007 for 3230
-- ==============================================================================

INSERT INTO datalake.addresses (address_id, customer_id, address_line1, city, state_province, postal_code,
                                country, start_date, end_date, as_of)
SELECT
    3000 + (nc.cust_id - 1223)                                                   AS address_id,
    nc.cust_id,
    nc.address_line1,
    nc.city,
    nc.state_prov,
    nc.postal_code,
    nc.country,
    DATE '2019-01-01' + ((hashtext(nc.cust_id::text || 'asd') & 2147483647) % 2100) AS start_date,
    NULL                                                                            AS end_date,
    d.as_of
FROM tmp_exp_cust nc
CROSS JOIN tmp_q4_all d;

-- ==============================================================================
-- SECTION 8: Extend existing addresses (customer_id 1001-1223) to Nov-Dec
-- Source: Oct 31 snapshot
-- Guard: customer_id <= 1223 ensures we do not re-extend new addresses from Section 7
-- ==============================================================================

INSERT INTO datalake.addresses (address_id, customer_id, address_line1, city, state_province, postal_code,
                                country, start_date, end_date, as_of)
SELECT a.address_id, a.customer_id, a.address_line1, a.city, a.state_province, a.postal_code,
       a.country, a.start_date, a.end_date, d.as_of
FROM datalake.addresses a
CROSS JOIN tmp_novdec_all d
WHERE a.as_of = '2024-10-31'
  AND a.customer_id <= 1223;

-- ==============================================================================
-- SECTION 9: New customers_segments (1224-3230) — all Q4 calendar days
-- Primary IDs: cust_id + 2000 (3224..5230), safely above existing max (~302).
--   Cannot reuse the original cust_id - 988 formula because that gives 236..2242
--   which collides with existing secondary segment assignments starting at 236.
-- Secondary IDs: 1001 + ROW_NUMBER via tmp_exp_cust_secondary_segs.
-- ==============================================================================

-- Primary (every new customer gets their regional segment)
INSERT INTO datalake.customers_segments (id, customer_id, segment_id, as_of)
SELECT
    nc.cust_id + 2000  AS id,
    nc.cust_id,
    CASE WHEN nc.country = 'US' THEN 1 ELSE 2 END  AS segment_id,
    d.as_of
FROM tmp_exp_cust nc
CROSS JOIN tmp_q4_all d;

-- Secondary (~33% of new customers get one additional segment)
INSERT INTO datalake.customers_segments (id, customer_id, segment_id, as_of)
SELECT ss.cs_id, ss.cust_id, ss.segment_id, d.as_of
FROM tmp_exp_cust_secondary_segs ss
CROSS JOIN tmp_q4_all d;

-- ==============================================================================
-- SECTION 10: Extend existing customers_segments (customer_id 1001-1223) to Nov-Dec
-- Source: Oct 31 snapshot
-- Guard: customer_id <= 1223 ensures we do not re-extend new assignments from Section 9
-- ==============================================================================

INSERT INTO datalake.customers_segments (id, customer_id, segment_id, as_of)
SELECT cs.id, cs.customer_id, cs.segment_id, d.as_of
FROM datalake.customers_segments cs
CROSS JOIN tmp_novdec_all d
WHERE cs.as_of = '2024-10-31'
  AND cs.customer_id <= 1223;

-- ==============================================================================
-- SECTION 11: Phone numbers — new customers (1224-3230), all Q4 calendar days
-- phone_id formula: (cust_id - 1001) * 3 + slot
-- For cust_id 1224: (1224-1001)*3+1 = 670. No collision with existing max 669.
-- Mobile: all customers; Home: ~60%; Work: ~30%
-- ==============================================================================

-- Mobile (all 2,007 new customers, all Q4 days)
INSERT INTO datalake.phone_numbers (phone_id, customer_id, phone_number, phone_type, as_of)
SELECT
    (nc.cust_id - 1001) * 3 + 1  AS phone_id,
    nc.cust_id,
    '(' || LPAD(((hashtext(nc.cust_id::text || 'pac') & 2147483647) % 900 + 100)::text, 3, '0')
        || ') 555-'
        || LPAD(((hashtext(nc.cust_id::text || 'pln') & 2147483647) % 10000)::text, 4, '0')  AS phone_number,
    'Mobile'  AS phone_type,
    d.as_of
FROM tmp_exp_cust nc
CROSS JOIN tmp_q4_all d;

-- Home (~60% of new customers)
INSERT INTO datalake.phone_numbers (phone_id, customer_id, phone_number, phone_type, as_of)
SELECT
    (nc.cust_id - 1001) * 3 + 2  AS phone_id,
    nc.cust_id,
    '(' || LPAD(((hashtext(nc.cust_id::text || 'hac') & 2147483647) % 900 + 100)::text, 3, '0')
        || ') 555-'
        || LPAD(((hashtext(nc.cust_id::text || 'hln') & 2147483647) % 10000)::text, 4, '0')  AS phone_number,
    'Home'  AS phone_type,
    d.as_of
FROM tmp_exp_cust nc
CROSS JOIN tmp_q4_all d
WHERE (hashtext(nc.cust_id::text || 'hhas') & 2147483647) % 10 < 6;

-- Work (~30% of new customers)
INSERT INTO datalake.phone_numbers (phone_id, customer_id, phone_number, phone_type, as_of)
SELECT
    (nc.cust_id - 1001) * 3 + 3  AS phone_id,
    nc.cust_id,
    '(' || LPAD(((hashtext(nc.cust_id::text || 'wac') & 2147483647) % 900 + 100)::text, 3, '0')
        || ') 555-'
        || LPAD(((hashtext(nc.cust_id::text || 'wln') & 2147483647) % 10000)::text, 4, '0')  AS phone_number,
    'Work'  AS phone_type,
    d.as_of
FROM tmp_exp_cust nc
CROSS JOIN tmp_q4_all d
WHERE (hashtext(nc.cust_id::text || 'whas') & 2147483647) % 10 < 3;

-- ==============================================================================
-- SECTION 12: Phone numbers — extend existing customers (1001-1223) to Nov-Dec
-- Source: Oct 31 snapshot
-- Guard: customer_id <= 1223 ensures we do not re-extend new phones from Section 11
-- ==============================================================================

INSERT INTO datalake.phone_numbers (phone_id, customer_id, phone_number, phone_type, as_of)
SELECT p.phone_id, p.customer_id, p.phone_number, p.phone_type, d.as_of
FROM datalake.phone_numbers p
CROSS JOIN tmp_novdec_all d
WHERE p.as_of = '2024-10-31'
  AND p.customer_id <= 1223;

-- ==============================================================================
-- SECTION 13: Email addresses — new customers (1224-3230), all Q4 calendar days
-- email_id formula: (cust_id - 1001) * 2 + slot
-- For cust_id 1224: (1224-1001)*2+1 = 447. No collision with existing max 446.
-- Personal: all customers; Work: ~40%
-- ==============================================================================

-- Personal (all 2,007 new customers)
INSERT INTO datalake.email_addresses (email_id, customer_id, email_address, email_type, as_of)
SELECT
    (nc.cust_id - 1001) * 2 + 1  AS email_id,
    nc.cust_id,
    lower(nc.first_name) || '.'
        || lower(nc.last_name)
        || ((hashtext(nc.cust_id::text || 'enum') & 2147483647) % 100)::text
        || '@'
        || (ARRAY['gmail.com','yahoo.com','outlook.com','hotmail.com','icloud.com'])[
                (hashtext(nc.cust_id::text || 'edom') & 2147483647) % 5 + 1]  AS email_address,
    'Personal'  AS email_type,
    d.as_of
FROM tmp_exp_cust nc
CROSS JOIN tmp_q4_all d;

-- Work (~40% of new customers)
INSERT INTO datalake.email_addresses (email_id, customer_id, email_address, email_type, as_of)
SELECT
    (nc.cust_id - 1001) * 2 + 2  AS email_id,
    nc.cust_id,
    lower(nc.first_name) || '.'
        || lower(nc.last_name)
        || '@'
        || (ARRAY['company.com','corp.net','business.org','enterprise.com','work.net'])[
                (hashtext(nc.cust_id::text || 'wedom') & 2147483647) % 5 + 1]  AS email_address,
    'Work'  AS email_type,
    d.as_of
FROM tmp_exp_cust nc
CROSS JOIN tmp_q4_all d
WHERE (hashtext(nc.cust_id::text || 'wehas') & 2147483647) % 10 < 4;

-- ==============================================================================
-- SECTION 14: Email addresses — extend existing customers (1001-1223) to Nov-Dec
-- Source: Oct 31 snapshot
-- Guard: customer_id <= 1223 ensures we do not re-extend new emails from Section 13
-- ==============================================================================

INSERT INTO datalake.email_addresses (email_id, customer_id, email_address, email_type, as_of)
SELECT e.email_id, e.customer_id, e.email_address, e.email_type, d.as_of
FROM datalake.email_addresses e
CROSS JOIN tmp_novdec_all d
WHERE e.as_of = '2024-10-31'
  AND e.customer_id <= 1223;

-- ==============================================================================
-- SECTION 15: Credit scores — new customers (1224-3230), all Q4 weekdays
-- credit_score_id formula: (cust_id - 1001) * 3 + slot
-- For cust_id 1224: (1224-1001)*3+1 = 670. No collision with existing max 669.
-- Base score per (customer, bureau) varies by +/-20; drifts +/-2 per weekday
-- ==============================================================================

INSERT INTO datalake.credit_scores (credit_score_id, customer_id, bureau, score, as_of)
SELECT
    (nc.cust_id - 1001) * 3 + b.slot  AS credit_score_id,
    nc.cust_id,
    b.bureau,
    GREATEST(300, LEAST(850,
        500 + (hashtext(nc.cust_id::text || 'bs') & 2147483647) % 350
        + (hashtext(nc.cust_id::text || b.bureau || 'bv') & 2147483647) % 41 - 20
        + (d.as_of - DATE '2024-10-01')
          * ((hashtext(nc.cust_id::text || b.bureau || d.as_of::text || 'dd') & 2147483647) % 5 - 2)
    ))::integer  AS score,
    d.as_of
FROM tmp_exp_cust nc
CROSS JOIN (VALUES (1,'Equifax'), (2,'TransUnion'), (3,'Experian')) AS b(slot, bureau)
CROSS JOIN tmp_q4_wd d;

-- ==============================================================================
-- SECTION 16: Credit scores — extend existing customers (1001-1223) to Nov-Dec weekdays
-- Source: recomputed from scratch using the same formula (not snapshot-based,
-- because the drift formula uses days-since-Oct-1 which works for any date)
-- Guard: id <= 1223 via the subquery restricting to existing customer IDs
-- ==============================================================================

INSERT INTO datalake.credit_scores (credit_score_id, customer_id, bureau, score, as_of)
SELECT
    (c.cust_id - 1001) * 3 + b.slot  AS credit_score_id,
    c.cust_id,
    b.bureau,
    GREATEST(300, LEAST(850,
        500 + (hashtext(c.cust_id::text || 'bs') & 2147483647) % 350
        + (hashtext(c.cust_id::text || b.bureau || 'bv') & 2147483647) % 41 - 20
        + (d.as_of - DATE '2024-10-01')
          * ((hashtext(c.cust_id::text || b.bureau || d.as_of::text || 'dd') & 2147483647) % 5 - 2)
    ))::integer  AS score,
    d.as_of
FROM (
    SELECT DISTINCT id AS cust_id FROM datalake.customers WHERE id <= 1223
) c
CROSS JOIN (VALUES (1,'Equifax'), (2,'TransUnion'), (3,'Experian')) AS b(slot, bureau)
CROSS JOIN tmp_novdec_wd d;

-- ==============================================================================
-- SECTION 17: Loan accounts — new expansion customers, all Q4 weekdays
-- Balance decreases slightly each weekday to simulate principal paydown
-- ==============================================================================

INSERT INTO datalake.loan_accounts (loan_id, customer_id, loan_type, original_amount, current_balance,
                                    interest_rate, origination_date, maturity_date, loan_status, as_of)
SELECT
    lc.loan_id,
    lc.cust_id,
    lc.loan_type,
    lc.original_amount,
    GREATEST(0.00,
        ROUND(lc.current_balance
            - (d.as_of - DATE '2024-10-01')
              * ROUND((lc.current_balance * 0.0003
                  + (hashtext(lc.loan_id::text || d.as_of::text || 'pd') & 2147483647) % 50)::numeric, 2),
        2)
    )  AS current_balance,
    lc.interest_rate,
    lc.origination_date,
    lc.maturity_date,
    lc.loan_status,
    d.as_of
FROM tmp_exp_loan_customers lc
CROSS JOIN tmp_q4_wd d;

-- ==============================================================================
-- SECTION 18: Loan accounts — extend existing customers (1001-1223) to Nov-Dec
-- Uses the original tmp_loan_customers pattern but rebuilds the base data from
-- the existing loan_accounts table. Source: Oct 31 snapshot for static fields,
-- then recompute current_balance using the same drift formula.
-- Guard: customer_id <= 1223
-- ==============================================================================

INSERT INTO datalake.loan_accounts (loan_id, customer_id, loan_type, original_amount, current_balance,
                                    interest_rate, origination_date, maturity_date, loan_status, as_of)
WITH existing_loans AS (
    SELECT loan_id, customer_id, loan_type, original_amount, current_balance,
           interest_rate, origination_date, maturity_date, loan_status
    FROM datalake.loan_accounts
    WHERE as_of = '2024-10-31'
      AND customer_id <= 1223
)
SELECT
    el.loan_id,
    el.customer_id,
    el.loan_type,
    el.original_amount,
    GREATEST(0.00,
        ROUND(el.current_balance
            - (d.as_of - DATE '2024-10-31')
              * ROUND((el.current_balance * 0.0003
                  + (hashtext(el.loan_id::text || d.as_of::text || 'pd') & 2147483647) % 50)::numeric, 2),
        2)
    )  AS current_balance,
    el.interest_rate,
    el.origination_date,
    el.maturity_date,
    el.loan_status,
    d.as_of
FROM existing_loans el
CROSS JOIN tmp_novdec_wd d;

-- ==============================================================================
-- SECTION 19: Transactions — new expansion customer accounts, all Q4 calendar days
-- ~50% of accounts active per day, 1-3 txns per active account
-- Transaction IDs continue after existing max
-- ==============================================================================

INSERT INTO datalake.transactions (transaction_id, account_id, txn_timestamp, txn_type, amount, description, as_of)
WITH max_txn AS (SELECT COALESCE(MAX(transaction_id), 0) AS max_id FROM datalake.transactions),
candidate_txns AS (
    SELECT a.account_id, d.as_of, t.slot
    FROM tmp_exp_accts a
    CROSS JOIN tmp_q4_all d
    CROSS JOIN (SELECT generate_series(1, 3) AS slot) t
    WHERE (hashtext(a.account_id::text || d.as_of::text || t.slot::text || 'nte') & 2147483647) % 10 < 5
),
numbered AS (
    SELECT *, ROW_NUMBER() OVER (ORDER BY as_of, account_id, slot) AS rn
    FROM candidate_txns
)
SELECT
    (SELECT max_id FROM max_txn) + rn  AS transaction_id,
    account_id,
    as_of::timestamp
        + (((hashtext(account_id::text || as_of::text || slot::text || 'nts') & 2147483647) % 28800)
           || ' seconds')::interval  AS txn_timestamp,
    CASE WHEN (hashtext(account_id::text || as_of::text || slot::text || 'ntt') & 2147483647) % 3 = 0
         THEN 'Credit' ELSE 'Debit' END  AS txn_type,
    ROUND((20 + (hashtext(account_id::text || as_of::text || slot::text || 'nta') & 2147483647) % 1781)::numeric, 2) AS amount,
    (ARRAY['Purchase','Payment','Transfer','ATM Withdrawal','Direct Deposit','Bill Pay','Refund'])[
        (hashtext(account_id::text || as_of::text || slot::text || 'ntd') & 2147483647) % 7 + 1]  AS description,
    as_of
FROM numbered;

-- ==============================================================================
-- SECTION 20: Transactions — extend existing accounts (customers 1001-1223) to Nov-Dec
-- ~50% of accounts active per day, 1-3 txns per active account
-- Transaction IDs continue after the max from Section 19
-- Guard: account_id <= 5423 ensures we only extend pre-existing accounts
-- ==============================================================================

INSERT INTO datalake.transactions (transaction_id, account_id, txn_timestamp, txn_type, amount, description, as_of)
WITH max_txn AS (SELECT COALESCE(MAX(transaction_id), 0) AS max_id FROM datalake.transactions),
candidate_txns AS (
    SELECT a.account_id, d.as_of, t.slot
    FROM datalake.accounts a
    CROSS JOIN tmp_novdec_all d
    CROSS JOIN (SELECT generate_series(1, 3) AS slot) t
    WHERE a.as_of = '2024-10-31'
      AND a.account_id <= 5423
      AND (hashtext(a.account_id::text || d.as_of::text || t.slot::text || 'te') & 2147483647) % 10 < 5
),
numbered AS (
    SELECT *, ROW_NUMBER() OVER (ORDER BY as_of, account_id, slot) AS rn
    FROM candidate_txns
)
SELECT
    (SELECT max_id FROM max_txn) + rn  AS transaction_id,
    account_id,
    as_of::timestamp
        + (((hashtext(account_id::text || as_of::text || slot::text || 'ts') & 2147483647) % 28800)
           || ' seconds')::interval  AS txn_timestamp,
    CASE WHEN (hashtext(account_id::text || as_of::text || slot::text || 'tt') & 2147483647) % 3 = 0
         THEN 'Credit' ELSE 'Debit' END  AS txn_type,
    ROUND((20 + (hashtext(account_id::text || as_of::text || slot::text || 'ta') & 2147483647) % 1781)::numeric, 2) AS amount,
    (ARRAY['Purchase','Payment','Transfer','ATM Withdrawal','Direct Deposit','Bill Pay','Refund'])[
        (hashtext(account_id::text || as_of::text || slot::text || 'td') & 2147483647) % 7 + 1]  AS description,
    as_of
FROM numbered;

-- ==============================================================================
-- SECTION 21: Branch visits — new customers (1224-3230), all Q4 calendar days
-- ~10% daily visit rate, sequential visit_ids starting after existing max
-- ==============================================================================

INSERT INTO datalake.branch_visits (visit_id, customer_id, branch_id, visit_timestamp, visit_purpose, as_of)
WITH max_visit AS (SELECT COALESCE(MAX(visit_id), 0) AS max_id FROM datalake.branch_visits),
candidate_visits AS (
    SELECT nc.cust_id AS customer_id, nc.home_branch_id AS branch_id, d.as_of
    FROM tmp_exp_cust nc
    CROSS JOIN tmp_q4_all d
    WHERE (hashtext(nc.cust_id::text || d.as_of::text || 'bv') & 2147483647) % 10 = 0
),
numbered AS (
    SELECT *, ROW_NUMBER() OVER (ORDER BY as_of, customer_id) AS rn
    FROM candidate_visits
)
SELECT
    (SELECT max_id FROM max_visit) + rn  AS visit_id,
    customer_id,
    branch_id,
    as_of::timestamp
        + (32400 + ((hashtext(customer_id::text || as_of::text || 'vt') & 2147483647) % 28800)
           || ' seconds')::interval  AS visit_timestamp,
    (ARRAY['Deposit','Withdrawal','Account Opening','Inquiry','Loan Application'])[
        (hashtext(customer_id::text || as_of::text || 'vp') & 2147483647) % 5 + 1]  AS visit_purpose,
    as_of
FROM numbered;

-- ==============================================================================
-- SECTION 22: Branch visits — extend existing customers (1001-1223) to Nov-Dec
-- ~10% daily visit rate, visit_ids continue after Section 21
-- Guard: customer_id <= 1223
-- ==============================================================================

INSERT INTO datalake.branch_visits (visit_id, customer_id, branch_id, visit_timestamp, visit_purpose, as_of)
WITH max_visit AS (SELECT COALESCE(MAX(visit_id), 0) AS max_id FROM datalake.branch_visits),
existing_cust_branches AS (
    SELECT DISTINCT ON (a.customer_id) a.customer_id, b.branch_id
    FROM datalake.addresses a
    JOIN tmp_branches b ON b.postal_code = a.postal_code
    WHERE a.as_of = '2024-10-31'
      AND a.customer_id <= 1223
    ORDER BY a.customer_id, a.start_date DESC
),
candidate_visits AS (
    SELECT cb.customer_id, cb.branch_id, d.as_of
    FROM existing_cust_branches cb
    CROSS JOIN tmp_novdec_all d
    WHERE (hashtext(cb.customer_id::text || d.as_of::text || 'bv') & 2147483647) % 10 = 0
),
numbered AS (
    SELECT *, ROW_NUMBER() OVER (ORDER BY as_of, customer_id) AS rn
    FROM candidate_visits
)
SELECT
    (SELECT max_id FROM max_visit) + rn  AS visit_id,
    customer_id,
    branch_id,
    as_of::timestamp
        + (32400 + ((hashtext(customer_id::text || as_of::text || 'vt') & 2147483647) % 28800)
           || ' seconds')::interval  AS visit_timestamp,
    (ARRAY['Deposit','Withdrawal','Account Opening','Inquiry','Loan Application'])[
        (hashtext(customer_id::text || as_of::text || 'vp') & 2147483647) % 5 + 1]  AS visit_purpose,
    as_of
FROM numbered;

COMMIT;
