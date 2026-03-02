# CustomerContactabilityV2 -- Functional Specification Document

## 1. Overview & Tier Selection

**Job:** CustomerContactabilityV2
**Config:** `customer_contactability_v2.json`
**Tier:** Tier 2 -- Framework + Minimal External (SCALPEL)

This job identifies customers who are contactable for marketing purposes. It finds customers who have opted in to `MARKETING_EMAIL` with both a valid email address and phone number on file, applies weekend fallback logic (Saturday/Sunday use Friday's preference data), and outputs to Parquet.

### Tier Justification

Tier 1 (pure SQL) was evaluated first but is insufficient for one specific reason:

**Weekend fallback date calculation requires access to `__maxEffectiveDate` from shared state at SQL construction time.** The V1 logic computes a `targetDate` from `maxDate` -- subtracting 1 day for Saturday, 2 days for Sunday -- and then conditionally applies a date filter on `customer_preferences` based on whether `targetDate != maxDate`. This date computation happens BEFORE the SQL executes, and the conditional filtering behavior (filter on weekends, no filter on weekdays) depends on comparing two computed values.

While SQLite has `strftime('%w', date)` for day-of-week extraction, the `__maxEffectiveDate` is injected as a DateOnly into shared state and is available in DataSourcing-fetched DataFrames as the `as_of` column boundary -- but it is NOT directly available as a scalar value inside a Transformation SQL query. The Transformation module registers DataFrames as tables, not individual shared state values.

A minimal External module (Tier 2) handles ONLY the weekend date computation and conditional preference filtering. DataSourcing handles all data retrieval. The writer is the framework's ParquetFileWriter.

---

## 2. V2 Module Chain

```
DataSourcing (customer_preferences)
  -> DataSourcing (customers)
  -> DataSourcing (email_addresses)
  -> DataSourcing (phone_numbers)
  -> External (CustomerContactabilityV2Processor -- weekend logic + join/filter)
  -> ParquetFileWriter
```

### Module Details

| Step | Module | Purpose |
|------|--------|---------|
| 1 | DataSourcing | Fetch `customer_preferences` (preference_id, customer_id, preference_type, opted_in) |
| 2 | DataSourcing | Fetch `customers` (id, first_name, last_name) -- AP4 eliminated: no prefix/suffix |
| 3 | DataSourcing | Fetch `email_addresses` (email_id, customer_id, email_address) |
| 4 | DataSourcing | Fetch `phone_numbers` (phone_id, customer_id, phone_number) |
| 5 | External | Weekend fallback calculation, preference date filtering, join logic, output assembly |
| 6 | ParquetFileWriter | Write output to `Output/double_secret_curated/customer_contactability/` |

---

## 3. Anti-Pattern Analysis

### Anti-Patterns Identified and Eliminated

| ID | Pattern | V1 Problem | V2 Action |
|----|---------|-----------|-----------|
| AP1 | Dead-end sourcing | `segments` table sourced but never used | **ELIMINATED.** Removed from V2 DataSourcing config entirely. |
| AP3 | Unnecessary External module | V1 External does join/filter logic that is partially expressible in SQL | **PARTIALLY ADDRESSED.** External module retained (Tier 2) but ONLY because weekend fallback requires access to `__maxEffectiveDate` shared state key for conditional date logic. The External module is minimal and focused. |
| AP4 | Unused columns | `prefix` and `suffix` sourced from `customers` but never used in output | **ELIMINATED.** V2 DataSourcing for `customers` only requests `id`, `first_name`, `last_name`. |
| AP6 | Row-by-row iteration | V1 uses `foreach` loops with dictionary lookups | **RETAINED with justification.** The External module still uses dictionary-based lookups, but this is the natural C# pattern for the join/filter logic within the External. The iteration is clean and well-structured. The weekend fallback logic prevents full SQL Tier 1 replacement. |

### Output-Affecting Wrinkles Identified and Preserved

| ID | Wrinkle | V1 Behavior | V2 Replication Strategy |
|----|---------|-------------|------------------------|
| W2 | Weekend fallback | Saturday uses Friday's data (maxDate - 1 day); Sunday uses Friday's data (maxDate - 2 days) | Reproduce with a clear guard clause and comments. Implement cleanly: compute `targetDate` from `maxDate.DayOfWeek`, document the fallback. |
| W9 | Wrong writeMode | Overwrite mode means only the last effective date's output persists in multi-day auto-advance runs | Reproduce V1's Overwrite write mode exactly. Document: `// V1 uses Overwrite -- prior days' data is lost on each run.` |

### Wrinkles NOT Applicable

| ID | Why Not Applicable |
|----|-------------------|
| W1 | V1 does NOT return empty on Sundays; it uses Friday's data. Sunday fallback is W2. |
| W3a-c | No summary/boundary rows in this job. |
| W4 | No integer division or percentage calculations. |
| W5 | No rounding operations. |
| W6 | No monetary accumulation with doubles. |
| W7 | No CSV trailer. |
| W8 | No trailer date. |
| W10 | numParts is 1, which is reasonable. |
| W12 | No CSV append with repeated headers. |

---

## 4. Output Schema

| Column | Type | Source | Transformation | BRD Ref |
|--------|------|--------|---------------|---------|
| customer_id | int | customer_preferences iteration | Customer IDs from the marketingOptIn set | BR-1, BR-2 |
| first_name | string | customers.first_name | ToString, null coalesced to "" | BR-9 |
| last_name | string | customers.last_name | ToString, null coalesced to "" | BR-9 |
| email_address | string | email_addresses.email_address | Last-wins dictionary overwrite by customer_id | BR-2, BR-8 |
| phone_number | string | phone_numbers.phone_number | Last-wins dictionary overwrite by customer_id | BR-2, BR-8 |
| as_of | DateOnly | Derived | Set to targetDate (Friday fallback on weekends) | BR-3, BR-5 |

### Non-Deterministic Fields

- **email_address**: When a customer has multiple email addresses within the effective date range, the value depends on database row ordering within DataSourcing (which orders by `as_of`, but within the same `as_of`, order is non-deterministic). The "last-wins" dictionary overwrite means the final row encountered determines the value.
- **phone_number**: Same non-determinism as email_address.
- **Row order**: V1 iterates over a `HashSet<int>`, which has no guaranteed order. V2 will also iterate over a similar structure. However, Proofmark is order-independent so this is not a comparison concern.

---

## 5. SQL Design

**Not applicable.** This is a Tier 2 design; there is no Transformation (SQL) module. The business logic is implemented in the External module because the weekend fallback requires access to `__maxEffectiveDate` from shared state.

The External module implements the following logic in C#:

### Algorithm

```
1. Read __maxEffectiveDate from shared state
2. Compute targetDate:
   - Saturday: maxDate - 1 day (Friday)
   - Sunday: maxDate - 2 days (Friday)
   - Weekday: maxDate (no change)
3. Build customer lookup: Dictionary<int, (string firstName, string lastName)> from customers DataFrame
4. Build email lookup: Dictionary<int, string> from email_addresses DataFrame (last-wins overwrite)
5. Build phone lookup: Dictionary<int, string> from phone_numbers DataFrame (last-wins overwrite)
6. Build marketingOptIn set from customer_preferences:
   - If targetDate != maxDate (weekend): skip rows where as_of != targetDate
   - If targetDate == maxDate (weekday): process ALL rows in the effective date range
   - For each remaining row: if opted_in == true AND preference_type == "MARKETING_EMAIL", add customer_id to set
7. For each customer_id in marketingOptIn:
   - Must exist in customerLookup, emailLookup, AND phoneLookup
   - Build output row with customer_id, first_name, last_name, email_address, phone_number, as_of=targetDate
8. Store output DataFrame in shared state as "output"
```

### Key Business Rules in Algorithm

- **BR-1**: Step 6 filters for `MARKETING_EMAIL` and `opted_in == true`
- **BR-2**: Step 7 requires presence in all three lookups (customer, email, phone)
- **BR-3/BR-4**: Steps 2 and 6 implement weekend fallback with conditional date filtering
- **BR-5**: Step 7 sets `as_of` to `targetDate`
- **BR-8**: Steps 4 and 5 use dictionary overwrite (last-wins)
- **BR-9**: Step 3 coalesces null names to empty string

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "CustomerContactabilityV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "customer_preferences",
      "schema": "datalake",
      "table": "customer_preferences",
      "columns": ["preference_id", "customer_id", "preference_type", "opted_in"]
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
      "columns": ["email_id", "customer_id", "email_address"]
    },
    {
      "type": "DataSourcing",
      "resultName": "phone_numbers",
      "schema": "datalake",
      "table": "phone_numbers",
      "columns": ["phone_id", "customer_id", "phone_number"]
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.CustomerContactabilityV2Processor"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/customer_contactability/",
      "numParts": 1,
      "writeMode": "Overwrite"
    }
  ]
}
```

### Changes from V1 Config

| Change | V1 | V2 | Rationale |
|--------|-----|-----|-----------|
| jobName | `CustomerContactability` | `CustomerContactabilityV2` | V2 naming convention |
| segments DataSourcing | Present | **Removed** | AP1: dead-end sourcing eliminated |
| customers columns | `id, prefix, first_name, last_name, suffix` | `id, first_name, last_name` | AP4: unused columns eliminated |
| External typeName | `CustomerContactabilityProcessor` | `CustomerContactabilityV2Processor` | V2 naming convention |
| outputDirectory | `Output/curated/customer_contactability/` | `Output/double_secret_curated/customer_contactability/` | V2 output path |

### Preserved from V1

| Property | Value | Reason |
|----------|-------|--------|
| Writer type | ParquetFileWriter | Must match V1 |
| numParts | 1 | Must match V1 |
| writeMode | Overwrite | Must match V1 (W9 documented) |
| firstEffectiveDate | 2024-10-01 | Must match V1 |

---

## 7. Writer Configuration

| Property | Value | Notes |
|----------|-------|-------|
| type | ParquetFileWriter | Matches V1 |
| source | output | DataFrame name in shared state |
| outputDirectory | Output/double_secret_curated/customer_contactability/ | V2 output path |
| numParts | 1 | Matches V1 |
| writeMode | Overwrite | Matches V1; W9: prior days' data is lost on each run |

---

## 8. Proofmark Config Design

### Starting Point: Strict

```yaml
comparison_target: "customer_contactability"
reader: parquet
threshold: 100.0
```

### Analysis of Potential Overrides

**Row order**: Proofmark is order-independent (hash-based row identification), so the non-deterministic row order from HashSet iteration is not a concern.

**email_address**: Customers with multiple email addresses on the same `as_of` date will have non-deterministic values due to "last-wins" dictionary overwrite depending on database row ordering. Database query confirmed customers with 2+ email rows per date exist (e.g., customer_id 1763, 2455, 3129). If V1 and V2 both use DataSourcing with the same query, the row ordering should be identical (both use `ORDER BY as_of` from DataSourcing), making the last-wins behavior deterministic BETWEEN V1 and V2 for the same database state. **Start strict; only add FUZZY/EXCLUDED if comparison fails.**

**phone_number**: Same analysis as email_address. Customers with 3+ phone rows per date exist (e.g., customer_id 2455, 2041). Same reasoning -- start strict.

**as_of**: Deterministic (derived from maxEffectiveDate and day-of-week calculation). No override needed.

### Recommended Config

```yaml
comparison_target: "customer_contactability"
reader: parquet
threshold: 100.0
```

Zero exclusions. Zero fuzzy columns. Both V1 and V2 use the same DataSourcing module with identical query parameters, so row ordering from PostgreSQL should be identical, making "last-wins" behavior consistent between V1 and V2. If comparison fails on email_address or phone_number, add EXCLUDED overrides with evidence.

---

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Implementation Detail |
|----------------|-------------|----------------------|
| BR-1: MARKETING_EMAIL opt-in filter | Algo Step 6 | Filter for `opted_in == true && preference_type == "MARKETING_EMAIL"` |
| BR-2: Must have email AND phone | Algo Step 7 | Three continue checks: customerLookup, emailLookup, phoneLookup |
| BR-3: Weekend fallback to Friday | Algo Step 2 | Saturday: maxDate - 1; Sunday: maxDate - 2 |
| BR-4: Date filtering on weekends only | Algo Step 6 | Conditional: if `targetDate != maxDate` then filter by `as_of == targetDate` |
| BR-5: as_of set to targetDate | Algo Step 7 | Output row `as_of` = targetDate |
| BR-6: Unused prefix/suffix | Config Section 6 | AP4 eliminated: columns removed from DataSourcing |
| BR-7: Unused segments table | Config Section 6 | AP1 eliminated: DataSourcing entry removed entirely |
| BR-8: Last-wins dictionary overwrite | Algo Steps 4-5 | Dictionary assignment overwrites duplicates for email/phone |
| BR-9: Null/empty guard for customers/prefs | Algo Step 8 (implicit) | If prefs or customers null/empty, output empty DataFrame |
| W2: Weekend fallback | Anti-Pattern Section 3 | Reproduced with clean guard clause and comments |
| W9: Overwrite write mode | Writer Section 7 | Preserved; documented as V1 behavior |
| AP1: Dead-end sourcing (segments) | Anti-Pattern Section 3 | Eliminated -- segments not sourced |
| AP4: Unused columns (prefix, suffix) | Anti-Pattern Section 3 | Eliminated -- columns not requested |
| AP3: Unnecessary External module | Anti-Pattern Section 3 | Partially addressed -- External retained at Tier 2 for weekend logic |
| AP6: Row-by-row iteration | Anti-Pattern Section 3 | Retained with justification -- necessary within External module |

---

## 10. External Module Design

### File: `ExternalModules/CustomerContactabilityV2Processor.cs`

### Class: `ExternalModules.CustomerContactabilityV2Processor`

### Interface: `IExternalStep`

### Responsibilities

The External module handles ONLY:
1. Weekend fallback date computation (requires `__maxEffectiveDate` from shared state)
2. Conditional preference date filtering (depends on weekend computation)
3. Join logic across 4 DataFrames (customers, preferences, emails, phones)
4. Output DataFrame assembly

### Design Notes

- **Clean code, not a V1 copy.** The V2 processor implements the same algorithm but with:
  - Clear comments explaining W2 weekend fallback behavior
  - Clear comments explaining W9 Overwrite mode implications
  - No dead-end data access (segments not read from shared state)
  - No unused column extraction (prefix/suffix not referenced)
  - Null coalescing on name fields documented as V1 behavior replication

- **Dictionary overwrite for email/phone (BR-8):** V1 uses dictionary assignment that overwrites duplicates. V2 replicates this exactly. The iteration order is determined by DataSourcing's `ORDER BY as_of`, so the "last" entry per customer_id is the one from the most recent date. Within the same date, order matches V1 because both use the same DataSourcing module.

- **Empty guard (BR-9):** If `customer_preferences` or `customers` DataFrames are null or empty, return an empty DataFrame with the correct column schema.

- **targetDate computation (BR-3):** Use `maxDate.DayOfWeek` with a clear switch or if/else, not magic numbers.

### Pseudocode

```csharp
public class CustomerContactabilityV2Processor : IExternalStep
{
    public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
    {
        var outputColumns = new List<string>
        {
            "customer_id", "first_name", "last_name",
            "email_address", "phone_number", "as_of"
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
        var phones = sharedState.GetValueOrDefault("phone_numbers") as DataFrame;

        // BR-9: Empty guard -- null or empty prefs/customers yields empty output
        if (prefs == null || prefs.Count == 0 || customers == null || customers.Count == 0)
        {
            sharedState["output"] = new DataFrame(new List<Row>(), outputColumns);
            return sharedState;
        }

        // Build customer lookup (id -> (firstName, lastName))
        // AP4 eliminated: no prefix/suffix extracted
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
        var emailLookup = new Dictionary<int, string>();
        if (emails != null)
        {
            foreach (var row in emails.Rows)
            {
                var custId = Convert.ToInt32(row["customer_id"]);
                emailLookup[custId] = row["email_address"]?.ToString() ?? "";
            }
        }

        // Build phone lookup -- BR-8: last-wins dictionary overwrite
        var phoneLookup = new Dictionary<int, string>();
        if (phones != null)
        {
            foreach (var row in phones.Rows)
            {
                var custId = Convert.ToInt32(row["customer_id"]);
                phoneLookup[custId] = row["phone_number"]?.ToString() ?? "";
            }
        }

        // BR-1: Find customers with MARKETING_EMAIL opt-in
        // BR-4: On weekends (targetDate != maxDate), only process
        //        preference rows matching the fallback Friday date.
        //        On weekdays, process ALL rows in the effective date range.
        var marketingOptIn = new HashSet<int>();
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

            if (optedIn && prefType == "MARKETING_EMAIL")
                marketingOptIn.Add(custId);
        }

        // BR-2: Customer must have entry in all three lookups
        var outputRows = new List<Row>();
        foreach (var custId in marketingOptIn)
        {
            if (!customerLookup.ContainsKey(custId)) continue;
            if (!emailLookup.ContainsKey(custId)) continue;
            if (!phoneLookup.ContainsKey(custId)) continue;

            var (firstName, lastName) = customerLookup[custId];

            // BR-5: as_of set to targetDate (may be Friday fallback on weekends)
            outputRows.Add(new Row(new Dictionary<string, object?>
            {
                ["customer_id"] = custId,
                ["first_name"] = firstName,
                ["last_name"] = lastName,
                ["email_address"] = emailLookup[custId],
                ["phone_number"] = phoneLookup[custId],
                ["as_of"] = targetDate
            }));
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
| Segments access | Reads segments from shared state (comment only) | Does not reference segments at all | AP1 eliminated |
| Customer columns | References prefix/suffix in comments | Does not reference unused columns | AP4 eliminated |
| Code comments | Sparse; anti-pattern codes in comments | Business rule IDs (BR-x) and wrinkle codes (W-x) documented | Clean documentation |
| Algorithm | Identical logic | Identical logic | Output equivalence required |
| Null handling | `?.ToString() ?? ""` | `?.ToString() ?? ""` | Same behavior preserved |

---

## Appendix: Edge Cases (from BRD)

| # | Edge Case | Expected Behavior | Covered By |
|---|-----------|-------------------|------------|
| 1 | Saturday execution | Uses Friday's preferences; as_of = Friday | BR-3, W2, Algo Step 2 |
| 2 | Sunday execution | Uses Friday's preferences (maxDate - 2); as_of = Friday | BR-3, W2, Algo Step 2 |
| 3 | Customer opted in but missing email or phone | Excluded from output | BR-2, Algo Step 7 |
| 4 | Customer opted in to MARKETING_SMS only | NOT included; only MARKETING_EMAIL qualifies | BR-1, Algo Step 6 |
| 5 | Weekday with multiple as_of dates in range | All preference rows processed (no date filter); customer opted-in on any day is included | BR-4, Algo Step 6 |
| 6 | Customers table has no weekend data | Consistent with weekend fallback; customer lookup uses all available dates | BRD Edge Case 6 |
| 7 | Null/empty prefs or customers | Empty DataFrame output | BR-9, Algo empty guard |
| 8 | Customer with multiple emails | Last-wins (dictionary overwrite); non-deterministic within same as_of date | BR-8, Non-deterministic fields |
| 9 | Customer with multiple phones | Same as #8 | BR-8, Non-deterministic fields |
