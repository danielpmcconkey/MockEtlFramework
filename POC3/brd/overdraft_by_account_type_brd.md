# OverdraftByAccountType — Business Requirements Document

## Overview
Calculates overdraft rates per account type by counting overdraft events and total accounts for each type (Checking, Savings, Credit). Output is a Parquet file. Contains a known integer division bug that causes `overdraft_rate` to always be 0.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `output`
- **outputDirectory**: `Output/curated/overdraft_by_account_type/`
- **numParts**: 1
- **writeMode**: Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.overdraft_events | overdraft_id, account_id, customer_id, overdraft_amount, fee_amount, fee_waived, event_timestamp | Effective date range injected via shared state | [overdraft_by_account_type.json:4-11] |
| datalake.accounts | account_id, customer_id, account_type, account_status | Effective date range injected via shared state | [overdraft_by_account_type.json:13-18] |

### Table Schemas (from database)

**overdraft_events**: overdraft_id (integer), account_id (integer), customer_id (integer), overdraft_amount (numeric), fee_amount (numeric), fee_waived (boolean), event_timestamp (timestamp), as_of (date)

**accounts**: account_id (integer), customer_id (integer), account_type (varchar: Checking, Savings, Credit), account_status (varchar), as_of (date)

## Business Rules

BR-1: Account type is determined by looking up each overdraft event's `account_id` in the accounts DataFrame. If no match is found, the account type defaults to `"Unknown"`.
- Confidence: HIGH
- Evidence: [OverdraftByAccountTypeProcessor.cs:31-37,53-56] `accountTypeLookup[accountId] = accountType;` and fallback `"Unknown"`

BR-2: Account counts are computed by iterating ALL accounts rows (across all `as_of` dates in the effective range), counting per `account_type`. This means the same account is counted once per `as_of` date it appears on.
- Confidence: HIGH
- Evidence: [OverdraftByAccountTypeProcessor.cs:40-47] Iterates `accounts.Rows` without date filtering

BR-3: Overdraft counts are computed by iterating ALL overdraft event rows, looking up account type, and counting per type.
- Confidence: HIGH
- Evidence: [OverdraftByAccountTypeProcessor.cs:50-61] Row-by-row iteration; Comment: `AP6: Row-by-row iteration`

BR-4: **Integer division bug** — `overdraft_rate` is computed as `(decimal)(odCount / accountCount)` where both operands are `int`. Integer division truncates to 0 unless `odCount >= accountCount`, which is extremely unlikely.
- Confidence: HIGH
- Evidence: [OverdraftByAccountTypeProcessor.cs:71] `decimal overdraftRate = (decimal)(odCount / accountCount);` — Comment: `W4: Integer division`

BR-5: The `as_of` value in the output is taken from the first row of overdraft_events, applied to all output rows.
- Confidence: HIGH
- Evidence: [OverdraftByAccountTypeProcessor.cs:28] `var asOf = overdraftEvents.Rows[0]["as_of"];`

BR-6: Output includes one row per account type that exists in the accounts table, even if that type has zero overdrafts (overdraft_count would be 0).
- Confidence: HIGH
- Evidence: [OverdraftByAccountTypeProcessor.cs:64-82] Iterates `accountCounts` (all types), defaults `odCount` to 0

BR-7: The account type lookup uses the LAST seen value for each `account_id` (dictionary overwrites). If an account changes type across `as_of` dates, only the last-loaded type is used.
- Confidence: MEDIUM
- Evidence: [OverdraftByAccountTypeProcessor.cs:34] `accountTypeLookup[accountId] = accountType;` — overwrites on each row

BR-8: Effective dates are injected by the executor at runtime.
- Confidence: HIGH
- Evidence: [overdraft_by_account_type.json:4-18] No hardcoded dates in DataSourcing configs

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| account_type | accounts.account_type | Lookup key from accounts | [OverdraftByAccountTypeProcessor.cs:43-44] |
| account_count | Derived | Count of account rows per type (across all dates) | [OverdraftByAccountTypeProcessor.cs:45-46] |
| overdraft_count | Derived | Count of overdraft events per type | [OverdraftByAccountTypeProcessor.cs:59-60] |
| overdraft_rate | Derived | Integer division: overdraft_count / account_count (always 0 due to bug) | [OverdraftByAccountTypeProcessor.cs:71] |
| as_of | overdraft_events (first row) | Object pass-through from first source row | [OverdraftByAccountTypeProcessor.cs:28] |

## Non-Deterministic Fields
None identified. Output is deterministic given the same source data, though the `as_of` value depends on row ordering.

## Write Mode Implications
- **Overwrite** mode: Each execution replaces the Parquet directory. On multi-day auto-advance, only the final effective date's output survives.
- Confidence: HIGH
- Evidence: [overdraft_by_account_type.json:24] `"writeMode": "Overwrite"`

## Edge Cases

EC-1: **Integer division always yields 0** — Since overdraft count per type is almost always less than account count per type, the integer division `odCount / accountCount` truncates to 0. The correct formula would be `(decimal)odCount / accountCount`.
- Confidence: HIGH
- Evidence: [OverdraftByAccountTypeProcessor.cs:71] Comment: `W4: Integer division`

EC-2: **Account count inflation** — Account counts include ALL `as_of` dates in the range, so the same account is counted once per snapshot date. With 92 dates and 5000 accounts, account_count per type could be ~150,000+ instead of ~1,700.
- Confidence: HIGH
- Evidence: [OverdraftByAccountTypeProcessor.cs:40-47] No date filtering on account iteration

EC-3: **Unknown account type** — Overdraft events referencing account IDs not found in the accounts lookup are categorized as `"Unknown"`. However, since `accountCounts` only contains types from accounts, the "Unknown" type only appears in `overdraftCounts`, not in the output iteration (which iterates `accountCounts`). "Unknown" overdrafts are silently lost.
- Confidence: HIGH
- Evidence: [OverdraftByAccountTypeProcessor.cs:54-56,64] Iterates `accountCounts` keys, not `overdraftCounts` keys

EC-4: **Empty source data** — Returns empty DataFrame if either source is empty.
- Confidence: HIGH
- Evidence: [OverdraftByAccountTypeProcessor.cs:22-26]

EC-5: **Overwrite on multi-day runs** — Only the last effective date's output survives.
- Confidence: HIGH
- Evidence: [overdraft_by_account_type.json:24] `"writeMode": "Overwrite"`

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Account type lookup | [OverdraftByAccountTypeProcessor.cs:31-37,53-56] |
| BR-2: Account count calculation | [OverdraftByAccountTypeProcessor.cs:40-47] |
| BR-3: Overdraft count calculation | [OverdraftByAccountTypeProcessor.cs:50-61] |
| BR-4: Integer division bug | [OverdraftByAccountTypeProcessor.cs:71] |
| BR-5: as_of from first row | [OverdraftByAccountTypeProcessor.cs:28] |
| BR-6: All types included | [OverdraftByAccountTypeProcessor.cs:64-82] |
| BR-7: Last-seen account type | [OverdraftByAccountTypeProcessor.cs:34] |
| EC-1: Rate always 0 | [OverdraftByAccountTypeProcessor.cs:71] |
| EC-3: Unknown type lost | [OverdraftByAccountTypeProcessor.cs:54-56,64] |

## Open Questions
1. **Is the integer division intentional?** The `W4` comment suggests this is a known issue. The `overdraft_rate` column will always be 0 in practice, making it useless. Confidence: HIGH that this is a bug.
2. **Should account counts be per-date or total?** Currently counts all snapshot rows (inflated). If the intent is accounts per date, the logic needs date filtering. Confidence: MEDIUM.
