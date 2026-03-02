# CrossSellCandidates -- Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | Product ownership determined per-customer from accounts, cards, investments |
| TC-02   | BR-2           | Account type matching uses exact strings: "Checking", "Savings", "Credit" |
| TC-03   | BR-3           | has_card uses asymmetric representation: "Yes" / "No Card" |
| TC-04   | BR-4           | has_investment uses numeric representation: 1 / 0 |
| TC-05   | BR-5           | missing_products excludes investment -- only checks Checking, Savings, Credit, Card |
| TC-06   | BR-6           | missing_products uses "; " separator, "None" when all products present |
| TC-07   | BR-7           | as_of derived from __maxEffectiveDate, formatted as MM/dd/yyyy |
| TC-08   | BR-8           | Empty customers input produces empty output; empty other tables do not |
| TC-09   | BR-9           | has_checking, has_savings, has_credit rendered as "True" / "False" strings |
| TC-10   | BR-10          | Every customer gets a row regardless of product ownership |
| TC-11   | Writer Config  | CSV output uses Overwrite mode, LF line endings, header, no trailer |
| TC-12   | AP3 / AP6      | V2 uses Tier 1 (no External module) -- SQL replaces row-by-row iteration |
| TC-13   | AP4            | Unused columns (account_id, card_id, investment_id) not sourced in V2 |
| TC-14   | AP5            | Asymmetric NULL representations reproduced in output |
| TC-15   | OQ-1           | Investment absence NOT included in missing_products list |
| TC-16   | Edge Case      | Customer with no accounts, cards, or investments |
| TC-17   | Edge Case      | Customer with all products -- missing_products is "None" |
| TC-18   | Edge Case      | NULL first_name or last_name coalesced to empty string |
| TC-19   | Edge Case      | Multi-day auto-advance run: only last date survives (Overwrite) |
| TC-20   | Edge Case      | Weekend date produces header-only CSV |
| TC-21   | Edge Case      | Month-end boundary date produces normal output |
| TC-22   | Edge Case      | Quarter-end boundary date produces normal output |
| TC-23   | Edge Case      | Customer with multiple accounts of the same type |
| TC-24   | Edge Case      | Customer with cards but no investments (and vice versa) |
| TC-25   | Edge Case      | Row ordering: output ordered by customer_id |
| TC-26   | Proofmark      | Proofmark comparison passes with zero exclusions and zero fuzzy columns |
| TC-27   | FSD: Tier 1    | V2 produces byte-identical output to V1 across full date range |

## Test Cases

### TC-01: Product ownership determined per-customer
- **Traces to:** BR-1
- **Input conditions:** Run V2 job for a single weekday (e.g., 2024-10-01) where all four source tables (customers, accounts, cards, investments) contain data.
- **Expected output:** Each customer row reflects their actual product ownership. A customer who has a Checking account, a card, and an investment shows `has_checking=True`, `has_card=Yes`, `has_investment=1`. Product flags are derived from LEFT JOINs to accounts (by account_type), cards (by customer_id), and investments (by customer_id).
- **Verification method:** Select a sample of 5-10 customers from the V2 output. For each customer, query the source tables directly:
  - `SELECT DISTINCT account_type FROM datalake.accounts WHERE customer_id = X AND as_of = '2024-10-01'`
  - `SELECT COUNT(*) FROM datalake.cards WHERE customer_id = X AND as_of = '2024-10-01'`
  - `SELECT COUNT(*) FROM datalake.investments WHERE customer_id = X AND as_of = '2024-10-01'`
  Verify each product flag in the output matches the source data. The FSD SQL uses LEFT JOINs with MAX(CASE) and COUNT(DISTINCT) patterns [FSD Section 5].

### TC-02: Account type matching uses exact string comparison
- **Traces to:** BR-2
- **Input conditions:** Run V2 job. Verify that `datalake.accounts` contains account_type values exactly matching "Checking", "Savings", and "Credit" (no leading/trailing whitespace, correct capitalization).
- **Expected output:** Product flags (has_checking, has_savings, has_credit) correctly identify customers based on exact string matches against "Checking", "Savings", "Credit". No case-insensitive matching, no LIKE patterns, no TRIM operations.
- **Verification method:** Query `SELECT DISTINCT account_type FROM datalake.accounts` to confirm the exact values in the database. Inspect the V2 SQL to verify it uses `a.account_type = 'Checking'` (exact match, not LIKE or LOWER). Cross-check a customer who has a "Checking" account and verify `has_checking=True` in output. The FSD SQL uses `CASE WHEN a.account_type = 'Checking'` [FSD Section 5].

### TC-03: has_card uses asymmetric representation "Yes" / "No Card"
- **Traces to:** BR-3, AP5
- **Input conditions:** Run V2 job for a date where some customers have cards and some do not.
- **Expected output:** Customers with at least one card entry in `datalake.cards` show `has_card=Yes`. Customers with no card entries show `has_card=No Card`. NOT "No" or "False" or "0" -- the exact string "No Card".
- **Verification method:** Identify a customer with cards and one without:
  - Customer WITH cards: `SELECT customer_id FROM datalake.cards WHERE as_of = '2024-10-01' LIMIT 1`
  - Customer WITHOUT cards: Find a customer_id in `datalake.customers` that is NOT in `datalake.cards` for that date
  Verify the output shows "Yes" and "No Card" respectively. This asymmetric representation is documented in BRD BR-3 and replicated intentionally in the FSD SQL: `CASE WHEN COUNT(DISTINCT cd.customer_id) > 0 THEN 'Yes' ELSE 'No Card' END` [FSD Section 5].

### TC-04: has_investment uses numeric representation 1 / 0
- **Traces to:** BR-4, AP5
- **Input conditions:** Run V2 job for a date where some customers have investments and some do not.
- **Expected output:** Customers with at least one investment show `has_investment=1`. Customers without investments show `has_investment=0`. This is an integer, not a boolean string -- distinct from the "True"/"False" pattern used for account flags and the "Yes"/"No Card" pattern for cards.
- **Verification method:** Identify customers with and without investments using the same approach as TC-03. Verify the output shows `1` and `0` respectively. Confirm it is NOT "True"/"False" or "Yes"/"No". The FSD SQL uses `CASE WHEN COUNT(DISTINCT inv.customer_id) > 0 THEN 1 ELSE 0 END` [FSD Section 5].

### TC-05: missing_products excludes investment
- **Traces to:** BR-5, OQ-1
- **Input conditions:** Find a customer who has Checking, Savings, Credit, and Card but does NOT have investments. Run V2 job.
- **Expected output:** That customer's `missing_products` is "None" -- NOT "Investment" or any investment-related string. Investment absence is deliberately not tracked in the missing products list, even though `has_investment=0` correctly reflects the absence.
- **Verification method:** Identify such a customer by cross-referencing source tables. Verify their output row shows `missing_products=None` despite `has_investment=0`. Also verify a customer missing ONLY investments shows `missing_products=None`. The FSD SQL's missing_products CASE only checks for Checking, Savings, Credit, and card presence [FSD Section 5]. BRD BR-5 explicitly documents this exclusion.

### TC-06: missing_products separator and "None" fallback
- **Traces to:** BR-6
- **Input conditions:** Run V2 job. Identify customers with varying numbers of missing products.
- **Expected output:**
  - Customer missing all 4 tracked products: `missing_products=Checking; Savings; Credit; No Card`
  - Customer missing 2 products (e.g., Savings and Card): `missing_products=Savings; No Card`
  - Customer missing 1 product (e.g., only Credit): `missing_products=Credit`
  - Customer with all 4 tracked products: `missing_products=None`
- **Verification method:** For each scenario, verify the exact string format. The separator is `"; "` (semicolon followed by space). Products appear in order: Checking, Savings, Credit, No Card (matching the V1 code's evaluation order [CrossSellCandidateFinder.cs:77-83]). The FSD SQL uses `SUBSTR(..., 3)` to strip the leading `"; "` prefix [FSD Section 5, note 2].

### TC-07: as_of derived from effective date, formatted as MM/dd/yyyy
- **Traces to:** BR-7
- **Input conditions:** Run V2 job for 2024-10-15.
- **Expected output:** Every row's `as_of` value is `10/15/2024` (MM/dd/yyyy format). NOT `2024-10-15` (yyyy-MM-dd). The date comes from the DataSourcing `as_of` column (which equals `__maxEffectiveDate` for single-day gap-fill runs) and is reformatted by the SQL.
- **Verification method:** Read the V2 CSV output and check the `as_of` column. All values should be `10/15/2024`. The FSD documents the date format conversion: `SUBSTR(c.as_of, 6, 2) || '/' || SUBSTR(c.as_of, 9, 2) || '/' || SUBSTR(c.as_of, 1, 4)` converts `"2024-10-15"` to `"10/15/2024"` [FSD Section 4, Section 5]. This matches V1's `DateOnly.ToString()` under InvariantCulture. Test with multiple dates to confirm:
  - 2024-10-01 -> `10/01/2024`
  - 2024-11-15 -> `11/15/2024`
  - 2024-12-31 -> `12/31/2024`

### TC-08: Empty customers input produces empty output; empty other tables do not
- **Traces to:** BR-8
- **Input conditions:** Two scenarios:
  1. Run V2 for a date where `datalake.customers` has zero rows (if such a date exists; otherwise, this is a theoretical edge case).
  2. Run V2 for a date where `datalake.cards` or `datalake.investments` has zero rows but `datalake.customers` has data.
- **Expected output:**
  1. Empty customers: Output CSV contains only the header row. Zero data rows.
  2. Empty cards/investments but non-empty customers: Output CSV contains a row for every customer. Product flags for the empty table show absence values (has_card="No Card", has_investment=0). The missing_products list reflects the missing products.
- **Verification method:** For scenario 1: Verify header-only output. For scenario 2: Verify all customers appear with correct absence flags for the empty table(s). The FSD notes that LEFT JOINs handle empty secondary tables naturally [FSD Section 5, Empty Guard Consideration]. Note: If any of the four tables has zero rows, Transformation.RegisterTable will skip it [Transformation.cs:46], causing a "no such table" SQL error. The FSD assesses this risk as LOW given the datalake's snapshot guarantee [FSD Section 5].

### TC-09: Boolean columns rendered as "True" / "False" strings
- **Traces to:** BR-9
- **Input conditions:** Run V2 job for a date where data exists.
- **Expected output:** The columns `has_checking`, `has_savings`, `has_credit` contain the strings `"True"` or `"False"` (capitalized first letter). NOT "true"/"false" (lowercase), NOT "1"/"0" (numeric), NOT "Yes"/"No".
- **Verification method:** Read the V2 CSV and inspect values in these three columns. Every value must be exactly `True` or `False`. The FSD documents this critical type note: V1 stores C# `bool` which `.ToString()` produces `"True"`/`"False"`. The V2 SQL must output these strings directly [FSD Section 4, Critical Type Representation Notes]. The SQL uses `THEN 'True' ELSE 'False'` [FSD Section 5].

### TC-10: Every customer gets a row regardless of product ownership
- **Traces to:** BR-10
- **Input conditions:** Run V2 job for a date. Count distinct customers in the source vs. rows in the output.
- **Expected output:** Row count in V2 output equals `SELECT COUNT(DISTINCT id) FROM datalake.customers WHERE as_of = '<date>'`. Every customer in the customers table appears in the output, even those with no accounts, no cards, and no investments.
- **Verification method:** Compare the customer count from the source table with the data row count in the CSV (total lines minus 1 for the header). The FSD SQL uses `customers` as the driving table with LEFT JOINs to all other tables [FSD Section 5], ensuring every customer produces a row.

### TC-11: Writer configuration matches V1
- **Traces to:** BRD Writer Configuration
- **Input conditions:** Run V2 job for a single weekday and inspect the output CSV.
- **Expected output:**
  - Header row is present (first line is column names)
  - Line endings are LF (`\n`), NOT CRLF
  - Write mode is Overwrite
  - No trailer row exists
  - Output path is `Output/double_secret_curated/cross_sell_candidates.csv`
- **Verification method:** Read the output file in binary mode. Verify:
  1. First line matches `customer_id,first_name,last_name,has_checking,has_savings,has_credit,has_card,has_investment,missing_products,as_of\n`
  2. Every line ends with `\n` (LF only, no `\r` before it)
  3. No trailer line at end of file
  4. Run twice for the same date -- file should be identical (Overwrite replaces entirely).
  Config matches FSD Section 7: includeHeader=true, writeMode=Overwrite, lineEnding=LF, no trailerFormat.

### TC-12: V2 uses Tier 1 -- no External module
- **Traces to:** AP3, AP6 elimination
- **Input conditions:** Inspect the V2 job config JSON (`cross_sell_candidates_v2.json`).
- **Expected output:** The V2 config contains no External module entry. The module chain is: 4x DataSourcing -> Transformation -> CsvFileWriter. The V1 External module (`CrossSellCandidateFinder`) with its row-by-row foreach iteration [CrossSellCandidateFinder.cs:65] and dictionary-based lookups is replaced entirely by a single SQL query with JOINs and GROUP BY.
- **Verification method:** Read `cross_sell_candidates_v2.json` and verify:
  1. No module entry with `"type": "External"` exists
  2. Four DataSourcing entries exist (customers, accounts, cards, investments)
  3. A Transformation module exists with `"resultName": "output"` and a SQL query containing LEFT JOINs and GROUP BY
  4. A CsvFileWriter module produces the output
  This confirms AP3 (unnecessary External) and AP6 (row-by-row iteration) are eliminated per FSD Section 3.

### TC-13: Unused columns not sourced in V2
- **Traces to:** AP4 elimination
- **Input conditions:** Inspect the V2 job config JSON.
- **Expected output:**
  - `accounts` DataSourcing: columns = `["customer_id", "account_type"]` -- `account_id` removed
  - `cards` DataSourcing: columns = `["customer_id"]` -- `card_id` removed
  - `investments` DataSourcing: columns = `["customer_id"]` -- `investment_id` removed
  - `customers` DataSourcing: columns = `["id", "first_name", "last_name"]` -- unchanged
- **Verification method:** Read `cross_sell_candidates_v2.json` and verify each DataSourcing entry's columns array. Compare against V1 config to confirm removed columns. The FSD documents these removals in Section 6 (Config Changes from V1) [FSD Section 6].

### TC-14: Asymmetric representations reproduced in output
- **Traces to:** AP5 (documented, reproduced for output equivalence)
- **Input conditions:** Run V2 job for a date. Find customers exhibiting each representation pattern.
- **Expected output:** Three different representation schemes coexist in the output:
  1. `has_checking`, `has_savings`, `has_credit`: `"True"` / `"False"` (boolean strings)
  2. `has_card`: `"Yes"` / `"No Card"` (custom strings)
  3. `has_investment`: `1` / `0` (integer)
- **Verification method:** Identify at least one customer in each state (has/doesn't have each product type). Verify the exact representation for each column. This asymmetry is intentional V1 replication, documented as AP5 in BRD and FSD Section 3. The FSD SQL deliberately uses different CASE expression outputs for each pattern [FSD Section 5].

### TC-15: Investment absence NOT in missing_products
- **Traces to:** OQ-1
- **Input conditions:** Find a customer who has all 4 tracked products (Checking, Savings, Credit, Card) but does NOT have investments.
- **Expected output:** `missing_products=None` for this customer, despite `has_investment=0`. Investment is never listed as a missing product.
- **Verification method:** Same approach as TC-05. Additionally, verify that NO row in the entire output contains "Investment" as a substring within the `missing_products` column. Run: search all `missing_products` values for the string "Investment" -- expect zero matches. BRD OQ-1 flags this as potentially a bug in V1, but V2 replicates the behavior [FSD Section 9].

### TC-16: Customer with no accounts, cards, or investments
- **Traces to:** Edge Case (BRD: Customer with no products)
- **Input conditions:** Find (or verify existence of) a customer in `datalake.customers` who has no entries in `datalake.accounts`, `datalake.cards`, or `datalake.investments` for the test date.
- **Expected output:** That customer's output row shows:
  - `has_checking=False`
  - `has_savings=False`
  - `has_credit=False`
  - `has_card=No Card`
  - `has_investment=0`
  - `missing_products=Checking; Savings; Credit; No Card`
- **Verification method:** Verify the customer's absence from all three product tables. Check their output row matches the expected values exactly. The LEFT JOIN + GROUP BY with CASE expressions correctly default to absence values when no joined rows exist [FSD Section 5, note 3].

### TC-17: Customer with all products -- missing_products is "None"
- **Traces to:** Edge Case (BR-6 "None" fallback)
- **Input conditions:** Find a customer who has at least one Checking account, at least one Savings account, at least one Credit account, and at least one card (investment doesn't matter per BR-5).
- **Expected output:** That customer's `missing_products` value is exactly the string `None`. Not an empty string, not "null", not blank.
- **Verification method:** Verify the customer has all required products in the source tables. Check the output CSV for the exact string `None` in the `missing_products` column. The FSD SQL uses the top-level CASE to output `'None'` when all product conditions are met [FSD Section 5].

### TC-18: NULL first_name or last_name coalesced to empty string
- **Traces to:** Edge Case (BRD Output Schema: null coalesce to "")
- **Input conditions:** Query for customers with NULL first_name or last_name: `SELECT * FROM datalake.customers WHERE first_name IS NULL OR last_name IS NULL AND as_of = '<date>'`.
- **Expected output:** In the V2 CSV output, those customers have empty strings (not "NULL", not missing) for the NULL name fields. The CSV field appears as two consecutive commas (e.g., `123,,Smith,...`).
- **Verification method:** If NULL name rows exist in the source, verify the corresponding output rows have empty strings for the NULL fields. The FSD SQL uses `COALESCE(c.first_name, '')` and `COALESCE(c.last_name, '')` [FSD Section 5]. If no NULL names exist in the data, verify the COALESCE is present in the SQL config as a defensive measure.

### TC-19: Multi-day auto-advance run -- only last date survives (Overwrite)
- **Traces to:** Edge Case (BRD: Write Mode Implications, W9)
- **Input conditions:** Run V2 job for two consecutive weekdays (e.g., 2024-10-01 then 2024-10-02).
- **Expected output:** After both dates execute, the output CSV contains ONLY the data from 2024-10-02. All `as_of` values are `10/02/2024`. Row count matches the customer count for that date.
- **Verification method:** Run the job for the two-day range. Read the output CSV. Verify:
  1. All `as_of` values are `10/02/2024`
  2. No rows from 2024-10-01 survive
  3. Row count matches `SELECT COUNT(DISTINCT id) FROM datalake.customers WHERE as_of = '2024-10-02'`
  The FSD documents W9 (Overwrite mode) as a wrinkle that is reproduced [FSD Section 3, Section 7].

### TC-20: Weekend date produces header-only CSV
- **Traces to:** Edge Case (weekend dates)
- **Input conditions:** Run V2 job for a Saturday (e.g., 2024-10-05) or Sunday (e.g., 2024-10-06).
- **Expected output:** Since the datalake has no weekend snapshots, DataSourcing returns zero rows for all four tables. The output CSV contains only the header. Because write mode is Overwrite, any prior weekday data is replaced.
- **Verification method:** Verify no weekend data exists: `SELECT COUNT(*) FROM datalake.customers WHERE as_of = '2024-10-05'` returns 0. Run V2 job and verify header-only output. Note: FSD Section 5 (Empty Guard) warns that missing SQLite tables may cause SQL errors if any sourced table has zero rows [Transformation.cs:46]. This is flagged as LOW risk.

### TC-21: Month-end boundary produces normal output
- **Traces to:** Edge Case (date boundaries)
- **Input conditions:** Run V2 job for 2024-10-31 (last day of October, Thursday).
- **Expected output:** Normal cross-sell output. No summary rows, boundary markers, or special behavior. Row count equals customer count for that date.
- **Verification method:** Verify row count matches source customer count. Verify no rows contain aggregated or special values. No W-codes (W3a/W3b/W3c) apply to this job [FSD Section 3].

### TC-22: Quarter-end boundary produces normal output
- **Traces to:** Edge Case (date boundaries)
- **Input conditions:** Run V2 job for 2024-12-31 (last day of Q4, Tuesday).
- **Expected output:** Normal output. No quarterly summary or special behavior.
- **Verification method:** Same as TC-21 for the quarter-end date.

### TC-23: Customer with multiple accounts of the same type
- **Traces to:** Edge Case (aggregate correctness)
- **Input conditions:** Find a customer who has multiple Checking accounts (or multiple accounts of any single type).
- **Expected output:** `has_checking=True` (not duplicated, not counted). The product flag is a simple presence check, not a count. The customer still gets exactly one output row. The `missing_products` list is unaffected by duplicate account types.
- **Verification method:** Query `SELECT customer_id, account_type, COUNT(*) FROM datalake.accounts WHERE as_of = '<date>' GROUP BY customer_id, account_type HAVING COUNT(*) > 1`. For a customer with duplicate account types, verify their output row has exactly one row with the correct boolean flag. The FSD SQL uses `MAX(CASE WHEN ... THEN 1 ELSE 0 END)` which correctly collapses duplicates [FSD Section 5, note 3]. The `GROUP BY c.id` ensures one output row per customer.

### TC-24: Customer with cards but no investments (and vice versa)
- **Traces to:** Edge Case (AP5 asymmetric handling)
- **Input conditions:** Find two customers:
  1. Customer A: has cards but no investments
  2. Customer B: has investments but no cards
- **Expected output:**
  - Customer A: `has_card=Yes`, `has_investment=0`
  - Customer B: `has_card=No Card`, `has_investment=1`
- **Verification method:** Query source tables to confirm product ownership. Verify the asymmetric representations: card uses string "Yes"/"No Card", investment uses integer 1/0. Also verify missing_products: Customer A's missing_products does NOT include investment-related text (per BR-5). Customer B's missing_products includes "No Card" [FSD Section 5].

### TC-25: Row ordering -- output ordered by customer_id
- **Traces to:** Edge Case (FSD SQL: ORDER BY c.id)
- **Input conditions:** Run V2 job for any date with data.
- **Expected output:** Output rows are sorted in ascending order by `customer_id`. The V2 SQL includes `ORDER BY c.id` [FSD Section 5].
- **Verification method:** Read the V2 CSV output and verify the `customer_id` column is in strictly ascending order. Note: the FSD Proofmark section [FSD Section 8, Known Risks, item 2] flags a potential discrepancy if V1's row order differs from `ORDER BY id`. If V1 outputs customers in a different order (e.g., PostgreSQL natural order), Proofmark row-by-row comparison could fail. This test documents the V2 ordering; Phase D Proofmark comparison will reveal if V1 ordering differs.

### TC-26: Proofmark comparison passes with zero exclusions and zero fuzzy columns
- **Traces to:** FSD Proofmark Config Design (Section 8)
- **Input conditions:** Run Proofmark with the designed config: `reader: csv`, `threshold: 100.0`, `header_rows: 1`, `trailer_rows: 0`, no `excluded_columns`, no `fuzzy_columns`.
- **Expected output:** Proofmark exits with code 0 (PASS). All 10 columns match exactly between V1 and V2 output.
- **Verification method:** Execute Proofmark comparison between `Output/curated/cross_sell_candidates.csv` (V1) and `Output/double_secret_curated/cross_sell_candidates.csv` (V2). Verify exit code is 0. Read the Proofmark report JSON and confirm zero mismatches across all columns. The FSD asserts all columns are deterministic and require no overrides [FSD Section 8].

### TC-27: V2 produces byte-identical output to V1 across full date range
- **Traces to:** FSD Tier 1 Justification, Dual Mandate (output equivalence)
- **Input conditions:** Run both V1 and V2 jobs for the full date range (2024-10-01 through 2024-12-31). Since both use Overwrite mode, only the last effective date's output survives.
- **Expected output:** The V2 output at `Output/double_secret_curated/cross_sell_candidates.csv` is byte-identical to V1 at `Output/curated/cross_sell_candidates.csv`. Same header, same data rows in the same order, same column values, same LF line endings.
- **Verification method:** Run both V1 and V2 for the full date range. Compare the two CSV files byte-for-byte. Key areas to validate:
  1. Boolean columns: `True`/`False` (not `true`/`false` or `1`/`0`)
  2. has_card: `Yes`/`No Card` (not `True`/`False`)
  3. has_investment: `1`/`0` (not `True`/`False`)
  4. missing_products: exact separator `"; "`, exact product names, exact "None" fallback
  5. as_of: `MM/dd/yyyy` format (not `yyyy-MM-dd`)
  6. Row order: by customer_id ascending
  This validates the FSD's Tier 1 justification that SQL with LEFT JOINs, CASE expressions, and GROUP BY can fully replace the V1 External module [FSD Section 1].
