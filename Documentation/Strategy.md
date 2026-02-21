# MockEtlFramework — Solution Strategy

## Purpose

This project is a C# proof-of-concept that mirrors the behavior of a production ETL Framework built on PySpark/Python. The production framework is a core platform component of a large big data system. The goal here is to replicate its execution model and module structure in C#, enabling further work that will be described once the core framework is established.

---

## Production ETL Framework — Background

The production ETL Framework operates by reading **job configuration files** (JSON). Each job conf contains a serialized, ordered list of **ETL modules** to execute in series. Modules communicate with one another through a **shared state** — a dictionary of named DataFrames that each module can read from and write to.

### Production Modules

| Module | Responsibility |
|---|---|
| **Data Sourcing** | Reads from the data lake. Users specify tables, columns, date ranges, and additional filters. Returns data as a PySpark DataFrame stored in shared state. |
| **Transformation** | Runs Spark SQL against DataFrames already in shared state, producing a new transformed DataFrame that is added back to shared state. |
| **DataFrame Writer** | Writes a named DataFrame from shared state out to a curation space for downstream consumers. |
| **External** | Executes a custom user-supplied Python class, allowing teams to perform any logic not covered by the standard modules. |

---

## This Project's Approach

Since the owner of this project is more comfortable in C# than Python, the ETL Framework execution model is being reproduced in C#. The core concepts map as follows:

| Production Concept | C# Equivalent |
|---|---|
| PySpark DataFrame | Custom `DataFrame` class (`Lib.DataFrames.DataFrame`) |
| Shared state | `Dictionary<string, object>` passed through module chain |
| ETL module | Classes implementing `IModule` interface |
| Job configuration | JSON file deserialized into an ordered list of module configs |
| Framework executor | A component that reads the job conf and runs modules in sequence |

### Key Design Principles

- **Module chain execution:** Modules run in the order defined by the job conf. Each module receives the current shared state, performs its operation, and returns the updated state.
- **Shared state as the integration contract:** Modules are decoupled from one another. They communicate only through named entries in the shared state dictionary.
- **JSON-driven configuration:** Job behavior is defined externally in JSON, not in code. This keeps the framework generic and reusable across many different jobs.
- **DataFrame as a first-class citizen:** The custom `DataFrame` class provides PySpark-like operations (Select, Filter, WithColumn, GroupBy, Join, etc.) so that transformation logic feels familiar to users of the production system.

---

## Current State

- `Lib.DataFrames.DataFrame` — Core DataFrame implementation with PySpark-inspired API
- `Lib.DataFrames.Row` — Row abstraction used by DataFrame
- `Lib.DataFrames.GroupedDataFrame` — GroupBy aggregation support
- `Lib.Modules.IModule` — Module interface (`Execute(sharedState) → sharedState`)
- `Lib.Modules.DataSourcing` — Data sourcing module backed by PostgreSQL; groups results by effective date into a `Dictionary<DateOnly, DataFrame>` stored in shared state

---

## Planned Additions

Architectural details will be added to this document as the design evolves. At minimum, the following are expected:

- Transformation module
- DataFrame Writer module
- External / custom-logic module
- Job configuration schema (JSON)
- Framework executor / job runner

---

*This document will be updated as architectural decisions are made.*
