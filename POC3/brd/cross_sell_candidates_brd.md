# CrossSellCandidates -- Business Requirements Document

## Overview
Identifies cross-sell opportunities by analyzing which banking products each customer currently holds (checking, savings, credit accounts, cards, investments) and listing the products they are missing. Output is a per-customer row with boolean/string product flags and a consolidated missing products list.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/cross_sell_candidates.csv`
- **includeHeader**: true
- **writeMode**: Overwrite
- **lineEnding**: LF
- **trailerFormat**: (not configured -- no trailer)

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.customers | id, first_name, last_name | Effective date range (injected by executor) | [cross_sell_candidates.json:8-10] |
| datalake.accounts | account_id, customer_id, account_type | Effective date range (injected by executor) | [cross_sell_candidates.json:14-16] |
| datalake.cards | card_id, customer_id | Effective date range (injected by executor) | [cross_sell_candidates.json:20-22] |
| datalake.investments | investment_id, customer_id | Effective date range (injected by executor) | [cross_sell_candidates.json:26-28] |

## Business Rules

BR-1: Product ownership is determined per-customer by checking account types ("Checking", "Savings", "Credit"), card presence, and investment presence.
- Confidence: HIGH
- Evidence: [CrossSellCandidateFinder.cs:70-74] -- `acctTypes.Contains("Checking")` etc., `customersWithCards.Contains(customerId)`, `customersWithInvestments.Contains(customerId)`.

BR-2: Account types are matched by exact string comparison against "Checking", "Savings", and "Credit".
- Confidence: HIGH
- Evidence: [CrossSellCandidateFinder.cs:70-72] -- `acctTypes.Contains("Checking")` etc. DB values confirmed as "Checking", "Credit", "Savings".

BR-3: The `has_card` column uses asymmetric representation: "Yes" if the customer has a card, "No Card" if they do not.
- Confidence: HIGH
- Evidence: [CrossSellCandidateFinder.cs:97] -- `hasCard ? "Yes" : "No Card"`. This is a string representation, not a boolean.

BR-4: The `has_investment` column uses a numeric representation: 1 if the customer has investments, 0 if not. This differs from the card representation.
- Confidence: HIGH
- Evidence: [CrossSellCandidateFinder.cs:85,98] -- `investmentValue = hasInvestment ? 1 : 0`.

BR-5: The missing_products list is built by checking for missing Checking, Savings, Credit, and Card (as "No Card"), but NOT missing Investment.
- Confidence: HIGH
- Evidence: [CrossSellCandidateFinder.cs:77-83] -- missing list adds "Checking", "Savings", "Credit", "No Card" but does not add an investment-related entry.

BR-6: Missing products are joined with "; " (semicolon space) separator. If the customer has all products, "None" is used.
- Confidence: HIGH
- Evidence: [CrossSellCandidateFinder.cs:87] -- `string.Join("; ", missing)` with fallback to `"None"`.

BR-7: The `as_of` column is set to the `__maxEffectiveDate` from shared state, not from individual source rows.
- Confidence: HIGH
- Evidence: [CrossSellCandidateFinder.cs:28,100] -- `maxDate = (DateOnly)sharedState["__maxEffectiveDate"]`.

BR-8: When the customers DataFrame is null or empty, the output is an empty DataFrame with correct schema. Other sources being null/empty does not trigger the empty guard.
- Confidence: HIGH
- Evidence: [CrossSellCandidateFinder.cs:22-26] -- only customers triggers the empty guard.

BR-9: has_checking, has_savings, has_credit are boolean values (true/false).
- Confidence: HIGH
- Evidence: [CrossSellCandidateFinder.cs:94-96] -- `hasChecking`, `hasSavings`, `hasCredit` are C# bool.

BR-10: Iteration is customer-driven. Every customer gets a row, regardless of product ownership.
- Confidence: HIGH
- Evidence: [CrossSellCandidateFinder.cs:65] -- `foreach (var custRow in customers.Rows)`.

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | customers.id | Cast to int via Convert.ToInt32 | [CrossSellCandidateFinder.cs:67] |
| first_name | customers.first_name | ToString with null coalesce to "" | [CrossSellCandidateFinder.cs:93] |
| last_name | customers.last_name | ToString with null coalesce to "" | [CrossSellCandidateFinder.cs:93] |
| has_checking | accounts.account_type | Boolean: true if customer has "Checking" account | [CrossSellCandidateFinder.cs:70,94] |
| has_savings | accounts.account_type | Boolean: true if customer has "Savings" account | [CrossSellCandidateFinder.cs:71,95] |
| has_credit | accounts.account_type | Boolean: true if customer has "Credit" account | [CrossSellCandidateFinder.cs:72,96] |
| has_card | cards.card_id | String: "Yes" if customer has cards, "No Card" if not | [CrossSellCandidateFinder.cs:73,97] |
| has_investment | investments.investment_id | Integer: 1 if customer has investments, 0 if not | [CrossSellCandidateFinder.cs:74,85,98] |
| missing_products | Derived | Semicolon-separated list of missing products, or "None" | [CrossSellCandidateFinder.cs:77-87,99] |
| as_of | __maxEffectiveDate | From shared state, not source rows | [CrossSellCandidateFinder.cs:28,100] |

## Non-Deterministic Fields
None identified.

## Write Mode Implications
- **Overwrite**: Each execution replaces the entire output file. For multi-day auto-advance runs, only the last effective date's output survives on disk.

## Edge Cases
- **Customer with no accounts, cards, or investments**: All product flags are false/0/"No Card". missing_products will list all checked products.
- **Asymmetric NULL handling**: Card absence is represented as string "No Card", investment absence as integer 0. This inconsistency appears intentional (commented as "AP5" in code).
- **Missing investments not tracked in missing_products**: A customer without investments does NOT get an investment-related entry in missing_products. Only Checking, Savings, Credit, and Card are checked.
- **Empty customers**: Returns empty DataFrame; other empty sources still produce output rows.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Product ownership detection | [CrossSellCandidateFinder.cs:70-74] |
| Asymmetric card representation | [CrossSellCandidateFinder.cs:82,97] |
| Asymmetric investment representation | [CrossSellCandidateFinder.cs:85,98] |
| Missing products semicolon join | [CrossSellCandidateFinder.cs:87] |
| as_of from __maxEffectiveDate | [CrossSellCandidateFinder.cs:28,100] |
| Empty guard on customers only | [CrossSellCandidateFinder.cs:22-26] |
| Overwrite mode | [cross_sell_candidates.json:43] |
| LF line endings | [cross_sell_candidates.json:44] |

## Open Questions
- OQ-1: Investment absence is not included in the missing_products list, unlike the other four product types. Whether this is intentional or a bug is unclear. Confidence: MEDIUM -- the code explicitly handles cards and investments differently from each other and from accounts.
