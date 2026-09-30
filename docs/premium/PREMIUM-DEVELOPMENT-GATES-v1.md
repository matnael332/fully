# Premium Development Gates v1

Status: **ACTIVE — Premium Gate P0 (Architecture Freeze) PASS, closed by the product owner on 2026-09-29.** P1 to P11
are the approved planned sequence and are **NOT STARTED**; each needs its own explicit authorization.

Premium development follows the discipline of Core Development Gates v1: one Gate at a time; a Gate is complete only
when its acceptance criteria and required verification pass; the product owner approves every Gate and every movement
between Gates. The architecture and rules these Gates build are in `docs/premium/FULLY-PREMIUM-ARCHITECTURE-v1.md` and
`docs/premium/PREMIUM-POLICIES-v1.md`.

## 1. Relationship to the Core Gates

- **One product, two tracks.** The Premium Gates add the Premium layer. They never replace, reorder or weaken a Core
  Gate, and they change no frozen Core document.
- **One Gate at a time across both tracks.** A Core Gate and a Premium Gate are never in progress together unless the
  product owner explicitly decides otherwise.
- **P0 and Core Gate 3.** P0 is a **planning prerequisite** to Core Gate 3, not a technical dependency of the Event
  Backbone. It comes first so that Gate 3's connector design already respects the final Premium contracts for
  identity, Tool boundaries, state, verification and future autonomous execution (Premium architecture §20). **The
  product owner closed P0 on 2026-09-29; Core Gate 3 may begin only after that closure is committed, and only on the
  product owner's explicit authorization.**
- **P1 to P11 do not need to be implemented before Core Gate 3.** When they run relative to Core Gates 4 to 9 is
  product-owner decision L1, taken before P1 is authorized.
- **Core capabilities come from Core Gates.** A Premium Gate never builds a Core capability ahead of the Core Gate that
  owns it. It waits for its Core prerequisites, listed below.

## 2. Workflow of every Premium Gate

**Definition of Done → Implementation → Automated Tests → Failure / Attack Tests → Core Contradiction Audit →
Security Review → Cost / Observability Check → Gate Report → PASS / FAIL → Product Owner Approval.**

This extends the Core Gate workflow (build, automated tests, edge and failure cases, PRD and architecture
contradiction check, report, PASS or FAIL, product-owner approval) with attack tests, a security review and a cost and
observability check. The standing rules of the Core Gates apply to every Premium Gate: no Azure change, deployment or
job start without separate product-owner approval; no change to a frozen document; tests and security rules are never
weakened to make a Gate pass.

## 3. The Gates

Premium Gates run in order, P1 to P11, each only after the previous one closed. The scope below is indicative; each
Gate's Definition of Done is written when the product owner authorizes it.

| Gate | Name | Delivers (indicative) | Core prerequisites | Status |
|---|---|---|---|---|
| P0 | Architecture Freeze | The Premium architecture, policies and Gate plan, and the contradiction audit against the Core documents | None: documentation only | **PASS** — architecture frozen (2026-09-29) |
| P1 | Agent Platform Foundation | Premium modules with enforced boundaries; entitlement and feature control, failing closed; the Agent and Tool registries and their contracts; tenant-owned Premium data foundations under forced row-level security | Core Gate 3 closed | Planned; NOT STARTED |
| P2 | Security & Authority | The Premium Autonomous Action Policy in the Core Policy Engine; Policy Grants; delegated identity; revocation at execution time; hierarchical kill switches; attack tests | The Core Policy Engine and Action Engine (Core Gate 6); Core kill switches and resource authorization (Core Gate 7) | Planned; NOT STARTED |
| P3 | Durable State & Adaptive Router | The durable Task store, checkpoints, handoffs, idempotent or effect-detecting operations, restart recovery; the deterministic Adaptive Router | As P2 | Planned; NOT STARTED |
| P4 | Agent Runtime | Agent execution through the Core AI Gateway; the Tool call path; budgets; structured, validated Agent output | The Core AI Gateway, Context Builder and structured output (Core Gate 4) | Planned; NOT STARTED |
| P5 | Multi-Agent Orchestration | Execution patterns 3 to 5, Domain Managers, the Super Agent, single ownership, Sub-Agent limits, the Shared Incident Context | As P4 | Planned; NOT STARTED |
| P6 | Memory & Intelligence | The five memory layers, Memory Proposals, retrieval scoping, retention classes, the cross-customer path | Core Work Memory and scoped pgvector retrieval (Core Gate 4) | Planned; NOT STARTED |
| P7 | Verification & Recovery | The verification service, rollback and compensation, the failure and escalation path | Action Engine verification (Core Gate 6); authoritative reread (Core Gate 3) | Planned; NOT STARTED |
| P8 | Support Domain | The reactive Support Domain, end to end, on helpdesk or CRM triggers | Helpdesk or CRM connectors approved (decision L3) | Planned; NOT STARTED |
| P9 | Cost / Observability / Quality | Cost per Task, Agent and model; budget variance; the Human Interruption Rate; the end-to-end trace; quality metrics | Core observability (Gate 1G) | Planned; NOT STARTED |
| P10 | Agent Improvement + Synthetic Agentic Pilot | The self-improvement pipeline; a synthetic agentic pilot with deterministic ground truth, fault injection and attack scenarios | Core synthetic pilot infrastructure (Core Gate 8) | Planned; NOT STARTED |
| P11 | Human Premium Pilot | A bounded human pilot with Premium enabled | Core human pilot readiness (Core Gate 9); legal and privacy review (decision L4) | Planned; NOT STARTED |

## 4. Indicative acceptance

Each Premium Gate's own Definition of Done sets its acceptance criteria. As an indication of what each must prove:

- **P0** — the Premium architecture is documented, audited against every Core document, and closed by the product
  owner with decisions D1 to D4 and C1 (`docs/gates/gate-P0-report.md`). Met: PASS on 2026-09-29.
- **P1** — a tenant without proven entitlement gets no Premium capability on any path; feature control works at every
  level; a Tool without a complete contract cannot be registered; module boundaries are enforced by architecture tests.
- **P2** — no Tool call executes without passing every check; revocation, expiry and usage limits take effect at the
  next call; a Sub-Agent never exceeds its parent's authority; every kill switch level stops work; attack tests show
  zero cross-tenant and zero unauthorized-action failures.
- **P3** — a crash at any step resumes from its checkpoint without replaying any external effect, and every handoff is
  durable before it counts.
- **P4** — every model call goes through the Core AI Gateway, Agent output is schema-validated, and budgets hold.
- **P5** — every Task has exactly one owner at all times; peers cannot assign work; depth and Sub-Agent limits hold; the
  Super Agent is used only for cross-Domain work.
- **P6** — no memory is ever a fact; promotion happens only through accepted Memory Proposals; cross-tenant retrieval is
  impossible; retention classes are enforced.
- **P7** — nothing counts as succeeded without authoritative verification; UNKNOWN is reconciled; rollbacks are
  verified.
- **P8** — Support scenarios end verified or escalated, with reversible fixes only and the minimum necessary data.
- **P9** — every Task reports its cost, budget variance and Human Interruption Rate in one trace, and telemetry hygiene
  tests pass.
- **P10** — a candidate version reaches production only through the pipeline, a Super Agent replacement needs human
  approval, and the synthetic agentic pilot passes its Go/No-Go criteria.
- **P11** — every prior Premium Gate and its Core prerequisites are green, and the human pilot meets the Go/No-Go
  criteria of PRD v1 §22, including its immediate No-Go conditions.

## 5. Status

| Gate | Status |
|---|---|
| P0 Architecture Freeze | **PASS** — architecture frozen; closed by the product owner on 2026-09-29 |
| P1 to P11 | Planned; NOT STARTED; each needs explicit authorization |
| Core Gate 3 (monday Connector) | IN PROGRESS: authorized 2026-09-29, after the P0 closure commit `999d2f9`; 3A PASS (2026-09-30); 3B PASS (2026-09-30); 3C PASS (2026-09-30); 3D next, not started |
