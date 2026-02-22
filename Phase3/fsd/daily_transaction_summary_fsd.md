# DailyTransactionSummary — Functional Specification Document

## Design Approach

**SQL-first.** The original already uses a SQL Transformation, but with unnecessary complexity: a subquery wrapper and verbose total_amount calculation. The V2 simplifies the SQL while producing identical output.

No External module needed (original did not use one either).

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y | Y | Removed unused `branches` DataSourcing module |
| AP-2    | N | N/A | Not applicable |
| AP-3    | N | N/A | Original already uses SQL Transformation |
| AP-4    | Y | Y | Removed unused columns `transaction_id`, `txn_timestamp`, `description` from transactions; sourcing only `account_id`, `txn_type`, `amount` |
| AP-5    | N | N/A | NULL handling not applicable (aggregation with CASE) |
| AP-6    | N | N/A | No External module |
| AP-7    | N | N/A | No magic values |
| AP-8    | Y | Y | Removed unnecessary subquery wrapper; simplified `total_amount` from `SUM(CASE Debit) + SUM(CASE Credit)` to `ROUND(SUM(t.amount), 2)` since all txn_types are either Debit or Credit |
| AP-9    | N | N/A | Name accurately reflects output |
| AP-10   | N | N/A | No undeclared dependencies |

## V2 Pipeline Design

1. **DataSourcing** `transactions` — `datalake.transactions` (account_id, txn_type, amount)
2. **Transformation** `daily_txn_summary` — Simplified aggregation SQL
3. **DataFrameWriter** — writes to `double_secret_curated.daily_transaction_summary`, Append mode

## SQL Transformation Logic

```sql
SELECT
    t.account_id,
    t.as_of,
    /* Total of all transaction amounts regardless of type */
    ROUND(SUM(t.amount), 2) AS total_amount,
    COUNT(*) AS transaction_count,
    /* Sum of Debit transaction amounts */
    ROUND(SUM(CASE WHEN t.txn_type = 'Debit' THEN t.amount ELSE 0 END), 2) AS debit_total,
    /* Sum of Credit transaction amounts */
    ROUND(SUM(CASE WHEN t.txn_type = 'Credit' THEN t.amount ELSE 0 END), 2) AS credit_total
FROM transactions t
GROUP BY t.account_id, t.as_of
ORDER BY t.as_of, t.account_id
```

**Key simplification:** The original computes `total_amount` as the sum of two CASE-based SUMs (`SUM(Debit) + SUM(Credit)`), which is equivalent to `SUM(amount)` since all transactions are either Debit or Credit. The V2 uses `SUM(amount)` directly and removes the unnecessary subquery wrapper.

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1: One row per account per date | `GROUP BY t.account_id, t.as_of` |
| BR-2: total_amount = sum of all amounts | `ROUND(SUM(t.amount), 2)` — equivalent to original's verbose form |
| BR-3: transaction_count = COUNT(*) | `COUNT(*) AS transaction_count` |
| BR-4: debit_total = sum of Debit amounts | `ROUND(SUM(CASE WHEN t.txn_type = 'Debit' THEN t.amount ELSE 0 END), 2)` |
| BR-5: credit_total = sum of Credit amounts | `ROUND(SUM(CASE WHEN t.txn_type = 'Credit' THEN t.amount ELSE 0 END), 2)` |
| BR-6: Ordered by as_of, account_id | `ORDER BY t.as_of, t.account_id` |
| BR-7: Append mode | DataFrameWriter `writeMode: "Append"` |
| BR-8: Weekend dates included | Transactions have weekend data; no date filtering applied |
| BR-9: All amounts rounded to 2 dp | `ROUND(..., 2)` on total_amount, debit_total, credit_total |
