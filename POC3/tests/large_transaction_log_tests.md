# LargeTransactionLog — V2 Test Plan

## Job Info
- **V2 Config**: `large_transaction_log_v2.json`
- **Tier**: Tier 1 (Framework Only)
- **External Module**: None (V1 used `ExternalModules.LargeTransactionProcessor`; V2 replaces with Transformation SQL)

## Pre-Conditions
- Data sources needed:
  - `datalake.transactions` — columns: `transaction_id`, `account_id`, `txn_timestamp`, `txn_type`, `amount`, `description`, `as_of` (auto-appended by DataSourcing)
  - `datalake.accounts` — columns: `account_id`, `customer_id`, `as_of` (auto-appended by DataSourcing)
  - `datalake.customers` — columns: `id`, `first_name`, `last_name`, `as_of` (auto-appended by DataSourcing)
- V1 also sourced `datalake.addresses` (AP1: dead-end sourcing — never referenced by External module) and extra account columns (AP4: `account_type`, `account_status`, `open_date`, `current_balance`, `interest_rate`, `credit_limit`, `apr`), both eliminated in V2
- Effective date range: `firstEffectiveDate` = `2024-10-01`, auto-advanced through `2024-12-31`
- V1 baseline output must exist at `Output/curated/large_transaction_log/` (Parquet part files)
- V2 output writes to `Output/double_secret_curated/large_transaction_log/`
- Expected data volume: approximately 288,340 rows with amount > 500 across the full date range (BR-10)

## Test Cases

### TC-1: Output Schema Validation
- **Expected columns** (exact order from FSD Section 4):
  1. `transaction_id` (INTEGER — direct passthrough from transactions)
  2. `account_id` (INTEGER — direct passthrough from transactions)
  3. `customer_id` (INTEGER — resolved via accounts LEFT JOIN; `COALESCE(a.customer_id, 0)` when no match)
  4. `first_name` (TEXT — from customers via two-step lookup; `COALESCE(c.first_name, '')` when NULL or no match)
  5. `last_name` (TEXT — from customers via two-step lookup; `COALESCE(c.last_name, '')` when NULL or no match)
  6. `txn_type` (TEXT — direct passthrough from transactions)
  7. `amount` (NUMERIC/DECIMAL — direct passthrough, only rows where amount > 500)
  8. `description` (TEXT — direct passthrough from transactions)
  9. `txn_timestamp` (TIMESTAMP — direct passthrough from transactions)
  10. `as_of` (DATE — direct passthrough from transactions.as_of)
- Verify column order matches V1 exactly: `transaction_id, account_id, customer_id, first_name, last_name, txn_type, amount, description, txn_timestamp, as_of`
- Verify no extra columns are present (notably, no address columns and no unused account columns like `account_type`, `account_status`, etc.)

### TC-2: Row Count Equivalence
- V1 vs V2 must produce identical row counts per effective date
- Row count equals the number of transactions where `amount > 500` (strictly greater than, not >=; BR-1)
- Transactions with `amount == 500` must be EXCLUDED
- Expected total across all dates: approximately 288,340 rows (BR-10)
- Since writeMode is Append, cumulative output grows with each effective date. Total row count in the output directory after full auto-advance must match V1.

### TC-3: Data Content Equivalence
- All values must be byte-identical to V1 output
- Key comparison areas:
  - `transaction_id`, `account_id`, `txn_type`, `amount`, `description`, `txn_timestamp`, `as_of` are direct passthroughs from transactions — must match exactly
  - `customer_id` must be 0 (not NULL) when no matching account is found for the transaction's `account_id` (BR-3)
  - `first_name` and `last_name` must be empty strings (not NULL) when no matching customer is found or when the name values are NULL in the source (BR-3, BR-8)
  - Two-step lookup chain must be correct: transaction.account_id -> accounts.customer_id -> customers.id (BR-2)
- **No W-codes affect this job** — byte-identical match is expected without special handling
- **Row ordering risk**: V1 iterates transactions in DataFrame order (DataSourcing's `ORDER BY as_of`). V2 SQL has no ORDER BY, so within a single as_of date the row order depends on SQLite's query plan for the LEFT JOIN. Parquet is columnar, and Proofmark should ideally compare row sets rather than row sequences. If comparison fails due to ordering, add `ORDER BY t.transaction_id` to the SQL.
- **Join strategy**: V2 joins on `t.as_of = a.as_of` and `t.as_of = c.as_of` to match each transaction to the same-day account/customer snapshot. This is equivalent to V1's dictionary behavior in single-day execution (auto-advance runs one date at a time), where only one as_of value exists per table per run.

### TC-4: Writer Configuration
- **Writer type**: `ParquetFileWriter` — verify output is Parquet format, not CSV
- **source**: `output` — the Transformation result DataFrame
- **outputDirectory**: `Output/double_secret_curated/large_transaction_log/`
- **numParts**: 3 — verify output is split across exactly 3 part files per effective date
- **writeMode**: `Append` — verify new part files are ADDED to the output directory on each run, not replacing existing ones. Multi-day runs accumulate data.
- Verify no header/trailer concerns (Parquet does not have CSV-style headers or trailers)

### TC-5: Anti-Pattern Elimination Verification
| AP-Code | What to Verify |
|---------|----------------|
| AP1 | V2 config does NOT contain a DataSourcing module for `addresses`. V1 sourced `datalake.addresses` (address_id, customer_id, address_line1, city) but the External module never referenced it. Confirm the `addresses` DataSourcing entry is completely absent from V2 config. |
| AP3 | V2 config does NOT contain an External module. The chain is DataSourcing (x3) + Transformation + ParquetFileWriter. The V1 External module `LargeTransactionProcessor` is entirely replaced by SQL. |
| AP4 | V2 `accounts` DataSourcing sources only `account_id` and `customer_id`. V1 sourced 9 columns from accounts (`account_id`, `customer_id`, `account_type`, `account_status`, `open_date`, `current_balance`, `interest_rate`, `credit_limit`, `apr`) but only used 2. Confirm the other 7 columns are absent from V2 config. |
| AP6 | No row-by-row iteration exists. The V1 `foreach` loop over transactions, the dictionary-building loops for account-to-customer and customer-to-name lookups — all replaced by a single SQL query with LEFT JOINs and WHERE. |
| AP7 | V2 SQL documents the magic value threshold (500) with a comment explaining its business meaning. The value itself is unchanged (output equivalence). |

### TC-6: Edge Cases
1. **Missing account mapping**: Transactions whose `account_id` has no match in the `accounts` table must still appear in output with `customer_id = 0` and `first_name = ""`, `last_name = ""`. LEFT JOIN + COALESCE handles this. Verify these transactions are NOT dropped.
2. **Missing customer**: If an account's `customer_id` has no match in `customers`, `first_name` and `last_name` must be empty strings. `customer_id` still resolves from the account row (it is not defaulted to 0 in this case — only the names default).
3. **NULL first_name or last_name in source data**: Must coalesce to empty string via `COALESCE(c.first_name, '')` and `COALESCE(c.last_name, '')` (BR-8). This is distinct from the missing-customer case but produces the same output.
4. **Boundary value: amount = 500**: Must be EXCLUDED. The filter is strictly `> 500`, not `>= 500`. Verify no rows with amount exactly 500 appear in output.
5. **Boundary value: amount = 500.01**: Must be INCLUDED. Just above the threshold.
6. **Append mode reprocessing**: If the same effective date is run twice, transactions are duplicated in the output directory. This is V1 behavior and V2 must replicate it. Verify that re-running does append, not replace.
7. **Weekend effective dates**: Transactions table may have no data for weekends. DataSourcing returns empty DataFrame, SQL returns zero rows, no part files are written (or empty part files). Both V1 and V2 should produce the same result.
8. **Empty accounts or customers table**: V1 returns empty output if accounts or customers is empty [LargeTransactionProcessor.cs:19-22]. V2's SQL LEFT JOIN would still return transaction rows with NULL/default customer info. **Potential divergence**: V1 outputs 0 rows, V2 outputs transactions with customer_id=0 and empty names. FSD notes this is theoretical — datalake always has account and customer data for every as_of date. If Proofmark comparison fails on this edge case, Tier 2 escalation may be needed.
9. **Empty transactions table**: SQL returns zero rows naturally. V1 also returns empty output [LargeTransactionProcessor.cs:25-29]. Both should match.
10. **3-part Parquet split**: Verify output is split across exactly 3 part files. The framework handles the splitting logic. Verify total row count across all 3 parts equals the expected filtered count.
11. **V1 dictionary overwrite behavior (BR-9)**: V1 builds account-to-customer and customer-to-name dictionaries by iterating ALL rows, with later entries overwriting earlier ones. In single-day execution (one as_of per run), this is a no-op — each key appears once. V2's `as_of`-aligned JOIN is equivalent. If multi-day ranges were ever used, behavior could diverge, but auto-advance runs one day at a time.

### TC-7: Proofmark Configuration
- **Expected proofmark settings** (from FSD Section 8):
  ```yaml
  comparison_target: "large_transaction_log"
  reader: parquet
  threshold: 100.0
  ```
- **Threshold**: 100.0 (all values must be a perfect match)
- **Excluded columns**: None (all columns are deterministic; no runtime timestamps or UUIDs)
- **Fuzzy columns**: None (no floating-point accumulation; `amount` is a direct passthrough, not accumulated or computed)
- **Row ordering risk**: Parquet is columnar. If Proofmark does order-sensitive comparison and ordering diverges between V1 and V2, add `ORDER BY t.transaction_id` to the V2 SQL. Start without it; add only if comparison fails.

## W-Code Test Cases

No W-codes apply to this job. The FSD explicitly analyzed and ruled out all W-codes:

| W-Code | Why Not Applicable |
|--------|-------------------|
| W1/W2 | No Sunday skip or weekend fallback logic. Empty output on weekends is natural (no data). |
| W3a/W3b/W3c | No boundary summary rows (weekly, monthly, or quarterly). |
| W4 | No integer division or percentage calculations. |
| W5 | No rounding operations. |
| W6 | No floating-point accumulation. `amount` is a direct passthrough. |
| W7/W8 | No trailer. Parquet output does not have trailers. |
| W9 | Append mode is correct and intentional for a transaction log. Not a wrong-write-mode bug. |
| W10 | numParts = 3 for ~288K rows. Reasonable; not absurd. |
| W12 | Parquet output. No CSV header duplication concerns. |

Since no W-codes apply, there are no TC-W test cases for this job. The absence of wrinkles means V2 output should be straightforwardly byte-identical to V1.

## Notes
- **Empty accounts/customers divergence is the highest-risk item** for this job. V1 short-circuits and returns empty output when accounts or customers is empty. V2's LEFT JOIN approach would still return transaction rows with default customer info. The FSD documents this as theoretical (datalake always has data for valid dates), and recommends Proofmark comparison to catch it if it materializes. If it does, Tier 2 escalation with an External module that replicates the early-return guard may be needed.
- **Row ordering is the second-highest risk**. V1's row order comes from DataFrame iteration (DataSourcing ORDER BY as_of). V2's SQL LEFT JOIN may produce a different row order within a single as_of date depending on SQLite's query plan. Parquet comparison should ideally be order-independent, but verify with Proofmark.
- **Data volume**: ~288,340 qualifying rows across the full date range (BR-10). With numParts=3 and Append mode, the output directory will accumulate many part files over the multi-day auto-advance. Verify the total row count matches V1 after a full run.
- **Two-step lookup correctness**: The join chain (transactions -> accounts -> customers) must be carefully verified. A wrong join condition (e.g., joining accounts on `customer_id` instead of `account_id`) would produce incorrect customer resolution. Verify the SQL join conditions: `t.account_id = a.account_id AND t.as_of = a.as_of` then `a.customer_id = c.id AND t.as_of = c.as_of`.
- **Output path difference**: The only intentional V1-vs-V2 difference is the directory (`curated` vs `double_secret_curated`). Proofmark handles this via `comparison_target` mapping.
- **Dead-end addresses source**: V1 sourced `datalake.addresses` but the External module never used it. V2 correctly removes this. If someone adds it back, it wastes resources but does not affect output.
- **Unused account columns**: V1 sourced 9 columns from accounts but only used `account_id` and `customer_id`. V2 correctly trims this to just the 2 needed columns.
