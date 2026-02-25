# Project ATC / MockEtlFramework -- Comprehensive Summary

**Purpose:** This document gives a new Claude session complete context for the project without reading every file. It covers what was built, what was tested, what was learned, and where to find details.

---

## 1. What the Project IS

This is a **proof-of-concept (POC)** for **Project ATC** -- an initiative to use autonomous AI agent swarms to reverse-engineer, document, and rebuild tens of thousands of ETL jobs on a production big data platform.

The owner (Dan) has a real PySpark/Python ETL platform with poorly documented, poorly understood, and inefficiently coded jobs. Rather than test AI agents against the real platform first, he built a **mock ETL framework in C# / .NET 8** that replicates the production platform's execution model. He then:

1. **Phase 1:** Built the mock framework and populated a PostgreSQL data lake with synthetic financial data (customers, accounts, transactions, branches, loans, credit scores, etc.)
2. **Phase 2:** Created 31 intentionally bad ETL jobs that produce correct output through terrible code
3. **Phase 3:** Turned AI agents loose on those jobs with zero documentation, asking them to reverse-engineer requirements, build improved replacements, and prove output equivalence

The goal is to prove that AI agents can autonomously infer business rules from code and data alone, then build better code -- all without human intervention.

**Reference:** `/workspace/MockEtlFramework/Documentation/POC.md`

---

## 2. Framework Architecture

The mock framework mirrors a production PySpark ETL system. Key concepts:

| Production Concept | C# Equivalent |
|---|---|
| PySpark DataFrame | `Lib.DataFrames.DataFrame` |
| Shared state (dict of DataFrames) | `Dictionary<string, object>` threaded through module chain |
| ETL modules | Classes implementing `IModule` |
| Job configuration | JSON files with ordered list of module configs |
| Spark SQL | In-memory SQLite via `Microsoft.Data.Sqlite` |
| Framework executor | `JobRunner` reads config, runs modules in sequence |

### Module Types

| Module | What It Does |
|---|---|
| **DataSourcing** | Reads from PostgreSQL datalake schema; injects effective dates via shared state keys `__minEffectiveDate` / `__maxEffectiveDate` |
| **Transformation** | Registers all DataFrames as SQLite tables, runs user-supplied SQL, stores result back in shared state |
| **DataFrameWriter** | Writes a DataFrame to a PostgreSQL curated schema; supports `Overwrite` (truncate+insert) and `Append` modes; Run 2 added configurable `targetSchema` parameter |
| **External** | Loads a .NET assembly via reflection, instantiates a class implementing `IExternalStep`, delegates execution -- allows arbitrary C# logic |

### Execution Model

- `JobExecutorService` reads job registrations from `control.jobs`, builds a topological execution plan based on dependencies, and auto-advances each job from its last succeeded date forward to today
- Effective dates are injected into shared state automatically -- job configs never hardcode dates
- Dependency types: `SameDay` (upstream must succeed same run_date) and `Latest` (upstream must have ever succeeded)
- Data lake uses a full-load snapshot pattern: each day's load is a complete picture with an `as_of` column

### Database Layout

- **PostgreSQL** at localhost, user `dansdev`, database `atc`
- Password: hex-encoded UTF-16 LE in env var `PGPASS`; decode with `echo "$PGPASS" | xxd -r -p | iconv -f UTF-16LE -t UTF-8`
- **Schemas:** `datalake` (source, NEVER modify), `curated` (Phase 2 output), `double_secret_curated` (Phase 3 agent output), `control` (job metadata and run history)

### Build & Run

```bash
dotnet build                                    # compile
dotnet test                                     # run xUnit tests
dotnet run --project JobExecutor                # auto-advance all active jobs
dotnet run --project JobExecutor -- JobName     # auto-advance one job
dotnet run --project JobExecutor -- 2024-10-15  # backfill specific date, all jobs
```

**Reference:** `/workspace/MockEtlFramework/Documentation/Strategy.md`

---

## 3. Phase 2: What Was Planted and Why

Phase 2 created **31 ETL jobs** (originally described as ~30; actual count after implementation is 31) that produce **correct curated output** through **intentionally terrible code**. Dan's instruction to Claude was to act like "a junior developer listening to Paul's Boutique and ripping bong hits while vibe coding with Grok."

### The 10 Anti-Pattern Categories

| Code | Anti-Pattern | Description | Jobs Affected |
|---|---|---|---|
| [1] | Redundant data sourcing | Sources tables never used by the job | 22 |
| [2] | Duplicated transformation logic | Re-computes what upstream jobs already produced | 4 |
| [3] | Unnecessary External module | Uses C# when SQL would suffice | 15-18 |
| [4] | Unused columns sourced | Requests columns never referenced | 23 |
| [5] | Dead-end DataFrames | Loaded into shared state, never consumed | ~20 |
| [6] | Row-by-row iteration | C# foreach loops instead of set-based SQL | 18 |
| [7] | Hardcoded magic values | Thresholds, dates, weights as literals | 10 |
| [8] | Overly complex SQL | Unnecessary CTEs, subqueries, window functions | 10 |
| [9] | Re-derives curated output | Recomputes from raw data instead of reading curated | ~10 |
| [10] | Missing dependency declaration | Should depend on upstream jobs but doesn't | ~8 |

Every job has at least one anti-pattern; most have 3-5. The output is correct -- a business user looking only at results would accept it. The code is a disaster.

### Weekend Handling

Some datalake tables (`customers`, `accounts`, `credit_scores`, `loan_accounts`) return 0 rows on weekends. External modules have a weekend guard pattern that returns an empty DataFrame. SQL-only jobs source only daily tables (transactions, branches, etc.) that always have data.

### Verification

- All 31 jobs ran for Oct 1-31, 2024: 930 Succeeded, 0 Failed
- `dotnet build`: 0 warnings, 0 errors
- `dotnet test`: 33 passed, 0 failed

**Reference:** `/workspace/MockEtlFramework/Documentation/Phase2Plan.md`

---

## 4. Phase 3 Run 1: 100% Equivalence, 0% Anti-Pattern Elimination

### Setup

A clean clone of the repo was created at a separate directory. All documentation except `Strategy.md` was deleted. A `CLAUDE.md` governed agent behavior with strict rules:
- Never read `Documentation/` except `Strategy.md`
- Never use git history, web search, or prior session memory
- All requirements must be derived from code, configs, database, and SQL scripts
- Every requirement needs evidence citations and confidence levels (HIGH/MEDIUM/LOW)
- Reviewer agents validate every deliverable

### What Happened

| Phase | Duration | Result |
|---|---|---|
| A: Analysis (Agent Teams, 4 analysts + 1 reviewer) | ~25 min | 32 BRDs produced, all passed first review attempt, zero revision cycles |
| B: Design & Implementation (standard subagents) | ~10 min | 32 FSDs, 32 test plans, 32 V2 implementations |
| C: Setup | ~5 min | Schema created, jobs registered, build clean |
| D: Comparison Loop | ~14 min | 3 iterations; all discrepancies were infrastructure (assembly naming, TRUNCATE permission, NUMERIC precision) |
| E: Governance | ~11 min | Executive summary + 32 per-job reports |
| **TOTAL** | **~65 min** | **100% data equivalence across 32 tables x 31 dates** |

### The Problem

**Every single anti-pattern was reproduced in the V2 code.** The agents:
- Correctly identified dead-end sourcing, unused columns, unnecessary CTEs, etc. in their BRDs
- Then faithfully reproduced all of it in V2 implementations
- Actually **worsened** anti-pattern [3]: SQL-only jobs gained unnecessary External writer modules (`DscWriterUtil.cs`) because the `DataFrameWriter` lacked a `targetSchema` parameter and agents couldn't modify `Lib/`

The gap between **observation** and **action** was total. Agents saw the problems, documented them, and reproduced them anyway.

**Root cause:** The mandate was equivalence, not improvement. Agents were never told to fix anything -- only to prove they understood the existing system.

**Reference:**
- `/workspace/MockEtlFramework/Documentation/Phase3AntiPatternAnalysis.md`
- `/workspace/MockEtlFramework/Documentation/Phase3Observations.md` (Run 1 section, Checks #1-#12)

---

## 5. Phase 3 Run 2: Success

### Changes from Run 1

1. **Framework fix:** `DataFrameWriter` now accepts a `targetSchema` parameter (default `"curated"`), eliminating the need for custom writer utilities
2. **Anti-pattern guide:** The CLAUDE.md included an explicit 10-category guide describing what bad code looks like and how to fix it
3. **Improvement mandate:** The mission explicitly required V2 code to be BETTER, not just equivalent. Reproducing anti-patterns = failure.

### What Happened

| Phase | Duration | Result |
|---|---|---|
| A: Analysis (Agent Teams) | ~34 min | 31 BRDs, 2 revision cycles (reviewer caught real issues) |
| B: Design & Implementation | ~2h 45m | 31 FSDs, 31 test plans, 31 V2 configs, 20 External modules (down from Run 1's 32) |
| C: Setup | ~25 min | Schema, registration, build |
| D: Comparison Loop | ~17 min | 5 iterations (DDL types, DateTime format, precision/rounding), then clean run |
| E: Governance | ~12 min | Executive summary + 31 per-job reports |
| **TOTAL** | **~4h 19min** | **100% data equivalence, 100% anti-pattern coverage** |

### Key Metrics

| Metric | Before (Original) | After (V2) |
|---|---|---|
| External modules with `foreach` loops | 22 | 6 |
| Total lines of C# business logic | 2,092 | 926 (-56%) |
| Jobs expressible as pure SQL | 0 | 11 |
| Redundant table loads per cycle | 22 | 0 |
| Unused columns loaded per cycle | 23+ instances | 0 |

### Anti-Pattern Results

- 115 instances identified across 10 categories
- 97 eliminated outright
- 18 documented and intentionally preserved (AP-5 NULL handling for backward compatibility, AP-9 naming for consumer compatibility)
- **0% elimination (Run 1) to 100% addressed (Run 2)**

### Why Run 2 Needed More Iterations Than Run 1

Run 2's V2 code was **genuinely different** (SQL-based instead of C#-based), so it hit different friction at the framework boundary: SQLite DateTime format differences, DDL type inference mismatches, and banker's rounding (C# `Math.Round` vs SQLite `ROUND`). These are consequences of the improved approach, not defects.

**Reference:**
- `/workspace/MockEtlFramework/Documentation/Phase3ExecutiveReport.md`
- `/workspace/MockEtlFramework/Documentation/Phase3Observations.md` (Run 2 section, Checks #1-#18)
- `/workspace/MockEtlFramework/Documentation/Phase3Blueprint.md` (Section 5: Run 2 design)

---

## 6. The Skeptic Report: 48 Concerns

An adversarial review agent was given full access to all project materials and told to find every reason Project ATC will fail. It produced a 48-concern risk register organized around these themes.

### Key Themes

**1. POC-to-Production Gap:**
- Planted anti-patterns are clean taxonomies; real ones are organic (C-01, C-02)
- POC compared within a single PostgreSQL instance; production has 6 output targets: ADLS, Synapse, Oracle, SQL Server, TIBCO MFT, Salesforce (C-03) -- rated CRITICAL
- POC's largest table: 750 rows. Production: PB-scale (C-05)

**2. Scaling Chasm:**
- Full-truncate-and-restart protocol is catastrophically expensive at scale (C-06) -- rated CRITICAL
- Context window overflow for lead agent at 500+ jobs (C-10, C-11)
- Dependency graph discovery for 50K implicit dependencies (C-12)
- Token costs potentially six figures, with no cost model in any document (C-13, C-14, C-31)

**3. Output-Is-King Limitations:**
- Non-deterministic output defeats comparison (C-16)
- Stateful transformations need multi-day context (C-17)
- External side effects (API calls, Salesforce updates) are invisible to I/O analysis (C-18)

**4. Governance Circularity (Most Important Finding):**
- Evidence package produced by the same system being validated (C-25) -- rated CRITICAL
- POC had Dan as independent validator; production has no equivalent (C-26)
- No red team for the evidence packager (C-27)

**5. Autonomous Agent Behavior:**
- Run 1 proved agents work around constraints in unpredictable ways (C-28)
- Test plans were never actually executed as automated tests (C-37)

### Skeptic's Verdict

Would not approve without: (1) a cost model, (2) an independent validation protocol, (3) a comparison strategy for each output target. Predicted the comparison loop failing to converge at scale as the most likely project death.

**Reference:** `/workspace/MockEtlFramework/Documentation/SkepticReport.md`

---

## 7. The Evaluator Report: Balanced Assessment

A neutral evaluator agent reviewed both the skeptic's report and all original materials, rendering verdict on each of the 48 concerns.

### Concern-by-Concern Adjustments

The evaluator adjusted 27 of 48 severity ratings (mostly downward), confirmed 3 concerns as unchanged CRITICAL or HIGH, and added context the skeptic missed. Key adjustments:

| Concern | Skeptic Rating | Evaluator Rating | Reason for Change |
|---|---|---|---|
| C-03 (Homogeneous comparison target) | CRITICAL | **CRITICAL** (unchanged) | Blocking concern; cannot validate equivalence without comparison strategy per target |
| C-06 (Full restart at scale) | CRITICAL | **HIGH** | Progressive scaling means team encounters this at 20 jobs, not 50K; architecture doc already describes targeted-fix model |
| C-11 (Master Orchestrator coherence) | CRITICAL | **MEDIUM** | Architecture doc's Work Queue Manager (Temporal/Airflow, not an LLM) handles high-volume state; Orchestrator handles only strategic decisions |
| C-25 (Governance circularity) | CRITICAL | **HIGH** | Progressive scaling lets governance team validate methodology on small batches first |
| C-01 (Planted vs organic anti-patterns) | HIGH | **MEDIUM** | POC's 10 categories are standard ETL problems, not exotic; iterative approach provides discovery checkpoints |

### Thematic Verdicts

1. **POC-to-Production Gap:** Real but not a chasm. The skeptic evaluates the plan against 50K jobs when the actual next step is 1 job on the real platform.
2. **Governance Circularity:** Genuine structural gap with a straightforward fix (human spot-check protocol). The skeptic's strongest finding.
3. **Cost and Timeline:** Under-examined but bounded by the phased approach. Phase 1 is a 6-week PoC, not full-platform commitment.
4. **Autonomous Agent Unpredictability:** Inherent to the approach, mitigated by progressive scaling and post-run workaround audits.
5. **Security:** Deployment maturity gap, not a design flaw. Standard enterprise practices apply.

### Recommended Action Plan

**Tier 1 (Must Address Before Proceeding):**
1. Design and test comparison strategies for each output target
2. Implement a human spot-check protocol for independent validation
3. Produce a cost model with three scenarios

**Tier 2 (Address During Execution):**
4. Redesign comparison loop for scale (targeted restart instead of full restart)
5. Build Strategy Doc through iterative conversation
6. Classify jobs by output characteristics
7. Conduct post-run workaround audits
8. Implement infrastructure-level security controls

**Tier 3 (Monitor but Don't Block):**
9-15. Anti-pattern guide completeness, context window management, reviewer quality, test plan execution, rollback planning, Agent Teams maturity, timeline tracking

### Overall Verdict

**Proceed with modifications.** The POC demonstrates genuine capability. The progressive scaling approach is sound. The skeptic's governance circularity critique is the most important finding and should be adopted. The skeptic's most overstated claim is framing the project as a failed lab experiment -- the Playbook explicitly describes a learning journey, not a production-ready architecture.

**Reference:** `/workspace/MockEtlFramework/Documentation/EvaluatorReport.md`

---

## 8. Key Technical Details

### Project Structure

```
MockEtlFramework/
├── Lib/                        # Framework library (DO NOT MODIFY)
│   ├── DataFrames/             # DataFrame, Row, GroupedDataFrame
│   ├── Modules/                # IModule, DataSourcing, Transformation,
│   │                           #   DataFrameWriter, External, IExternalStep,
│   │                           #   ModuleFactory
│   ├── Control/                # JobExecutorService, ExecutionPlan,
│   │                           #   ControlDb, JobRegistration, JobDependency
│   ├── ConnectionHelper.cs     # DB connection (decodes PGPASS)
│   ├── JobConf.cs              # JSON config model
│   └── JobRunner.cs            # Runs module chain
├── JobExecutor/                # Console app entry point
│   └── Jobs/                   # Job configuration JSON files
├── ExternalModules/            # User-supplied C# processors
├── Lib.Tests/                  # xUnit tests (DataFrame, Transformation, ModuleFactory)
├── SQL/                        # SQL scripts
├── Documentation/              # All project docs (THIS directory)
└── Phase3/                     # Agent-produced artifacts (in clone directories)
    ├── brd/                    # Business Requirements Documents
    ├── fsd/                    # Functional Specification Documents
    ├── tests/                  # Test Plans
    ├── logs/                   # Comparison log, discussions
    ├── governance/             # Executive summary, per-job reports
    └── sql/                    # double_secret_curated DDL
```

### PostgreSQL Connection Pattern

```bash
export PGPASSWORD=$(echo "$PGPASS" | xxd -r -p | iconv -f UTF-16LE -t UTF-8) && \
  psql -h localhost -U dansdev -d atc -c "SELECT ..."
```

### Job Registration

```sql
INSERT INTO control.jobs (job_name, description, job_conf_path, is_active)
VALUES ('SomeJobV2', 'Description', 'JobExecutor/Jobs/some_job_v2.json', true)
ON CONFLICT (job_name) DO NOTHING;
```

---

## 9. The CLAUDE.md Instruction Set

The `CLAUDE.md` at `/workspace/MockEtlFramework/CLAUDE.md` is the governance document that controlled Phase 3 agent behavior. It defines:

- **Mission:** Reverse-engineer 32 jobs, produce BRDs/FSDs/test plans, build V2 replacements, prove equivalence, produce governance artifacts -- all with zero human intervention
- **Forbidden sources:** Never read Documentation/ (except Strategy.md), never use git history, never use web search, never reference prior sessions
- **Evidence protocol:** Every requirement needs confidence level (HIGH/MEDIUM/LOW) and source citations with file:line references
- **Quality gates (Watcher Protocol):** Separate reviewer subagent validates every deliverable; max 3 revision cycles; checks for hallucination and "impossible knowledge"
- **Agent workflow:** Phase A (Agent Teams for parallel analysis) through Phase E (governance reports)
- **Comparison loop:** Full truncate-and-restart from Oct 1 on ANY discrepancy; EXCEPT-based SQL for exact row-level comparison
- **Guardrails:** Never modify Lib/, never touch datalake data, never modify originals, never skip reviewer, never fabricate evidence

Run 2's CLAUDE.md added: anti-pattern guide (10 categories with fix instructions), SQL-first mandate, improvement requirement, `targetSchema` documentation, and anti-pattern scorecard sections in BRD/FSD/governance templates.

**Reference:** `/workspace/MockEtlFramework/CLAUDE.md`

---

## 10. Hand-Crafted BRDs as Reference Benchmarks

Two BRDs were hand-crafted by Dan (with AI assistance) as quality benchmarks:

### CustomerAddressDeltasBrd.md

A day-over-day **change detection** pipeline on customer address data. Key features:
- Compares current day's address snapshot against previous day's
- Classifies records as NEW (address_id absent from prior day) or UPDATED (field values changed)
- Enriches with customer name (first_name + " " + last_name joined via customer_id)
- NULL-to-empty conversion on end_date
- Produces output file per day, even when zero changes (header + "Expected records: 0" footer)
- 13 numbered business rules, all HIGH confidence with specific evidence
- 7 ambiguities documented with impact assessment
- Full traceability matrix from input fields to output fields

**Reference:** `/workspace/MockEtlFramework/Documentation/CustomerAddressDeltasBrd.md`

### CoveredTransactionsBrd.md

The most complex job in the framework -- a daily **covered transactions** report. Key features:
- Filters transactions to: Checking accounts only, customer must have active US address on effective date
- Joins 6 tables (transactions, accounts, customers, addresses, customers_segments, segments)
- Active address = end_date IS NULL OR end_date >= effective date
- Snapshot fallback: weekday-only tables (accounts, customers) use most recent available snapshot on weekends
- Segment enrichment: first segment_code alphabetically when customer has multiple segments; deduplication required
- 17 numbered business rules with stakeholder-confirmed resolutions
- 11 resolved ambiguities (filtering logic, segment selection, NULL rendering, quoting rules)
- Detailed join chain with all filter conditions
- Known deviation documented: test output shows "USRET" for customer 1001 but confirmed alphabetical rule yields "CANRET"

**Reference:** `/workspace/MockEtlFramework/Documentation/CoveredTransactionsBrd.md`

---

## Quick Reference: Where to Find Things

| What You Need | Where It Is |
|---|---|
| Framework architecture | `/workspace/MockEtlFramework/Documentation/Strategy.md` |
| Project origin and intent | `/workspace/MockEtlFramework/Documentation/POC.md` |
| What was planted (anti-patterns, all 31 jobs) | `/workspace/MockEtlFramework/Documentation/Phase2Plan.md` |
| Phase 3 agent instructions (CLAUDE.md) | `/workspace/MockEtlFramework/CLAUDE.md` |
| Phase 3 launch plan + Run 2 design | `/workspace/MockEtlFramework/Documentation/Phase3Blueprint.md` |
| Run 1 anti-pattern failure analysis | `/workspace/MockEtlFramework/Documentation/Phase3AntiPatternAnalysis.md` |
| Run 2 executive results | `/workspace/MockEtlFramework/Documentation/Phase3ExecutiveReport.md` |
| Real-time monitoring log (both runs) | `/workspace/MockEtlFramework/Documentation/Phase3Observations.md` |
| Hostile technical review (48 concerns) | `/workspace/MockEtlFramework/Documentation/SkepticReport.md` |
| Balanced evaluator assessment | `/workspace/MockEtlFramework/Documentation/EvaluatorReport.md` |
| Skeptic agent launch prompt | `/workspace/MockEtlFramework/Documentation/SkepticBlueprint.md` |
| Evaluator agent launch prompt | `/workspace/MockEtlFramework/Documentation/EvaluatorBlueprint.md` |
| Hand-crafted BRD: address deltas | `/workspace/MockEtlFramework/Documentation/CustomerAddressDeltasBrd.md` |
| Hand-crafted BRD: covered transactions | `/workspace/MockEtlFramework/Documentation/CoveredTransactionsBrd.md` |
