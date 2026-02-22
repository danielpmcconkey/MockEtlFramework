# FSD: CoveredTransactionsV2

## Overview
CoveredTransactionsV2 replicates the exact business logic of CoveredTransactions, producing a denormalized transaction report for Checking-account transactions held by customers with active US addresses. The V2 implementation uses DscWriterUtil to write to `double_secret_curated.covered_transactions` instead of the framework's DataFrameWriter writing to `curated`.

## Design Decisions
- **External module approach (Pattern A)**: The original is an External-only pipeline with direct database queries (no DataSourcing). The V2 replicates this exactly with an identical External module that adds a DscWriterUtil.Write() call.
- **Write mode**: Append (overwrite=false) to match the original's Append write mode.
- **No DataFrameWriter step**: The V2 External module handles writing directly via DscWriterUtil.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | External | CoveredTransactionsV2Processor - replicates all original logic + writes to dsc |

## V2 External Module: CoveredTransactionsV2Processor
- File: ExternalModules/CoveredTransactionsV2Processor.cs
- Processing logic: Direct database queries for transactions (exact date), accounts (snapshot fallback, Checking only), customers (snapshot fallback), addresses (active US, earliest start_date), segments (first alphabetically). Joins, filters, sorts by customer_id ASC / transaction_id DESC. Sets record_count. Handles zero-row sentinel.
- Output columns: transaction_id, txn_timestamp, txn_type, amount, description, customer_id, name_prefix, first_name, last_name, sort_name, name_suffix, customer_segment, address_id, address_line1, city, state_province, postal_code, country, account_id, account_type, account_status, account_opened, as_of, record_count
- Target table: double_secret_curated.covered_transactions
- Write mode: Append (overwrite=false)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 (Checking only) | Account filter: `account_type == "Checking"` in checkingAccounts dictionary |
| BR-2 (Active US address) | Address query filter: `country = 'US' AND (end_date IS NULL OR end_date >= @date)`, and address lookup gate |
| BR-3 (Account snapshot fallback) | Account query: `WHERE as_of <= @date ORDER BY account_id, as_of DESC` |
| BR-4 (Customer snapshot fallback) | Customer query: `WHERE as_of <= @date ORDER BY id, as_of DESC` |
| BR-5 (Earliest address by start_date) | Address query: `ORDER BY customer_id, start_date ASC`, first-row-wins logic |
| BR-6 (First segment alphabetically) | Segment query: `DISTINCT ON (cs.customer_id) ORDER BY cs.customer_id, s.segment_code ASC` |
| BR-7 (Sort customer_id ASC, txn_id DESC) | Sort comparison in output row list |
| BR-8 (record_count on every row) | Post-processing sets record_count = total row count |
| BR-9 (Zero-row sentinel) | Conditional null row with as_of and record_count=0 |
| BR-10 (String trimming) | .Trim() on all string fields |
| BR-11 (Timestamp/date formatting) | FormatTimestamp and FormatDate helper methods |
| BR-12 (Append mode) | DscWriterUtil.Write with overwrite=false |
| BR-13 (External-only pipeline) | No DataSourcing steps in job config |
| BR-14 (Exact date for transactions) | Transaction query: `WHERE as_of = @date` |
