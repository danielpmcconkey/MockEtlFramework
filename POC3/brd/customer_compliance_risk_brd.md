# CustomerComplianceRisk — Business Requirements Document

## Overview
Calculates a composite compliance risk score per customer based on the count of compliance events, wire transfers, and high-value transactions. Produces one row per customer with risk scoring factors and a weighted total score.

## Output Type
CsvFileWriter

## Writer Configuration
- **outputFile**: `Output/curated/customer_compliance_risk.csv`
- **includeHeader**: true
- **trailerFormat**: None (no trailer)
- **writeMode**: Overwrite
- **lineEnding**: LF

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.compliance_events | event_id, customer_id, event_type, status | Effective date range via executor; counted per customer_id | [customer_compliance_risk.json:4-11] |
| datalake.wire_transfers | wire_id, customer_id, amount, direction | Effective date range via executor; counted per customer_id | [customer_compliance_risk.json:12-19] |
| datalake.transactions | transaction_id, account_id, amount, txn_type | Effective date range via executor; filtered to amount > 5000 | [customer_compliance_risk.json:20-27] |
| datalake.customers | id, first_name, last_name | Effective date range via executor; drives output (one row per customer) | [customer_compliance_risk.json:28-35] |

### Source Table Schemas (from database)

**compliance_events**: event_id (integer), customer_id (integer), event_type (varchar), event_date (date), status (varchar), review_date (date), as_of (date)

**wire_transfers**: wire_id (integer), customer_id (integer), account_id (integer), direction (varchar), amount (numeric), counterparty_name (varchar), counterparty_bank (varchar), status (varchar), wire_timestamp (timestamp), as_of (date)

**transactions**: transaction_id (integer), account_id (integer), txn_timestamp (timestamp), txn_type (varchar), amount (numeric), description (varchar), as_of (date)

**customers**: id (integer), prefix (varchar), first_name (varchar), last_name (varchar), sort_name (varchar), suffix (varchar), birthdate (date), as_of (date)

## Business Rules

BR-1: One output row is produced per customer in the customers DataFrame. Customers with zero compliance events, wires, or high-value transactions still appear with counts of 0.
- Confidence: HIGH
- Evidence: [CustomerComplianceRiskCalculator.cs:77-78] — iterates `customers.Rows`, uses `GetValueOrDefault(..., 0)`

BR-2: Compliance event count is ALL events per customer_id (no status filter). Every event_type and status contributes equally.
- Confidence: HIGH
- Evidence: [CustomerComplianceRiskCalculator.cs:32-41] — no filter on event_type or status

BR-3: Wire count is ALL wire transfers per customer_id (no direction or amount filter).
- Confidence: HIGH
- Evidence: [CustomerComplianceRiskCalculator.cs:44-53] — no filter on direction, amount, or status

BR-4: High-value transaction count uses amount > 5000 threshold and is keyed by account_id, NOT customer_id. The account_id is used as a proxy for customer_id without performing a proper join through the accounts table.
- Confidence: HIGH
- Evidence: [CustomerComplianceRiskCalculator.cs:57-73] — comment "use account_id as customer_id"; `highTxnCountByCustomer[accountId]` — dictionary keyed by accountId but looked up by customerId

BR-5: Due to BR-4, the high_txn_count for a customer is only non-zero if their customer_id numerically equals one of their account_ids. This is effectively a bug — the account-to-customer mapping is not performed.
- Confidence: HIGH
- Evidence: [CustomerComplianceRiskCalculator.cs:66-67,85] — `highTxnCountByCustomer` is keyed by accountId; [CustomerComplianceRiskCalculator.cs:85] — looked up by customerId; in the data, customer_ids start at 1001+ while account_ids start at 3001+, so there will be NO matches.

BR-6: Risk score formula: `risk_score = (compliance_events * 30.0) + (wire_count * 20.0) + (high_txn_count * 10.0)`. Uses `double` arithmetic, not `decimal`.
- Confidence: HIGH
- Evidence: [CustomerComplianceRiskCalculator.cs:88] — `double riskScore = (complianceCount * 30.0) + (wireCount * 20.0) + (highTxnCount * 10.0)`

BR-7: Risk score is rounded to 2 decimal places using banker's rounding (MidpointRounding.ToEven).
- Confidence: HIGH
- Evidence: [CustomerComplianceRiskCalculator.cs:91] — `Math.Round(riskScore, 2, MidpointRounding.ToEven)`

BR-8: Since high_txn_count is effectively always 0 (BR-5) and risk score components are integer multiples of 30.0 and 20.0, the double arithmetic and banker's rounding have no practical effect for current data — results will always be exact integers.
- Confidence: HIGH
- Evidence: Mathematical analysis of BR-5, BR-6, and current data distribution

BR-9: If the customers DataFrame is null or empty, an empty output DataFrame is produced.
- Confidence: HIGH
- Evidence: [CustomerComplianceRiskCalculator.cs:21-24]

BR-10: NULL first_name or last_name in customers is coalesced to empty string.
- Confidence: HIGH
- Evidence: [CustomerComplianceRiskCalculator.cs:80-81] — `?.ToString() ?? ""`

BR-11: The as_of value for each output row comes directly from the customer row's as_of field, not from `__maxEffectiveDate`.
- Confidence: HIGH
- Evidence: [CustomerComplianceRiskCalculator.cs:102] — `["as_of"] = custRow["as_of"]`

BR-12: Current data shows max transaction amount is 4200.00 and max wire amount is 49959.00, meaning the > 5000 threshold for transactions will never be met, and high_txn_count will always be 0 regardless of the account_id/customer_id mismatch.
- Confidence: HIGH
- Evidence: [DB query: SELECT MAX(amount) FROM datalake.transactions → 4200.00]

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | customers.id | Convert.ToInt32 | [CustomerComplianceRiskCalculator.cs:79] |
| first_name | customers.first_name | NULL coalesced to "" | [CustomerComplianceRiskCalculator.cs:80] |
| last_name | customers.last_name | NULL coalesced to "" | [CustomerComplianceRiskCalculator.cs:81] |
| compliance_events | Computed | COUNT of all compliance_events per customer_id | [CustomerComplianceRiskCalculator.cs:83] |
| wire_count | Computed | COUNT of all wire_transfers per customer_id | [CustomerComplianceRiskCalculator.cs:84] |
| high_txn_count | Computed | COUNT of transactions where amount > 5000, keyed by account_id (mismatched lookup) | [CustomerComplianceRiskCalculator.cs:85] |
| risk_score | Computed | `(compliance_events * 30) + (wire_count * 20) + (high_txn_count * 10)`, double arithmetic, banker's rounding to 2 dp | [CustomerComplianceRiskCalculator.cs:88,91] |
| as_of | customers.as_of | Direct passthrough from customer row | [CustomerComplianceRiskCalculator.cs:102] |

## Non-Deterministic Fields
None identified. Output row order follows the iteration order of the customers DataFrame.

## Write Mode Implications
- **Overwrite** mode: each run replaces the entire output file. Multi-day runs retain only the last effective date's output.
- Evidence: [customer_compliance_risk.json:43]

## Edge Cases

1. **Empty customers**: If customers DataFrame is null or empty, empty output is produced.
   - Evidence: [CustomerComplianceRiskCalculator.cs:21-24]

2. **Account_id/customer_id mismatch**: High-value transactions are counted by account_id but looked up by customer_id. In current data, customer IDs (1001+) never match account IDs (3001+), so high_txn_count is always 0.
   - Evidence: [CustomerComplianceRiskCalculator.cs:66-67,85]; [DB data ranges]

3. **No transactions > 5000**: Current data max transaction amount is 4200, so even if the lookup worked correctly, high_txn_count would be 0.
   - Evidence: [DB query: MAX(amount) = 4200.00]

4. **Double precision**: Risk score uses double arithmetic, which could introduce floating-point epsilon differences compared to decimal arithmetic. For integer inputs, this is not an issue.
   - Evidence: [CustomerComplianceRiskCalculator.cs:88]

5. **Customers with no related data**: Customers with zero compliance events and zero wires get risk_score = 0.
   - Evidence: [CustomerComplianceRiskCalculator.cs:83-85] — `GetValueOrDefault(..., 0)`

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: One row per customer | [CustomerComplianceRiskCalculator.cs:77-78] |
| BR-2: All compliance events counted | [CustomerComplianceRiskCalculator.cs:32-41] |
| BR-3: All wires counted | [CustomerComplianceRiskCalculator.cs:44-53] |
| BR-4: High txn keyed by account_id | [CustomerComplianceRiskCalculator.cs:57-73] |
| BR-5: account_id/customer_id mismatch bug | [CustomerComplianceRiskCalculator.cs:66-67,85] |
| BR-6: Risk score formula (double) | [CustomerComplianceRiskCalculator.cs:88] |
| BR-7: Banker's rounding | [CustomerComplianceRiskCalculator.cs:91] |
| BR-8: Effective zero high_txn | Mathematical analysis |
| BR-9: Empty input guard | [CustomerComplianceRiskCalculator.cs:21-24] |
| BR-10: NULL coalescing | [CustomerComplianceRiskCalculator.cs:80-81] |
| BR-11: as_of from customer row | [CustomerComplianceRiskCalculator.cs:102] |
| BR-12: Data range constraint | [DB queries on datalake.transactions] |

## Open Questions
1. The account_id-as-customer_id proxy for high-value transaction counting appears to be a bug. Should transactions be joined through the accounts table to resolve customer_id?
   - Confidence: HIGH — the code comment acknowledges this is "simplified" but the mismatch means the feature is non-functional
2. With max transaction amount of 4200 and the threshold at 5000, should the threshold be lowered, or is the current data set simply not representative?
   - Confidence: LOW
