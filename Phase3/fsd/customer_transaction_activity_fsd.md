# CustomerTransactionActivity — Functional Specification Document

## Design Approach

**SQL-first.** The original External module (CustomerTxnActivityBuilder.cs) builds an account-to-customer lookup dictionary, then iterates transactions to aggregate by customer. This is a standard SQL JOIN + GROUP BY pattern.

The SQL approach uses `INNER JOIN` between transactions and accounts on `account_id` and `as_of`, which naturally:
- Maps transactions to customers via account ownership
- Excludes transactions with unknown account_ids (matching the `customerId == 0 continue` guard)
- Produces zero rows on weekends (accounts has no weekend data, so JOIN returns nothing — matching the empty guard)

No External module needed.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | N | N/A | No unused data sources |
| AP-2    | N | N/A | Not applicable |
| AP-3    | Y | Y | Replaced External module with SQL Transformation. The logic (JOIN accounts to transactions, GROUP BY customer_id with COUNT/SUM) is standard SQL. |
| AP-4    | Y | Y | Removed unused `transaction_id` from transactions DataSourcing. V2 sources only `account_id`, `txn_type`, `amount`. |
| AP-5    | N | N/A | The asymmetric empty guards (accounts vs transactions) are replaced by a single SQL JOIN that handles both cases naturally |
| AP-6    | Y | Y | Two foreach loops (account lookup build + transaction aggregation) replaced with a single SQL query using JOIN + GROUP BY |
| AP-7    | N | N/A | No magic values |
| AP-8    | N | N/A | No complex SQL |
| AP-9    | N | N/A | Name accurately reflects output |
| AP-10   | N | N/A | No undeclared dependencies |

## V2 Pipeline Design

1. **DataSourcing** `transactions` — `datalake.transactions` (account_id, txn_type, amount)
2. **DataSourcing** `accounts` — `datalake.accounts` (account_id, customer_id)
3. **Transformation** `activity_output` — SQL JOIN + GROUP BY
4. **DataFrameWriter** — writes to `double_secret_curated.customer_transaction_activity`, Append mode

## SQL Transformation Logic

```sql
SELECT
    a.customer_id,
    t.as_of,
    COUNT(*) AS transaction_count,
    SUM(t.amount) AS total_amount,
    /* Count of Debit transactions */
    SUM(CASE WHEN t.txn_type = 'Debit' THEN 1 ELSE 0 END) AS debit_count,
    /* Count of Credit transactions */
    SUM(CASE WHEN t.txn_type = 'Credit' THEN 1 ELSE 0 END) AS credit_count
FROM transactions t
JOIN accounts a ON t.account_id = a.account_id AND t.as_of = a.as_of
GROUP BY a.customer_id, t.as_of
```

**Weekend handling:** On weekends, the accounts table has no data, so the INNER JOIN returns zero rows. This exactly matches the original External module's behavior where the empty guard on accounts returns an empty DataFrame.

**Orphan transaction handling:** Transactions with account_ids not present in the accounts table are naturally excluded by the INNER JOIN, matching the original's `if (customerId == 0) continue` guard.

**Note on ordering:** The original External module produces output in dictionary iteration order (unspecified). The SQL does not include an ORDER BY because the original output has no guaranteed ordering. The comparison in Phase D will use set-based EXCEPT which is order-independent.

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1: One row per customer with transactions | `GROUP BY a.customer_id, t.as_of` — only customers with at least one transaction appear |
| BR-2: Customer attribution via accounts | `JOIN accounts a ON t.account_id = a.account_id AND t.as_of = a.as_of` |
| BR-3: Orphan transactions skipped | INNER JOIN excludes unmatched account_ids |
| BR-4: transaction_count | `COUNT(*)` counts all transactions per customer |
| BR-5: total_amount | `SUM(t.amount)` sums all amounts per customer |
| BR-6: debit_count | `SUM(CASE WHEN t.txn_type = 'Debit' THEN 1 ELSE 0 END)` |
| BR-7: credit_count | `SUM(CASE WHEN t.txn_type = 'Credit' THEN 1 ELSE 0 END)` |
| BR-8: as_of from transaction data | `t.as_of` in GROUP BY — same for all rows on a single-date run |
| BR-9: Empty on weekends (accounts empty) | INNER JOIN returns zero rows when accounts has no data |
| BR-10: Empty when no transactions | GROUP BY returns zero rows when no transactions exist |
| BR-11: Append mode | DataFrameWriter `writeMode: "Append"` |
