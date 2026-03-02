# TransactionAnomalyFlags -- Functional Specification Document

## 1. Job Summary

The `TransactionAnomalyFlags` job detects anomalous transactions by computing per-account statistical baselines (population mean and standard deviation of transaction amounts) and flagging individual transactions whose deviation from the account mean exceeds 3 standard deviations. It resolves `customer_id` via an account-to-customer lookup, applies Banker's rounding to all numeric output fields, and writes flagged transactions as CSV. The V1 implementation uses an External module (`TransactionAnomalyFlagger.cs`) with a mixed `decimal`/`double` stddev computation that introduces IEEE 754 precision artifacts into the output.

## 2. V2 Module Chain

**Tier:** 2 -- Framework + Minimal External (SCALPEL)

```
DataSourcing ("transactions")
  -> DataSourcing ("accounts")
    -> External (TransactionAnomalyFlagsV2Processor — stats + flagging)
      -> CsvFileWriter
```

### Tier Justification

Tier 1 (pure SQL) is **insufficient** for two reasons:

1. **SQLite lacks a SQRT function.** Population standard deviation requires `SQRT(AVG((x - mean)^2))`. SQLite has no built-in `SQRT()` and no aggregate `STDEV()` function. There is no way to compute standard deviation in a single SQL Transformation pass.

2. **Mixed-precision replication (BR-8).** V1 computes variance by casting `decimal` differences to `double`, squaring in double space, averaging in double, then casting `Math.Sqrt(variance)` back to `decimal`. This specific decimal-to-double-to-decimal conversion path introduces IEEE 754 precision artifacts that are baked into the V1 output. Even if SQLite had SQRT, SQLite's native REAL (double) arithmetic would not precisely replicate the V1 conversion path because V1 starts the subtraction in decimal space before casting to double.

Tier 3 is **unnecessary** because DataSourcing can handle the data access pattern. The job reads from `transactions` and `accounts` using standard effective date injection -- no cross-date-range querying or complex snapshot logic is needed.

**V2 Design:** DataSourcing pulls data from `transactions` and `accounts`. The External module performs the statistical computation and anomaly flagging using the same `decimal`/`double` mixed-precision path as V1 (to replicate BR-8), but with clean code: named constants, set-based operations via LINQ where possible, and no dead-end data sources. The framework CsvFileWriter handles file output.

### Module 1: DataSourcing -- transactions
- **resultName:** `transactions`
- **schema:** `datalake`
- **table:** `transactions`
- **columns:** `transaction_id`, `account_id`, `amount`
- Effective dates injected by executor via shared state (no hardcoded dates)
- **Note:** `txn_type` removed vs V1 (AP4 -- unused column; see Section 7)

### Module 2: DataSourcing -- accounts
- **resultName:** `accounts`
- **schema:** `datalake`
- **table:** `accounts`
- **columns:** `account_id`, `customer_id`
- Effective dates injected by executor via shared state

### Module 3: External -- TransactionAnomalyFlagsV2Processor
- **assemblyPath:** `ExternalModules/bin/Debug/net8.0/ExternalModules.dll`
- **typeName:** `ExternalModules.TransactionAnomalyFlagsV2Processor`
- Reads `transactions` and `accounts` from shared state
- Writes `output` DataFrame to shared state
- Full logic described in Section 4

### Module 4: CsvFileWriter
- **source:** `output`
- **outputFile:** `Output/double_secret_curated/transaction_anomaly_flags.csv`
- **includeHeader:** `true`
- **writeMode:** `Overwrite`
- **lineEnding:** `LF`
- No trailer configured (matches V1)

## 3. DataSourcing Config

### transactions

| Property | Value |
|----------|-------|
| resultName | `transactions` |
| schema | `datalake` |
| table | `transactions` |
| columns | `transaction_id`, `account_id`, `amount` |
| Effective dates | Injected via shared state (`__minEffectiveDate`, `__maxEffectiveDate`) |

**Changes from V1:**
- Removed `txn_type` (AP4: sourced but never referenced in output or logic -- [TransactionAnomalyFlagger.cs:10-14] output schema does not include it, no code path reads it)

### accounts

| Property | Value |
|----------|-------|
| resultName | `accounts` |
| schema | `datalake` |
| table | `accounts` |
| columns | `account_id`, `customer_id` |
| Effective dates | Injected via shared state |

**No changes from V1.** V1 sources exactly these two columns.

### customers -- REMOVED (AP1)

V1 sources `datalake.customers` with columns `id`, `first_name`, `last_name`. This DataFrame is null-checked but **never used for enrichment** -- `customer_id` is resolved from `accounts`, not `customers`. Customer names (`first_name`, `last_name`) do not appear in the output schema.

- Evidence: [TransactionAnomalyFlagger.cs:18-20] -- `customers` loaded and null-checked but never iterated or read
- Evidence: [TransactionAnomalyFlagger.cs:10-14] -- output columns are `transaction_id`, `account_id`, `customer_id`, `amount`, `account_mean`, `account_stddev`, `deviation_factor`, `as_of`
- Evidence: BRD BR-9, BR-10: customers is a dead-end source

**V2 does not source `customers` at all.** This is a clean elimination of AP1.

## 4. Transformation SQL

**Not applicable.** There is no SQL Transformation module in the V2 chain. The statistical computation and anomaly detection are performed by the External module (see below).

### External Module Logic: TransactionAnomalyFlagsV2Processor

The External module implements the following algorithm, replicating V1's exact computation path with clean code:

**Step 1: Build account-to-customer lookup**
```
Dictionary<int, int> accountToCustomer
For each row in accounts:
    accountToCustomer[account_id] = customer_id
```
- Evidence: [TransactionAnomalyFlagger.cs:27-33]

**Step 2: Collect per-account transaction amounts**
```
Dictionary<int, List<decimal>> accountAmounts
List<(int txnId, int accountId, decimal amount, object? asOf)> txnData
For each row in transactions:
    Collect amount per account_id
    Store (transaction_id, account_id, amount, as_of) for later iteration
```
- Evidence: [TransactionAnomalyFlagger.cs:36-49]

**Step 3: Compute per-account statistics (mixed precision -- BR-8 replication)**
```csharp
// Named constants (AP7 elimination)
const decimal DeviationThreshold = 3.0m;  // Anomaly detection threshold

foreach account in accountAmounts:
    decimal mean = amounts.Average();
    // BR-8: V1 mixes decimal and double for variance computation
    // Subtract in decimal, cast to double for squaring, average in double
    double variance = amounts.Select(a => (double)(a - (decimal)mean) * (double)(a - (decimal)mean)).Average();
    decimal stddev = (decimal)Math.Sqrt(variance);
```
- Evidence: [TransactionAnomalyFlagger.cs:53-59]
- The `(decimal)mean` cast is redundant (mean is already decimal) but replicates V1 exactly
- The double-precision squaring and averaging introduces IEEE 754 artifacts
- `Math.Sqrt` operates on double, result cast back to decimal

**Step 4: Flag anomalous transactions**
```csharp
foreach (txnId, accountId, amount, asOf) in txnData:
    if stddev == 0m: skip  // BR-6: zero stddev exclusion
    deviationFactor = Math.Abs(amount - mean) / stddev
    if deviationFactor > DeviationThreshold:  // BR-5: > 3.0 (strict greater-than)
        customerId = accountToCustomer.GetValueOrDefault(accountId, 0)  // BR-12: default 0
        // W5: Banker's rounding on all numeric output fields
        Emit row with Math.Round(..., 2, MidpointRounding.ToEven) applied to:
            amount, account_mean, account_stddev, deviation_factor
```
- Evidence: [TransactionAnomalyFlagger.cs:64-91]

**Step 5: Write output DataFrame**
```
sharedState["output"] = new DataFrame(outputRows, outputColumns)
```

### V2 Code Quality Improvements (vs V1)

| Aspect | V1 | V2 |
|--------|----|----|
| Threshold | Hardcoded `3.0m` literal | Named constant `DeviationThreshold = 3.0m` with comment |
| Data sourcing | Sources `customers` (dead-end) and `txn_type` (unused) | Neither sourced |
| Empty guard | Checks `customers` for null (pointless) | Only checks `transactions` and `accounts` |
| Code comments | Sparse inline notes | Full documentation of V1 behavior replication, W-codes, AP-codes |

## 5. Writer Config

| Property | Value | Matches V1? |
|----------|-------|-------------|
| type | CsvFileWriter | YES |
| source | `output` | YES |
| outputFile | `Output/double_secret_curated/transaction_anomaly_flags.csv` | Path updated per V2 convention |
| includeHeader | `true` | YES |
| writeMode | `Overwrite` | YES |
| lineEnding | `LF` | YES |
| trailerFormat | (not configured) | YES -- V1 has no trailer |

### Write Mode Implications

V1 uses `Overwrite` mode. During multi-day auto-advance runs, each effective date overwrites the prior day's output. Only the last effective date's output survives in the file. V2 replicates this exactly.

- Evidence: [transaction_anomaly_flags.json:36] `"writeMode": "Overwrite"`
- Evidence: BRD "Write Mode Implications" section

## 6. Wrinkle Replication

| ID | Name | Applies? | V1 Evidence | V2 Replication Strategy |
|----|------|----------|-------------|------------------------|
| W5 | Banker's rounding | **YES** | [TransactionAnomalyFlagger.cs:84-87] `Math.Round(..., 2, MidpointRounding.ToEven)` on all four numeric output fields | **REPRODUCED.** V2 External module uses `Math.Round(value, 2, MidpointRounding.ToEven)` on `amount`, `account_mean`, `account_stddev`, and `deviation_factor`. This is intentional: `// W5: Banker's rounding (MidpointRounding.ToEven) — V1 behavior replicated for output equivalence.` |
| W9 | Wrong writeMode | **POSSIBLE** | [transaction_anomaly_flags.json:35] `"writeMode": "Overwrite"` — multi-day runs lose prior days' output | **REPRODUCED.** V2 uses `Overwrite` to match V1 exactly. `// W9: V1 uses Overwrite — prior days' data is lost on each run.` |
| W1 | Sunday skip | NO | No day-of-week guard in V1 source | N/A |
| W2 | Weekend fallback | NO | No weekend date logic in V1 source | N/A |
| W3a-c | Boundary rows | NO | No summary row generation in V1 source | N/A |
| W4 | Integer division | NO | V1 uses decimal division, not integer | N/A |
| W6 | Double epsilon | **PARTIAL** | [TransactionAnomalyFlagger.cs:57-58] Variance computed in double, stddev cast from double. This is not monetary accumulation (W6's specific concern), but it introduces floating-point precision artifacts into statistical outputs. | **REPRODUCED via BR-8.** The mixed decimal/double computation path is replicated exactly: `(double)(a - (decimal)mean) * (double)(a - (decimal)mean)` then `(decimal)Math.Sqrt(variance)`. The double-precision artifacts in `account_stddev` and `deviation_factor` are baked into V1 output and must be matched. |
| W7 | Trailer inflated count | NO | V1 has no trailer | N/A |
| W8 | Trailer stale date | NO | V1 has no trailer | N/A |
| W10 | Absurd numParts | NO | V1 uses CsvFileWriter (single file, not Parquet) | N/A |
| W12 | Header every append | NO | V1 uses Overwrite mode | N/A |

### Note on BR-8 (Mixed Precision) and Its Relationship to W6

BR-8 describes a precision wrinkle that does not map cleanly to any single W-code. It shares characteristics with W6 (double-precision artifacts) but is specific to the stddev computation, not monetary accumulation. The V2 External module replicates the exact computation path documented in BR-8 to ensure output equivalence.

## 7. Anti-Pattern Elimination

| ID | Name | Applies? | V1 Evidence | V2 Disposition |
|----|------|----------|-------------|----------------|
| AP1 | Dead-end sourcing | **YES** | [TransactionAnomalyFlagger.cs:18-20] `customers` DataFrame loaded and null-checked but never iterated; [transaction_anomaly_flags.json:19-25] sources `customers` with `id`, `first_name`, `last_name` | **ELIMINATED.** V2 does not include a DataSourcing module for `customers`. The `customer_id` is resolved from `accounts.customer_id` (via the account-to-customer lookup), not from the `customers` table. |
| AP3 | Unnecessary External | **NO (justified)** | The External module is needed because SQLite lacks SQRT and cannot replicate V1's mixed-precision stddev computation | **RETAINED with justification.** Tier 2 External is the minimum viable approach. The External handles ONLY the statistical computation and anomaly flagging. DataSourcing handles data access. CsvFileWriter handles output. |
| AP4 | Unused columns | **YES** | [transaction_anomaly_flags.json:10] `txn_type` sourced in `transactions` DataSourcing; [TransactionAnomalyFlagger.cs:10-14] output schema does not include `txn_type`; no code path reads `txn_type` | **ELIMINATED.** V2 DataSourcing for `transactions` sources only `transaction_id`, `account_id`, `amount`. |
| AP6 | Row-by-row iteration | **YES** | [TransactionAnomalyFlagger.cs:38-49] `foreach` loop to collect amounts per account; [TransactionAnomalyFlagger.cs:64-91] `foreach` loop to flag anomalies | **PARTIALLY ELIMINATED.** The per-account stats computation is converted to LINQ GroupBy/ToDictionary for the collection phase. The anomaly flagging loop remains as a `foreach` because each row needs conditional output with per-account stats lookup, which is a natural iteration pattern. The inner loop pattern (`foreach` over `txnData` checking against `accountStats`) is structurally sound -- the anti-pattern was in the per-account amount collection, where V1 manually builds the dictionary instead of using `GroupBy`. |
| AP7 | Magic values | **YES** | [TransactionAnomalyFlagger.cs:74] hardcoded `3.0m` threshold; [TransactionAnomalyFlagger.cs:69] hardcoded `0m` stddev check | **ELIMINATED.** V2 uses named constants: `const decimal DeviationThreshold = 3.0m; // Standard anomaly detection threshold: flag transactions > 3 standard deviations from account mean`. The zero-stddev guard uses `0m` which is self-documenting in context. |
| AP2 | Duplicated logic | NO | No cross-job duplication identified for this job's specific logic | N/A |
| AP5 | Asymmetric NULLs | NO | V1 handles missing customer_id with a consistent default of 0 (BR-12); no asymmetric NULL patterns | N/A |
| AP8 | Complex SQL / unused CTEs | NO | V1 uses no SQL (External module only) | N/A |
| AP9 | Misleading names | NO | Job name accurately describes what it does | N/A |
| AP10 | Over-sourcing dates | NO | V1 uses framework effective date injection (no hardcoded dates, no SQL date filter) | N/A |

## 8. Proofmark Config

```yaml
comparison_target: "transaction_anomaly_flags"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

### Proofmark Design Rationale

- **reader: csv** -- V1 output is a CSV file via CsvFileWriter
- **header_rows: 1** -- V1 has `includeHeader: true`
- **trailer_rows: 0** -- V1 has no `trailerFormat` configured
- **threshold: 100.0** -- All fields are deterministic (BRD identifies no non-deterministic fields). V2 replicates V1's exact computation path including the mixed-precision decimal/double arithmetic (BR-8). Output must be byte-identical.
- **No EXCLUDED columns** -- No non-deterministic fields exist (BRD "Non-Deterministic Fields: None identified")
- **No FUZZY columns** -- Because V2 replicates V1's exact decimal-to-double-to-decimal computation path (same C# code pattern), the IEEE 754 artifacts should be identical between V1 and V2. If Proofmark reveals epsilon-level differences in `account_stddev` or `deviation_factor`, a FUZZY override with tight absolute tolerance (e.g., 0.01) would be the fallback. This is not expected because the computation code is structurally identical.

### Proofmark Risk Assessment

The highest-risk columns for comparison are `account_stddev` and `deviation_factor`, because they pass through double-precision arithmetic. However, since V2 replicates the exact same C# expression (`(double)(a - (decimal)mean) * (double)(a - (decimal)mean)` and `(decimal)Math.Sqrt(variance)`), the IEEE 754 results should be bit-identical given the same input data and execution order. **Start strict; add FUZZY only if comparison fails.**

## 9. Open Questions

1. **Cross-date baseline (BRD Edge Case 5):** V1 computes per-account statistics across ALL transactions in the DataFrame, which may span multiple `as_of` dates if the DataSourcing effective date range covers more than one day. This means the statistical baseline and anomaly flags may differ depending on the date range. In Overwrite mode, only the last day's run output survives, so this only matters for the final effective date in a multi-day run. V2 replicates this behavior exactly. Is this intentional or a V1 design flaw?
   - **Impact:** None for output equivalence. V2 matches V1.
   - **Confidence:** HIGH that behavior is replicated; MEDIUM that behavior is intentional.

2. **Row ordering:** V1 iterates `txnData` in the order transactions were added (which follows DataSourcing row order). V2 preserves this same iteration order. If the underlying DataSourcing row order changes between V1 and V2 runs (e.g., due to PostgreSQL query plan differences), row order in the output could differ. Neither V1 nor V2 applies an explicit sort. If Proofmark fails on row ordering, adding an `OrderBy` on `transaction_id` (or `as_of, transaction_id`) to the output DataFrame would be a safe fix.
   - **Impact:** LOW -- DataSourcing returns rows in `as_of` order; within a date, PostgreSQL typically returns rows in insertion order.
   - **Confidence:** HIGH.

3. **Why was `customers` sourced in V1?** BRD Open Question 1 asks this. The most likely explanation: name enrichment was planned but never implemented. V2 removes it cleanly (AP1).
   - **Impact:** None. Removing `customers` does not affect output.
   - **Confidence:** HIGH.

## Appendix: V2 Job Config JSON

```json
{
  "jobName": "TransactionAnomalyFlagsV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "transactions",
      "schema": "datalake",
      "table": "transactions",
      "columns": ["transaction_id", "account_id", "amount"]
    },
    {
      "type": "DataSourcing",
      "resultName": "accounts",
      "schema": "datalake",
      "table": "accounts",
      "columns": ["account_id", "customer_id"]
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.TransactionAnomalyFlagsV2Processor"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/transaction_anomaly_flags.csv",
      "includeHeader": true,
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

### Config Changes from V1

| Aspect | V1 | V2 | Reason |
|--------|----|----|--------|
| jobName | `TransactionAnomalyFlags` | `TransactionAnomalyFlagsV2` | V2 naming convention |
| transactions columns | `transaction_id, account_id, txn_type, amount` | `transaction_id, account_id, amount` | AP4: removed unused `txn_type` |
| customers DataSourcing | Present (id, first_name, last_name) | **REMOVED** | AP1: dead-end source, never used in output |
| External typeName | `TransactionAnomalyFlagger` | `TransactionAnomalyFlagsV2Processor` | V2 naming convention |
| Output path | `Output/curated/transaction_anomaly_flags.csv` | `Output/double_secret_curated/transaction_anomaly_flags.csv` | V2 output directory |
| Writer config | includeHeader=true, writeMode=Overwrite, lineEnding=LF, no trailer | Identical | All writer params preserved |

## Appendix: Output Schema

| # | Column | Type | Source | Transformation | Evidence |
|---|--------|------|--------|---------------|----------|
| 1 | transaction_id | int | transactions.transaction_id | Convert.ToInt32 | [TransactionAnomalyFlagger.cs:42,81] |
| 2 | account_id | int | transactions.account_id | Convert.ToInt32 | [TransactionAnomalyFlagger.cs:41,82] |
| 3 | customer_id | int | Computed | Resolved via accounts lookup; default 0 if not found (BR-12) | [TransactionAnomalyFlagger.cs:76,83] |
| 4 | amount | decimal | transactions.amount | Banker's rounding to 2dp (W5) | [TransactionAnomalyFlagger.cs:84] |
| 5 | account_mean | decimal | Computed | Mean of all transaction amounts for the account, Banker's rounding to 2dp (W5) | [TransactionAnomalyFlagger.cs:56,85] |
| 6 | account_stddev | decimal | Computed | Population stddev via mixed decimal/double path (BR-8), Banker's rounding to 2dp (W5) | [TransactionAnomalyFlagger.cs:57-58,86] |
| 7 | deviation_factor | decimal | Computed | `abs(amount - mean) / stddev`, Banker's rounding to 2dp (W5) | [TransactionAnomalyFlagger.cs:71,87] |
| 8 | as_of | date | transactions.as_of | Direct passthrough (injected by DataSourcing) | [TransactionAnomalyFlagger.cs:88] |

## Appendix: Traceability Matrix

| BRD Requirement | FSD Section | Design Decision | Evidence |
|-----------------|-------------|-----------------|----------|
| BR-1: Account-to-customer lookup | External Logic Step 1 | Dictionary lookup from accounts, identical to V1 | [TransactionAnomalyFlagger.cs:27-33] |
| BR-2: Per-account statistics (all amounts) | External Logic Step 3 | GroupBy account_id, compute mean+stddev across all amounts in DataFrame | [TransactionAnomalyFlagger.cs:36-59] |
| BR-3: Population stddev (divide by N) | External Logic Step 3 | `.Average()` on squared deviations = population variance | [TransactionAnomalyFlagger.cs:57] |
| BR-4: Deviation factor formula | External Logic Step 4 | `Math.Abs(amount - mean) / stddev` | [TransactionAnomalyFlagger.cs:71] |
| BR-5: 3.0 threshold (strict >) | External Logic Step 4, AP7 | Named constant `DeviationThreshold = 3.0m`; `if (deviationFactor > DeviationThreshold)` | [TransactionAnomalyFlagger.cs:74] |
| BR-6: Zero stddev exclusion | External Logic Step 4 | `if (stddev == 0m) continue;` guard clause | [TransactionAnomalyFlagger.cs:69] |
| BR-7: Banker's rounding (all numeric fields) | Wrinkle Replication W5 | `Math.Round(..., 2, MidpointRounding.ToEven)` on amount, mean, stddev, deviation_factor | [TransactionAnomalyFlagger.cs:84-87] |
| BR-8: Mixed decimal/double computation | External Logic Step 3, Wrinkle Replication W6 | Exact replication of V1 expression: `(double)(a - (decimal)mean) * (double)(a - (decimal)mean)` then `(decimal)Math.Sqrt(variance)` | [TransactionAnomalyFlagger.cs:57-58] |
| BR-9/BR-10: Dead-end customers | DataSourcing Config (customers REMOVED) | V2 does not source customers; AP1 eliminated | [TransactionAnomalyFlagger.cs:18-20] |
| BR-11: Empty input guard | External Logic | If transactions or accounts is null/empty, output empty DataFrame | [TransactionAnomalyFlagger.cs:20-24] |
| BR-12: Default customer_id = 0 | External Logic Step 4 | `accountToCustomer.GetValueOrDefault(accountId, 0)` | [TransactionAnomalyFlagger.cs:76] |
| BR-13: Unused txn_type column | DataSourcing Config | `txn_type` not sourced in V2; AP4 eliminated | [transaction_anomaly_flags.json:10] |
| Writer: Overwrite mode | Writer Config | `writeMode: Overwrite` matches V1 | [transaction_anomaly_flags.json:35] |
| Writer: LF line ending | Writer Config | `lineEnding: LF` matches V1 | [transaction_anomaly_flags.json:37] |
| Writer: includeHeader | Writer Config | `includeHeader: true` matches V1 | [transaction_anomaly_flags.json:35] |
| Writer: no trailer | Writer Config | No `trailerFormat` matches V1 | [transaction_anomaly_flags.json (absence)] |
