ETL Reverse-Engineering Analysis
=================================

Executive Summary
-----------------

The ETL pipeline performs a daily change-detection (delta/diff) process on customer address data. It compares each day's
address snapshot against the previous day's snapshot, identifies new and modified address records, enriches them with
the customer's name (joined from a separate customer dataset), and outputs a change log file per day. Days with no
changes still produce an output file containing only the header and a zero record count. A footer validation line
(Expected records: N) is appended to each output file as a record-count integrity check.


Observed Transformations
------------------------

1. Change Detection (Row-Level Diff)

   Input fields:    All fields from addresses_YYYYMMDD.csv (current day vs. previous day)
   Output field:    change_type
   Observed pattern: NEW when an address_id exists in the current snapshot but not the previous;
   UPDATED when an address_id exists in both but at least one field value differs
   Likely rule:     Compare current-day addresses against previous-day addresses by address_id;
   classify as NEW or UPDATED accordingly
   Confidence:      High
   Evidence:        Oct 2: address 2002 appears for the first time -> NEW; address 2001 exists in
   both Oct 1 and Oct 2 but end_date changed -> UPDATED. Oct 4: address 2003
   end_date changed -> UPDATED. Oct 5: address 2004 appears for the first time -> NEW

2. Customer Name Enrichment (Join)

   Input fields:    customers.first_name, customers.last_name (joined via addresses.customer_id = customers.id)
   Output field:    customer_name
   Observed pattern: Concatenation of first_name + space + last_name
   Likely rule:     customer_name = first_name || ' ' || last_name
   Confidence:      High
   Evidence:        Customer 1001: first_name="Ethan", last_name="Carter" -> "Ethan Carter".
   Customer 1015: first_name="Elijah", last_name="Das" -> "Elijah Das".
   Fields prefix, suffix, sort_name, and birthdate are all excluded

3. NULL-to-Empty Conversion

   Input fields:    end_date
   Output field:    end_date
   Observed pattern: Input NULL becomes an empty/blank value in output
   Likely rule:     Replace NULL literals with empty strings
   Confidence:      High
   Evidence:        Oct 2, address 2002: input end_date = NULL -> output end_date is blank.
   Oct 5, address 2004: same pattern

4. Unchanged Records Filtered Out

   Input fields:    All address fields
   Output field:    N/A (row excluded)
   Observed pattern: Records with no field changes between days are excluded from output
   Likely rule:     Only emit records where at least one field value differs or the record is entirely new
   Confidence:      High
   Evidence:        Oct 2 output has 2 records out of 24 total addresses. Oct 3 has 0 records
   (no changes). The 21+ unchanged records are never emitted

5. Record Count Footer

   Input fields:    N/A (derived)
   Output field:    Footer line: Expected records: N
   Observed pattern: N equals the exact count of data rows (excluding header and footer)
   Likely rule:     Append a validation/audit line with the data row count
   Confidence:      High
   Evidence:        Oct 2: 2 data rows -> Expected records: 2. Oct 3: 0 data rows ->
   Expected records: 0. Oct 4: 1 -> Expected records: 1.
   Consistent across all 6 files

6. Output File Generation Cadence

   Input fields:    Existence of current-day and previous-day address files
   Output field:    File existence
   Observed pattern: One output file per day starting from the second input day. No output for the
   baseline (first day). Output generated even when there are no changes
   Likely rule:     Always produce an output file for each day after the baseline, even if empty
   Confidence:      High
   Evidence:        No address_changes_20241001.csv exists. Files for Oct 3, 6, and 7 exist
   but contain 0 data records


Inferred Business Rules
-----------------------

BR-1:  The pipeline processes a single effective date per invocation. The previous day is always
the calendar day immediately before the effective date (effective_date - 1).
Evidence: Oct 2 output compares against Oct 1; Oct 5 compares against Oct 4; etc.
Confidence: High

BR-2:  The effective date's address snapshot is compared to the previous calendar day's snapshot
to detect changes.
Evidence: Changes on Oct 2 are relative to Oct 1; Oct 4 relative to Oct 3; etc.
Confidence: High

BR-3:  A record with an address_id not present in the prior day is classified as NEW.
Evidence: Addresses 2002 (Oct 2) and 2004 (Oct 5).
Confidence: High

BR-4:  A record with an address_id present in both days but with any field value difference is
classified as UPDATED.
Evidence: Addresses 2001 (Oct 2, end_date changed) and 2003 (Oct 4, end_date changed).
Confidence: High

BR-5:  The output record reflects the current day's field values (post-change state), not the
prior day's.
Evidence: UPDATED address 2001 shows end_date=2024-10-02 (the new value, not the old NULL).
Confidence: High

BR-6:  Customer name is derived by joining to customer data on customer_id = id and concatenating
first_name + " " + last_name.
Evidence: Both output names match this pattern; prefix/suffix/sort_name excluded.
Confidence: High

BR-7:  Only customer_name is sourced from the customer dataset; all other customer fields (prefix,
suffix, sort_name, birthdate) are excluded.
Evidence: No trace of these fields in any output.
Confidence: High

BR-8:  An output file is always generated for each processing day, even if there are zero changes.
Evidence: Oct 3, 6, and 7 produce files with header + Expected records: 0.
Confidence: High

BR-9:  Each output file includes a footer line "Expected records: N" as a record-count integrity check.
Evidence: Present in all 6 output files with correct counts.
Confidence: High

BR-10: NULL values in end_date are rendered as empty/blank in output.
Evidence: Active addresses (NEW) have blank end_date.
Confidence: High

BR-11: Records with no changes between consecutive days are excluded from output.
Evidence: Consistently only changed/new records appear.
Confidence: High

BR-12: Output records appear ordered by address_id ascending.
Evidence: Oct 2: 2001 before 2002; all single-record files are trivially ordered.
Confidence: Medium

BR-13: The customer data used for the join does not need to have a same-day file; the most recent
or applicable customer data is used.
Evidence: No customer files exist for Oct 5 and Oct 6, yet output is produced for those dates.
Confidence: Medium


Data Validation & Constraints
-----------------------------

- Output header is always present, even in zero-change files.
  Evidence: All 6 output files include the header row. Confidence: High

- Expected records count must match actual data row count.
  Evidence: Verified across all 6 files. Confidence: High

- change_type is a controlled vocabulary: NEW, UPDATED.
  Evidence: Only these two values observed. Confidence: High (but see Ambiguities for DELETED)

- customer_name is always quoted in output.
  Evidence: Both names appear in double quotes. Confidence: High

- country field is not quoted in output (despite being quoted in input).
  Evidence: US and CA appear unquoted in all output records. Confidence: High

- change_type, dates, and numeric fields are unquoted in output.
  Evidence: Consistent across all records. Confidence: High

- String fields containing spaces or special characters are quoted in output.
  Evidence: address_line1, city, state_province, postal_code, customer_name. Confidence: High


Ambiguities & Hypotheses
-------------------------

A-1: Does a DELETED change type exist?
No address records were removed in the test data, so it is unknown whether the ETL would
emit a DELETED row if an address_id disappeared from the snapshot.
Alternatives: (a) DELETED type exists but was not triggered; (b) Deletions are not tracked.
Impact: High - a real implementation must decide.

A-2: Output ordering
With limited multi-record output (only Oct 2 has >1 row), the sort order could be by
address_id, by change_type, or by source file order.
Most likely address_id ascending, but insufficient data to confirm.
Impact: Low

A-3: Which customer snapshot is used for the join?
Customer files are missing for Oct 5-6, yet customer data is unchanged all week. It is
unclear whether the ETL (a) uses the same-date customer file, (b) uses the most recent
available, or (c) uses a persistent/cumulative customer source.
Cannot distinguish with static customer data.
Impact: Medium

A-4: What happens when multiple fields change on the same record?
Only end_date changes were observed for UPDATED records. It is unknown if changes to other
fields (e.g., city, postal_code) would also produce UPDATED records.
Likely yes, but unconfirmed.
Impact: Medium

A-5: What if a customer_id has no matching customer record?
No orphaned address records exist in the test data.
Alternatives: (a) Row is excluded; (b) customer_name is blank; (c) ETL errors.
Impact: Medium

A-6: Quoting rules
Country is unquoted while other strings are quoted. This could be (a) intentional for short
fixed-length codes, (b) a quirk of the CSV writer, or (c) only fields that could contain
commas/spaces are quoted.
Hard to determine without more varied data.
Impact: Low

A-7: Are customers_* files required on the same date, or can a stale file be used?
Since customer data never changes in the test data, this cannot be determined.
See A-3.
Impact: Medium


Traceability Matrix
-------------------

Input Source              | Input Field              | Output Field        | Inferred Rule                                              | Evidence
--------------------------|--------------------------|---------------------|------------------------------------------------------------|------------------
addresses (curr vs prior) | (row presence)           | change_type         | NEW if absent in prior; UPDATED if present but changed     | BR-3, BR-4
addresses                 | address_id               | address_id          | Direct passthrough                                         | All output records
addresses                 | customer_id              | customer_id         | Direct passthrough                                         | All output records
customers                 | first_name, last_name    | customer_name       | first_name + " " + last_name (joined on customer_id = id) | BR-6
addresses                 | address_line1            | address_line1       | Direct passthrough                                         | All output records
addresses                 | city                     | city                | Direct passthrough                                         | All output records
addresses                 | state_province           | state_province      | Direct passthrough                                         | All output records
addresses                 | postal_code              | postal_code         | Direct passthrough                                         | All output records
addresses                 | country                  | country             | Direct passthrough (quoting removed)                       | All output records
addresses                 | start_date               | start_date          | Direct passthrough (quote removal)                         | All output records
addresses                 | end_date                 | end_date            | Direct passthrough; NULL -> empty string                   | BR-10
(derived)                 | (row count)              | Expected records: N | Count of data rows in output                               | BR-9