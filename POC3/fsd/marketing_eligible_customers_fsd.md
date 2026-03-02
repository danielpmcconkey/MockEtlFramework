# MarketingEligibleCustomersV2 -- Functional Specification Document

## 1. Job Summary

This job identifies customers eligible for marketing by requiring opt-in (`opted_in = true`) to ALL THREE required marketing channels: `MARKETING_EMAIL`, `MARKETING_SMS`, and `PUSH_NOTIFICATIONS`. It implements weekend fallback logic (Saturday/Sunday use Friday's preference data), joins customer names and email addresses, and outputs to CSV. **Critical BRD discrepancy:** the BRD (BR-1, Overview, Edge Case 5) claims only two channels are required and that `PUSH_NOTIFICATIONS` is ignored. The V1 source code at `[MarketingEligibleProcessor.cs:62-64]` explicitly includes `PUSH_NOTIFICATIONS` in the `requiredTypes` set, and the eligibility check at `[MarketingEligibleProcessor.cs:92]` compares against `requiredTypes.Count` (which is 3). V2 follows the code, not the BRD, because output equivalence requires matching V1 behavior as written.

---

## 2. V2 Module Chain

**Tier:** Tier 2 -- Framework + Minimal External (SCALPEL)

```
DataSourcing (customer_preferences)
  -> DataSourcing (customers)
  -> DataSourcing (email_addresses)
  -> External (MarketingEligibleCustomersV2Processor)
  -> CsvFileWriter
```

### Tier Justification

Tier 1 (pure SQL) was evaluated first but is insufficient. The weekend fallback logic computes a `targetDate` from `__maxEffectiveDate` in shared state -- subtracting 1 day for Saturday, 2 days for Sunday -- and then conditionally applies a date filter on `customer_preferences` only when `targetDate != maxDate`. The Transformation module registers DataFrames as SQLite tables but does not expose shared state scalar values like `__maxEffectiveDate` as queryable values inside SQL. The conditional behavior (apply date filter on weekends, skip it entirely on weekdays) is procedural and cannot be cleanly expressed in a single SQL statement.

A minimal External module (Tier 2) handles ONLY the weekend date computation, conditional preference filtering, three-channel eligibility check, join logic, and output assembly. DataSourcing handles all data retrieval. CsvFileWriter handles output.

### Module Details

| Step | Module | Purpose |
|------|--------|---------|
| 1 | DataSourcing | Fetch `customer_preferences` (customer_id, preference_type, opted_in) |
| 2 | DataSourcing | Fetch `customers` (id, first_name, last_name) -- AP4: prefix, suffix, birthdate removed |
| 3 | DataSourcing | Fetch `email_addresses` (customer_id, email_address) -- AP4: email_id, email_type removed |
| 4 | External | Weekend fallback date computation, conditional preference date filtering, 3-channel eligibility check, customer/email join, output DataFrame assembly |
| 5 | CsvFileWriter | Write to `Output/double_secret_curated/marketing_eligible_customers.csv` |

---

## 3. DataSourcing Config

### customer_preferences

| Property | Value |
|----------|-------|
| resultName | `customer_preferences` |
| schema | `datalake` |
| table | `customer_preferences` |
| columns | `customer_id`, `preference_type`, `opted_in` |

**Effective date handling:** Framework-injected via `__minEffectiveDate` / `__maxEffectiveDate` in shared state. DataSourcing automatically filters on `as_of` between these dates and appends `as_of` as a column. No `additionalFilter` needed.

**AP4 note:** V1 sources `preference_id` (`[marketing_eligible_customers.json:10]`) but it is never referenced in the processor logic -- the code iterates `prefs.Rows` and accesses only `customer_id`, `preference_type`, `opted_in`, and the auto-injected `as_of` column (`[MarketingEligibleProcessor.cs:73-79]`). V2 eliminates `preference_id`.

### customers

| Property | Value |
|----------|-------|
| resultName | `customers` |
| schema | `datalake` |
| table | `customers` |
| columns | `id`, `first_name`, `last_name` |

**AP4 note:** V1 sources `prefix`, `suffix`, `birthdate` (`[marketing_eligible_customers.json:15-17]`) but these are never referenced in the processor -- only `id`, `first_name`, and `last_name` are extracted at `[MarketingEligibleProcessor.cs:46-47]`. V2 eliminates `prefix`, `suffix`, `birthdate`.

### email_addresses

| Property | Value |
|----------|-------|
| resultName | `email_addresses` |
| schema | `datalake` |
| table | `email_addresses` |
| columns | `customer_id`, `email_address` |

**AP4 note:** V1 sources `email_id` and `email_type` (`[marketing_eligible_customers.json:21-23]`) but neither is referenced in the processor -- only `customer_id` and `email_address` are accessed at `[MarketingEligibleProcessor.cs:56-57]`. V2 eliminates `email_id` and `email_type`.

---

## 4. Transformation SQL

**Not applicable.** This is a Tier 2 design with no Transformation (SQL) module. All business logic is implemented in the External module because weekend fallback requires procedural access to `__maxEffectiveDate` from shared state.

### External Module Algorithm

```
1. Read __maxEffectiveDate from shared state
2. Compute targetDate:
   - Saturday: maxDate - 1 day (Friday)
   - Sunday: maxDate - 2 days (Friday)
   - Weekday: maxDate (no change)
3. Read customer_preferences, customers, email_addresses DataFrames from shared state
4. Empty guard: if prefs or customers is null/empty, return empty DataFrame
   with schema [customer_id, first_name, last_name, email_address, as_of]
5. Build customerLookup: Dictionary<int, (string firstName, string lastName)>
   from customers DataFrame. Null names coalesced to "" via ?.ToString() ?? ""
6. Build emailLookup: Dictionary<int, string> from email_addresses DataFrame.
   Last-wins dictionary overwrite for duplicate customer_ids.
7. Build customerOptIns: Dictionary<int, HashSet<string>> from customer_preferences:
   - If targetDate != maxDate (weekend): skip rows where as_of != targetDate
   - If targetDate == maxDate (weekday): process ALL rows in the effective date range
   - For each remaining row: if opted_in == true AND preference_type is in
     RequiredMarketingChannels, add the preference_type to the customer's HashSet
8. RequiredMarketingChannels = { "MARKETING_EMAIL", "MARKETING_SMS", "PUSH_NOTIFICATIONS" }
9. For each entry in customerOptIns:
   - If HashSet.Count == RequiredMarketingChannels.Count (3) AND customerLookup
     contains the customer_id:
     - Get firstName, lastName from customerLookup
     - Get email from emailLookup (default "" if missing)
     - Build output row: customer_id, first_name, last_name, email_address, as_of=targetDate
10. Store output DataFrame in shared state as "output"
```

### Key Business Rules Mapped to Algorithm

| Rule | Algorithm Step | V1 Evidence |
|------|---------------|-------------|
| BR-1 (CORRECTED: 3 channels) | Steps 8-9: requiredTypes has 3 entries; count check requires all 3 | `[MarketingEligibleProcessor.cs:62-64, 92]` |
| BR-2 (W2: Weekend fallback) | Step 2: targetDate computation | `[MarketingEligibleProcessor.cs:20-22]` |
| BR-3 (Conditional date filter) | Step 7: filter only when targetDate != maxDate | `[MarketingEligibleProcessor.cs:71-75]` |
| BR-4 (Customer must exist) | Step 9: customerLookup.ContainsKey check | `[MarketingEligibleProcessor.cs:92]` |
| BR-5 (Missing email = "") | Step 9: emailLookup.GetValueOrDefault(key, "") | `[MarketingEligibleProcessor.cs:95]` |
| BR-6 (as_of = targetDate) | Step 9: output row as_of set to targetDate | `[MarketingEligibleProcessor.cs:103]` |
| BR-8 (Last-wins email) | Step 6: dictionary assignment overwrites | `[MarketingEligibleProcessor.cs:56]` |
| BR-9 (Empty guard) | Step 4: null/empty check on prefs and customers | `[MarketingEligibleProcessor.cs:36-39]` |

---

## 5. Writer Config

| Property | Value | Notes |
|----------|-------|-------|
| type | CsvFileWriter | Matches V1 `[marketing_eligible_customers.json:32]` |
| source | `output` | DataFrame name in shared state |
| outputFile | `Output/double_secret_curated/marketing_eligible_customers.csv` | V2 output path (V1: `Output/curated/marketing_eligible_customers.csv`) |
| includeHeader | `true` | Matches V1 `[marketing_eligible_customers.json:35]` |
| writeMode | `Overwrite` | Matches V1 `[marketing_eligible_customers.json:36]`; W9: prior days' data is lost on each run |
| lineEnding | `LF` | Matches V1 `[marketing_eligible_customers.json:37]` |
| trailerFormat | (not set) | Matches V1 -- no trailer row |
| numParts | N/A | CSV, not Parquet |

### Output Schema

| Column | Type | Source | Transformation |
|--------|------|--------|---------------|
| customer_id | int | customerOptIns iteration key | Customer IDs that passed 3-channel eligibility |
| first_name | string | customers.first_name | `?.ToString() ?? ""` (null coalesced to empty string) |
| last_name | string | customers.last_name | `?.ToString() ?? ""` (null coalesced to empty string) |
| email_address | string | email_addresses.email_address | Last-wins dictionary overwrite by customer_id; `""` if customer has no email on file |
| as_of | DateOnly | Derived from `__maxEffectiveDate` | Set to targetDate (Friday fallback on weekends) |

---

## 6. Wrinkle Replication

### W2: Weekend Fallback

- **V1 behavior:** Saturday uses Friday's data (`maxDate - 1`); Sunday uses Friday's data (`maxDate - 2`). On weekdays, no date adjustment. Evidence: `[MarketingEligibleProcessor.cs:20-22]`.
- **V2 replication:** Clean guard clause with explicit comments:
  ```csharp
  // W2: Weekend fallback -- Saturday/Sunday use Friday's preference data
  DateOnly targetDate = maxDate;
  if (maxDate.DayOfWeek == DayOfWeek.Saturday)
      targetDate = maxDate.AddDays(-1); // Friday
  else if (maxDate.DayOfWeek == DayOfWeek.Sunday)
      targetDate = maxDate.AddDays(-2); // Friday
  ```
- **Additionally**, when weekend fallback is active (`targetDate != maxDate`), only preference rows with `as_of == targetDate` are processed. On weekdays, all rows in the effective date range are processed without date filtering. Evidence: `[MarketingEligibleProcessor.cs:71-75]`.

### W9: Wrong writeMode

- **V1 behavior:** Uses `Overwrite` mode, meaning each execution replaces the entire CSV. In multi-day auto-advance runs, only the last effective date's output persists on disk. Evidence: `[marketing_eligible_customers.json:36]`.
- **V2 replication:** Preserve `Overwrite` write mode exactly. Add comment: `// V1 uses Overwrite -- prior days' data is lost on each run.`

### Wrinkles Not Applicable

| ID | Why Not Applicable |
|----|-------------------|
| W1 | V1 does NOT return empty on Sundays; it uses Friday's data via W2. |
| W3a-c | No summary/boundary rows in this job. |
| W4 | No integer division or percentage calculations. |
| W5 | No rounding operations. |
| W6 | No monetary accumulation with doubles. |
| W7 | No trailer. CsvFileWriter has no trailerFormat. |
| W8 | No trailer date. |
| W10 | Not a Parquet job. |
| W12 | Not an Append CSV job. |

---

## 7. Anti-Pattern Elimination

### AP3: Unnecessary External Module -- PARTIALLY ADDRESSED

- **V1 problem:** External module does join/filter logic partially expressible in SQL.
- **V2 action:** External module retained at Tier 2, but ONLY because weekend fallback requires procedural access to `__maxEffectiveDate` in shared state for conditional date logic. The module is minimal and focused -- it does not perform data retrieval (DataSourcing does that) or file output (CsvFileWriter does that).

### AP4: Unused Columns -- ELIMINATED

- **V1 problem:** Four unused columns sourced across three tables:
  - `customers`: `prefix`, `suffix`, `birthdate` sourced but never referenced. Evidence: `[marketing_eligible_customers.json:15-17]`, `[MarketingEligibleProcessor.cs:34, 46-47]` (only `first_name` and `last_name` extracted).
  - `email_addresses`: `email_type` sourced but never referenced. Evidence: `[marketing_eligible_customers.json:23]`, `[MarketingEligibleProcessor.cs:56-57]` (only `customer_id` and `email_address` used).
  - `customer_preferences`: `preference_id` sourced but never referenced. Evidence: `[marketing_eligible_customers.json:10]`, `[MarketingEligibleProcessor.cs:73-79]` (only `customer_id`, `preference_type`, `opted_in`, and auto-injected `as_of` used).
  - `email_addresses`: `email_id` sourced but never referenced. Evidence: `[marketing_eligible_customers.json:21]`, `[MarketingEligibleProcessor.cs:54-57]` (only `customer_id` and `email_address` used).
- **V2 action:** All six unused columns removed from V2 DataSourcing configs.

### AP6: Row-by-Row Iteration -- RETAINED WITH JUSTIFICATION

- **V1 problem:** `foreach` loops with dictionary lookups to build customer/email maps and aggregate opt-ins.
- **V2 action:** Retained. The External module uses dictionary-based lookups because this is the natural C# pattern for the join/filter/aggregation logic required by the weekend fallback behavior. The iteration is clean and well-structured. Full SQL replacement (Tier 1) is blocked by the weekend fallback's procedural nature.

### AP7: Magic Values -- ELIMINATED

- **V1 problem:** `requiredTypes` defined as an inline `new HashSet<string>` inside the method body at `[MarketingEligibleProcessor.cs:62-64]` without descriptive naming or documentation.
- **V2 action:** Extracted to a `private static readonly` field named `RequiredMarketingChannels` with a documentation comment explaining: (a) the 3-channel requirement, (b) the BRD discrepancy, and (c) the V1 source code evidence.

### Anti-Patterns Not Applicable

| ID | Why Not Applicable |
|----|-------------------|
| AP1 | No dead-end sourcing -- all three sourced tables are used in the processing logic. |
| AP2 | No cross-job duplicated logic within this job's scope. |
| AP5 | No asymmetric NULL handling -- all string fields use consistent `?.ToString() ?? ""` coalescing. |
| AP8 | No SQL / no CTEs -- business logic is in External module. |
| AP9 | Job name accurately describes output (marketing eligible customers). |
| AP10 | DataSourcing uses framework effective date injection. No manual date filtering in SQL. |

---

## 8. Proofmark Config

```yaml
comparison_target: "marketing_eligible_customers"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

### Rationale

**Zero exclusions. Zero fuzzy columns.** Starting strict per best practices.

- **Row order:** V1 iterates over `Dictionary<int, HashSet<string>>`, which has no guaranteed iteration order in .NET. V2 uses the same data structure and population pattern. Both V1 and V2 process identically-ordered DataSourcing results (same `ORDER BY as_of` from the framework), so dictionary insertion order will match. If row order diverges during comparison, investigate before adding overrides.
- **email_address:** When a customer has multiple email addresses on the same `as_of` date, the "last-wins" dictionary overwrite depends on database row ordering. Both V1 and V2 use DataSourcing with identical query parameters against the same database, so row ordering should be consistent. Start strict; add EXCLUDED only if comparison fails with evidence.
- **as_of:** Deterministic -- derived from `__maxEffectiveDate` and day-of-week calculation. No override needed.
- **header_rows: 1:** V1 config has `includeHeader: true` `[marketing_eligible_customers.json:35]`.
- **trailer_rows: 0:** V1 config has no `trailerFormat` field.

---

## 9. Open Questions

1. **Weekday multi-date accumulation:** On weekdays, opt-ins accumulate across all dates in the effective date range without date filtering. A customer who opts in to `MARKETING_EMAIL` on day 1, `MARKETING_SMS` on day 2, and `PUSH_NOTIFICATIONS` on day 3 would qualify even though they were never opted in to all three on the same day. Is this intended behavior or a V1 bug? The HashSet accumulation at `[MarketingEligibleProcessor.cs:82-86]` never removes entries. **Confidence: MEDIUM.** V2 replicates this behavior for output equivalence regardless.

2. **BRD accuracy -- 3-channel vs 2-channel requirement:** The BRD states only two channels are required (MARKETING_EMAIL and MARKETING_SMS) and claims PUSH_NOTIFICATIONS is ignored. The V1 source code requires all three. The BRD must be corrected. See Appendix B for full discrepancy details. **Confidence: HIGH** that the BRD is wrong -- the code is unambiguous.

3. **No phone number requirement:** Unlike `CustomerContactability`, this job does NOT require the customer to have a phone number on file. Only customer existence in the `customers` table is checked `[MarketingEligibleProcessor.cs:92]`. This may be intentional (different jobs, different requirements) or an oversight. **Confidence: LOW** -- no evidence either way. V2 replicates V1 behavior (no phone check).

---

## Appendix A: V2 Job Config JSON

```json
{
  "jobName": "MarketingEligibleCustomersV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "customer_preferences",
      "schema": "datalake",
      "table": "customer_preferences",
      "columns": ["customer_id", "preference_type", "opted_in"]
    },
    {
      "type": "DataSourcing",
      "resultName": "customers",
      "schema": "datalake",
      "table": "customers",
      "columns": ["id", "first_name", "last_name"]
    },
    {
      "type": "DataSourcing",
      "resultName": "email_addresses",
      "schema": "datalake",
      "table": "email_addresses",
      "columns": ["customer_id", "email_address"]
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.MarketingEligibleCustomersV2Processor"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/marketing_eligible_customers.csv",
      "includeHeader": true,
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

### Changes from V1 Config

| Change | V1 | V2 | Rationale |
|--------|-----|-----|-----------|
| jobName | `MarketingEligibleCustomers` | `MarketingEligibleCustomersV2` | V2 naming convention |
| customer_preferences columns | `preference_id, customer_id, preference_type, opted_in` | `customer_id, preference_type, opted_in` | AP4: `preference_id` never used |
| customers columns | `id, prefix, first_name, last_name, suffix, birthdate` | `id, first_name, last_name` | AP4: `prefix`, `suffix`, `birthdate` never used |
| email_addresses columns | `email_id, customer_id, email_address, email_type` | `customer_id, email_address` | AP4: `email_id`, `email_type` never used |
| External typeName | `MarketingEligibleProcessor` | `MarketingEligibleCustomersV2Processor` | V2 naming convention |
| outputFile | `Output/curated/...` | `Output/double_secret_curated/...` | V2 output path |

### Preserved from V1 (output equivalence)

| Property | Value | V1 Evidence |
|----------|-------|-------------|
| Writer type | CsvFileWriter | `[marketing_eligible_customers.json:32]` |
| includeHeader | true | `[marketing_eligible_customers.json:35]` |
| writeMode | Overwrite | `[marketing_eligible_customers.json:36]` (W9) |
| lineEnding | LF | `[marketing_eligible_customers.json:37]` |
| trailerFormat | (absent) | No trailerFormat in V1 config |
| firstEffectiveDate | 2024-10-01 | `[marketing_eligible_customers.json:3]` |

---

## Appendix B: BRD Corrections Required

The following BRD claims are contradicted by V1 source code and must be corrected during the resolution phase:

| BRD Section | BRD Claim | Actual V1 Behavior | V1 Evidence |
|-------------|-----------|---------------------|-------------|
| Overview | "BOTH required marketing channels (MARKETING_EMAIL and MARKETING_SMS)" | THREE required channels: MARKETING_EMAIL, MARKETING_SMS, PUSH_NOTIFICATIONS | `[MarketingEligibleProcessor.cs:62-64]` |
| BR-1 | "opted in to BOTH required channels: MARKETING_EMAIL and MARKETING_SMS" | Opted in to ALL THREE required channels | `[MarketingEligibleProcessor.cs:62-64, 92]` |
| Edge Case 5 | "PUSH_NOTIFICATIONS ... are ignored (not in the requiredTypes set)" | PUSH_NOTIFICATIONS IS in the requiredTypes set | `[MarketingEligibleProcessor.cs:64]` |
| Edge Case 1 | "Must opt in to both: MARKETING_EMAIL AND MARKETING_SMS" | Must opt in to all three | `[MarketingEligibleProcessor.cs:62-64, 92]` |

---

## Appendix C: External Module Design

### File: `ExternalModules/MarketingEligibleCustomersV2Processor.cs`
### Class: `ExternalModules.MarketingEligibleCustomersV2Processor`
### Interface: `IExternalStep`

### Responsibilities

The External module handles ONLY:
1. Weekend fallback date computation (requires `__maxEffectiveDate` from shared state)
2. Conditional preference date filtering (depends on weekend computation result)
3. Three-channel eligibility check (MARKETING_EMAIL + MARKETING_SMS + PUSH_NOTIFICATIONS)
4. Join logic across 3 DataFrames (customers, preferences, emails)
5. Output DataFrame assembly

### Design Notes

- **Clean code, not a V1 copy.** The V2 processor implements the same algorithm with:
  - W2 weekend fallback documented with clear guard clause
  - W9 Overwrite mode implications documented
  - No unused column extraction (AP4 eliminated)
  - Named constant `RequiredMarketingChannels` with documentation (AP7 eliminated)
  - Business rule IDs and wrinkle codes in comments throughout
  - BRD discrepancy (3 channels vs 2) documented in code

- **Dictionary overwrite for email (BR-8):** V1 uses dictionary assignment that overwrites duplicates. V2 replicates exactly. Iteration order is determined by DataSourcing's `ORDER BY as_of`, so the "last" entry per customer_id is from the most recent date. Within the same date, order matches V1 because both use the same DataSourcing module.

- **Empty guard (BR-9):** If `customer_preferences` or `customers` DataFrames are null or empty, return an empty DataFrame with schema `[customer_id, first_name, last_name, email_address, as_of]`.

- **No phone number requirement:** Unlike CustomerContactability, this job does NOT check for phone numbers. Only customer existence in the customers table is validated.

### Pseudocode

```csharp
using Lib.DataFrames;
using Lib.Modules;

namespace ExternalModules;

public class MarketingEligibleCustomersV2Processor : IExternalStep
{
    // V1 source [MarketingEligibleProcessor.cs:62-64] requires all 3 channels.
    // BRD incorrectly states only 2 (MARKETING_EMAIL, MARKETING_SMS).
    // V2 follows V1 code for output equivalence.
    private static readonly HashSet<string> RequiredMarketingChannels = new()
    {
        "MARKETING_EMAIL",
        "MARKETING_SMS",
        "PUSH_NOTIFICATIONS"
    };

    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "first_name", "last_name", "email_address", "as_of"
        };

        var maxDate = sharedState.ContainsKey("__maxEffectiveDate")
            ? (DateOnly)sharedState["__maxEffectiveDate"]
            : DateOnly.FromDateTime(DateTime.Today);

        // W2: Weekend fallback -- Saturday/Sunday use Friday's preference data
        DateOnly targetDate = maxDate;
        if (maxDate.DayOfWeek == DayOfWeek.Saturday)
            targetDate = maxDate.AddDays(-1); // Friday
        else if (maxDate.DayOfWeek == DayOfWeek.Sunday)
            targetDate = maxDate.AddDays(-2); // Friday

        var prefs = sharedState.GetValueOrDefault("customer_preferences") as DataFrame;
        var customers = sharedState.GetValueOrDefault("customers") as DataFrame;
        var emails = sharedState.GetValueOrDefault("email_addresses") as DataFrame;

        // BR-9: Empty guard -- null or empty prefs/customers yields empty output
        if (prefs == null || prefs.Count == 0 || customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Build customer lookup (id -> (firstName, lastName))
        // AP4 eliminated: no prefix, suffix, or birthdate extracted
        var customerLookup = new Dictionary<int, (string firstName, string lastName)>();
        foreach (var row in customers.Rows)
        {
            var id = Convert.ToInt32(row["id"]);
            customerLookup[id] = (
                row["first_name"]?.ToString() ?? "",
                row["last_name"]?.ToString() ?? ""
            );
        }

        // Build email lookup -- BR-8: last-wins dictionary overwrite
        // BR-5: If customer has no email, defaults to "" (empty string)
        var emailLookup = new Dictionary<int, string>();
        if (emails != null)
        {
            foreach (var row in emails.Rows)
            {
                var custId = Convert.ToInt32(row["customer_id"]);
                emailLookup[custId] = row["email_address"]?.ToString() ?? "";
            }
        }

        // Build customer opt-in map: customer_id -> set of opted-in preference types
        // BR-3: On weekends (targetDate != maxDate), only process preference rows
        //        matching the fallback Friday date.
        //        On weekdays (targetDate == maxDate), process ALL rows in the range.
        var customerOptIns = new Dictionary<int, HashSet<string>>();
        foreach (var row in prefs.Rows)
        {
            if (targetDate != maxDate)
            {
                var rowDate = (DateOnly)row["as_of"];
                if (rowDate != targetDate) continue;
            }

            var custId = Convert.ToInt32(row["customer_id"]);
            var prefType = row["preference_type"]?.ToString() ?? "";
            var optedIn = Convert.ToBoolean(row["opted_in"]);

            if (optedIn && RequiredMarketingChannels.Contains(prefType))
            {
                if (!customerOptIns.ContainsKey(custId))
                    customerOptIns[custId] = new HashSet<string>();
                customerOptIns[custId].Add(prefType);
            }
        }

        // BR-1 (CORRECTED): Customer must be opted in to ALL 3 required channels
        // BR-4: Customer must exist in the customers table
        var outputRows = new List<Row>();
        foreach (var kvp in customerOptIns)
        {
            if (kvp.Value.Count == RequiredMarketingChannels.Count
                && customerLookup.ContainsKey(kvp.Key))
            {
                var (firstName, lastName) = customerLookup[kvp.Key];
                // BR-5: Empty string if customer has no email on file
                var email = emailLookup.GetValueOrDefault(kvp.Key, "");

                // BR-6: as_of set to targetDate (may be Friday fallback on weekends)
                outputRows.Add(new Row(new Dictionary<string, object?>
                {
                    ["customer_id"] = kvp.Key,
                    ["first_name"] = firstName,
                    ["last_name"] = lastName,
                    ["email_address"] = email,
                    ["as_of"] = targetDate
                }));
            }
        }

        // W9: Output uses Overwrite mode -- prior days' data is lost on each run
        sharedState["output"] = new DataFrame(outputRows, outputColumns);
        return sharedState;
    }
}
```

### Differences from V1 External Module

| Aspect | V1 | V2 | Reason |
|--------|-----|-----|--------|
| customer_preferences columns | Sources `preference_id` (never used) | Does not source `preference_id` | AP4 eliminated |
| customers columns | Sources `prefix`, `suffix`, `birthdate` (never used) | Does not source or reference these | AP4 eliminated |
| email_addresses columns | Sources `email_id`, `email_type` (never used) | Does not source these | AP4 eliminated |
| requiredTypes definition | Inline `new HashSet<string>` in method body | Static readonly field `RequiredMarketingChannels` with docs | AP7 eliminated |
| Code comments | Sparse; AP codes in comments | Business rule IDs (BR-x) and wrinkle codes (W-x) throughout | Clean documentation |
| Algorithm | 3-channel check, weekend fallback, last-wins email, empty guard | Identical logic | Output equivalence required |
| Null handling | `?.ToString() ?? ""` | `?.ToString() ?? ""` | Same behavior preserved |

---

## Appendix D: Edge Cases

| # | Edge Case | Expected Behavior | Covered By |
|---|-----------|-------------------|------------|
| 1 | Saturday execution | Uses Friday's preferences; as_of = Friday | BR-2, W2 |
| 2 | Sunday execution | Uses Friday's preferences (maxDate - 2); as_of = Friday | BR-2, W2 |
| 3 | Customer opted in to 1 or 2 of 3 channels | NOT included; must opt in to all 3 | BR-1 (corrected) |
| 4 | Customer opted in to all 3 but no email on file | Included; email_address = "" | BR-5 |
| 5 | Weekday with multiple as_of dates | All rows processed; opt-ins accumulate across dates | BR-3, Open Question 1 |
| 6 | Null/empty prefs or customers | Empty DataFrame with correct schema | BR-9 |
| 7 | Customer with multiple emails | Last-wins dictionary overwrite; non-deterministic within same as_of | BR-8 |
| 8 | Preference types outside required set | Ignored (E_STATEMENTS, PAPER_STATEMENTS not in RequiredMarketingChannels) | Algorithm Step 7 |

---

## Appendix E: Traceability Matrix

| BRD Requirement | FSD Section | Implementation Detail |
|----------------|-------------|----------------------|
| BR-1 (CORRECTED: 3 channels) | Section 4, Algo Steps 8-9 | RequiredMarketingChannels = 3 entries; count check |
| BR-2: Weekend fallback | Section 6 (W2), Algo Step 2 | Saturday: maxDate - 1; Sunday: maxDate - 2 |
| BR-3: Conditional date filtering | Algo Step 7 | if targetDate != maxDate then filter by as_of == targetDate |
| BR-4: Customer must exist | Algo Step 9 | customerLookup.ContainsKey check |
| BR-5: Missing email = "" | Algo Step 9 | emailLookup.GetValueOrDefault(key, "") |
| BR-6: as_of = targetDate | Algo Step 9 | Output row as_of set to targetDate |
| BR-7: Unused columns | Sections 3, 7 (AP4) | All 6 unused columns removed from V2 DataSourcing |
| BR-8: Last-wins email | Algo Step 6 | Dictionary assignment overwrites duplicates |
| BR-9: Empty guard | Algo Step 4 | Return empty DataFrame if prefs/customers null/empty |
| W2: Weekend fallback | Section 6 | Reproduced with clean guard clause |
| W9: Overwrite write mode | Section 5, Section 6 | Preserved; documented as V1 behavior |
| AP3: Unnecessary External | Section 7 | Partially addressed -- retained at Tier 2 for weekend logic |
| AP4: Unused columns | Section 3, Section 7 | Eliminated -- 6 columns removed across 3 tables |
| AP6: Row-by-row iteration | Section 7 | Retained with justification |
| AP7: Magic values | Section 7 | Eliminated -- named constant with documentation |
