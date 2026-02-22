ETL Reverse-Engineering Analysis: Covered Transactions
=======================================================

Executive Summary
-----------------

The ETL pipeline produces a daily "covered transactions" report. For a given effective date (as_of),
it identifies all transactions on Checking accounts where the account holder has an active US address,
then enriches each transaction with the customer's demographic data (name, prefix, suffix, sort_name),
mailing address, account details, and customer segment classification. The output is a single CSV file
per day containing these denormalized, enriched transaction records.

The pipeline joins data from six input tables (transactions, accounts, customers, addresses,
customers_segments, segments) and applies filtering criteria to select only Checking account
transactions where the customer has an active US address. The segment data is included for
enrichment purposes only and has no bearing on transaction filtering or downstream consumption.

Each output file includes a header row, data rows sorted by customer_id ascending then transaction_id
descending, a blank line, and a footer line with an expected record count for integrity verification.


Input Data Profile
------------------

Table                | as_of dates available       | Rows per date | Notes
---------------------|-----------------------------|---------------|------
transactions         | Oct 1-7 (all 7 days)        | 9-16          | One row per transaction per day
accounts             | Oct 1-4, Oct 7 (5 days)     | 23            | Weekday-only delivery; missing Oct 5 (Sat), Oct 6 (Sun)
customers            | Oct 1-4, Oct 7 (5 days)     | 23            | Weekday-only delivery; missing Oct 5 (Sat), Oct 6 (Sun)
addresses            | Oct 1-7 (all 7 days)        | 23-25         | Gains new addresses over time
customers_segments   | Oct 1-7 (all 7 days)        | 30            | Static; includes duplicate rows
segments             | Oct 1-7 (all 7 days)        | 3             | Static

Key observations:
- Account and customer data is completely static across all snapshots (zero field changes)
- Segment and customer_segment assignments are static across all snapshots
- Address data changes: customer 1001 gains a CA address on Oct 2 (US address ends Oct 2);
  customer 1015 gains a US address on Oct 5 (CA address ends Oct 4)
- Customer 1015 has duplicate rows in customers_segments (two entries for segment_id=3/RICH)


Output Schema
-------------

22 fields per record:

"transaction_id","txn_timestamp","txn_type","amount","description","customer_id","name_prefix",
"first_name","last_name","sort_name","name_suffix","customer_segment","address_id","address_line1",
"city","state_province","postal_code","country","account_id","account_type","account_status",
"account_opened"

Output record counts per day:
Oct 1: 6 of 15 transactions (40%)
Oct 2: 2 of 16 transactions (13%)
Oct 3: 4 of 13 transactions (31%)
Oct 4: 3 of 9  transactions (33%)
Oct 5: 5 of 14 transactions (36%)
Oct 6: 1 of 15 transactions (7%)
Oct 7: 2 of 14 transactions (14%)


Observed Transformations
------------------------

1. Transaction Filtering (Core Business Logic)

   Input fields:    transactions.as_of, accounts.account_type, addresses.country, addresses.end_date
   Output field:    Row inclusion/exclusion
   Observed pattern: Only transactions meeting ALL of the following criteria appear in output:
   (a) Transaction as_of = effective date
   (b) Account type = 'Checking'
   (c) Customer has at least one US address that is "active" on the effective date
   (active = end_date IS NULL OR end_date >= effective date)
   Rule:            Include transaction WHERE account_type = 'Checking'
   AND EXISTS active US address for the customer on the effective date.
   account_status is NOT a filter — all statuses are included.
   Segment membership is NOT a filter — it is enrichment only.
   Confidence:      High (confirmed by stakeholder)
   Evidence:        - All 23 output records across 7 files are for Checking accounts (3001, 3004,
   3006, 3008, 3010). Zero Savings or Credit account transactions appear.
   - All output records show country = 'US'.
   - Customer 1001 appears on Oct 1 (US addr active, no end_date) and Oct 2
   (US addr end_date = Oct 2, still active). Customer 1001 does NOT appear
   on Oct 3-7 despite having Checking account transactions on Oct 3 and Oct 6
   — because US address 2001 end_date (Oct 2) < Oct 3.
   - Checking-account customers with CA-only addresses (1013, 1016, 1018, 1021,
   1023) never appear in output despite having transactions.
   - Non-Checking accounts (Savings: 1002, 1005, 1009, 1012, 1014, 1017, 1020,
   1022; Credit: 1003, 1007, 1011, 1015, 1019) never appear.

2. Customer Enrichment (Join)

   Input fields:    customers.prefix, customers.first_name, customers.last_name,
   customers.sort_name, customers.suffix
   Output fields:   name_prefix, first_name, last_name, sort_name, name_suffix
   Observed pattern: Direct passthrough with field renames:
   prefix -> name_prefix
   suffix -> name_suffix
   Rule:            JOIN customers ON accounts.customer_id = customers.id
   AND customers.as_of = effective date (with fallback, see BR-12)
   Confidence:      High
   Evidence:        Customer 1001: prefix="Mr.", first_name="Ethan", last_name="Carter",
   sort_name="Carter Ethan", suffix=NULL -> output shows "Mr.","Ethan","Carter",
   "Carter Ethan",NULL. All 5 customers' data matches exactly.

3. Address Enrichment (Join)

   Input fields:    addresses.address_id, address_line1, city, state_province, postal_code, country
   Output fields:   address_id, address_line1, city, state_province, postal_code, country
   Observed pattern: The active US address is selected; all address fields pass through directly
   Rule:            JOIN addresses ON customers.id = addresses.customer_id
   AND addresses.as_of = effective date
   AND addresses.country = 'US'
   AND (addresses.end_date IS NULL OR addresses.end_date >= effective date)
   When multiple active US addresses exist, select earliest start_date.
   Confidence:      High (multi-address tie-breaking confirmed by stakeholder)
   Evidence:        Oct 2, customer 1001: has US address 2001 (end_date=Oct 2, active) and CA
   address 2002 (active). Output shows address 2001 (US). All other customers
   have a single US address that matches output exactly.

4. Account Enrichment (Join)

   Input fields:    accounts.account_id, account_type, account_status, open_date
   Output fields:   account_id, account_type, account_status, account_opened
   Observed pattern: Direct passthrough with one rename: open_date -> account_opened.
   Fields current_balance, interest_rate, credit_limit, and apr are excluded.
   Rule:            JOIN accounts ON transactions.account_id = accounts.account_id
   AND accounts.as_of = effective date (with fallback, see BR-12)
   Confidence:      High
   Evidence:        Account 3001: account_type="Checking", account_status="Active",
   open_date=2021-01-15 -> output shows "Checking","Active","2021-01-15".
   Consistent across all 23 output records.

5. Segment Enrichment (Join + Selection)

   Input fields:    customers_segments.segment_id, segments.segment_code
   Output field:    customer_segment
   Observed pattern: The segment_code from the segments table is output. When a customer belongs
   to multiple segments, the first segment_code alphabetically is selected.
   Segment membership has no bearing on filtering — it is enrichment only.
   Duplicate segment assignments for the same customer must not produce
   duplicate output rows.
   Rule:            JOIN customers_segments ON customer_id = customers_segments.customer_id
   AND customers_segments.as_of = effective date
   JOIN segments ON customers_segments.segment_id = segments.segment_id
   AND segments.as_of = effective date
   Selection: First segment_code alphabetically (e.g., CANRET < RICH < USRET).
   Deduplicate to ensure one segment per customer.
   Confidence:      High (confirmed by stakeholder)
   Evidence:        Customer 1001 (in CANRET + USRET) -> output shows first alpha = "CANRET"?
   NOTE: The test output shows "USRET" for customer 1001, which conflicts with
   the alphabetical rule (CANRET < USRET). The stakeholder-confirmed rule is
   alphabetical sort; the test output may reflect a prior implementation that
   used a different selection strategy. The implementation should follow the
   confirmed alphabetical rule.

6. NULL Rendering

   Input fields:    customers.suffix (NULL in database)
   Output field:    name_suffix
   Observed pattern: Database NULL values render as literal unquoted NULL in CSV output
   Rule:            NULL -> literal string "NULL" (unquoted). Applies to all nullable fields
   (prefix, suffix, description, etc.) regardless of data type.
   Confidence:      High (confirmed by stakeholder)
   Evidence:        All 5 output customers have suffix=NULL in database; all 23 output records
   show unquoted NULL for name_suffix.

7. Record Count Footer

   Input fields:    N/A (derived)
   Output field:    Footer line: Expected records: N
   Observed pattern: N equals the exact count of data rows (excluding header, blank line, and footer)
   Rule:            Append "Expected records: {count}" as integrity check
   Confidence:      High
   Evidence:        All 7 output files have correct counts: 6, 2, 4, 3, 5, 1, 2.

8. Sort Order

   Input fields:    customer_id, transaction_id
   Output field:    Row ordering
   Observed pattern: Records are sorted by customer_id ascending, then transaction_id descending
   Rule:            ORDER BY customer_id ASC, transaction_id DESC
   Confidence:      High
   Evidence:        Oct 1: customers 1001(txns 5001,4001), 1004(5031), 1006(4011), 1010(5032,4019)
   Oct 3: 1004(4007), 1006(5013), 1008(5038,4015)
   Oct 5: 1006(5044,4012), 1008(5018), 1010(5042,5021)
   Within each customer group, transaction_ids are in descending order.
   Customer_ids are in ascending order across groups.


Inferred Business Rules
-----------------------

BR-1:  The pipeline processes a single effective date per invocation. The effective date is passed
as a command-line argument in YYYYMMDD format.
Evidence: Command-line example shows "20241002" as the second argument.
Confidence: High

BR-2:  Only transactions with as_of matching the effective date are considered.
Evidence: Each output file contains only transactions from that file's date.
Confidence: High

BR-3:  Only transactions on Checking accounts are included. Savings and Credit account transactions
are excluded. account_status is NOT a filter — transactions are included regardless of
whether the account is Active, Closed, or any other status.
Evidence: All 23 output records are for Checking accounts (3001, 3004, 3006, 3008, 3010).
Transactions on Savings (3002, 3005, 3009, 3012, 3014, 3017, 3020, 3022) and Credit
(3003, 3007, 3011, 3015, 3019) accounts never appear.
Confidence: High (account_status exclusion confirmed by stakeholder)

BR-4:  Only transactions for customers with an active US address on the effective date are included.
An address is "active" if end_date IS NULL or end_date >= effective date. The filtering is
based solely on address country — segment membership plays no role in filtering.
Evidence: Customer 1001 included Oct 1-2 (US addr active), excluded Oct 3-7 (US addr ended
Oct 2). CA-address-only customers (1013, 1016, 1018, 1021, 1023) with Checking accounts
never appear.
Confidence: High (confirmed by stakeholder)

BR-5:  The output address is the customer's active US address on the effective date. When a customer
has multiple active US addresses on the same date, select the one with the earliest start_date.
Evidence: Oct 2, customer 1001: has both US address 2001 (ending today) and CA address 2002
(active). Output shows address 2001 (US).
Confidence: High (multi-address tie-breaking confirmed by stakeholder)

BR-6:  The customer_segment field is derived by joining customers_segments to segments on segment_id
(and matching as_of). The segment_code is used as the output value.
Evidence: Output values "USRET" and "RICH" match segment_codes in the segments table.
Confidence: High

BR-7:  When a customer belongs to multiple segments, the first segment_code alphabetically is
selected (e.g., CANRET before RICH before USRET). Duplicate segment assignments for the
same customer must not produce duplicate output rows. Segment membership is for enrichment
only and has no bearing on filtering or downstream consumption.
Evidence: Customer 1010 (in RICH + USRET) -> alphabetical first = "RICH". Confirmed by
stakeholder.
NOTE: The test output shows "USRET" for customer 1001 (in CANRET + USRET), but the
alphabetical rule yields "CANRET". This is an accepted deviation — the stakeholder
confirmed alphabetical ordering as the correct business rule.
Confidence: High (confirmed by stakeholder)

BR-8:  The customer's prefix is renamed to name_prefix in the output. The customer's suffix is
renamed to name_suffix. The account's open_date is renamed to account_opened.
Evidence: Output header uses these renamed field names; values match source fields exactly.
Confidence: High

BR-9:  The following account fields are excluded from output: current_balance, interest_rate,
credit_limit, apr.
Evidence: These fields exist in the accounts table but do not appear in any output file.
Confidence: High

BR-10: The following customer fields are excluded from output: birthdate.
Evidence: birthdate exists in customers table but never appears in output.
Confidence: High

BR-11: The following address fields are excluded from output: start_date, end_date.
Evidence: These fields exist in addresses table but never appear in output.
Confidence: High

BR-12: When a table's snapshot does not exist for the effective date, the most recent available
snapshot (max as_of <= effective date) is used. Accounts and customers data is delivered on
weekdays only, so weekend effective dates will always require fallback to the preceding
Friday's snapshot.
Evidence: Accounts and customers tables have no data for Oct 5 (Sat) or Oct 6 (Sun), yet
the pipeline produces output for those dates using the Oct 4 (Fri) snapshot.
Confidence: High (confirmed by stakeholder)

BR-13: Each output file includes a header row (all field names quoted), data rows, a blank line,
and a footer line "Expected records: N" where N is the count of data rows.
Evidence: All 7 output files follow this exact format.
Confidence: High

BR-14: Output records are sorted by customer_id ascending, then transaction_id descending.
Evidence: Verified across all 7 output files. All multi-record customer groups show
descending transaction_id order. Customer groups are in ascending customer_id order.
Confidence: High

BR-15: NULL database values are rendered as literal unquoted NULL in the CSV output.
Evidence: All name_suffix values in output are unquoted NULL, matching the NULL suffix
values in the customers table.
Confidence: High

BR-16: An output file is always produced for every effective date, even when zero transactions
qualify. Zero-transaction files contain a header row, a blank line, and a footer line
"Expected records: 0".
Evidence: Output files exist for all 7 days (Oct 1-7). No date is skipped.
Confidence: High (zero-record behavior confirmed by stakeholder)

BR-17: The segments and customers_segments tables use the as_of matching the effective date.
These tables have data for all 7 dates in the test set. If data were missing, the same
fallback rule as BR-12 (most recent snapshot <= effective date) should apply.
Evidence: Both tables have snapshots for all 7 dates.
Confidence: High


Data Validation & Constraints
-----------------------------

- Output header is always present, with all 22 field names double-quoted.
  Evidence: All 7 output files include identical header rows. Confidence: High

- Expected records count matches actual data row count.
  Evidence: Verified across all 7 files. Confidence: High

- Quoting is based on database column type (confirmed by stakeholder):
    - Integer fields (transaction_id, customer_id, address_id, account_id) are unquoted.
    - All other non-NULL fields (strings, timestamps, decimals) are double-quoted.
    - NULL values of any type are rendered as literal unquoted NULL.
      Evidence: All 23 output records show consistent type-based quoting. Confidence: High

- Amount values retain exactly 2 decimal places.
  Evidence: All amounts in output (142.50, 500.00, 275.00, 25.50, 1850.00, etc.) have 2 decimal
  places, matching database precision. Confidence: High

- Timestamp format is YYYY-MM-DD HH:MM:SS (no timezone, no milliseconds).
  Evidence: All txn_timestamp values follow this format. Confidence: High

- Date fields (account_opened) use YYYY-MM-DD format.
  Evidence: All account_opened values follow this format. Confidence: High

- A blank line separates the last data row from the footer.
  Evidence: All 7 files show this pattern. Confidence: High

- The transaction date (txn_timestamp::date) always matches the as_of/effective date.
  Evidence: Verified — zero mismatches in the entire transactions table. Confidence: High


Resolved Ambiguities (Stakeholder-Confirmed)
----------------------------------------------

A-1: RESOLVED — Filtering is based on active US address only. Segment membership has no bearing
on transaction filtering. Segments are included in the output for enrichment/display only.

A-2: RESOLVED — When a customer belongs to multiple segments, select the first segment_code
alphabetically (ascending). For example: CANRET < RICH < USRET. Downstream consumers do
not depend on the segment value. Duplicate segment assignments (e.g., customer 1015 with
two RICH entries) must not produce duplicate output rows — deduplicate before selection.

A-3: RESOLVED — Use the most recent available snapshot (max as_of <= effective date). Accounts
and customers data is delivered on weekdays only, so weekend runs will fall back to the
preceding Friday's snapshot.

A-4: RESOLVED — If a customer with a Checking account has no active US address on the effective
date, their transactions are excluded from that day's output. No error is raised.

A-5: RESOLVED — When zero transactions qualify, produce a file with header, blank line, and
footer "Expected records: 0".

A-6: RESOLVED — When no transaction data exists at all for the effective date, produce a file
with header, blank line, and footer "Expected records: 0". Same behavior as A-5.

A-7: RESOLVED — account_status does NOT play a role in filtering. Transactions on Checking
accounts are included regardless of account_status. The field is passed through to output.

A-8: RESOLVED — When a customer has multiple active US addresses on the same date, select the
address with the earliest start_date.

A-9: RESOLVED — Duplicate segment assignments must not produce duplicate output rows. The segment
join must be deduplicated so each customer maps to exactly one segment_code per the
alphabetical selection rule in A-2.

A-10: RESOLVED — NULL prefix renders as literal unquoted NULL, same as any other NULL value.

A-11: RESOLVED — Quoting is based on database column type. Integer fields are unquoted. All other
non-NULL fields (strings, timestamps, decimals) are double-quoted. NULL values of any type
are rendered as literal unquoted NULL.


Source Data Notes
-----------------

N-1: Multiple segment assignments per customer

     Customer 1015 has two rows with segment_id=3 (RICH) in customers_segments (ids 33 and 34).
     This is valid — customers may belong to multiple segments, and duplicate assignments can
     occur. The ETL must deduplicate segment assignments to produce exactly one customer_segment
     value per customer (first alphabetically by segment_code). See BR-7 and A-2.

N-2: Typo in segments table

     segment_id 3 has segment_name = "Affluent houshold" (misspelling of "household"). This is
     present in the upstream data lake delivery and is outside the ETL's control. The ETL passes
     through segment_code (not segment_name), so this does not affect output.


Traceability Matrix
-------------------

Input Source              | Input Field              | Output Field        | Inferred Rule                                               | Evidence
--------------------------|--------------------------|---------------------|-------------------------------------------------------------|------------------
transactions              | transaction_id           | transaction_id      | Direct passthrough (unquoted)                               | All output records
transactions              | txn_timestamp            | txn_timestamp       | Direct passthrough (quoted, YYYY-MM-DD HH:MM:SS)            | All output records
transactions              | txn_type                 | txn_type            | Direct passthrough (quoted)                                 | All output records
transactions              | amount                   | amount              | Direct passthrough (quoted, 2 decimal places)               | All output records
transactions              | description              | description         | Direct passthrough (quoted)                                 | All output records
accounts                  | customer_id              | customer_id         | Direct passthrough (unquoted); also join key to customers   | All output records
customers                 | prefix                   | name_prefix         | Renamed; NULL -> literal NULL (unquoted)                    | BR-8, BR-15
customers                 | first_name               | first_name          | Direct passthrough (quoted)                                 | All output records
customers                 | last_name                | last_name           | Direct passthrough (quoted)                                 | All output records
customers                 | sort_name                | sort_name           | Direct passthrough (quoted)                                 | All output records
customers                 | suffix                   | name_suffix         | Renamed; NULL -> literal NULL (unquoted)                    | BR-8, BR-15
segments                  | segment_code             | customer_segment    | Renamed; via customers_segments join; first alphabetically  | BR-6, BR-7
addresses                 | address_id               | address_id          | Direct passthrough (unquoted); active US address selected   | BR-5
addresses                 | address_line1            | address_line1       | Direct passthrough (quoted)                                 | All output records
addresses                 | city                     | city                | Direct passthrough (quoted)                                 | All output records
addresses                 | state_province           | state_province      | Direct passthrough (quoted)                                 | All output records
addresses                 | postal_code              | postal_code         | Direct passthrough (quoted)                                 | All output records
addresses                 | country                  | country             | Direct passthrough (quoted)                                 | All output records
accounts                  | account_id               | account_id          | Direct passthrough (unquoted); also in transactions         | All output records
accounts                  | account_type             | account_type        | Direct passthrough (quoted); filtered to 'Checking'         | BR-3
accounts                  | account_status           | account_status      | Direct passthrough (quoted)                                 | All output records
accounts                  | open_date                | account_opened      | Renamed (quoted, YYYY-MM-DD)                                | BR-8
(derived)                 | (row count)              | Expected records: N | Count of data rows in output                                | BR-13
(not in output)           | accounts.current_balance | -                   | Excluded                                                    | BR-9
(not in output)           | accounts.interest_rate   | -                   | Excluded                                                    | BR-9
(not in output)           | accounts.credit_limit    | -                   | Excluded                                                    | BR-9
(not in output)           | accounts.apr             | -                   | Excluded                                                    | BR-9
(not in output)           | customers.birthdate      | -                   | Excluded                                                    | BR-10
(not in output)           | addresses.start_date     | -                   | Excluded                                                    | BR-11
(not in output)           | addresses.end_date       | -                   | Excluded (used for filtering only)                          | BR-11
(not in output)           | segments.segment_name    | -                   | Excluded                                                    | BR-6
(not in output)           | all as_of fields         | -                   | Excluded (used for snapshot selection only)                  | All tables


Join Chain
----------

transactions
-> accounts     ON transactions.account_id = accounts.account_id
AND accounts.as_of = effective_date (with fallback to most recent <= effective_date)
-> customers    ON accounts.customer_id = customers.id
AND customers.as_of = effective_date (with fallback to most recent <= effective_date)
-> addresses    ON accounts.customer_id = addresses.customer_id
AND addresses.as_of = effective_date
AND addresses.country = 'US'
AND (addresses.end_date IS NULL OR addresses.end_date >= effective_date)
-> customers_segments ON accounts.customer_id = customers_segments.customer_id
AND customers_segments.as_of = effective_date
-> segments     ON customers_segments.segment_id = segments.segment_id
AND segments.as_of = effective_date

Filters applied:
- transactions.as_of = effective_date
- accounts.account_type = 'Checking'
- Active US address must exist (see addresses join above)
- account_status is NOT a filter (all statuses included)

Address tie-breaking (multiple active US addresses):
- Select the address with the earliest start_date

Segment selection (multiple segments per customer):
- Deduplicate, then select first segment_code alphabetically (ASC)
- Segment has no bearing on filtering — enrichment only

Snapshot fallback (accounts and customers — weekday-only delivery):
- Use max(as_of) WHERE as_of <= effective_date

Known deviation from test output:
- Customer 1001 (in CANRET + USRET): test output shows "USRET", but the confirmed
  alphabetical rule yields "CANRET". The implementation should follow the confirmed rule.