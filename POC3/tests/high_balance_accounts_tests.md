# HighBalanceAccounts — V2 Test Plan

## Job Info
- **V2 Config**: `high_balance_accounts_v2.json`
- **Tier**: Tier 1 (Framework Only)
- **External Module**: None (V1 used `ExternalModules.HighBalanceFilter`; V2 replaces with Transformation SQL)

## Pre-Conditions
- Data sources needed:
  - `datalake.accounts` — columns: `account_id`, `customer_id`, `account_type`, `current_balance`, `as_of` (auto-appended by DataSourcing)
  - `datalake.customers` — columns: `id`, `first_name`, `last_name`, `as_of` (auto-appended by DataSourcing)
- V1 also sourced `account_status` from accounts (AP4 unused column), which V2 drops
- Effective date range: `firstEffectiveDate` = `2024-10-01`, auto-advanced through `2024-12-31`
- V1 baseline output must exist at `Output/curated/high_balance_accounts.csv`
- V2 output writes to `Output/double_secret_curated/high_balance_accounts.csv`
- Accounts table is weekday-only; weekend effective dates produce empty output

## Test Cases

### TC-1: Output Schema Validation
- **Expected columns** (exact order from FSD Section 4):
  1. `account_id` (INTEGER — passthrough from accounts)
  2. `customer_id` (INTEGER — passthrough from accounts)
  3. `account_type` (TEXT — passthrough from accounts)
  4. `current_balance` (REAL/NUMERIC — passthrough, only rows > 10000)
  5. `first_name` (TEXT — from customers via LEFT JOIN, empty string if no match)
  6. `last_name` (TEXT — from customers via LEFT JOIN, empty string if no match)
  7. `as_of` (TEXT/DATE — from accounts.as_of per FSD correction of BR-6)
- Verify the header row is `account_id,customer_id,account_type,current_balance,first_name,last_name,as_of`
- Verify no extra columns are present (notably, `account_status` must NOT appear)

### TC-2: Row Count Equivalence
- V1 vs V2 must produce identical row counts
- Row count equals the number of accounts where `current_balance > 10000` (strictly greater than, not >=)
- Accounts with `current_balance == 10000` must be EXCLUDED

### TC-3: Data Content Equivalence
- All values must be byte-identical to V1 output
- Key comparison areas:
  - `account_id`, `customer_id`, `account_type` are passthrough values — must match exactly
  - `current_balance` is a passthrough (no arithmetic) — must match exactly
  - `first_name` and `last_name` must be empty strings (not NULL) for customers with no match
  - `as_of` must match V1's per-row value from the account row (FSD correction to BR-6: V1 code actually uses `acctRow["as_of"]`, not `sharedState["__maxEffectiveDate"]`)
- **No W-codes affect this job** — there are no output-affecting wrinkles, so byte-identical match is expected without any special handling
- Row ordering: V1 does NOT sort output. Row order depends on DataSourcing iteration order (`ORDER BY as_of`). V2 SQL has no ORDER BY, so order depends on the SQLite query plan. Verify ordering matches V1 or confirm Proofmark handles unordered comparison.

### TC-4: Writer Configuration
- **includeHeader**: `true` — verify header row is present as first line
- **writeMode**: `Overwrite` — verify file is replaced on each run (only last effective date's data persists)
- **lineEnding**: `LF` — verify all line endings are `\n` (not `\r\n`)
- **trailerFormat**: Not specified — verify NO trailer row exists in the output file
- **encoding**: UTF-8 without BOM
- **outputFile**: `Output/double_secret_curated/high_balance_accounts.csv`

### TC-5: Anti-Pattern Elimination Verification
| AP-Code | What to Verify |
|---------|----------------|
| AP3 | V2 config does NOT contain an External module. The chain is DataSourcing + DataSourcing + Transformation + CsvFileWriter. The V1 External module `HighBalanceFilter` is entirely replaced by SQL. |
| AP4 | V2 `accounts` DataSourcing sources only `account_id`, `customer_id`, `account_type`, `current_balance` (not `account_status`). Confirm `account_status` is absent from the V2 config. |
| AP6 | No row-by-row iteration exists. Balance filtering and customer name lookup are performed by a single SQL query with JOIN + WHERE. |

### TC-6: Edge Cases
1. **Empty input behavior**:
   - If `accounts` is empty: SQL produces zero rows. CsvFileWriter writes header-only file. V1 returns early with empty DataFrame — framework CsvFileWriter then writes header-only file. Should match.
   - If `customers` is empty: Transformation module's `RegisterTable` skips the `customers` table (no rows). The LEFT JOIN in SQL will fail because the `customers` table does not exist in SQLite. **Potential divergence**: V1 returns empty output if customers is empty; V2 may throw a SQL error. In practice, `customers` always has data in the datalake for valid dates. If this edge case surfaces, Tier 2 escalation may be needed.
2. **Exactly $10,000 balance**: Accounts with `current_balance == 10000` must be EXCLUDED. The SQL uses `> 10000` (strictly greater than), matching V1's `balance > 10000`.
3. **Negative balances**: Credit accounts can have negative balances (e.g., -2688.00). These are always < 10000, so they are always excluded. No special handling needed.
4. **Weekend dates**: The accounts table is weekday-only. Weekend effective dates yield an empty accounts table, producing a header-only output. Both V1 and V2 should produce the same empty result.
5. **No qualifying accounts**: If all accounts have `current_balance <= 10000`, output is header-only CSV (zero data rows). V1 produces the same result.
6. **Customer ID with no match**: Accounts whose `customer_id` has no corresponding `id` in `customers` must have `first_name` and `last_name` set to empty strings (not NULL, not "Unknown"). SQL `LEFT JOIN` + `COALESCE(..., '')` handles this.
7. **CAST behavior**: V2 SQL uses `CAST(a.current_balance AS REAL) > 10000` for the balance comparison. V1 uses `Convert.ToDecimal`. For values well above or well below 10000, these are equivalent. Verify no edge-case values near the 10000 boundary are affected by REAL vs decimal precision.
8. **as_of source discrepancy**: BRD states `as_of` comes from `__maxEffectiveDate` (shared state). FSD corrects this: actual V1 code uses `acctRow["as_of"]`. In practice these are identical (executor runs one date at a time). Verify V2's `a.as_of` matches V1 output.

### TC-7: Proofmark Configuration
- **Expected proofmark settings** (from FSD Section 8):
  ```yaml
  comparison_target: "high_balance_accounts"
  reader: csv
  threshold: 100.0
  csv:
    header_rows: 1
    trailer_rows: 0
  ```
- **Threshold**: 100.0 (all values must be a perfect match)
- **Excluded columns**: None (all columns are deterministic; `as_of` is derived from effective date, not execution time)
- **Fuzzy columns**: None (no floating-point accumulation, no rounding, no percentage calculations; `current_balance` is a passthrough value)

## W-Code Test Cases

No W-codes apply to this job. The FSD explicitly analyzed and ruled out all W-codes:

| W-Code | Why Not Applicable |
|--------|-------------------|
| W1/W2 | No special weekend logic. Empty output on weekends is natural (no data in datalake). |
| W4 | No percentage calculations. |
| W5 | No rounding operations. |
| W6 | No floating-point accumulation. Balance is passthrough. |
| W7/W8 | No trailer in this job. |
| W9 | Overwrite is the documented V1 behavior and is correctly reproduced. Not a "wrong" write mode bug — it's intentional V1 behavior. |
| W12 | Overwrite mode, not Append. No repeated headers. |

Since no W-codes apply, there are no TC-W test cases for this job. The absence of wrinkles means V2 output should be straightforwardly byte-identical to V1.

## Notes
- **Row ordering is the primary risk** for this job. V1 does not explicitly sort output — row order depends on the iteration order of the accounts DataFrame, which comes from DataSourcing's `ORDER BY as_of`. V2's SQL has no ORDER BY clause. If SQLite returns rows in a different order than DataSourcing's DataFrame iteration order, Proofmark comparison may fail on row ordering even though the data content is identical. If this occurs, either add an ORDER BY clause to the V2 SQL (matching DataSourcing's implicit ordering) or configure Proofmark for order-independent comparison.
- **BR-6 correction**: The BRD states `as_of` comes from `__maxEffectiveDate`. The FSD corrects this based on code analysis: V1 actually uses `acctRow["as_of"]`. In practice they're equivalent (single-day execution), but the V2 SQL correctly uses `a.as_of` to match actual V1 code behavior.
- **Empty customers edge case**: The FSD appendix (BR-4 Edge Case Analysis) documents that if `customers` is empty, V1 returns empty output, but V2's SQL LEFT JOIN would fail because Transformation's `RegisterTable` does not create the SQLite table for empty DataFrames. This is a known potential divergence point. In practice, `customers` always has data for valid effective dates (2024-10-01 through 2024-12-31), so this is unlikely to surface.
- **Output path difference**: The only intentional V1-vs-V2 difference is the directory (`curated` vs `double_secret_curated`). Proofmark handles this via `comparison_target` mapping.
- **BRD Open Question**: Whether the threshold should be strictly greater than ($10,000) or greater-than-or-equal-to ($10,000). V1 code uses `>` (strictly greater), so V2 replicates that. This is a business rule question, not a V2 migration concern.
