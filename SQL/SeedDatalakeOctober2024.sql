-- ==============================================================================
-- SeedDatalakeOctober2024.sql
-- Expands the datalake with full October 2024 data.
--
-- Prerequisite: Run CreateNewDataLakeTables.sql first.
-- Existing data covers Oct 1-7 for customers 1001-1023.
-- This script adds:
--   - 5 new segments (IDs 4-8)
--   - 40 branches (one per postal code, all 31 days)
--   - 200 new customers (IDs 1024-1223) for all of October
--   - Existing customers extended to Oct 31
--   - Phone numbers, email addresses, credit scores, loan accounts (all customers)
--   - Branch visits (transactional)
--   - Transactions extended to Oct 31
--
-- NOTE: "extend" sections always include AND id <= 1023 (or account_id <= 3023 etc.)
-- so that they operate only on pre-existing records and do not re-process rows
-- inserted earlier in this same script.
-- ==============================================================================

BEGIN;

-- ==============================================================================
-- STEP 0: Temporary reference tables reused throughout this script
-- ==============================================================================

-- All October 2024 weekdays
CREATE TEMP TABLE tmp_oct_wd AS
SELECT d::date AS as_of
FROM generate_series('2024-10-01'::date, '2024-10-31'::date, '1 day'::interval) d
WHERE EXTRACT(DOW FROM d) NOT IN (0, 6);

-- All October 2024 calendar days
CREATE TEMP TABLE tmp_oct_all AS
SELECT d::date AS as_of
FROM generate_series('2024-10-01'::date, '2024-10-31'::date, '1 day'::interval) d;

-- New October dates only (Oct 8-31) for extending existing records
CREATE TEMP TABLE tmp_new_wd AS
SELECT d::date AS as_of
FROM generate_series('2024-10-08'::date, '2024-10-31'::date, '1 day'::interval) d
WHERE EXTRACT(DOW FROM d) NOT IN (0, 6);

CREATE TEMP TABLE tmp_new_all AS
SELECT d::date AS as_of
FROM generate_series('2024-10-08'::date, '2024-10-31'::date, '1 day'::interval) d;

-- All 40 branches (one per postal code across US and Canadian locations)
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
-- US locations (existing postal codes from customers 1001-1023)
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
-- US locations (new postal codes introduced by customers 1024-1223)
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
-- Canadian locations (existing postal codes from customers 1001-1023)
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
-- Canadian locations (new postal codes introduced by customers 1024-1223)
(37, 'Calgary AB Branch',        '400 3rd Ave SW',         'Calgary',       'AB', 'T2P 1J9', 'CA'),
(38, 'Quebec City QC Branch',    '900 Rene-Levesque Blvd', 'Quebec City',   'QC', 'G1R 1Z3', 'CA'),
(39, 'Victoria BC Branch',       '1 Government St',        'Victoria',      'BC', 'V8W 1M1', 'CA'),
(40, 'Saskatoon SK Branch',      '244 1st Ave N',          'Saskatoon',     'SK', 'S7K 1J5', 'CA');

-- New customer base data: one row per new customer (1024-1223).
-- Names, birthdate, and location are derived deterministically from customer_id.
-- Uses (hashtext(x) & 2147483647) instead of abs(hashtext(x)) to avoid INT overflow.
CREATE TEMP TABLE tmp_new_cust AS
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
        1023 + gs                                                                      AS cust_id,
        CASE WHEN (hashtext((1023+gs)::text || 'g') & 2147483647) % 2 = 0 THEN 'F' ELSE 'M' END AS gender,
        (hashtext((1023+gs)::text || 'fn') & 2147483647) % 50 + 1                     AS fn_idx,
        (hashtext((1023+gs)::text || 'ln') & 2147483647) % 100 + 1                    AS ln_idx,
        (hashtext((1023+gs)::text || 'px') & 2147483647) % 7                           AS px_raw,
        DATE '1950-01-01' + ((hashtext((1023+gs)::text || 'bd') & 2147483647) % 18263) AS birthdate,
        CASE WHEN (hashtext((1023+gs)::text || 'co') & 2147483647) % 5 < 3 THEN 'US' ELSE 'CA' END AS country,
        (hashtext((1023+gs)::text || 'ul') & 2147483647) % 24 + 1                      AS us_idx,
        (hashtext((1023+gs)::text || 'cl') & 2147483647) % 16 + 1                      AS ca_idx,
        (hashtext((1023+gs)::text || 'sn') & 2147483647) % 20 + 1                      AS street_name_idx,
        (hashtext((1023+gs)::text || 'st') & 2147483647) % 10 + 1                      AS street_type_idx,
        ((hashtext((1023+gs)::text || 'hn') & 2147483647) % 9000) + 100                AS house_num
    FROM generate_series(1, 200) gs
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

-- Primary accounts for new customers (account_id = cust_id + 2000)
CREATE TEMP TABLE tmp_new_accts_primary AS
WITH acct_type AS (
    SELECT
        nc.cust_id,
        nc.cust_id + 2000                                                               AS account_id,
        (ARRAY['Checking','Savings','Credit'])[(hashtext(nc.cust_id::text || 'at') & 2147483647) % 3 + 1] AS account_type,
        'Active'                                                                         AS account_status,
        DATE '2018-01-01' + ((hashtext(nc.cust_id::text || 'od') & 2147483647) % 2190)  AS open_date
    FROM tmp_new_cust nc
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

-- Secondary accounts (~30% of new customers; account_id = cust_id + 4200)
-- Two-step CTE: first resolve type, then compute type-dependent fields.
CREATE TEMP TABLE tmp_new_accts_secondary AS
WITH typed AS (
    SELECT
        nc.cust_id,
        nc.cust_id + 4200  AS account_id,
        CASE a.account_type
            WHEN 'Checking' THEN (ARRAY['Savings','Credit'])[(hashtext(nc.cust_id::text || 'at2') & 2147483647) % 2 + 1]
            WHEN 'Savings'  THEN (ARRAY['Checking','Credit'])[(hashtext(nc.cust_id::text || 'at2') & 2147483647) % 2 + 1]
            WHEN 'Credit'   THEN (ARRAY['Checking','Savings'])[(hashtext(nc.cust_id::text || 'at2') & 2147483647) % 2 + 1]
        END  AS account_type,
        'Active'  AS account_status,
        DATE '2020-01-01' + ((hashtext(nc.cust_id::text || 'od2') & 2147483647) % 1461)  AS open_date
    FROM tmp_new_cust nc
    JOIN tmp_new_accts_primary a ON a.cust_id = nc.cust_id
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

-- All new accounts combined
CREATE TEMP TABLE tmp_new_accts AS
SELECT * FROM tmp_new_accts_primary
UNION ALL
SELECT * FROM tmp_new_accts_secondary;

-- Customers eligible for loan accounts (~40% of all 223 customers)
CREATE TEMP TABLE tmp_loan_customers AS
WITH all_custs AS (
    SELECT id AS cust_id, birthdate FROM datalake.customers WHERE as_of = '2024-10-01'
    UNION ALL
    SELECT cust_id, birthdate FROM tmp_new_cust
),
eligible AS (
    SELECT cust_id, birthdate
    FROM all_custs
    WHERE (hashtext(cust_id::text || 'loan') & 2147483647) % 10 < 4
)
SELECT
    ROW_NUMBER() OVER (ORDER BY cust_id)                                              AS loan_id,
    cust_id,
    (ARRAY['Mortgage','Auto','Personal','Student'])[(hashtext(cust_id::text || 'lt') & 2147483647) % 4 + 1] AS loan_type,
    ROUND((5000  + (hashtext(cust_id::text || 'lo') & 2147483647) % 295001)::numeric, 2) AS original_amount,
    ROUND((5000  + (hashtext(cust_id::text || 'lo') & 2147483647) % 295001)::numeric * 0.85, 2) AS current_balance,
    ROUND((2.50  + (hashtext(cust_id::text || 'lr') & 2147483647) % 1250 / 100.0)::numeric, 2)  AS interest_rate,
    DATE '2018-01-01' + ((hashtext(cust_id::text || 'lod') & 2147483647) % 2190)    AS origination_date,
    DATE '2025-01-01' + ((hashtext(cust_id::text || 'lmd') & 2147483647) % 3650)    AS maturity_date,
    CASE WHEN (hashtext(cust_id::text || 'ls') & 2147483647) % 20 = 0 THEN 'Delinquent' ELSE 'Active' END AS loan_status
FROM eligible;

-- Secondary segment assignments for new customers (computed once, then cross-joined with dates)
-- IDs start at 236 (primary assignments use cust_id - 988 = 36..235)
CREATE TEMP TABLE tmp_new_cust_secondary_segs AS
WITH secondary_eligible AS (
    SELECT
        nc.cust_id,
        CASE
            WHEN (hashtext(nc.cust_id::text || 'sr') & 2147483647) % 10 = 0 THEN 3  -- 10% RICH
            WHEN (hashtext(nc.cust_id::text || 'sp') & 2147483647) % 7  = 0 THEN 7  -- ~14% PREM
            WHEN nc.birthdate < DATE '1960-01-01'                          THEN 6  -- seniors → SENR
            WHEN (hashtext(nc.cust_id::text || 'ss') & 2147483647) % 8  = 0 THEN 4  -- ~12% SMBIZ
            ELSE 8                                                                   -- rest → INTL
        END  AS segment_id
    FROM tmp_new_cust nc
    WHERE (hashtext(nc.cust_id::text || 'has_sec') & 2147483647) % 3 = 0
)
SELECT
    235 + ROW_NUMBER() OVER (ORDER BY cust_id)  AS cs_id,
    cust_id,
    segment_id
FROM secondary_eligible;

-- ==============================================================================
-- SECTION 1: New segments (IDs 4-8) for all 31 days of October
-- ==============================================================================

INSERT INTO datalake.segments (segment_id, segment_name, segment_code, as_of)
SELECT s.segment_id, s.segment_name, s.segment_code, d.as_of
FROM (VALUES
    (4, 'Small business banking', 'SMBIZ'),
    (5, 'Student banking',        'STUD'),
    (6, 'Senior banking',         'SENR'),
    (7, 'Premium banking',        'PREM'),
    (8, 'International banking',  'INTL')
) AS s(segment_id, segment_name, segment_code)
CROSS JOIN tmp_oct_all d;

-- ==============================================================================
-- SECTION 2: Extend original 3 segments (IDs 1-3) to Oct 8-31
-- Guard: segment_id <= 3 ensures we do not re-extend the new segments just inserted
-- ==============================================================================

INSERT INTO datalake.segments (segment_id, segment_name, segment_code, as_of)
SELECT s.segment_id, s.segment_name, s.segment_code, d.as_of
FROM datalake.segments s
CROSS JOIN tmp_new_all d
WHERE s.as_of = '2024-10-07'
  AND s.segment_id <= 3;

-- ==============================================================================
-- SECTION 3: Branches (all 40 branches, all 31 days of October)
-- ==============================================================================

INSERT INTO datalake.branches (branch_id, branch_name, address_line1, city, state_province, postal_code, country, as_of)
SELECT b.branch_id, b.branch_name, b.address_line1, b.city, b.state_province, b.postal_code, b.country, d.as_of
FROM tmp_branches b
CROSS JOIN tmp_oct_all d;

-- ==============================================================================
-- SECTION 4: New customers (1024-1223) — weekdays only (customers is weekday full-load)
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
FROM tmp_new_cust nc
CROSS JOIN tmp_oct_wd d;

-- ==============================================================================
-- SECTION 5: Extend existing 23 customers (1001-1023) to Oct 8-31 weekdays
-- Guard: id <= 1023 ensures we do not re-extend new customers inserted in Section 4
-- ==============================================================================

INSERT INTO datalake.customers (id, prefix, first_name, last_name, sort_name, suffix, birthdate, as_of)
SELECT c.id, c.prefix, c.first_name, c.last_name, c.sort_name, c.suffix, c.birthdate, d.as_of
FROM datalake.customers c
CROSS JOIN tmp_new_wd d
WHERE c.as_of = '2024-10-07'
  AND c.id <= 1023;

-- ==============================================================================
-- SECTION 6: New customer accounts — weekdays only
-- Balance drifts slightly each weekday (±$100) based on (account_id, as_of) hash
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
FROM tmp_new_accts a
CROSS JOIN tmp_oct_wd d;

-- ==============================================================================
-- SECTION 7: Extend existing 23 accounts (3001-3023) to Oct 8-31 weekdays
-- Guard: account_id <= 3023 ensures we do not re-extend new accounts from Section 6
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
CROSS JOIN tmp_new_wd d
WHERE a.as_of = '2024-10-07'
  AND a.account_id <= 3023;

-- ==============================================================================
-- SECTION 8: New customer addresses — all 31 calendar days
-- address_id = 2030 + (cust_id - 1023) → 2031 for customer 1024, 2230 for 1223
-- Using a deterministic formula avoids ROW_NUMBER over the date cross-join
-- ==============================================================================

INSERT INTO datalake.addresses (address_id, customer_id, address_line1, city, state_province, postal_code,
                                country, start_date, end_date, as_of)
SELECT
    2030 + (nc.cust_id - 1023)                                                   AS address_id,
    nc.cust_id,
    nc.address_line1,
    nc.city,
    nc.state_prov,
    nc.postal_code,
    nc.country,
    DATE '2019-01-01' + ((hashtext(nc.cust_id::text || 'asd') & 2147483647) % 2100) AS start_date,
    NULL                                                                            AS end_date,
    d.as_of
FROM tmp_new_cust nc
CROSS JOIN tmp_oct_all d;

-- ==============================================================================
-- SECTION 9: Extend existing addresses (customer_id 1001-1023) to Oct 8-31
-- Guard: customer_id <= 1023 ensures we do not re-extend new addresses from Section 8
-- Uses Oct 7 snapshot which already reflects the two SCD moves in the first week
-- ==============================================================================

INSERT INTO datalake.addresses (address_id, customer_id, address_line1, city, state_province, postal_code,
                                country, start_date, end_date, as_of)
SELECT a.address_id, a.customer_id, a.address_line1, a.city, a.state_province, a.postal_code,
       a.country, a.start_date, a.end_date, d.as_of
FROM datalake.addresses a
CROSS JOIN tmp_new_all d
WHERE a.as_of = '2024-10-07'
  AND a.customer_id <= 1023;

-- ==============================================================================
-- SECTION 10: New customers_segments — all 31 calendar days
-- Primary IDs: cust_id - 988 (1024-988=36 ... 1223-988=235)
-- Secondary IDs: 236+ from tmp_new_cust_secondary_segs (stable across dates)
-- ==============================================================================

-- Primary (every new customer gets their regional segment)
INSERT INTO datalake.customers_segments (id, customer_id, segment_id, as_of)
SELECT
    nc.cust_id - 988  AS id,
    nc.cust_id,
    CASE WHEN nc.country = 'US' THEN 1 ELSE 2 END  AS segment_id,
    d.as_of
FROM tmp_new_cust nc
CROSS JOIN tmp_oct_all d;

-- Secondary (~33% of new customers get one additional segment)
INSERT INTO datalake.customers_segments (id, customer_id, segment_id, as_of)
SELECT ss.cs_id, ss.cust_id, ss.segment_id, d.as_of
FROM tmp_new_cust_secondary_segs ss
CROSS JOIN tmp_oct_all d;

-- ==============================================================================
-- SECTION 11: Extend existing customers_segments (customer_id 1001-1023) to Oct 8-31
-- Guard: customer_id <= 1023 ensures we do not re-extend new assignments from Section 10
-- ==============================================================================

INSERT INTO datalake.customers_segments (id, customer_id, segment_id, as_of)
SELECT cs.id, cs.customer_id, cs.segment_id, d.as_of
FROM datalake.customers_segments cs
CROSS JOIN tmp_new_all d
WHERE cs.as_of = '2024-10-07'
  AND cs.customer_id <= 1023;

-- ==============================================================================
-- SECTION 12: Phone numbers — all customers, all 31 days (full-load daily)
-- phone_id formula: (cust_id - 1001) * 3 + slot (1=Mobile, 2=Home, 3=Work)
-- Mobile: all customers; Home: ~60%; Work: ~30%
-- ==============================================================================

-- Mobile (all 223 customers)
INSERT INTO datalake.phone_numbers (phone_id, customer_id, phone_number, phone_type, as_of)
SELECT
    (cust_id - 1001) * 3 + 1  AS phone_id,
    cust_id,
    '(' || LPAD(((hashtext(cust_id::text || 'pac') & 2147483647) % 900 + 100)::text, 3, '0')
        || ') 555-'
        || LPAD(((hashtext(cust_id::text || 'pln') & 2147483647) % 10000)::text, 4, '0')  AS phone_number,
    'Mobile'  AS phone_type,
    d.as_of
FROM (
    SELECT id AS cust_id FROM datalake.customers WHERE as_of = '2024-10-01' AND id <= 1023
    UNION ALL
    SELECT cust_id FROM tmp_new_cust
) all_custs
CROSS JOIN tmp_oct_all d;

-- Home (~60% of customers)
INSERT INTO datalake.phone_numbers (phone_id, customer_id, phone_number, phone_type, as_of)
SELECT
    (cust_id - 1001) * 3 + 2  AS phone_id,
    cust_id,
    '(' || LPAD(((hashtext(cust_id::text || 'hac') & 2147483647) % 900 + 100)::text, 3, '0')
        || ') 555-'
        || LPAD(((hashtext(cust_id::text || 'hln') & 2147483647) % 10000)::text, 4, '0')  AS phone_number,
    'Home'  AS phone_type,
    d.as_of
FROM (
    SELECT id AS cust_id FROM datalake.customers WHERE as_of = '2024-10-01' AND id <= 1023
    UNION ALL
    SELECT cust_id FROM tmp_new_cust
) all_custs
CROSS JOIN tmp_oct_all d
WHERE (hashtext(cust_id::text || 'hhas') & 2147483647) % 10 < 6;

-- Work (~30% of customers)
INSERT INTO datalake.phone_numbers (phone_id, customer_id, phone_number, phone_type, as_of)
SELECT
    (cust_id - 1001) * 3 + 3  AS phone_id,
    cust_id,
    '(' || LPAD(((hashtext(cust_id::text || 'wac') & 2147483647) % 900 + 100)::text, 3, '0')
        || ') 555-'
        || LPAD(((hashtext(cust_id::text || 'wln') & 2147483647) % 10000)::text, 4, '0')  AS phone_number,
    'Work'  AS phone_type,
    d.as_of
FROM (
    SELECT id AS cust_id FROM datalake.customers WHERE as_of = '2024-10-01' AND id <= 1023
    UNION ALL
    SELECT cust_id FROM tmp_new_cust
) all_custs
CROSS JOIN tmp_oct_all d
WHERE (hashtext(cust_id::text || 'whas') & 2147483647) % 10 < 3;

-- ==============================================================================
-- SECTION 13: Email addresses — all customers, all 31 days (full-load daily)
-- email_id formula: (cust_id - 1001) * 2 + slot (1=Personal, 2=Work)
-- Personal: all customers; Work: ~40%
-- ==============================================================================

-- Personal (all 223 customers)
INSERT INTO datalake.email_addresses (email_id, customer_id, email_address, email_type, as_of)
SELECT
    (cust_id - 1001) * 2 + 1  AS email_id,
    cust_id,
    lower(first_name) || '.'
        || lower(last_name)
        || ((hashtext(cust_id::text || 'enum') & 2147483647) % 100)::text
        || '@'
        || (ARRAY['gmail.com','yahoo.com','outlook.com','hotmail.com','icloud.com'])[
                (hashtext(cust_id::text || 'edom') & 2147483647) % 5 + 1]  AS email_address,
    'Personal'  AS email_type,
    d.as_of
FROM (
    SELECT id AS cust_id, first_name, last_name FROM datalake.customers WHERE as_of = '2024-10-01' AND id <= 1023
    UNION ALL
    SELECT cust_id, first_name, last_name FROM tmp_new_cust
) all_custs
CROSS JOIN tmp_oct_all d;

-- Work (~40% of customers)
INSERT INTO datalake.email_addresses (email_id, customer_id, email_address, email_type, as_of)
SELECT
    (cust_id - 1001) * 2 + 2  AS email_id,
    cust_id,
    lower(first_name) || '.'
        || lower(last_name)
        || '@'
        || (ARRAY['company.com','corp.net','business.org','enterprise.com','work.net'])[
                (hashtext(cust_id::text || 'wedom') & 2147483647) % 5 + 1]  AS email_address,
    'Work'  AS email_type,
    d.as_of
FROM (
    SELECT id AS cust_id, first_name, last_name FROM datalake.customers WHERE as_of = '2024-10-01' AND id <= 1023
    UNION ALL
    SELECT cust_id, first_name, last_name FROM tmp_new_cust
) all_custs
CROSS JOIN tmp_oct_all d
WHERE (hashtext(cust_id::text || 'wehas') & 2147483647) % 10 < 4;

-- ==============================================================================
-- SECTION 14: Credit scores — all customers, weekdays only, 3 bureaus (full-load weekdays)
-- credit_score_id formula: (cust_id - 1001) * 3 + slot (1=Equifax, 2=TransUnion, 3=Experian)
-- Base score per (customer, bureau) varies by ±20; drifts ±2 per weekday
-- ==============================================================================

INSERT INTO datalake.credit_scores (credit_score_id, customer_id, bureau, score, as_of)
SELECT
    (cust_id - 1001) * 3 + b.slot  AS credit_score_id,
    cust_id,
    b.bureau,
    GREATEST(300, LEAST(850,
        -- customer base score: 500-849
        500 + (hashtext(cust_id::text || 'bs') & 2147483647) % 350
        -- per-bureau variance: ±20
        + (hashtext(cust_id::text || b.bureau || 'bv') & 2147483647) % 41 - 20
        -- daily drift: ±2 points per weekday elapsed since Oct 1
        + (d.as_of - DATE '2024-10-01')
          * ((hashtext(cust_id::text || b.bureau || d.as_of::text || 'dd') & 2147483647) % 5 - 2)
    ))::integer  AS score,
    d.as_of
FROM (
    SELECT id AS cust_id FROM datalake.customers WHERE as_of = '2024-10-01' AND id <= 1023
    UNION ALL
    SELECT cust_id FROM tmp_new_cust
) all_custs
CROSS JOIN (VALUES (1,'Equifax'), (2,'TransUnion'), (3,'Experian')) AS b(slot, bureau)
CROSS JOIN tmp_oct_wd d;

-- ==============================================================================
-- SECTION 15: Loan accounts — eligible customers (~40%), weekdays only
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
FROM tmp_loan_customers lc
CROSS JOIN tmp_oct_wd d;

-- ==============================================================================
-- SECTION 16: Transactions — existing accounts (3001-3023), Oct 8-31
-- ~50% of accounts transact on any given day, 1-3 transactions per account per day
-- New transaction IDs start at 5051
-- ==============================================================================

INSERT INTO datalake.transactions (transaction_id, account_id, txn_timestamp, txn_type, amount, description, as_of)
WITH candidate_txns AS (
    SELECT a.account_id, d.as_of, t.slot
    FROM datalake.accounts a
    CROSS JOIN tmp_new_wd d
    CROSS JOIN (SELECT generate_series(1, 3) AS slot) t
    WHERE a.as_of = '2024-10-07'
      AND a.account_id <= 3023
      AND (hashtext(a.account_id::text || d.as_of::text || t.slot::text || 'te') & 2147483647) % 10 < 5
),
numbered AS (
    SELECT *, ROW_NUMBER() OVER (ORDER BY as_of, account_id, slot) AS rn
    FROM candidate_txns
)
SELECT
    5050 + rn  AS transaction_id,
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
-- SECTION 17: Transactions — new customer accounts (3024+), all of October
-- IDs continue after the max from Section 16
-- ==============================================================================

INSERT INTO datalake.transactions (transaction_id, account_id, txn_timestamp, txn_type, amount, description, as_of)
WITH max_txn AS (SELECT MAX(transaction_id) AS max_id FROM datalake.transactions),
candidate_txns AS (
    SELECT a.account_id, d.as_of, t.slot
    FROM tmp_new_accts a
    CROSS JOIN tmp_oct_all d
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
-- SECTION 18: Branch visits — all customers, daily transactional (~10% visit rate)
-- visit_id is sequential (this is transactional data, each row is a unique event)
-- ==============================================================================

INSERT INTO datalake.branch_visits (visit_id, customer_id, branch_id, visit_timestamp, visit_purpose, as_of)
WITH existing_cust_branches AS (
    -- Map existing customers to their home branch via postal code of their active address
    SELECT DISTINCT ON (a.customer_id) a.customer_id, b.branch_id
    FROM datalake.addresses a
    JOIN tmp_branches b ON b.postal_code = a.postal_code
    WHERE a.as_of = '2024-10-07'
      AND a.customer_id <= 1023
    ORDER BY a.customer_id, a.start_date DESC
),
all_cust_branches AS (
    SELECT customer_id, branch_id FROM existing_cust_branches
    UNION ALL
    SELECT cust_id AS customer_id, home_branch_id AS branch_id FROM tmp_new_cust
),
candidate_visits AS (
    SELECT cb.customer_id, cb.branch_id, d.as_of
    FROM all_cust_branches cb
    CROSS JOIN tmp_oct_all d
    WHERE (hashtext(cb.customer_id::text || d.as_of::text || 'bv') & 2147483647) % 10 = 0
),
numbered AS (
    SELECT *, ROW_NUMBER() OVER (ORDER BY as_of, customer_id) AS rn
    FROM candidate_visits
)
SELECT
    rn  AS visit_id,
    customer_id,
    branch_id,
    -- Business hours: 9am (32400s) to 5pm (61200s)
    as_of::timestamp
        + (32400 + ((hashtext(customer_id::text || as_of::text || 'vt') & 2147483647) % 28800)
           || ' seconds')::interval  AS visit_timestamp,
    (ARRAY['Deposit','Withdrawal','Account Opening','Inquiry','Loan Application'])[
        (hashtext(customer_id::text || as_of::text || 'vp') & 2147483647) % 5 + 1]  AS visit_purpose,
    as_of
FROM numbered;

COMMIT;
