# FSD: CustomerTransactionActivityV2

## Overview
Replaces the original CustomerTransactionActivity job with a V2 implementation that writes to the `double_secret_curated` schema instead of `curated`. The V2 External module replicates the exact same business logic as the original CustomerTxnActivityBuilder and then writes directly to `double_secret_curated.customer_transaction_activity` via DscWriterUtil.

## Design Decisions
- **Pattern A (External module replacement)**: The original job uses DataSourcing x2 -> External (CustomerTxnActivityBuilder) -> DataFrameWriter. The V2 replaces both the External and DataFrameWriter steps with a single V2 External module that combines the processing logic and writing.
- The V2 module replicates the original's account-to-customer lookup, transaction aggregation per customer (count, total_amount, debit_count, credit_count), and as_of extraction from first transaction row.
- Write mode is Append (matching original), so DscWriterUtil.Write is called with overwrite=false.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=transactions, columns=[transaction_id, account_id, txn_type, amount], resultName=transactions |
| 2 | DataSourcing | schema=datalake, table=accounts, columns=[account_id, customer_id], resultName=accounts |
| 3 | External | CustomerTransactionActivityV2Processor -- replicates original logic + writes to dsc |

## V2 External Module: CustomerTransactionActivityV2Processor
- File: ExternalModules/CustomerTransactionActivityV2Processor.cs
- Processing logic: Reads transactions and accounts from shared state. Builds account_id -> customer_id lookup. Iterates transactions, maps to customer_id via account lookup, aggregates count/amount/debit_count/credit_count per customer. Gets as_of from first transaction row. Writes to double_secret_curated via DscWriterUtil with overwrite=false.
- Output columns: customer_id, as_of, transaction_count, total_amount, debit_count, credit_count
- Target table: double_secret_curated.customer_transaction_activity
- Write mode: Append (overwrite=false)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | V2 processor builds accountToCustomer lookup, maps transactions to customers |
| BR-2 | V2 processor skips transactions with customerId == 0 (unmatched account_id) |
| BR-3 | V2 processor aggregates count, totalAmount, debits, credits per customer |
| BR-4 | V2 processor checks txnType == "Debit" and == "Credit" for debit/credit counts |
| BR-5 | Non-Debit/Credit types contribute 0 to debit_count and credit_count but are counted in transaction_count |
| BR-6 | V2 processor gets as_of from transactions.Rows[0]["as_of"] |
| BR-7 | V2 processor has guard clause returning empty DataFrame when accounts is null/empty |
| BR-8 | V2 processor has guard clause returning empty DataFrame when transactions is null/empty |
| BR-9 | DscWriterUtil.Write called with overwrite=false (Append) |
| BR-10 | Guard clause on accounts ensures no output on weekends (accounts table is weekday-only) |
| BR-11 | V2 processor sums all transaction amounts regardless of type |
