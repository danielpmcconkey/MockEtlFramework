# DormantAccountDetection -- V2 Test Plan

## Job Info
- **V2 Config**: `dormant_account_detection_v2.json`
- **Tier**: Tier 1 (Framework Only)
- **External Module**: None (V1 used `ExternalModules.DormantAccountDetector`, eliminated via AP3)

## Pre-Conditions
- **Data sources required:**
  - `datalake.accounts` -- columns: `account_id`, `customer_id`, `account_type`, `current_balance`, `as_of` (auto-appended). Schema: account_id (integer), customer_id (integer), account_type (varchar), current_balance (numeric), as_of (date).
  - `datalake.transactions` -- columns: `account_id`, `as_of` (auto-appended). V2 drops `transaction_id`, `txn_type`, `amount` (AP4 elimination). Schema: account_id (integer), as_of (date).
  - `datalake.customers` -- columns: `id`, `first_name`, `last_name`, `as_of` (auto-appended). Schema: id (integer), first_name (varchar), last_name (varchar), as_of (date).
- **Effective date range:** `firstEffectiveDate` = `2024-10-01`. Auto-advance runs one date at a time (`minEffectiveDate == maxEffectiveDate`).
- **Date considerations:** Weekend dates (Saturday/Sunday) trigger W2 (weekend fallback) -- target date shifts to the preceding Friday. This affects which transactions are checked for dormancy, NOT which accounts are sourced.

## Test Cases

### TC-1: Output Schema Validation
- **Expected columns (exact order per FSD Section 4):**
  1. `account_id` -- INTEGER (V1 casts via `Convert.ToInt32`)
  2. `customer_id` -- INTEGER (V1 casts via `Convert.ToInt32`)
  3. `first_name` -- TEXT (string, empty string default for missing customers)
  4. `last_name` -- TEXT (string, empty string default for missing customers)
  5. `account_type` -- TEXT (string, passthrough from accounts)
  6. `current_balance` -- NUMERIC/DECIMAL (passthrough from accounts; see type risk note below)
  7. `as_of` -- TEXT (string, yyyy-MM-dd format, weekend-adjusted target date)
- **Type risk:** V2 passes data through SQLite Transformation, which converts `int` to `long` and `decimal` to `double`. Parquet column types may differ from V1 (`int?` vs `long?` for IDs, `decimal?` vs `double?` for current_balance). FSD Section 4 documents this risk with an escalation path to Tier 2 if Parquet comparison fails.
- **Verification:** Inspect V2 Parquet output schema. Compare column types against V1 Parquet output. Flag any type mismatches for escalation.

### TC-2: Row Count Equivalence
- V1 and V2 must produce identical row counts for every effective date in the auto-advance range.
- On weekends (W2), target date shifts to Friday. Both V1 and V2 should produce the same dormant account set because they use the same shifted target date for transaction matching.
- **Multi-date account duplication (BR-12):** V1 does not dedup accounts by account_id. An account appearing on multiple as_of dates in the effective range produces multiple output rows. V2's SQL preserves this behavior (no GROUP BY on account_id in the final SELECT). In single-day execution, each account_id appears once per run.
- **Verification method:** Run both V1 and V2 across the full effective date range. Compare row counts per date. Proofmark threshold is 100.0.

### TC-3: Data Content Equivalence
- All values must be byte-identical to V1 output for every effective date (subject to type considerations noted in TC-1).
- Column values to verify:
  - `account_id`: Integer passthrough. V1 casts via `Convert.ToInt32`. V2 uses `CAST(a.account_id AS INTEGER)`.
  - `customer_id`: Integer passthrough. Same cast pattern as account_id.
  - `first_name` / `last_name`: String values from customer lookup. NULL defaults to empty string. V2 uses `COALESCE(cl.first_name, '')` matching V1's `GetValueOrDefault(customerId, ("", ""))`.
  - `account_type`: String passthrough, no transformation.
  - `current_balance`: Numeric passthrough. **Risk:** V1 preserves PostgreSQL `decimal` type; V2 converts through SQLite `REAL` (double). Values may have floating-point representation differences.
  - `as_of`: String. V1 uses `targetDate.ToString("yyyy-MM-dd")`. V2 uses SQLite `date()` function which returns `YYYY-MM-DD` format strings. Should match.
- **Row ordering:** No explicit ORDER BY in V1 or V2. Natural order from DataSourcing (ascending `as_of`) is preserved. V2 SQL has no ORDER BY clause, matching V1.
- **W-codes affecting comparison:** W2 (weekend fallback) -- on weekends, as_of column contains Friday's date, not the actual Saturday/Sunday date.
- **Verification method:** Proofmark comparison with threshold 100.0. If type mismatches cause failure, follow FSD escalation path (fuzzy matching first, then Tier 2).

### TC-4: Writer Configuration
- **Verify these ParquetFileWriter properties match V1:**

| Property | Expected Value | V1 Reference |
|----------|---------------|--------------|
| source | `output` | [dormant_account_detection.json:33] |
| numParts | `1` | [dormant_account_detection.json:33] |
| writeMode | `Overwrite` | [dormant_account_detection.json:34] |
| outputDirectory | `Output/double_secret_curated/dormant_account_detection/` | Path change per V2 convention (V1: `Output/curated/...`) |

- **Parquet part file verification:** Confirm exactly 1 part file is written per execution (numParts=1).
- **Overwrite verification:** Confirm each execution replaces the entire output directory contents. No accumulation across auto-advance dates.

### TC-5: Anti-Pattern Elimination Verification

| AP-code | What to Verify |
|---------|---------------|
| AP3 (Unnecessary External module) | V2 config contains NO `External` module entry. The module chain is `DataSourcing (x3) -> Transformation -> ParquetFileWriter`. The `DormantAccountDetector` assembly is not referenced. |
| AP4 (Unused columns) | V2 DataSourcing for `transactions` sources only `["account_id"]`. V1's `transaction_id`, `txn_type`, and `amount` are removed. Confirm these columns do not appear in the V2 config. |
| AP6 (Row-by-row iteration) | V2 uses a SQL Transformation module with CTEs for target date computation, active account anti-join, and customer lookup. No `foreach` loops. Confirm the Transformation SQL is fully set-based. |

### TC-6: Edge Cases

| Edge Case | Expected Behavior | Verification |
|-----------|-------------------|--------------|
| Saturday execution (W2) | Target date shifts to Friday (maxDate - 1 day). Transactions checked against Friday. Since DataSourcing fetches Saturday data but target is Friday, no transactions match -- ALL accounts are dormant. | Run on a Saturday (e.g., 2024-10-05). Confirm all accounts appear in output. Confirm as_of = `2024-10-04` (Friday). Compare against V1. |
| Sunday execution (W2) | Target date shifts to Friday (maxDate - 2 days). Same behavior as Saturday -- all accounts dormant. | Run on a Sunday (e.g., 2024-10-06). Confirm all accounts in output. Confirm as_of = `2024-10-04` (Friday). Compare against V1. |
| Weekday execution | Normal processing. Target date = effective date. Accounts with transactions on that date are active; rest are dormant. | Run on a weekday (e.g., 2024-10-01, Tuesday). Confirm dormant accounts match V1. |
| Empty accounts DataFrame | Empty output. V1 returns empty DataFrame [DormantAccountDetector.cs:20-24]. V2: SQL produces zero rows (or fails if SQLite table not registered). | Verify graceful handling. See FSD caveat about empty DataFrame and SQLite table registration. |
| No transactions at all | All accounts are dormant. `active_accounts` CTE returns empty set, so the anti-join includes all accounts. | Verify all accounts appear in output with dormant status. |
| Customer not found for an account | `first_name` and `last_name` default to empty string `""`. V2 uses LEFT JOIN + COALESCE. V1 uses `GetValueOrDefault(customerId, ("", ""))`. | Verify orphaned accounts (customer_id not in customers table) show empty strings for name fields. |
| Multi-date account duplication (BR-12) | In multi-day effective range, same account_id appears once per date snapshot. Each row independently checked for dormancy. In single-day execution, each account_id appears once. | Verify row count matches V1 exactly. No dedup should occur. |
| Weekend transaction data ignored | If datalake has transaction data for Saturday/Sunday, those transactions are NOT used for dormancy detection (target date is Friday). | Verify that weekend transactions do not prevent accounts from being flagged dormant. |
| as_of output is string, not DateOnly | V2 produces as_of as TEXT from SQLite `date()` function. V1 produces it via `targetDate.ToString("yyyy-MM-dd")`. Both should be string type in Parquet. | Verify Parquet schema shows `as_of` as string type in both V1 and V2. |

### TC-7: Proofmark Configuration
- **Expected proofmark settings from FSD Section 8:**

```yaml
comparison_target: "dormant_account_detection"
reader: parquet
threshold: 100.0
```

- **Threshold:** 100.0 (strict match). No tolerance for differences unless type issues force adjustment.
- **Excluded columns:** None initially.
- **Fuzzy columns:** None initially. If `current_balance` type conversion (decimal -> double) causes comparison failure, escalation path is:
  1. Add fuzzy matching for `current_balance` with absolute tolerance of 0.001.
  2. If insufficient, escalate to Tier 2 External module for type coercion.
- **Rationale:** All output fields are deterministic. BRD states "Non-Deterministic Fields: None identified." However, the SQLite type coercion risk (int->long, decimal->double) may require proofmark config adjustments after Phase D testing.

## W-Code Test Cases

### TC-W2: Weekend Fallback (W2)
- **What the wrinkle is:** V1 shifts the target date from Saturday to Friday (maxDate - 1 day) and from Sunday to Friday (maxDate - 2 days) [DormantAccountDetector.cs:28-30]. This means dormancy is always evaluated against a weekday. On weekends, since DataSourcing fetches weekend-dated data but the target is Friday, no transactions match the target date, making ALL accounts dormant.
- **How V2 handles it:** SQL CTE `target` uses a CASE expression:
  - `WHEN strftime('%w', MAX(a.as_of)) = '6' THEN date(MAX(a.as_of), '-1 day')` (Saturday -> Friday)
  - `WHEN strftime('%w', MAX(a.as_of)) = '0' THEN date(MAX(a.as_of), '-2 days')` (Sunday -> Friday)
  - `ELSE MAX(a.as_of)` (weekday, no adjustment)

  The `active_accounts` CTE then filters transactions by `t.as_of = td.target_date`, which on weekends yields no matches (no Friday transactions in Saturday/Sunday DataSourcing data).
- **What to verify:**
  1. Run V2 on Saturday 2024-10-05. Confirm `target_date` resolves to `2024-10-04` (Friday).
  2. Run V2 on Sunday 2024-10-06. Confirm `target_date` resolves to `2024-10-04` (Friday).
  3. Run V2 on Monday 2024-10-07. Confirm `target_date` remains `2024-10-07`.
  4. For all three dates, confirm the `as_of` column in output matches the adjusted target date (Friday for weekend runs).
  5. On weekends, confirm ALL accounts appear as dormant (since no transactions match Friday target in weekend-sourced data).
  6. Compare V2 weekend output byte-for-byte against V1 weekend output.

## Notes
- **Type coercion risk (CRITICAL):** This is the biggest risk for this job. The FSD documents that SQLite Transformation converts `int` to `long` and `decimal` to `double`. Parquet output types may differ between V1 (`int?`, `decimal?`) and V2 (`long?`, `double?`). The FSD includes explicit CAST operations and an escalation path. During Phase D, if Proofmark comparison fails on type grounds:
  1. First, try fuzzy matching for `current_balance` (tolerance 0.001).
  2. If ID types differ (int vs long in Parquet schema), escalate to Tier 2 with a minimal External module for type coercion after the Transformation.
  3. Last resort: Tier 3 if business logic and type coercion can't be separated.
- **Customer lookup last-write-wins (BR-8):** V1 builds a dictionary iterating customers in DataSourcing order (ascending as_of). Last write wins, meaning the row with the highest as_of per customer_id determines the name. V2's `customer_lookup` CTE uses `MAX(as_of)` GROUP BY + self-join to pick the latest row. In single-day execution, each customer_id has one row, so this is moot. In multi-day ranges, both approaches select the latest as_of. Verify names match V1 for all dates.
- **Multi-date account duplication (BRD OQ-1):** The BRD notes this may be unintentional -- an account appearing on 5 as_of dates produces 5 output rows all with the same adjusted target date as as_of. In single-day auto-advance, each account appears once per run, so this is not observable. V2 preserves V1 behavior regardless.
- **Account status not filtered (BRD OQ-2):** V1 does not filter by account_status (e.g., only Active accounts). V2 reproduces this -- all accounts are evaluated for dormancy regardless of status. This is documented but not changed.
- **firstEffectiveDate:** V2 uses `2024-10-01` matching V1 exactly.
- **No ORDER BY:** Neither V1 nor V2 explicitly sorts output. Row order depends on DataSourcing's natural order (ascending as_of). Proofmark comparison should account for potential row-order differences in Parquet files if the reader is order-sensitive.
