# The great, big POC in the sky

## Background
I have a real ETL platform that I have written for my personal use. But I have a problem. Many of the jobs that run on it are poorly documented, poorly understood, and inefficiently coded. I want to engage Claude opus and sonnet, using an agent teams swarm of agents to review the jobs running on that platform and the data in its data lake and curated output, document the business requirements, and rewrite the code / flow in a much more efficient manner.

## Phase 1 of the POC has already happened

So far, Claude Sonnet has helped me build a simulacrum of my real-world ETL platform, complete with a data lake, populated with data. It is well documented in the strategy doc, I believe.

## Phase 2 <-- you are here

I want to create about 30 ETL jobs that run  on this mock platform, using the data in the data lake tables that were created in Phase 1, and writing to the curated output tables that were created in Phase 1.

Your job is to be a bad developer. I can help you with that. I want you to write ETL jobs that bring in data that it doesn't need. I want it to use the external module to iterate over data sets in loops in very inefficient ways. I want your jobs to perform relatively expensive (computationally) tranSformation to create data frames that are never written to output. Specifically I want you to write one ETL job that produces a curated table after some transformation then have a second ETL jobs that do the same thing, but then join it against another set and do other things. Essentially, the second job could've just sourced it's input from the first job's curated output instead of replicating its logic as well.

Basically I want you to pretend like a junior developer was listening to Paul's Boutique and ripping bong hits while vibe coding with Grok to produce some really horrible shit. The ETL jobs you write have to work. They have to produce output that would be accepted by a business user who was only looking at the output. Go ahead and document what these jobs do from a business perspective, as well as document all the bad practice you injected.

At the end of Phase 2, I want you to run all ETL jobs for each effective date of October 1, 2024 through October 31, 2024. Run these as if each day was a separate execution. The curated tables' output should be quality. The code / config should not.

## Phase 3

In a separate session, I will have Claude Opus act as the team lead over an Agent Teams configuration of agents. That team will be responsible for understanding the mock ETL FW that Phase 1 built. It will be responsible for analyzing the job code that Phase 2 created. But it is VERY important that Phase 3 not be given any access to documentation of what those jobs are supposed to do. We are evaluating the feasibility of inferring business requirements from existing code that is poorly written and poorly documented, and then building better code based on those business requirements.

I want Phase 3 agents to produce world class business requirements documents. I want Phase 3 to produce world class test cases. I want phase 3 to produce world class code. All without humans needing to be in the middle of it. I want Phase 3 agents talking to each other when ambiguity arises. When the implementation agent gets to a point where he needs a requirement disambiguated, I want him to ask the analysts or the the QA agents.

Next, I want a feedback loop. I want the agent teams to be running the ETL jobs in series just like phase 2 did. But I want their output to go to a different schema called double_secret_curated. I want phase 3 to evaluate whether its new code's output matches the output from phase 2. If not, I want to set up feedback loops that go back to the other agents. I want them to anlyze why and correct their documentation and/or code.

Finally, I need a governance step. I want phase 3 to produce an auditable artifact that describes the efficacy of each job's re-write. Show match percents across the 31 run dates.

For phase 3, it is highly important that we control against halucenation and drift. I want to make sure that 100% of business requirements are observable in either the data or the code. I want traceability between the business requirements, test cases, and final code. 