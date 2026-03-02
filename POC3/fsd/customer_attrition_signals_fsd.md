# CustomerAttritionSignals -- Functional Specification Document

## 1. Overview

CustomerAttritionSignalsV2 produces a per-customer attrition risk scorecard by combining account counts, transaction activity, and average balance into a weighted attrition score with a categorical risk level. Output is a Parquet snapshot per effective date.

**Tier: 2 (Framework + Minimal External)** -- `DataSourcing -> Transformation (SQL) -> External (scoring + type fixup) -> ParquetFileWriter`

**Tier Justification:** The V1 External module (`CustomerAttritionScorer`) performs three categories of work:

1. **Set-based aggregation** (joins, counts, sums) -- this is expressible in SQL and eliminates AP6 (row-by-row iteration).
2. **Type-sensitive computation** -- the attrition score uses `double` arithmetic (W6), and `avg_balance` uses `decimal` with banker's rounding (W5/BR-9). SQLite's Transformation module converts all numerics to REAL (double) [Transformation.cs:101], which would change the Parquet column types and potentially alter rounding behavior. This cannot be handled in Tier 1.
3. **Shared state access** -- the `as_of` output column reads `__maxEffectiveDate` from shared state [CustomerAttritionScorer.cs:27-28], which is not a DataFrame and therefore not accessible from SQL Transformations.

Tier 1 is insufficient because:
- SQLite ROUND() uses half-away-from-zero rounding, not banker's rounding (BR-9/W5) -- values at .XX5 boundaries would differ.
- All numerics returned from SQLite are `double` or `long`, but V1 outputs `avg_balance` as `decimal` and `account_count`/`txn_count` as `int` -- Parquet schema would not match.
- `__maxEffectiveDate` (DateOnly) is in shared state, not in any DataFrame table, so SQL cannot reference it.

Tier 3 is unnecessary because DataSourcing fully supports the data access pattern (three tables with standard effective date filtering).

**Empty-input handling:** The External module checks if the pre-aggregated DataFrame is empty (zero rows). If so, it produces an empty DataFrame with the correct 9-column schema and stores it as `output`. This matches V1 behavior [CustomerAttritionScorer.cs:21-25].

## 2. V2 Module Chain

| Step | Module Type | Config Key | Details |
|------|------------|------------|---------|
| 1 | DataSourcing | `customers` | schema=`datalake`, table=`customers`, columns=`[id, first_name, last_name]`. Effective dates injected by executor. |
| 2 | DataSourcing | `accounts` | schema=`datalake`, table=`accounts`, columns=`[account_id, customer_id, current_balance]`. Effective dates injected by executor. |
| 3 | DataSourcing | `transactions` | schema=`datalake`, table=`transactions`, columns=`[transaction_id, account_id]`. Effective dates injected by executor. |
| 4 | Transformation | `pre_scored` | SQL aggregation: joins customers to account stats and transaction counts via subqueries. Produces one row per customer source row with aggregated metrics. See Section 5 for full SQL. |
| 5 | External | -- | `CustomerAttritionSignalsV2Processor`: reads `pre_scored` DataFrame, computes `avg_balance` (decimal, banker's rounding), `attrition_score` (double, W6), `risk_level` (string), and `as_of` (DateOnly from `__maxEffectiveDate`). Writes `output` to shared state. |
| 6 | ParquetFileWriter | -- | source=`output`, outputDirectory=`Output/double_secret_curated/customer_attrition_signals/`, numParts=1, writeMode=Overwrite |

### Key Design Decisions

- **Keep all three DataSourcing entries.** V1 sources customers, accounts, and transactions, and all three are used in the computation. No dead-end sources (AP1 clean).
- **Remove `amount` column from transactions DataSourcing.** V1 sources `[transaction_id, account_id, amount]` but the External module never uses `amount` -- only `transaction_id` and `account_id` are used for counting [CustomerAttritionScorer.cs:54-63]. This eliminates AP4. Note: `transaction_id` is still needed because DataSourcing requires at least the columns in the SELECT, and `transaction_id` is what gets counted in the SQL.
- **SQL Transformation for aggregation.** The joins and GROUP BY operations that V1 implements with three separate `foreach` loops and Dictionary lookups [CustomerAttritionScorer.cs:34-63] are replaced with a single SQL query. This eliminates AP6 (row-by-row iteration) for the aggregation phase.
- **Minimal External for type-sensitive scoring.** The External module handles ONLY the computation that cannot be done correctly in SQLite: `decimal` avg_balance with banker's rounding, `double` attrition score, `DateOnly` as_of injection, and proper C# types for Parquet schema matching.

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (W-codes)

| W-code | Applicable? | Rationale | V2 Handling |
|--------|------------|-----------|-------------|
| W5 (Banker's rounding) | **YES** | `Math.Round(avgBalance, 2)` uses default `MidpointRounding.ToEven` [CustomerAttritionScorer.cs:100]. | Replicate in External module using `Math.Round(avgBalance, 2)` (default banker's rounding). Comment: `// V1 uses Math.Round with default banker's rounding (MidpointRounding.ToEven). Replicated for output equivalence.` |
| W6 (Double epsilon) | **YES** | Attrition score accumulated as `double` [CustomerAttritionScorer.cs:76-86]. Factor weights multiplied and summed using IEEE 754 double arithmetic. | Replicate in External module using `double` for all score variables. Comment: `// V1 uses double (not decimal) for attrition score accumulation. Replicated for output equivalence.` |
| W1 (Sunday skip) | No | No day-of-week logic in V1 code. |
| W2 (Weekend fallback) | No | No date fallback logic in V1 code. |
| W3a/b/c (Boundary rows) | No | No summary row generation. |
| W4 (Integer division) | No | Division is decimal/int which promotes to decimal [CustomerAttritionScorer.cs:73]. |
| W7 (Trailer inflated count) | No | Parquet writer, no trailers. |
| W8 (Trailer stale date) | No | Parquet writer, no trailers. |
| W9 (Wrong writeMode) | No | Overwrite mode is appropriate for a per-date snapshot that replaces on each run. |
| W10 (Absurd numParts) | No | numParts=1 is reasonable. |
| W12 (Header every append) | No | Parquet writer, no header concerns. |

### Code-Quality Anti-Patterns (AP-codes)

| AP-code | Identified? | V1 Problem | V2 Resolution |
|---------|------------|------------|---------------|
| **AP3** (Unnecessary External) | **PARTIAL** | V1 uses a full External module for logic that is partially expressible in SQL. | **Partially eliminated.** The aggregation (joins, counts, sums) is moved to SQL. A minimal External remains for type-sensitive scoring that SQLite cannot reproduce (decimal avg_balance, double attrition score, DateOnly as_of). The External module is reduced from ~100 lines of aggregation+scoring to ~50 lines of scoring-only. |
| **AP4** (Unused columns) | **YES** | V1 sources `amount` from transactions but never uses it. Evidence: [CustomerAttritionScorer.cs:54-63] -- only increments count via `account_id` lookup, never reads `amount`. BRD Open Question 2 confirms. | **Eliminated.** V2 DataSourcing for transactions requests only `[transaction_id, account_id]`. |
| **AP6** (Row-by-row iteration) | **YES** | V1 uses three separate `foreach` loops with Dictionary lookups to aggregate accounts, build account-to-customer mapping, and count transactions [CustomerAttritionScorer.cs:34-63]. | **Eliminated for aggregation.** The SQL Transformation replaces all three loops with set-based JOIN + GROUP BY operations. The remaining `foreach` in the External module is for the scoring computation only (which requires procedural type handling). |
| **AP7** (Magic values) | **YES** | Hardcoded thresholds: `< 3` for declining transactions [line 78], `< 100.0` for low balance [line 80], `40.0`/`35.0`/`25.0` for factor weights [lines 84-86], `>= 75.0`/`>= 40.0` for risk classification [lines 89-90]. | **Eliminated.** V2 External module uses named constants with descriptive comments. Values are unchanged for output equivalence. |
| AP1 (Dead-end sourcing) | No | All three sourced tables are used. |
| AP2 (Duplicated logic) | No | No cross-job duplication identified. |
| AP5 (Asymmetric NULLs) | **Noted** | Names null-coalesced to empty string [lines 96-97], but no other NULL handling asymmetry. This is consistent behavior, not asymmetric. | Replicated via COALESCE in SQL. |
| AP8 (Complex SQL) | No | V1 has no SQL. V2's SQL is straightforward joins with aggregation -- no unused CTEs or window functions. |
| AP9 (Misleading names) | No | "CustomerAttritionSignals" accurately describes the output. |
| AP10 (Over-sourcing dates) | No | V1 uses executor-injected effective dates correctly. |

### BRD Weight Discrepancy

**CRITICAL:** The BRD states dormancy weight=35 and declining transaction weight=40 (BR-4). However, the V1 source code uses dormancy weight=**40** and declining transaction weight=**35** [CustomerAttritionScorer.cs:84-85]:
```csharp
attritionScore += dormancyFactor * 40.0;   // BRD says 35
attritionScore += decliningTxnFactor * 35.0; // BRD says 40
```
**The V2 implementation follows the source code, not the BRD text.** The BRD has the weights swapped between dormancy and declining transaction factors. Output equivalence requires matching the code. The BRD should be corrected to match the code.

## 4. Output Schema

| Column | Type | Source | Transformation | Evidence |
|--------|------|--------|---------------|----------|
| customer_id | int | customers.id | Cast to int via Convert.ToInt32 | [CustomerAttritionScorer.cs:69] |
| first_name | string | customers.first_name | Null-coalesced to empty string via COALESCE | [CustomerAttritionScorer.cs:96] |
| last_name | string | customers.last_name | Null-coalesced to empty string via COALESCE | [CustomerAttritionScorer.cs:97] |
| account_count | int | Computed | COUNT of account rows per customer_id (all statuses) | [CustomerAttritionScorer.cs:37, 70] |
| txn_count | int | Computed | COUNT of transaction rows per customer via account join | [CustomerAttritionScorer.cs:54-63, 71] |
| avg_balance | decimal | Computed | total_balance / account_count, rounded to 2 dp (banker's rounding). 0 if no accounts. | [CustomerAttritionScorer.cs:73, 100] |
| attrition_score | double | Computed | Weighted sum of 3 binary factors using double arithmetic (W6). See scoring formula. | [CustomerAttritionScorer.cs:83-86] |
| risk_level | string | Computed | "High" (>=75.0), "Medium" (>=40.0), "Low" (<40.0) | [CustomerAttritionScorer.cs:88-91] |
| as_of | DateOnly | sharedState.__maxEffectiveDate | Constant for entire run, injected by executor | [CustomerAttritionScorer.cs:27-28, 103] |

**Column order:** customer_id, first_name, last_name, account_count, txn_count, avg_balance, attrition_score, risk_level, as_of. Matches V1's `outputColumns` list [CustomerAttritionScorer.cs:10-14].

**Scoring Formula (using V1 source code weights):**
```
dormancy_factor    = (account_count == 0) ? 1.0 : 0.0
declining_txn_factor = (txn_count < 3)    ? 1.0 : 0.0
low_balance_factor = (avg_balance < 100.0) ? 1.0 : 0.0

attrition_score = dormancy_factor * 40.0
                + declining_txn_factor * 35.0
                + low_balance_factor * 25.0
```
Score range: 0.0 to 100.0. All arithmetic in `double` (W6).

## 5. SQL Design

The SQL Transformation aggregates account and transaction data per customer, producing a pre-scored intermediate DataFrame. The External module then computes the final score using proper C# types.

```sql
SELECT
    c.id AS customer_id,
    COALESCE(c.first_name, '') AS first_name,
    COALESCE(c.last_name, '') AS last_name,
    COALESCE(acct.account_count, 0) AS account_count,
    COALESCE(txn.txn_count, 0) AS txn_count,
    COALESCE(acct.total_balance, 0) AS total_balance
FROM customers c
LEFT JOIN (
    SELECT customer_id,
           COUNT(*) AS account_count,
           SUM(current_balance) AS total_balance
    FROM accounts
    GROUP BY customer_id
) acct ON c.id = acct.customer_id
LEFT JOIN (
    SELECT a.customer_id,
           COUNT(*) AS txn_count
    FROM transactions t
    INNER JOIN accounts a ON t.account_id = a.account_id
    GROUP BY a.customer_id
) txn ON c.id = txn.customer_id
```

### SQL Design Notes

1. **LEFT JOIN for account stats and txn counts:** Ensures all customers appear in the output, even those with zero accounts or zero transactions. Matches V1 behavior where `GetValueOrDefault` returns 0 for missing dictionary keys [CustomerAttritionScorer.cs:70-72].

2. **COALESCE for names:** Converts NULL first_name/last_name to empty string, matching V1's `?.ToString() ?? ""` pattern [CustomerAttritionScorer.cs:96-97]. This is done in SQL rather than in the External module because COALESCE is a natural SQL operation.

3. **COALESCE for aggregates:** Converts NULL counts/sums (from LEFT JOIN no-match) to 0, matching V1's `GetValueOrDefault` with default 0 [CustomerAttritionScorer.cs:70-72].

4. **INNER JOIN for txn-to-account mapping:** Transactions with unknown account_id are silently dropped, matching V1 behavior where `accountToCustomer.GetValueOrDefault(acctId, 0)` returns 0 and the `if (custId == 0) continue` skips them [CustomerAttritionScorer.cs:59-60].

5. **total_balance passed as raw sum:** The SQL passes the raw sum rather than computing avg_balance, because the avg_balance computation requires `decimal` division and banker's rounding (W5), which SQLite cannot reproduce. The External module computes `avg_balance = total_balance / account_count` using C# `decimal` arithmetic.

6. **No ORDER BY:** V1 iterates customers in the order DataSourcing returns them (ordered by as_of per DataSourcing.cs:85). The SQL result order follows the same pattern since the SQLite table is populated from DataSourcing's ordered rows. However, if ordering matters for Parquet byte-equivalence, we may need to add ORDER BY in resolution.

7. **No explicit as_of in SQL:** The `as_of` column is set by the External module from `__maxEffectiveDate` in shared state, matching V1 [CustomerAttritionScorer.cs:27-28, 103]. This value is not available to the SQL engine.

## 6. V2 Job Config

```json
{
  "jobName": "CustomerAttritionSignalsV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "customers",
      "schema": "datalake",
      "table": "customers",
      "columns": ["id", "first_name", "last_name"]
    },
    {
      "type": "DataSourcing",
      "resultName": "accounts",
      "schema": "datalake",
      "table": "accounts",
      "columns": ["account_id", "customer_id", "current_balance"]
    },
    {
      "type": "DataSourcing",
      "resultName": "transactions",
      "schema": "datalake",
      "table": "transactions",
      "columns": ["transaction_id", "account_id"]
    },
    {
      "type": "Transformation",
      "resultName": "pre_scored",
      "sql": "SELECT c.id AS customer_id, COALESCE(c.first_name, '') AS first_name, COALESCE(c.last_name, '') AS last_name, COALESCE(acct.account_count, 0) AS account_count, COALESCE(txn.txn_count, 0) AS txn_count, COALESCE(acct.total_balance, 0) AS total_balance FROM customers c LEFT JOIN (SELECT customer_id, COUNT(*) AS account_count, SUM(current_balance) AS total_balance FROM accounts GROUP BY customer_id) acct ON c.id = acct.customer_id LEFT JOIN (SELECT a.customer_id, COUNT(*) AS txn_count FROM transactions t INNER JOIN accounts a ON t.account_id = a.account_id GROUP BY a.customer_id) txn ON c.id = txn.customer_id"
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.CustomerAttritionSignalsV2Processor"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/customer_attrition_signals/",
      "numParts": 1,
      "writeMode": "Overwrite"
    }
  ]
}
```

### Differences from V1 Config

| Change | V1 | V2 | Reason |
|--------|----|----|--------|
| Job name | `CustomerAttritionSignals` | `CustomerAttritionSignalsV2` | V2 naming convention |
| transactions columns | `[transaction_id, account_id, amount]` | `[transaction_id, account_id]` | AP4: `amount` never used |
| Transformation module | Not present | Added: SQL aggregation | AP6: replaces row-by-row iteration with set-based SQL |
| External module | `CustomerAttritionScorer` (full pipeline) | `CustomerAttritionSignalsV2Processor` (scoring only) | AP3: reduced External scope; aggregation moved to SQL |
| Output directory | `Output/curated/customer_attrition_signals/` | `Output/double_secret_curated/customer_attrition_signals/` | V2 convention |

## 7. Writer Configuration

| Property | V1 Value | V2 Value | Match? |
|----------|----------|----------|--------|
| type | ParquetFileWriter | ParquetFileWriter | YES |
| source | `output` | `output` | YES |
| numParts | 1 | 1 | YES |
| writeMode | Overwrite | Overwrite | YES |
| outputDirectory | `Output/curated/customer_attrition_signals/` | `Output/double_secret_curated/customer_attrition_signals/` | Changed per V2 convention |

## 8. Proofmark Config Design

**Excluded columns:** None. The BRD states "Non-Deterministic Fields: None identified." All output is deterministic given the same input data and effective date.

**Fuzzy columns:** None initially. Start strict per best practices.

**Rationale:** Although attrition_score uses `double` arithmetic (W6), the V2 External module uses the same `double` type and arithmetic operations as V1. Since both V1 and V2 compute the score identically in `double`, there should be zero epsilon difference between them. The specific weights (40.0, 35.0, 25.0) are exact in IEEE 754, and the binary factors (0.0 or 1.0) are also exact. The accumulation `0.0 + factor * weight` produces identical results in both implementations.

If Proofmark comparison reveals epsilon differences in `attrition_score`, add a fuzzy override:
```yaml
columns:
  fuzzy:
    - name: "attrition_score"
      tolerance: 0.0001
      tolerance_type: absolute
      reason: "Double-precision arithmetic accumulation (W6) -- V1 and V2 may differ at epsilon level [CustomerAttritionScorer.cs:83-86]"
```

**Proofmark config:**
```yaml
comparison_target: "customer_attrition_signals"
reader: parquet
threshold: 100.0
```

## 9. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|-------------|-----------------|----------|
| Source customers, accounts, transactions | BR-1, BR-3 source tables | [customer_attrition_signals.json:7-24] |
| Remove `amount` from transactions columns | AP4 + BRD Open Question 2 | [CustomerAttritionScorer.cs:54-63] -- amount never read |
| SQL LEFT JOIN customers to account stats | BR-1: account count per customer, all statuses | [CustomerAttritionScorer.cs:34-39] |
| SQL LEFT JOIN for txn count via account join | BR-3: txn count via account_id mapping | [CustomerAttritionScorer.cs:54-63] |
| SQL INNER JOIN transactions to accounts | BR-3: transactions with unknown account_id dropped | [CustomerAttritionScorer.cs:59-60] |
| COALESCE(name, '') in SQL | BR-10: null name -> empty string | [CustomerAttritionScorer.cs:96-97] |
| COALESCE(count, 0) in SQL | BR-1, BR-3: 0 for missing customers | [CustomerAttritionScorer.cs:70-72] |
| External: decimal avg_balance with banker's rounding | BR-2, BR-9, W5 | [CustomerAttritionScorer.cs:73, 100] |
| External: double attrition_score (W6) | BR-4, BR-8, W6 | [CustomerAttritionScorer.cs:76-86] |
| External: dormancy weight = 40.0 | BR-4 (corrected from BRD text) | [CustomerAttritionScorer.cs:84] |
| External: declining txn weight = 35.0 | BR-4 (corrected from BRD text) | [CustomerAttritionScorer.cs:85] |
| External: low balance weight = 25.0 | BR-4 | [CustomerAttritionScorer.cs:86] |
| External: risk_level thresholds | BR-5 | [CustomerAttritionScorer.cs:88-91] |
| External: as_of from __maxEffectiveDate | BR-6 | [CustomerAttritionScorer.cs:27-28, 103] |
| External: empty-input guard | BR-7 | [CustomerAttritionScorer.cs:21-25] |
| Named constants for thresholds | AP7 prescription | Hardcoded values at [CustomerAttritionScorer.cs:78-80, 84-86, 89-90] |
| numParts=1 | BRD Writer Configuration | [customer_attrition_signals.json:35] |
| writeMode=Overwrite | BRD Writer Configuration | [customer_attrition_signals.json:36] |
| firstEffectiveDate=2024-10-01 | Job config | [customer_attrition_signals.json:3] |
| No Proofmark exclusions or fuzzy | BRD: no non-deterministic fields | BRD Non-Deterministic Fields section |

## 10. External Module Design

### File: `ExternalModules/CustomerAttritionSignalsV2Processor.cs`

**Purpose:** Minimal scoring module that takes pre-aggregated data from SQL Transformation and produces the final output with proper C# types for Parquet schema equivalence.

**Input:** Reads `pre_scored` DataFrame from shared state (produced by SQL Transformation). Columns: `customer_id` (long from SQLite), `first_name` (string), `last_name` (string), `account_count` (long from SQLite), `txn_count` (long from SQLite), `total_balance` (double from SQLite).

**Output:** Writes `output` DataFrame to shared state with 9 columns matching V1 schema and types exactly.

### Named Constants

```csharp
// Attrition score factor weights [CustomerAttritionScorer.cs:84-86]
private const double DormancyWeight = 40.0;
private const double DecliningTxnWeight = 35.0;
private const double LowBalanceWeight = 25.0;

// Binary factor thresholds [CustomerAttritionScorer.cs:78-80]
private const int DecliningTxnThreshold = 3;      // txn_count < 3 = "declining"
private const double LowBalanceThreshold = 100.0;  // avg_balance < 100 = "low"

// Risk level classification thresholds [CustomerAttritionScorer.cs:89-90]
private const double HighRiskThreshold = 75.0;
private const double MediumRiskThreshold = 40.0;
```

### Processing Logic

```
1. Read pre_scored DataFrame from shared state
2. If pre_scored is null or empty:
   - Create empty DataFrame with output schema (9 columns)
   - Store as "output" in shared state
   - Return (matches BR-7)
3. Read __maxEffectiveDate from shared state as DateOnly
4. For each row in pre_scored:
   a. Extract customer_id (Convert.ToInt32), first_name, last_name (already COALESCE'd by SQL)
   b. Extract account_count (Convert.ToInt32), txn_count (Convert.ToInt32) from long
   c. Extract total_balance (Convert.ToDecimal) from double
   d. Compute avg_balance as decimal:
      - If account_count > 0: total_balance / account_count
      - Else: 0m
      - Round to 2 decimal places: Math.Round(avgBalance, 2)
      // V1 uses Math.Round with default banker's rounding (MidpointRounding.ToEven). Replicated for output equivalence.
   e. Compute attrition_score as double (W6):
      - dormancyFactor = (account_count == 0) ? 1.0 : 0.0
      - decliningTxnFactor = (txn_count < DecliningTxnThreshold) ? 1.0 : 0.0
      - lowBalanceFactor = ((double)avgBalance < LowBalanceThreshold) ? 1.0 : 0.0
      - attritionScore = dormancyFactor * DormancyWeight
                       + decliningTxnFactor * DecliningTxnWeight
                       + lowBalanceFactor * LowBalanceWeight
      // V1 uses double (not decimal) for attrition score accumulation. Replicated for output equivalence.
   f. Classify risk_level:
      - attritionScore >= HighRiskThreshold -> "High"
      - attritionScore >= MediumRiskThreshold -> "Medium"
      - else -> "Low"
   g. Build output Row with proper types:
      - customer_id: int
      - first_name: string
      - last_name: string
      - account_count: int
      - txn_count: int
      - avg_balance: decimal (rounded)
      - attrition_score: double
      - risk_level: string
      - as_of: DateOnly
5. Create output DataFrame with 9-column schema
6. Store as "output" in shared state
```

### Type Conversion Notes

The SQL Transformation returns values in SQLite types:
- `customer_id` -> long (SQLite INTEGER) -> must Convert.ToInt32 for int Parquet type
- `account_count` -> long (SQLite INTEGER) -> must Convert.ToInt32 for int Parquet type
- `txn_count` -> long (SQLite INTEGER) -> must Convert.ToInt32 for int Parquet type
- `total_balance` -> double (SQLite REAL) -> must Convert.ToDecimal for decimal division
- `first_name`, `last_name` -> string (SQLite TEXT) -> no conversion needed

These conversions ensure the output DataFrame has the same C# types as V1, producing identical Parquet column schemas.

### Edge Cases Handled

1. **Empty pre_scored DataFrame (BR-7):** Returns empty output with correct schema.
2. **Customer with zero accounts (Edge Case 1):** account_count=0 from COALESCE, avg_balance=0m, dormancy=1.0 (40 pts), declining=1.0 (35 pts), low_balance=1.0 (25 pts), score=100.0, "High" risk.
3. **Customer with accounts but no transactions (Edge Case 2):** txn_count=0 from COALESCE, declining=1.0 (35 pts). Risk depends on balance.
4. **Transactions with unknown account_id (Edge Case 3):** Dropped by INNER JOIN in SQL.
5. **Float precision (Edge Case 6):** The specific weights (40.0, 35.0, 25.0) and binary factors (0.0, 1.0) are all exactly representable in IEEE 754 double. Accumulation of `factor * weight` where factor is 0.0 or 1.0 produces exact results. Score values are always in {0.0, 25.0, 35.0, 40.0, 60.0, 65.0, 75.0, 100.0}, all exactly representable.
