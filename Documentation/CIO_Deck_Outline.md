# Project ATC — CIO Presentation Outline

**Audience:** Business Line CIO + Enterprise CIO (GSIB)
**Context:** Update on green-lit skunk works initiative. CDO already bought in.
**The Ask:** Tooling (Claude Code, Opus/Sonnet), prod data/code access, dedicated team focus, ongoing AI spend.
**Tone:** Confident, evidence-backed, governance-forward. Revolutionary in substance, conservative in language.

---

## 1. The Problem (Keep Brief — They Already Know This)

- Tens of thousands of ETL jobs powering the data platform
- Many are poorly documented, written by contractors, full of anti-patterns
- Maintaining them is expensive, risky, and getting worse over time
- Traditional refactoring at this scale is a multi-year, army-of-consultants problem
- *[Insert your compelling business case numbers here — they've seen these]*

**Transition:** "We asked: what if AI could do this? Not hypothetically. We tested it."

---

## 2. What We Proved (The POC Results — This Is Your Sausage)

### First Test: Blind Reverse Engineering
- Gave AI agents **only** data inputs and outputs — no source code at all
- They successfully reverse-engineered the business requirements for 2 production jobs
- Proved: AI can infer *what* code does from *what* data goes in and comes out

### Second Test: Full Pipeline (MockETL Framework)
- Built a realistic mock of our platform: 32 ETL jobs, synthetic financial data, real anti-patterns
- AI agents were given the code but **zero documentation**
- Results:
  - 32 out of 32 jobs: requirements inferred, code rewritten, output verified
  - 100% data accuracy across all jobs, all test dates
  - 56% reduction in code complexity
  - 11 jobs simplified from code to pure SQL
  - 97 of 115 anti-patterns eliminated; 18 intentionally preserved for backward compatibility

**Key message:** This isn't a demo. This is 32 successful executions of an end-to-end pipeline — from "here's some bad code" to "here's better code, and here's the proof."

---

## 3. Why It Works: The Governance Model

**Key message:** The results are impressive, but the *process* is what makes this viable at a GSIB. We didn't just turn AI loose. We built a governance framework around it.

### Adversarial Validation
- The system doesn't just build — it **challenges itself**
- A separate review process (the Skeptic) assumes the work is wrong and tries to break it
- A neutral evaluator adjudicates between the builder's claims and the skeptic's concerns
- Same principle as red team / blue team. Attack your own work before anyone else can.

### Independent Validation (Segregation of Duties)
- Every output goes through independent validation — a separate review process that checks for accuracy, hallucination, and traceability back to source
- Same principle as maker-checker. Same principle as segregation of duties in audit.
- The builder never approves its own work

### Evidence-Based Attestation
- Every requirement is traced back to the source code and data that proves it
- Every decision carries a confidence level and a citation
- The final deliverable isn't "trust me" — it's "here's the evidence, verify it yourself"

### The Comparison Tool: Traditional QA Validates AI Output
- A separate, purpose-built comparison tool validates that new code produces correct output
- **This tool is developed and tested through traditional SDLC processes** — unit tests, code reviews, standard QA
- The AI never grades its own homework
- The thing doing the judging is software your existing QA process already trusts
- *[Analogy: It's like having a certified scale verify weights. We don't care who built the widget — we care that the scale is calibrated.]*

**Transition:** "So: adversarial review, independent validation, evidence-based attestation, and a traditionally validated comparison tool. An auditor reviewing this process should find it *more* rigorous than what we do today — because most of our current jobs have none of this."

---

## 4. How We Get This Done (Mobilization)

### The Team
- Intentionally small: 3 people. Me and our two strongest engineers.
- Small by design — avoids committees, avoids design-by-consensus, moves fast
- The rest of the platform team continues BAU work
- New feature requests get prioritized against the strategic value of what we're delivering

### The Tooling
- Claude Code (AI development environment) + Claude Opus and Sonnet (latest models)
- These are the specific tools the POC validated against. This isn't a generic "AI" request — we've tested with these exact capabilities and know they work.

### The Approach
- We need access to production code and data — the real thing, not more mocks
- 120-day prototype
- Start with 1 job. Then 10. Then 50. Then 100.
- Each stage: full end-to-end pipeline including the attestation package
- **Defined stage gate:** to move from prototype to full deployment, we demonstrate the complete process on real jobs with real data, validated by the traditionally-tested comparison tool, producing attestation packages that pass review
- After prototype: this recontextualizes how we think about platform engineering entirely

### What the 120 Days Delivers
1. **The reverse-engineering pipeline** — battle-tested on real production jobs
2. **The comparison/validation tool** — traditionally QA'd, capable of comparing outputs across our target formats
3. **The attestation package template** — what the final deliverable looks like for every job
4. **The governance framework** — documented, auditable, ready to scale
5. **Proof on real jobs** — not mock data, not synthetic scenarios, real production ETL

---

## 5. What We're Asking For

1. **Tooling:** Claude Code licenses, Claude Opus and Sonnet API access
2. **Access:** Production ETL code and data for the prototype
3. **Focus:** Dedicated time for the 3-person team for 120 days
4. **AI spend:** Ongoing API costs — this is the fuel *(have a rough monthly/quarterly number ready if asked)*

**Frame:** "This is a small, contained investment. Three people, 120 days, known tooling costs. If it doesn't work, we've lost a quarter. If it works, we've changed how this company manages its entire data platform."

---

## 6. What Happens After (The Vision — Keep It Short)

- Prototype succeeds → scale to full platform
- Every legacy job gets: reverse-engineered requirements, improved code, attestation package
- We stop *maintaining* legacy code and start *certifying autonomous rewrites*
- The platform gets cleaner, faster, and more documented with every job we process
- Long-term: this becomes the standard process for how we handle platform evolution

---

## Appendix: Anticipated Questions

**"What about regulatory risk?"**
The governance model is built for exactly this. Adversarial review, independent validation, traditional QA on the comparison tooling, full evidence trail. Every attestation package is designed to satisfy an auditor who's never heard of an LLM.

**"What if the AI gets it wrong?"**
That's what the comparison tool catches — and it's validated through traditional SDLC, not AI. Plus the adversarial review process is designed to find errors before they reach comparison. Multiple independent layers, same as defense-in-depth.

**"Why these three people? What happens to their current work?"**
We're pulling the strongest engineers because this requires deep platform knowledge. The remaining team handles BAU. New feature requests are prioritized against the strategic value of this initiative.

**"Why not use [other AI tool]?"**
The POC was validated against these specific models and tools. We know they work for this use case. Switching introduces unknown risk for no proven benefit.

**"What's the ongoing cost?"**
*[Have your numbers ready. Token costs, monthly API spend projections, and comparison to consultant costs for manual refactoring.]*

**"How do we know the prototype results will scale?"**
That's the whole point of 1 → 10 → 50 → 100. Each step tests at greater scale. The stage gate between prototype and full deployment is explicitly defined.

**"Can the auditors actually audit this?"**
Yes. The attestation package is designed to be readable by someone with zero AI knowledge. It says: here's what the old code did, here's what the new code does, here's the evidence they match (or where the new code is better and why), here's the independent validation. It's a paper trail, not a black box.
