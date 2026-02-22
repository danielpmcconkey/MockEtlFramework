# CreditScoreAverage -- Test Plan

## Test Cases

TC-1: One output row per customer with credit scores AND customer record -- Traces to BR-1
- Input: 223 customers, 669 credit scores (223 customers x 3 bureaus) for 2024-10-31
- Expected: 223 rows in output
- Verification: Row count comparison; EXCEPT query returns zero rows

TC-2: avg_score is arithmetic mean rounded to 2 decimal places -- Traces to BR-2
- Input: Customer 1001 has scores 850, 836, 850
- Expected: avg_score = 845.33 (ROUND((850+836+850)/3, 2))
- Verification: Compare specific customer's avg_score value

TC-3: Bureau scores are pivoted into separate columns -- Traces to BR-3
- Input: Customer 1001 with Equifax=850, TransUnion=836, Experian=850
- Expected: equifax_score=850, transunion_score=836, experian_score=850
- Verification: Compare individual bureau column values

TC-4: Missing bureau score produces NULL -- Traces to BR-4
- Input: A customer with fewer than 3 bureau scores (if any exist)
- Expected: Missing bureau column is NULL
- Verification: Check for NULL values in bureau score columns

TC-5: as_of comes from customers table -- Traces to BR-5
- Input: Run for any effective date
- Expected: as_of matches the effective date from the customers data
- Verification: All rows have the same as_of matching the run date

TC-6: Empty input produces empty output -- Traces to BR-6
- Input: Weekend date with no credit_scores or customers data
- Expected: Zero rows in output; table is empty after Overwrite
- Verification: Count rows after weekend run

TC-7: Bureau matching is case-insensitive -- Traces to BR-7
- Input: Bureau values like "Equifax", "TransUnion", "Experian"
- Expected: Correctly mapped to equifax_score, transunion_score, experian_score
- Verification: No NULL bureau columns when bureau data exists

TC-8: Overwrite mode replaces previous data -- Traces to BR-8
- Input: Run for Oct 30, then Oct 31
- Expected: Only Oct 31 data remains
- Verification: Single distinct as_of after second run

TC-9: Name fields coalesced to empty string -- Traces to BR-5 / AP-5
- Input: Customer records with NULL first_name or last_name (if any)
- Expected: first_name and last_name are empty string (not NULL)
- Verification: Check that no NULL name fields exist in output

TC-10: Data values match curated output exactly -- Traces to all BRs
- Input: All dates Oct 1-31
- Expected: Exact match between curated and double_secret_curated per date
- Verification: EXCEPT-based comparison
