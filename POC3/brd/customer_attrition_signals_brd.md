# CustomerAttritionSignals — Business Requirements Document

## Overview
Produces a per-customer attrition risk scorecard by combining account counts, transaction activity, and average balance into a weighted attrition score with a categorical risk level. Output is a Parquet snapshot per effective date.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `output`
- **outputDirectory**: `Output/curated/customer_attrition_signals/`
- **numParts**: 1
- **writeMode**: Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.customers | id, first_name, last_name | Effective date range (injected by executor) | [customer_attrition_signals.json:8-11] |
| datalake.accounts | account_id, customer_id, current_balance | Effective date range (injected by executor) | [customer_attrition_signals.json:13-17] |
| datalake.transactions | transaction_id, account_id, amount | Effective date range (injected by executor) | [customer_attrition_signals.json:19-23] |

### Table Schemas (from database)

**customers**: id (integer), prefix (varchar), first_name (varchar), last_name (varchar), sort_name (varchar), suffix (varchar), birthdate (date), as_of (date) — ~2,230 rows per as_of date.

**accounts**: account_id (integer), customer_id (integer), account_type (varchar), account_status (varchar), open_date (date), current_balance (numeric), interest_rate (numeric), credit_limit (numeric), apr (numeric), as_of (date).

**transactions**: transaction_id (integer), account_id (integer), txn_timestamp (timestamp), txn_type (varchar: Credit/Debit), amount (numeric), description (varchar), as_of (date).

## Business Rules

BR-1: Account count per customer is computed by counting all account rows linked via `customer_id`, regardless of account status.
- Confidence: HIGH
- Evidence: [CustomerAttritionScorer.cs:34-39] — iterates all account rows, no status filter applied

BR-2: Average balance per customer = total balance across all accounts / number of accounts. If a customer has zero accounts, avg_balance = 0.
- Confidence: HIGH
- Evidence: [CustomerAttritionScorer.cs:73] — `avgBalance = acctCount > 0 ? totalBalance / acctCount : 0m`

BR-3: Transaction count per customer is computed by joining transactions to accounts via `account_id`, then aggregating by `customer_id`. Transactions whose `account_id` does not map to any known account are silently dropped.
- Confidence: HIGH
- Evidence: [CustomerAttritionScorer.cs:54-63] — uses `accountToCustomer` lookup; `if (custId == 0) continue;`

BR-4: Attrition score is computed as a weighted sum of three binary factors:
  - **Dormancy factor** (weight 35): 1.0 if customer has zero accounts, else 0.0
  - **Declining transaction factor** (weight 40): 1.0 if transaction count < 3, else 0.0
  - **Low balance factor** (weight 25): 1.0 if average balance < $100, else 0.0
  - Score range: 0.0 to 100.0
- Confidence: HIGH
- Evidence: [CustomerAttritionScorer.cs:76-86] — explicit factor computation and accumulation

BR-5: Risk level classification based on attrition score:
  - Score >= 75.0 → "High"
  - Score >= 40.0 → "Medium"
  - Score < 40.0 → "Low"
- Confidence: HIGH
- Evidence: [CustomerAttritionScorer.cs:88-91] — explicit conditional chain

BR-6: The `as_of` column in the output is set to `__maxEffectiveDate` (a single date value for the entire run), not the per-row `as_of` from source data.
- Confidence: HIGH
- Evidence: [CustomerAttritionScorer.cs:28, 103] — reads `sharedState["__maxEffectiveDate"]` and assigns to every output row

BR-7: Empty output (zero-row DataFrame with correct schema) is produced if `customers` is null or empty.
- Confidence: HIGH
- Evidence: [CustomerAttritionScorer.cs:21-25] — explicit empty DataFrame guard

BR-8: The attrition score is accumulated using `double` (IEEE 754 floating-point) arithmetic, not `decimal`. This may introduce floating-point precision artifacts.
- Confidence: HIGH
- Evidence: [CustomerAttritionScorer.cs:76-86] — all factor variables and `attritionScore` declared as `double`; code comment "W6: Double epsilon"

BR-9: Average balance is rounded to 2 decimal places using `Math.Round` (default banker's rounding).
- Confidence: HIGH
- Evidence: [CustomerAttritionScorer.cs:100] — `Math.Round(avgBalance, 2)`

BR-10: Customer name fields use null-coalescing to empty string — null first_name or last_name becomes "".
- Confidence: HIGH
- Evidence: [CustomerAttritionScorer.cs:97-98] — `?.ToString() ?? ""`

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | customers.id | Cast to int via Convert.ToInt32 | [CustomerAttritionScorer.cs:69] |
| first_name | customers.first_name | Null-coalesced to empty string | [CustomerAttritionScorer.cs:97] |
| last_name | customers.last_name | Null-coalesced to empty string | [CustomerAttritionScorer.cs:98] |
| account_count | Computed | Count of account rows per customer_id | [CustomerAttritionScorer.cs:70] |
| txn_count | Computed | Count of transaction rows per customer (via account join) | [CustomerAttritionScorer.cs:71] |
| avg_balance | Computed | total_balance / account_count, rounded to 2 dp | [CustomerAttritionScorer.cs:73, 100] |
| attrition_score | Computed | Weighted sum of 3 binary factors (double type) | [CustomerAttritionScorer.cs:83-86] |
| risk_level | Computed | "High" / "Medium" / "Low" from attrition_score thresholds | [CustomerAttritionScorer.cs:88-91] |
| as_of | sharedState.__maxEffectiveDate | DateOnly, constant for entire run | [CustomerAttritionScorer.cs:28, 103] |

## Non-Deterministic Fields
None identified. All computations are deterministic given the same input data and effective date.

## Write Mode Implications
**Overwrite** mode: Each effective date run replaces the entire output directory. In multi-day gap-fill scenarios, only the last day's output survives. Previous days' outputs are overwritten by subsequent days.

## Edge Cases

1. **Customer with no accounts**: account_count=0, avg_balance=0, dormancy factor=1.0 (35 points). txn_count=0, declining factor=1.0 (40 points). Low balance factor=1.0 (25 points). Total score=100.0 → "High" risk.
   - Evidence: [CustomerAttritionScorer.cs:70-73, 76-80]

2. **Customer with accounts but no transactions**: txn_count=0, declining factor=1.0 (40 points). Risk depends on balance level.
   - Evidence: [CustomerAttritionScorer.cs:78]

3. **Transaction with unknown account_id**: Silently dropped — does not contribute to any customer's txn_count.
   - Evidence: [CustomerAttritionScorer.cs:60] — `if (custId == 0) continue;`

4. **NULL balance values**: `Convert.ToDecimal` on null would throw. No explicit null guard — assumes current_balance is never null in source data.
   - Evidence: [CustomerAttritionScorer.cs:38]

5. **Duplicate customer IDs in accounts**: Each account row increments the count and adds to the balance. The last-written dictionary entry for accountToCustomer wins for duplicate account_ids.
   - Evidence: [CustomerAttritionScorer.cs:34-39, 46-49]

6. **Float precision**: Score computation uses double arithmetic. Values like 35.0 + 40.0 = 75.0 are exact in IEEE 754, so the >= 75.0 threshold comparison is reliable for these specific weights. However, accumulation pattern could theoretically produce epsilon errors in edge cases.
   - Evidence: [CustomerAttritionScorer.cs:83-86] — code comment "W6: Double accumulation with floating-point errors"

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Account counting (no status filter) | [CustomerAttritionScorer.cs:34-39] |
| BR-2: Average balance formula | [CustomerAttritionScorer.cs:73] |
| BR-3: Txn count via account join | [CustomerAttritionScorer.cs:54-63] |
| BR-4: Weighted attrition score | [CustomerAttritionScorer.cs:76-86] |
| BR-5: Risk level thresholds | [CustomerAttritionScorer.cs:88-91] |
| BR-6: as_of from maxEffectiveDate | [CustomerAttritionScorer.cs:28, 103] |
| BR-7: Empty output guard | [CustomerAttritionScorer.cs:21-25] |
| BR-8: Double arithmetic | [CustomerAttritionScorer.cs:76-86] |
| BR-9: Balance rounding | [CustomerAttritionScorer.cs:100] |
| BR-10: Null name handling | [CustomerAttritionScorer.cs:97-98] |
| Output: Parquet, 1 part, Overwrite | [customer_attrition_signals.json:33-37] |
| Source: customers table | [customer_attrition_signals.json:8-11] |
| Source: accounts table | [customer_attrition_signals.json:13-17] |
| Source: transactions table | [customer_attrition_signals.json:19-23] |

## Open Questions

1. **No account status filter**: All accounts are counted regardless of status (Active, Closed, etc.). Is this intentional or should only Active accounts count toward attrition scoring?
   - Confidence: MEDIUM — code is explicit about including all, but business intent is unclear

2. **Transaction amount not used**: The `amount` column is sourced from transactions but never used in the scorer. Only transaction count matters. The column sourcing may be unnecessary.
   - Confidence: HIGH — [CustomerAttritionScorer.cs:54-63] only increments count, never reads amount

3. **Multi-date data in single run**: DataSourcing pulls all rows across the effective date range. The scorer does not filter or deduplicate by as_of date, meaning a customer appearing on multiple dates has their accounts/transactions counted multiple times. With Overwrite mode and daily gap-fill, this typically means single-day ranges, but multi-day ranges would inflate counts.
   - Confidence: MEDIUM — depends on executor behavior and date range width
