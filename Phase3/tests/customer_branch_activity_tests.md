# CustomerBranchActivity -- Test Plan

## Test Cases

TC-1: One row per customer with branch visits -- Traces to BR-1
- Input: 29 branch visits for 2024-10-01 across multiple customers
- Expected: One row per distinct customer_id with visit data
- Verification: Row count comparison; EXCEPT query returns zero rows

TC-2: visit_count matches actual visit count per customer -- Traces to BR-2
- Input: Customer with multiple visits on a date
- Expected: visit_count equals the number of visit records for that customer
- Verification: Compare visit_count against manual count from datalake.branch_visits

TC-3: Customer name enrichment with NULL fallback -- Traces to BR-3
- Input: Customer IDs with and without matching customer records
- Expected: Matching customers have first_name/last_name; non-matching have NULL
- Verification: Check name fields against datalake.customers

TC-4: as_of from branch_visits DataFrame -- Traces to BR-4
- Input: Run for 2024-10-01
- Expected: All rows have as_of = 2024-10-01
- Verification: All as_of values match the effective date

TC-5: Empty output when customers is empty (weekends) -- Traces to BR-5, BR-7
- Input: Weekend date (e.g., Oct 5) where customers has no data
- Expected: Zero rows appended for that date
- Verification: No rows for Oct 5 or Oct 6 in output

TC-6: Empty output when branch_visits is empty -- Traces to BR-5
- Input: A hypothetical date with no branch visits
- Expected: Zero rows in output
- Verification: Count rows for that date

TC-7: Append mode accumulates rows across dates -- Traces to BR-6
- Input: Run for Oct 1, Oct 2, Oct 3
- Expected: All three dates have rows in the table
- Verification: SELECT DISTINCT as_of shows all three dates

TC-8: Data values match curated output exactly -- Traces to all BRs
- Input: All dates Oct 1-31
- Expected: Exact match between curated and double_secret_curated per date
- Verification: EXCEPT-based comparison per as_of date
