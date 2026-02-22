# CustomerAddressDeltas -- Test Plan

## Test Cases

TC-1: Day-over-day comparison uses current and previous day -- Traces to BR-1
- Input: Run for 2024-10-02
- Expected: Compares Oct 2 addresses against Oct 1 addresses
- Verification: Delta results match manual comparison of the two dates

TC-2: NEW addresses detected correctly -- Traces to BR-2
- Input: Address_id present in Oct 2 but not Oct 1
- Expected: Row with change_type = "NEW"
- Verification: Check for NEW rows matching address_ids not in previous date

TC-3: UPDATED addresses detected correctly -- Traces to BR-3
- Input: Address_id present in both Oct 1 and Oct 2 with changed fields
- Expected: Row with change_type = "UPDATED"
- Verification: Check for UPDATED rows and verify field changes

TC-4: Compare fields are correct -- Traces to BR-4
- Input: Address with only non-compare field changed
- Expected: Not detected as UPDATED
- Verification: Only changes in the 8 compare fields trigger UPDATED

TC-5: Field comparison normalizes values -- Traces to BR-5
- Input: Addresses with various NULL, date, and string values
- Expected: Comparison is case-sensitive and normalizes types
- Verification: Exact match of delta detection with manual comparison

TC-6: Customer names use snapshot fallback -- Traces to BR-6
- Input: Customers with varying snapshot dates
- Expected: Most recent customer record <= effective date is used
- Verification: Compare customer_name values against DISTINCT ON query

TC-7: Baseline day (Oct 1) produces null-row sentinel -- Traces to BR-7
- Input: Oct 1 (first date, Sep 30 has no data)
- Expected: Single row with all NULLs except as_of and record_count = 0
- Verification: Check Oct 1 output for null-row sentinel

TC-8: record_count reflects delta count -- Traces to BR-8
- Input: Date with known deltas
- Expected: record_count matches number of delta rows
- Verification: All rows have same record_count = total delta rows

TC-9: No-change day produces null-row sentinel -- Traces to BR-9
- Input: Date where current and previous snapshots are identical
- Expected: Single row with NULLs except as_of and record_count = 0
- Verification: Check for null-row sentinel on no-change dates

TC-10: Output ordered by address_id ascending -- Traces to BR-10
- Input: Multiple delta rows
- Expected: address_id values in ascending order
- Verification: Verify address_id ordering

TC-11: Country trimmed, dates formatted -- Traces to BR-11
- Input: Addresses with country and date values
- Expected: country trimmed, dates as yyyy-MM-dd
- Verification: Check formatting in output

TC-12: Append mode accumulates daily deltas -- Traces to BR-12
- Input: Run for Oct 1 through Oct 5
- Expected: All dates have rows in the table
- Verification: SELECT DISTINCT as_of shows all dates

TC-13: Deleted addresses are NOT detected -- Traces to BR-13
- Input: Address present in Oct 1 but not Oct 2
- Expected: No row for this address (only NEW and UPDATED detected)
- Verification: Verify no rows for deleted address_ids

TC-14: Data values match curated output exactly -- Traces to all BRs
- Input: All dates Oct 1-31
- Expected: Exact match between curated and double_secret_curated per date
- Verification: EXCEPT-based comparison per as_of date
