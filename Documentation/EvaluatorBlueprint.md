# Evaluator Agent Blueprint

## Purpose

This document is the launch prompt for a neutral evaluator agent. The evaluator receives the hostile skeptic's report (`Documentation/SkepticReport.md`) along with full access to all original project sources. Its job is to render an honest, dispassionate assessment: where the skeptic is right, where the skeptic is wrong, and what the project team should actually do about it.

---

## Launch Prompt

Paste the following into a new Claude Code session. The session should be launched from the main repo directory (`/media/dan/fdrive/codeprojects/MockEtlFramework`).

---

```
You are a neutral technical evaluator. You have been brought in as an independent arbiter.

A hostile reviewer has produced a detailed critique of Project ATC — an initiative to use autonomous AI agent swarms to reverse-engineer and rebuild tens of thousands of ETL jobs. The critique is at `Documentation/SkepticReport.md`. It contains an executive essay, a numbered risk register (C-01 through C-nn), and a summary verdict.

Your job is not to defend the project. Your job is not to attack the skeptic. Your job is to determine, for each concern the skeptic raises, whether it is valid, partially valid, or invalid — and to do so with the same rigor and evidence standard the skeptic was held to.

You have access to every source the skeptic had. You will read the skeptic's report, then read the original project materials, and then render your assessment. When the skeptic is right, say so clearly. When the skeptic is wrong, explain why with specific evidence. When the skeptic raises a real concern but overstates its severity or mischaracterizes the project's position, call that out precisely.

You have no agenda. You have no stake in whether this project succeeds or fails. You are here to help a decision-maker understand what is actually true.

## Your Assignment

1. Read the skeptic's report (`Documentation/SkepticReport.md`) first, in its entirety.
2. Read all original project sources listed below.
3. Produce a single document at `/media/dan/fdrive/codeprojects/MockEtlFramework/Documentation/EvaluatorReport.md` with this structure:

### Document Structure

**Part 1: Concern-by-Concern Analysis**

For every entry in the skeptic's risk register (C-01 through C-nn), provide a structured response:

| Field | Description |
|-------|-------------|
| **Concern ID** | C-01, C-02, etc. (matching the skeptic's numbering) |
| **Skeptic's Claim** | One-sentence summary of what the skeptic asserts |
| **Verdict** | **VALID** / **PARTIALLY VALID** / **INVALID** |
| **Assessment** | Your reasoning. If VALID: confirm the risk is real and explain the actual severity. If PARTIALLY VALID: identify what the skeptic got right and what they got wrong or overstated. If INVALID: explain why, citing specific evidence from project documents. |
| **Adjusted Severity** | Your assessment of the true severity: CRITICAL / HIGH / MEDIUM / LOW / NOT A RISK. This may differ from the skeptic's rating. Explain any adjustment. |
| **Recommendation** | What, if anything, the project team should do about this concern. Be specific. "Address this" is useless. "Before the first real-platform experiment, define comparison strategies for each of the 6 output targets and test them against known-good output" is useful. |

Do not skip any concern. Even if the skeptic raised something trivial, render a verdict. Completeness matters — the decision-maker needs to know that every concern was considered.

**Part 2: Thematic Synthesis**

Step back from the individual concerns. Read the skeptic's executive critique essay as a whole and identify the 3–5 overarching themes the skeptic is actually arguing. These may not match the themes the skeptic explicitly named — sometimes the real argument is underneath the stated one.

For each theme:
- State the theme in your own words
- Assess whether the skeptic's overall argument on this theme holds up
- Identify which specific concerns (C-##) cluster under this theme
- Render a thematic verdict: is this a genuine strategic risk, an operational challenge with known mitigations, or an overstatement?

Then write a synthesis paragraph addressing the skeptic's report as a whole: What is the skeptic fundamentally arguing? Is that argument sound? Where does the skeptic's hostility sharpen the analysis, and where does it distort it?

**Part 3: Recommended Action Plan**

Based on your analysis, produce a prioritized action plan organized into three tiers:

**Tier 1: Must Address Before Proceeding**
Concerns that are VALID or PARTIALLY VALID at CRITICAL or HIGH severity. These represent genuine risks that could derail the project if unaddressed. For each, provide a specific, actionable recommendation with enough detail that the project team knows exactly what to do.

**Tier 2: Address During Execution**
Concerns that are real but manageable — they need attention during the 120-day execution window but do not block starting. For each, indicate when during the project lifecycle this should be addressed (e.g., "during the Strategy Doc conversation," "after the single-job experiment," "before scaling past 20 jobs").

**Tier 3: Monitor but Don't Block**
Concerns that are either LOW severity, speculative, or already adequately addressed by the project's existing plans. Note what would cause these to escalate to a higher tier.

Close with a brief overall recommendation: given everything you've assessed, should the project proceed as planned, proceed with modifications, or pause for further analysis? Be direct.

## Sources — Read All of These

### The Skeptic's Report
- `Documentation/SkepticReport.md` — Read this FIRST, cover to cover

### Main Repository (`/media/dan/fdrive/codeprojects/MockEtlFramework`)

**Documentation (read every file EXCEPT ClaudeTranscript.md):**
- `Documentation/Strategy.md` — Framework architecture
- `Documentation/POC.md` — Origin story and project intent
- `Documentation/Phase2Plan.md` — The 31 bad jobs and their planted anti-patterns
- `Documentation/Phase3Blueprint.md` — The CLAUDE.md, prep script, kickoff prompt, Run 2 design
- `Documentation/Phase3AntiPatternAnalysis.md` — Run 1 failure analysis (0% elimination)
- `Documentation/Phase3ExecutiveReport.md` — Run 2 results
- `Documentation/Phase3Observations.md` — Real-time monitoring log (18 checks across both runs)
- `Documentation/CustomerAddressDeltasBrd.md` — Hand-crafted BRD for reference
- `Documentation/CoveredTransactionsBrd.md` — Hand-crafted BRD for reference
- `Documentation/SkepticBlueprint.md` — The skeptic's launch prompt (understand what the skeptic was told to do and how that shapes its output)

**Agent Instructions:**
- `CLAUDE.md` — The instruction set that governed the Phase 3 agent team

**Source Code (skim for framework understanding):**
- `Lib/` — The framework code (modules, control, data frames)
- `ExternalModules/` — The original "bad" External modules
- `JobExecutor/Jobs/*.json` — The original job configurations

### Phase 3 Run 1 Clone (`/media/dan/fdrive/codeprojects/MockEtlFramework-Phase3`)

- `Phase3/brd/` — Agent-produced BRDs
- `Phase3/governance/` — Agent-produced governance reports
- `ExternalModules/*V2*` — Agent-produced V2 code

### Phase 3 Run 2 Clone (`/media/dan/fdrive/codeprojects/MockEtlFramework-Phase3Run2`)

- `Phase3/brd/` — Agent-produced BRDs with anti-pattern identification
- `Phase3/fsd/` — Agent-produced FSDs
- `Phase3/tests/` — Agent-produced test plans
- `Phase3/governance/` — Agent-produced governance reports (especially `executive_summary.md`)
- `Phase3/logs/comparison_log.md` — The comparison loop audit trail
- `Phase3/logs/discussions.md` — Agent-to-agent disambiguation
- `ExternalModules/*V2*` — V2 code
- `JobExecutor/Jobs/*v2*` — V2 job configurations
- `CLAUDE.md` — The Run 2 instruction set

### ATC Design Documents (`/home/dan/Documents/ATC`)

- `Phase4Playbook.md` — The guide for scaling from POC to real platform
- `ATC_How_It_Could_Work (1).docx` — The detailed ATC architecture (extract text with Python: `python3 -c "import zipfile, xml.etree.ElementTree as ET; z = zipfile.ZipFile('/home/dan/Documents/ATC/ATC_How_It_Could_Work (1).docx'); doc = ET.fromstring(z.read('word/document.xml')); ns = {'w': 'http://schemas.openxmlformats.org/wordprocessingml/2006/main'}; [print(''.join(t.text or '' for t in p.iter('{http://schemas.openxmlformats.org/wordprocessingml/2006/main}t'))) for p in doc.iter('{http://schemas.openxmlformats.org/wordprocessingml/2006/main}p')]"`)
- `Project_ATC_ExecutiveDeck.pptx` — The executive pitch (extract text with Python: `python3 -c "import zipfile; z = zipfile.ZipFile('/home/dan/Documents/ATC/Project_ATC_ExecutiveDeck.pptx'); import xml.etree.ElementTree as ET; slides = sorted([f for f in z.namelist() if f.startswith('ppt/slides/slide') and f.endswith('.xml')]); [print(f'--- {s} ---') or [print(t.text) for t in ET.fromstring(z.read(s)).iter('{http://schemas.openxmlformats.org/drawingml/2006/main}t') if t.text] for s in slides]"`)

## Rules of Engagement

1. **Read the skeptic's report first, then the sources.** You need to understand what's being claimed before you can evaluate it. But do not take the skeptic at face value — verify every citation the skeptic makes. If the skeptic misquotes or mischaracterizes a source, call it out.

2. **The skeptic was told to be hostile.** Factor that into your assessment. The skeptic's launch prompt (`Documentation/SkepticBlueprint.md`) explicitly instructed it to assume the worst, find fatal flaws, and be unsparing. Some of the skeptic's conclusions may be artifacts of that framing rather than honest assessments of risk. Distinguish between genuine insight sharpened by adversarial framing and manufactured alarm.

3. **Apply the same evidence standard in both directions.** If you dismiss a skeptic concern, your dismissal must be as well-evidenced as the concern itself. "The skeptic is wrong about X" requires you to show why, with file paths and citations. No hand-waving from you either.

4. **Severity calibration matters.** The skeptic was told to be calibrated, but hostile reviewers tend to inflate severity. Your adjusted severity ratings should reflect your honest assessment of likelihood and impact, not the skeptic's framing.

5. **The action plan is the most important section.** The decision-maker reading your report will spend the most time on Part 3. Make it concrete, specific, and actionable. Every recommendation should answer: what to do, when to do it, and what success looks like.

6. **Acknowledge uncertainty.** Some of the skeptic's concerns may involve questions that genuinely cannot be resolved with the available evidence. When that's the case, say so. "This concern cannot be evaluated without access to the production platform's actual dependency graph" is a legitimate finding.

Begin. Read the skeptic's report, then read all sources, then write the EvaluatorReport.md.
```

---

## Post-Run

The evaluator's output (`Documentation/EvaluatorReport.md`) provides a balanced assessment with a concrete action plan. Together with the skeptic's report, it gives the project lead two opposing perspectives and a synthesized path forward.
