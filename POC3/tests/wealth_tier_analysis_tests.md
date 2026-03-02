# WealthTierAnalysis -- Test Plan

## Traceability

| Test ID | BRD Requirement | Description |
|---------|----------------|-------------|
| TC-01   | BR-1           | Total wealth per customer sums account balances and investment values |
| TC-02   | BR-2           | Wealth tier thresholds: Bronze < 10000, Silver < 100000, Gold < 500000, Platinum >= 500000 |
| TC-03   | BR-3           | Only customers with at least one account or investment appear in wealth calc |
| TC-04   | BR-4           | Output always has exactly 4 rows (one per tier), even if a tier has 0 customers |
| TC-05   | BR-5           | Tier order is fixed: Bronze, Silver, Gold, Platinum |
| TC-06   | BR-6, W5       | pct_of_customers uses banker's rounding (MidpointRounding.ToEven) to 2 decimals |
| TC-07   | BR-7, W5       | total_wealth and avg_wealth use banker's rounding to 2 decimals |
| TC-08   | BR-8           | Empty tier has customer_count=0, total_wealth=0, avg_wealth=0, pct_of_customers=0 |
| TC-09   | BR-9           | as_of column set to __maxEffectiveDate |
| TC-10   | BR-10          | Empty customers table produces zero-row output |
| TC-11   | BR-11          | totalCustomers is count of customers with wealth data, not from customers table |
| TC-12   | BR-12, AP4     | Unused columns (first_name, last_name) removed from customers DataSourcing |
| TC-13   | AP3            | External module eliminated -- V2 uses Tier 1 framework chain |
| TC-14   | AP6            | Row-by-row iteration replaced with set-based SQL |
| TC-15   | AP7            | Magic threshold values documented with inline SQL comments |
| TC-16   | Writer Config  | CsvFileWriter: Overwrite, LF, header, trailer with TRAILER|{row_count}|{date} |
| TC-17   | Edge Case      | Customer with accounts but no investments uses account balance only |
| TC-18   | Edge Case      | Customer with negative balances reduces total wealth, may shift tier |
| TC-19   | Edge Case      | Trailer row_count is always 4 in normal execution |
| TC-20   | Edge Case      | Multi-day auto-advance retains only final day (Overwrite mode) |
| TC-21   | Edge Case      | Percentage sum across tiers equals approximately 100% |
| TC-22   | Proofmark      | Proofmark comparison passes at 100% with 1 trailer row stripped |

## Test Cases

### TC-01: Total wealth per customer sums account balances and investment values
- **Traces to:** BR-1
- **Input conditions:** Run V2 job for a single effective date (e.g., 2024-10-01).
- **Expected output:** Each customer's total wealth is the sum of all their `current_balance` values from `accounts` plus all their `current_value` values from `investments` across the effective date range. A customer appearing in both tables has wealth from both sources combined.
- **Verification method:** For a sample of customers, independently compute `SUM(current_balance)` from `datalake.accounts` + `SUM(current_value)` from `datalake.investments` for the same date range. Verify V2's tier assignment is consistent with the computed wealth. Compare against V1 output to confirm identical tier counts [FSD Section 4, WealthTierAnalyzer.cs:30-47].

### TC-02: Wealth tier thresholds are correctly applied
- **Traces to:** BR-2
- **Input conditions:** Run V2 job for a date with customers whose wealth spans all four tiers.
- **Expected output:** Tier assignment uses these thresholds: Bronze (wealth < 10000), Silver (10000 <= wealth < 100000), Gold (100000 <= wealth < 500000), Platinum (wealth >= 500000). These match V1 source code, NOT the original BRD draft which incorrectly stated Bronze < $25,000 [FSD OQ-3, RESOLVED].
- **Verification method:** Independently compute customer wealth from source data, apply the CASE WHEN thresholds, and count customers per tier. Compare per-tier `customer_count` against V2 output. Verify against V1 output for exact match [FSD Section 4, WealthTierAnalyzer.cs:59-65].

### TC-03: Only customers with account/investment records appear in wealth calc
- **Traces to:** BR-3
- **Input conditions:** Run V2 job. Query `datalake.customers` for customers who have no matching rows in `accounts` or `investments` for the effective date.
- **Expected output:** Customers with zero account and zero investment records are excluded from wealth calculations. They do not appear in any tier's customer_count. The `totalCustomers` denominator for percentage calculations also excludes them.
- **Verification method:** Identify customers in `datalake.customers` with no matching `accounts` or `investments` rows. Verify `SUM(customer_count)` across all tiers in V2 output equals the count of distinct customer_ids found in the UNION of accounts and investments, NOT the count from the customers table [FSD Section 4, WealthTierAnalyzer.cs:58].

### TC-04: Output always has exactly 4 rows
- **Traces to:** BR-4
- **Input conditions:** Run V2 job for any date with at least one customer record (customers table non-empty).
- **Expected output:** Output CSV contains exactly 4 data rows (plus 1 header row and 1 trailer row = 6 lines total). One row per tier: Bronze, Silver, Gold, Platinum. Even if no customers fall into a given tier, that tier still appears with zeroed-out values.
- **Verification method:** Read V2 CSV. Count data rows (excluding header and trailer). Must be exactly 4. Compare to V1 output which also always has 4 data rows [FSD Section 4, WealthTierAnalyzer.cs:50-56,74].

### TC-05: Tier order is fixed: Bronze, Silver, Gold, Platinum
- **Traces to:** BR-5
- **Input conditions:** Run V2 job for any date.
- **Expected output:** The 4 data rows appear in this exact order: row 1 = Bronze, row 2 = Silver, row 3 = Gold, row 4 = Platinum. This is NOT alphabetical order (which would be Bronze, Gold, Platinum, Silver). The order is enforced by the `all_tiers` CTE with explicit `sort_order` values.
- **Verification method:** Read V2 CSV and verify the `wealth_tier` column values in row order. Compare to V1 output to confirm identical ordering. The FSD implements this via `ORDER BY a.sort_order` using an `all_tiers` CTE with sort_order 1-4 [FSD Section 4].

### TC-06: pct_of_customers uses banker's rounding to 2 decimal places
- **Traces to:** BR-6, W5
- **Input conditions:** Run V2 job for a date where percentage calculations produce values ending in exactly .5 at the third decimal place (e.g., a percentage that would round to X.XX5).
- **Expected output:** `pct_of_customers` is rounded using banker's rounding (round half to even): 0.5 rounds to 0, 1.5 rounds to 2, 2.5 rounds to 2, 3.5 rounds to 4. SQLite's ROUND() uses this behavior by default, matching V1's `MidpointRounding.ToEven` [WealthTierAnalyzer.cs:80].
- **Verification method:** Compare V2 output against V1 output for `pct_of_customers` values. For any value that sits on a .XX5 boundary, verify the rounding direction matches banker's rounding, not standard rounding. The BRD was corrected to state ToEven (originally said AwayFromZero) per FSD OQ-4 [FSD Section 6, W5].

### TC-07: total_wealth and avg_wealth use banker's rounding to 2 decimal places
- **Traces to:** BR-7, W5
- **Input conditions:** Run V2 job and examine monetary values.
- **Expected output:** Both `total_wealth` and `avg_wealth` are rounded to 2 decimal places using banker's rounding (ToEven). SQLite ROUND(value, 2) matches V1's `Math.Round(value, 2, MidpointRounding.ToEven)`.
- **Verification method:** Compare V2 output against V1 output for `total_wealth` and `avg_wealth` values. If any values sit on rounding boundaries, verify banker's rounding is applied. For values not on boundaries, exact match is sufficient [FSD Section 6, WealthTierAnalyzer.cs:87-88].

### TC-08: Empty tier has zeroed-out values
- **Traces to:** BR-8
- **Input conditions:** Run V2 job for a date where at least one tier has zero customers.
- **Expected output:** The empty tier's row has: `customer_count = 0`, `total_wealth = 0` (or `0.0`/`0.00`), `avg_wealth = 0` (guarded by count > 0 check), `pct_of_customers = 0` (or `0.0`/`0.00`).
- **Verification method:** Read V2 CSV and find any tier with `customer_count = 0`. Verify all other columns for that row are zero. Confirm V1 has the same behavior via `count > 0 ? totalWealth / count : 0m` guard [FSD Section 4, WealthTierAnalyzer.cs:77].

### TC-09: as_of column set to max effective date
- **Traces to:** BR-9
- **Input conditions:** Run V2 job for a specific effective date (e.g., 2024-10-15).
- **Expected output:** All 4 data rows have `as_of` equal to the max effective date (`__maxEffectiveDate`). V2 derives this via `SELECT MAX(as_of) FROM accounts`. When the executor runs for a single date, this equals that date.
- **Verification method:** Read V2 CSV and verify all `as_of` values are identical and match the expected max effective date. Compare to V1 output. Note FSD OQ-2: if accounts is empty but investments is not, V2's `MAX(as_of) FROM accounts` could return NULL. This edge case should be monitored but is unlikely in practice due to the customers empty guard [FSD Section 4, OQ-2].

### TC-10: Empty customers table produces zero-row output
- **Traces to:** BR-10
- **Input conditions:** This edge case applies if a date exists where `datalake.customers` has zero rows (while accounts/investments might have data). In practice this is a theoretical guard.
- **Expected output:** V2 produces zero data rows. The output CSV has only the header line and trailer line (with row_count = 0).
- **Verification method:** Verify by SQL analysis that the V2 query's `WHERE (SELECT cnt FROM customers_guard) > 0` clause produces zero rows when customers is empty. Compare to V1's early return of empty DataFrame [WealthTierAnalyzer.cs:20-24]. If no empty-customers test date exists, validate by code inspection [FSD Section 4].

### TC-11: totalCustomers count comes from wealth data, not customers table
- **Traces to:** BR-11
- **Input conditions:** Run V2 job. Count distinct customer_ids in `datalake.customers` vs distinct customer_ids in accounts/investments.
- **Expected output:** The denominator for `pct_of_customers` is the count of distinct customers who have at least one row in accounts or investments -- NOT the row count from the customers table. V2's `total_customers` CTE counts from `total_wealth_per_customer`, which only includes customers with wealth data.
- **Verification method:** Compute `SELECT COUNT(DISTINCT customer_id) FROM (SELECT customer_id FROM accounts UNION SELECT customer_id FROM investments)` for the date. Verify `SUM(pct_of_customers)` across all tiers in V2 output is approximately 100% (accounting for rounding). If `totalCustomers` were incorrectly derived from the customers table, the percentages would not sum correctly [FSD Section 4, WealthTierAnalyzer.cs:71].

### TC-12: Unused columns removed from customers DataSourcing (AP4)
- **Traces to:** BR-12, AP4
- **Input conditions:** Inspect V2 job config JSON.
- **Expected output:** V2 DataSourcing for `customers` has `columns: ["id"]`. The columns `first_name` and `last_name` are absent. V1 sourced these but never used them in output or calculation.
- **Verification method:** Read V2 job config. Verify the customers DataSourcing entry columns list. V1's config had `["id", "first_name", "last_name"]` but the External module only used customers for the empty guard [FSD Section 3.3, WealthTierAnalyzer.cs:18,20-24].

### TC-13: External module eliminated -- Tier 1 framework chain (AP3)
- **Traces to:** AP3 elimination
- **Input conditions:** Inspect V2 job config JSON.
- **Expected output:** V2 module chain is: DataSourcing (accounts) -> DataSourcing (investments) -> DataSourcing (customers) -> Transformation (SQL) -> CsvFileWriter. There is no External module entry. V1's `WealthTierAnalyzer.cs` is fully replaced by SQL.
- **Verification method:** Read V2 job config and verify no module has `type: "External"`. Verify the chain matches: 3 DataSourcing + 1 Transformation + 1 CsvFileWriter = 5 modules total. Output must be byte-identical to V1 despite the tier change from Tier 3 to Tier 1 [FSD Section 2].

### TC-14: Row-by-row iteration replaced with set-based SQL (AP6)
- **Traces to:** AP6 elimination
- **Input conditions:** Inspect V2 job config SQL.
- **Expected output:** V2 SQL uses set-based operations: `GROUP BY customer_id` for wealth aggregation, `CASE WHEN` for tier assignment, `GROUP BY wealth_tier` for statistics. No foreach loops or row-by-row iteration (which was in V1's C# External module).
- **Verification method:** Read V2 SQL and verify it uses GROUP BY, CASE WHEN, SUM, COUNT, AVG -- all set-based SQL constructs. The V1 External module used two `foreach` loops for accumulation and a third for tier assignment [FSD Section 7, WealthTierAnalyzer.cs:33-37,42-46,58-69].

### TC-15: Magic threshold values documented with inline comments (AP7)
- **Traces to:** AP7 elimination
- **Input conditions:** Inspect V2 job config SQL.
- **Expected output:** The tier threshold values (10000, 100000, 500000) remain identical to V1 for output equivalence, but the SQL includes comments documenting the thresholds and their source (WealthTierAnalyzer.cs:62-65).
- **Verification method:** Read V2 SQL string. Verify threshold values match V1: `< 10000` (Bronze), `< 100000` (Silver), `< 500000` (Gold), `>= 500000` (Platinum). Verify inline comments exist explaining the thresholds. Note: SQL in JSON config may have comments stripped -- verify in FSD that documentation intent is captured [FSD Section 7, AP7].

### TC-16: Writer configuration matches V1 (with trailer)
- **Traces to:** BRD Writer Configuration
- **Input conditions:** Run V2 job and inspect output file and job config.
- **Expected output:** V2 config specifies: `type: CsvFileWriter`, `source: "output"`, `includeHeader: true`, `trailerFormat: "TRAILER|{row_count}|{date}"`, `writeMode: "Overwrite"`, `lineEnding: "LF"`. Output file at `Output/double_secret_curated/wealth_tier_analysis.csv`.
- **Verification method:** Read V2 job config and verify all writer parameters match V1 (except outputFile path). Run the job and confirm: (1) first line is column header, (2) last line is trailer in format `TRAILER|4|<date>`, (3) LF line endings, (4) file fully replaced on each run. W7 and W8 do NOT apply -- the framework's CsvFileWriter handles trailer correctly [FSD Section 5].

### TC-17: Customer with accounts but no investments
- **Traces to:** BRD Edge Case
- **Input conditions:** Identify a customer_id that appears in `datalake.accounts` but NOT in `datalake.investments` for the effective date.
- **Expected output:** That customer's total wealth is based solely on their account balances (`SUM(current_balance)`). They are still included in tier assignment and count toward totalCustomers.
- **Verification method:** Query `datalake.accounts` and `datalake.investments` to find such a customer. Compute their expected wealth. Verify V2's tier assignment for this customer's wealth bracket is consistent with the output counts. Reverse applies for customers with investments but no accounts [BRD Edge Case, FSD Section 4 UNION ALL approach].

### TC-18: Customer with negative balances
- **Traces to:** BRD Edge Case
- **Input conditions:** If any customer has negative `current_balance` values that reduce their total wealth, verify the behavior.
- **Expected output:** Negative balances reduce total wealth. No floor is applied. A customer with -5000 in accounts and +3000 in investments has total wealth of -2000, which falls into the Bronze tier (wealth < 10000).
- **Verification method:** Query source data for customers with negative balances. Verify their computed wealth and tier assignment. Compare against V1 output. The CASE WHEN logic does not filter negatives -- any wealth < 10000 (including negative) is Bronze [BRD Edge Case].

### TC-19: Trailer row_count is always 4 in normal execution
- **Traces to:** BRD Edge Case (trailer)
- **Input conditions:** Run V2 job for any date where customers table is non-empty.
- **Expected output:** The trailer line is `TRAILER|4|<maxEffectiveDate>`. The `{row_count}` token always resolves to 4 because the output always has exactly 4 data rows (one per tier). The `{date}` token resolves to `__maxEffectiveDate`.
- **Verification method:** Read V2 CSV output and inspect the last line. Verify it matches the expected format. W7 (inflated trailer count) does NOT apply because V2 uses the framework's CsvFileWriter which counts output rows correctly, and V1 also uses CsvFileWriter (not manual file writing) [FSD Section 5, BRD Edge Case].

### TC-20: Multi-day auto-advance retains only final day (Overwrite)
- **Traces to:** BRD Write Mode Implications
- **Input conditions:** Run V2 job for a multi-day range (e.g., 2024-10-01 through 2024-10-03).
- **Expected output:** After auto-advance completes, the output CSV contains only the final day's results. The `as_of` column in all 4 data rows equals the final date (2024-10-03). Trailer `{date}` also reflects the final date. Data from earlier days is overwritten.
- **Verification method:** Run V2 for the date range. Read output CSV. Verify all `as_of` values equal the final date. Verify trailer date matches. Confirm only one date's data is present [FSD Section 5, writeMode: Overwrite].

### TC-21: Percentage sum across tiers approximately equals 100%
- **Traces to:** BR-6, BR-11
- **Input conditions:** Run V2 job for any date with customers.
- **Expected output:** `SUM(pct_of_customers)` across the 4 tier rows should be approximately 100.00. Due to banker's rounding on individual percentages, the sum may differ slightly from exactly 100.00 (e.g., 99.99 or 100.01). This matches V1 behavior.
- **Verification method:** Read V2 CSV and sum the `pct_of_customers` column. Verify it is within 0.04 of 100.00 (max rounding error for 4 rows each rounded to 2 decimal places). Compare the exact sum to V1 output.

### TC-22: Proofmark comparison passes at 100% threshold
- **Traces to:** FSD Proofmark Config
- **Input conditions:** Run both V1 and V2 jobs for the full effective date range. Run Proofmark with config: `reader: csv`, `threshold: 100.0`, `header_rows: 1`, `trailer_rows: 1`.
- **Expected output:** Proofmark reports PASS. All 4 data rows match exactly between `Output/curated/wealth_tier_analysis.csv` and `Output/double_secret_curated/wealth_tier_analysis.csv`. The trailer is stripped by Proofmark (`trailer_rows: 1`) and not compared. Zero mismatches, zero exclusions, zero fuzzy columns.
- **Verification method:** Execute Proofmark with the config from FSD Section 8. Verify exit code is 0. Confirm the report shows 100% match across all 6 columns (`wealth_tier`, `customer_count`, `total_wealth`, `avg_wealth`, `pct_of_customers`, `as_of`). W5 (banker's rounding) is naturally handled by SQLite ROUND matching V1's ToEven, so no fuzzy columns are needed [FSD Sections 6, 8].
