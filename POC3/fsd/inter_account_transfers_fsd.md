# InterAccountTransfersV2 — Functional Specification Document

## 1. Overview

**Job:** InterAccountTransfersV2
**Config:** `inter_account_transfers_v2.json`
**Tier:** Tier 2 — Framework + Minimal External (SCALPEL)

This job detects inter-account transfers by matching debit and credit transaction pairs that share the same amount, timestamp, and different account IDs. It produces a Parquet file of matched transfer pairs per effective date.

**Tier Justification:** The V1 matching algorithm is a greedy, sequential, first-match-wins nested loop: for each debit (in iteration order), it scans all unmatched credits and takes the first match. Once a credit is matched, it cannot be matched to any other debit. This is a stateful sequential assignment that cannot be reliably expressed in pure SQL. A SQL self-join would produce the correct result only when matches are unambiguous (one credit per debit per amount+timestamp group), but per BR-8, the algorithm is explicitly iteration-order-dependent, meaning the same data could produce different pairings if the join strategy differs. A minimal External module is needed solely for the greedy matching assignment. DataSourcing handles data retrieval, and ParquetFileWriter handles output.

---

## 2. V2 Module Chain

```
DataSourcing (transactions)
  -> Transformation (SQL: separate debits/credits, build candidate pairs)
  -> External (greedy first-match-wins assignment)
  -> ParquetFileWriter
```

### Module 1: DataSourcing — `transactions`
- **Table:** `datalake.transactions`
- **Columns:** `transaction_id`, `account_id`, `txn_timestamp`, `txn_type`, `amount`
- **Effective dates:** Injected by executor via `__minEffectiveDate` / `__maxEffectiveDate`
- **Result name:** `transactions`

Note: The `as_of` column is automatically appended by DataSourcing (not listed in columns, but present in the resulting DataFrame).

### Module 2: Transformation — `candidates`
- **Result name:** `candidates`
- **SQL:** Joins debits to credits on matching criteria, producing all candidate pairs with deterministic ordering. See Section 5 for full SQL design.

### Module 3: External — `InterAccountTransfersV2Processor`
- **Assembly:** `ExternalModules/bin/Debug/net8.0/ExternalModules.dll`
- **Type:** `ExternalModules.InterAccountTransfersV2Processor`
- **Responsibility:** Reads the `candidates` DataFrame (pre-joined debit-credit pairs ordered by debit then credit transaction_id), applies greedy first-match-wins assignment, and writes the `output` DataFrame.

### Module 4: ParquetFileWriter
- **Source:** `output`
- **Output directory:** `Output/double_secret_curated/inter_account_transfers/`
- **numParts:** 1
- **writeMode:** Overwrite

---

## 3. Anti-Pattern Analysis

### Identified Anti-Patterns

| Code | Name | Applies? | V2 Action |
|------|------|----------|-----------|
| AP1 | Dead-end sourcing | YES | **ELIMINATED.** V1 sources `accounts` table (account_id, customer_id) but never uses it in the matching logic (BR-6). V2 removes this DataSourcing entry entirely. |
| AP3 | Unnecessary External module | PARTIAL | V1 uses a full External module for everything including data separation and matching. V2 moves debit/credit separation and candidate pair generation to SQL (Transformation), keeping the External module only for the greedy matching algorithm that SQL cannot express. Downgraded from Tier 3 to Tier 2. |
| AP4 | Unused columns | YES — via AP1 | **ELIMINATED.** The entire `accounts` DataSourcing entry is removed (see AP1). All columns sourced from `transactions` are used. |
| AP6 | Row-by-row iteration | PARTIAL | **PARTIALLY ELIMINATED.** V1 uses a foreach loop to separate debits/credits (replaced by SQL WHERE clause) and an O(n^2) nested loop for matching (retained in External module as greedy assignment, but implemented with efficient HashSet lookups). The SQL Transformation pre-filters and pre-joins candidates, reducing the External module's work to a linear scan of pre-sorted candidate pairs. |
| AP10 | Over-sourcing dates | NO | V1 already relies on executor-injected effective dates via DataSourcing. No change needed. |
| W9 | Wrong writeMode | NOTED | V1 uses Overwrite mode for Parquet output. In multi-day gap-fill, only the last day's output survives. This is V1 behavior. V2 reproduces it with a comment. |

### Anti-Patterns NOT Present

| Code | Name | Why Not |
|------|------|---------|
| W1-W3 | Sunday/Weekend/Boundary | No date-based conditional logic in V1 |
| W4 | Integer division | No percentage calculations |
| W5 | Banker's rounding | No rounding operations |
| W6 | Double epsilon | V1 uses decimal for amount matching (exact equality) |
| W7/W8 | Trailer issues | Parquet output, no trailers |
| W10 | Absurd numParts | numParts = 1, reasonable |
| W12 | Header every append | Parquet output, no CSV headers |
| AP2 | Duplicated logic | No cross-job duplication identified |
| AP5 | Asymmetric NULLs | No NULL/default handling asymmetries |
| AP7 | Magic values | No hardcoded thresholds |
| AP8 | Complex SQL / unused CTEs | V1 has no SQL; V2 introduces clean SQL |
| AP9 | Misleading names | Job name accurately describes function |

---

## 4. Output Schema

| Column | Type | Source | Transformation | Traceability |
|--------|------|--------|---------------|--------------|
| debit_txn_id | integer | transactions.transaction_id | From matched debit row | BR-2, BRD output schema |
| credit_txn_id | integer | transactions.transaction_id | From matched credit row | BR-2, BRD output schema |
| from_account_id | integer | transactions.account_id | From debit row | BR-2, BRD output schema |
| to_account_id | integer | transactions.account_id | From credit row | BR-2, BRD output schema |
| amount | decimal | transactions.amount | From debit row (same as credit by match condition BR-2.1) | BR-2, BRD output schema |
| txn_timestamp | string | transactions.txn_timestamp | From debit row, as string (`.ToString()`) | BR-2, BRD output schema |
| as_of | date | transactions.as_of | From debit row (BR-5) | BR-5, BRD output schema |

---

## 5. SQL Design

### Transformation: `candidates`

The SQL Transformation produces all valid candidate debit-credit pairs, ordered deterministically to match V1's iteration-order behavior. The External module consumes these pairs in order and applies the greedy assignment.

```sql
SELECT
    d.transaction_id AS debit_txn_id,
    d.account_id     AS debit_account_id,
    d.amount         AS amount,
    d.txn_timestamp  AS debit_timestamp,
    d.as_of          AS debit_as_of,
    c.transaction_id AS credit_txn_id,
    c.account_id     AS credit_account_id
FROM transactions d
JOIN transactions c
    ON  d.txn_type = 'Debit'
    AND c.txn_type = 'Credit'
    AND d.amount = c.amount
    AND CAST(d.txn_timestamp AS TEXT) = CAST(c.txn_timestamp AS TEXT)
    AND d.account_id != c.account_id
ORDER BY d.transaction_id, c.transaction_id
```

**Design rationale:**

- **BR-1 compliance:** Only rows with exactly "Debit" or "Credit" txn_type participate. The WHERE/ON clause filters explicitly.
- **BR-2 compliance:** Match conditions (same amount, same timestamp as string, different account) are applied in the JOIN.
- **Timestamp as string (Edge Case 4):** `CAST(d.txn_timestamp AS TEXT)` replicates V1's `.ToString()` comparison behavior. Both sides are cast to text to ensure string equality semantics.
- **Deterministic ordering:** `ORDER BY d.transaction_id, c.transaction_id` provides deterministic iteration order for the greedy algorithm. Since DataSourcing returns rows ordered by `as_of` and then by natural database order (which is by primary key / insertion order), `transaction_id` order matches V1's iteration order.
- **No accounts table:** Per AP1 elimination, the accounts table is not sourced or referenced.
- **Cross-date matches (Edge Case 3):** No date constraint is applied within the matching — debits and credits from different as_of dates within the effective date range can match, replicating V1 behavior.

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "InterAccountTransfersV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "transactions",
      "schema": "datalake",
      "table": "transactions",
      "columns": ["transaction_id", "account_id", "txn_timestamp", "txn_type", "amount"]
    },
    {
      "type": "Transformation",
      "resultName": "candidates",
      "sql": "SELECT d.transaction_id AS debit_txn_id, d.account_id AS debit_account_id, d.amount AS amount, d.txn_timestamp AS debit_timestamp, d.as_of AS debit_as_of, c.transaction_id AS credit_txn_id, c.account_id AS credit_account_id FROM transactions d JOIN transactions c ON d.txn_type = 'Debit' AND c.txn_type = 'Credit' AND d.amount = c.amount AND CAST(d.txn_timestamp AS TEXT) = CAST(c.txn_timestamp AS TEXT) AND d.account_id != c.account_id ORDER BY d.transaction_id, c.transaction_id"
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.InterAccountTransfersV2Processor"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/inter_account_transfers/",
      "numParts": 1,
      "writeMode": "Overwrite"
    }
  ]
}
```

---

## 7. Writer Configuration

| Property | Value | V1 Match | Notes |
|----------|-------|----------|-------|
| Writer type | ParquetFileWriter | YES | Same as V1 |
| source | `output` | YES | Same DataFrame name |
| outputDirectory | `Output/double_secret_curated/inter_account_transfers/` | Path changed | V1: `Output/curated/inter_account_transfers/` |
| numParts | 1 | YES | Same as V1 |
| writeMode | Overwrite | YES | Same as V1. // V1 uses Overwrite — in multi-day gap-fill, only the last day's output survives (W9 noted). |

---

## 8. Proofmark Config Design

### Recommended Configuration

```yaml
comparison_target: "inter_account_transfers"
reader: parquet
threshold: 100.0
```

### Justification for Zero Overrides

- **No excluded columns:** All output columns are deterministic. There are no runtime timestamps, UUIDs, or execution-time-dependent values. The `as_of` column comes from source data (debit row), not from execution context.
- **No fuzzy columns:** All numeric comparisons in V1 use exact decimal equality (`debit.amount == credit.amount`). The `amount` column in the output is the raw debit amount, not a computed value. No floating-point arithmetic is involved.
- **Threshold 100.0:** Full strict comparison. Every row must match. The matching algorithm is deterministic for a given input order, and V2 replicates the same ordering semantics via `ORDER BY transaction_id`.

### Risk Assessment

The one risk to strict comparison is **BR-8 (iteration-order dependence)**. If V1's DataFrame row ordering differs from V2's DataSourcing row ordering (both come from the same database with `ORDER BY as_of`), different debit-credit pairings could result. Mitigation: V2's SQL orders candidates by `transaction_id`, and the External module processes them in that order. Since V1 iterates debits and credits in the order they appear in the DataSourcing result (ordered by `as_of`, then natural PK order), and `transaction_id` is the PK with monotonically increasing values, the orderings should be equivalent.

If initial Proofmark comparison fails due to ordering differences, the resolution path is:
1. Query the data to identify which pairs differ
2. Verify whether the difference is a legitimate alternate valid pairing (different but equivalent) vs. a bug
3. If alternate valid pairing: no code change needed, investigate if ordering can be tightened
4. If bug: fix the ordering in the SQL or External module

---

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Implementation |
|-----------------|-------------|----------------|
| BR-1: Debit/Credit separation by txn_type | SQL Design (Section 5) | SQL JOIN conditions: `d.txn_type = 'Debit' AND c.txn_type = 'Credit'`. Non-Debit/Credit rows silently excluded. |
| BR-2: Match conditions (amount, timestamp, account) | SQL Design (Section 5) | SQL JOIN: `d.amount = c.amount AND CAST(d.txn_timestamp AS TEXT) = CAST(c.txn_timestamp AS TEXT) AND d.account_id != c.account_id` |
| BR-3: Single credit match (first-match-wins) | External Module Design (Section 10) | HashSet tracks matched credit IDs; each credit used once. |
| BR-4: Single debit match (break after first) | External Module Design (Section 10) | Break after first matched credit per debit. |
| BR-5: as_of from debit row | SQL Design (Section 5), External Module (Section 10) | `debit_as_of` column carried from SQL; External writes it as `as_of`. |
| BR-6: Accounts table unused | Anti-Pattern Analysis (Section 3, AP1) | Accounts DataSourcing entry removed entirely. |
| BR-7: Empty output on null/empty transactions | External Module Design (Section 10) | Guard clause returns empty DataFrame with correct schema if candidates is empty. |
| BR-8: Iteration-order dependent matching | SQL Design (Section 5), External Module (Section 10) | SQL orders candidates by `(debit_txn_id, credit_txn_id)`. External iterates in that order. |
| Edge Case 1: Multiple credits for same debit | External Module Design (Section 10) | Break after first match — only first eligible credit in order is chosen. |
| Edge Case 2: Same-account pairs excluded | SQL Design (Section 5) | `d.account_id != c.account_id` in JOIN condition. |
| Edge Case 3: Cross-date matches allowed | SQL Design (Section 5) | No date constraint in JOIN — matches can span as_of dates within the effective range. |
| Edge Case 4: Timestamp string comparison | SQL Design (Section 5) | `CAST(txn_timestamp AS TEXT)` on both sides replicates `.ToString()` behavior. |
| Edge Case 5: Accounts sourced but unused | Anti-Pattern Analysis (Section 3, AP1) | ELIMINATED — accounts not sourced in V2. |
| Edge Case 6: Unmatched transactions dropped | External Module Design (Section 10) | Only matched pairs are added to output rows. |
| Output: Parquet, 1 part, Overwrite | Writer Config (Section 7) | ParquetFileWriter with numParts=1, writeMode=Overwrite. |

---

## 10. External Module Design

### Class: `InterAccountTransfersV2Processor`
### File: `ExternalModules/InterAccountTransfersV2Processor.cs`

**Responsibility:** Greedy first-match-wins assignment only. All data retrieval, filtering, joining, and candidate generation is handled upstream by DataSourcing + Transformation. The External module reads pre-joined candidate pairs and assigns matches.

### Input
- Reads `candidates` DataFrame from shared state (produced by Transformation module)
- Columns: `debit_txn_id`, `debit_account_id`, `amount`, `debit_timestamp`, `debit_as_of`, `credit_txn_id`, `credit_account_id`
- Rows are ordered by `(debit_txn_id, credit_txn_id)` — deterministic from SQL ORDER BY

### Output
- Writes `output` DataFrame to shared state
- Columns: `debit_txn_id`, `credit_txn_id`, `from_account_id`, `to_account_id`, `amount`, `txn_timestamp`, `as_of`

### Algorithm

```
1. Read `candidates` DataFrame from shared state.
2. If candidates is null or empty:
   - Write empty DataFrame with output schema to shared state as "output".
   - Return. (BR-7)
3. Initialize: matchedCredits = new HashSet<int>, matchedDebits = new HashSet<int>, outputRows = new List<Row>
4. Iterate candidate rows in order (already sorted by debit_txn_id, credit_txn_id):
   a. debitId = row["debit_txn_id"]
   b. creditId = row["credit_txn_id"]
   c. If matchedDebits contains debitId: skip (this debit already matched — BR-4)
   d. If matchedCredits contains creditId: skip (this credit already matched — BR-3)
   e. Add debitId to matchedDebits. Add creditId to matchedCredits.
   f. Add output row:
      - debit_txn_id = debitId
      - credit_txn_id = creditId
      - from_account_id = row["debit_account_id"]
      - to_account_id = row["credit_account_id"]
      - amount = row["amount"]
      - txn_timestamp = row["debit_timestamp"]   // string, from debit row
      - as_of = row["debit_as_of"]               // from debit row (BR-5)
5. Write DataFrame(outputRows, outputColumns) to shared state as "output".
```

### Design Notes

- **Greedy semantics preserved:** Since candidates are ordered by `(debit_txn_id, credit_txn_id)`, the first valid pair for each debit is the credit with the lowest transaction_id — matching V1's iteration order where debits and credits are iterated in their DataFrame appearance order (which follows DataSourcing's `ORDER BY as_of`, then natural PK order).
- **O(n) scan, not O(n^2):** Unlike V1's nested loop, the External module does a single pass over pre-joined candidates. The SQL JOIN handles the cross-product. This eliminates AP6 for the matching step while preserving identical output.
- **decimal for amounts:** The `amount` value flows through from the SQL Transformation. No arithmetic is performed on it in the External module — it is simply copied to the output row.
- **No accounts data:** The External module does not reference the accounts table at all (AP1 eliminated).
- **matchedDebits HashSet:** This is the V2 improvement over V1. V1 uses `break` to stop scanning credits after a match, then moves to the next debit. V2 pre-joins all candidate pairs and skips already-matched debits via HashSet lookup. The effect is identical: each debit matches at most one credit.
